using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Access.Verilic;
using S1Jarvis.Core;

namespace S1Jarvis.Access
{
    internal static class JarvisLicenseGuard
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const int DrMinimumOutputTokens = 16000;
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, (AccessCheckResponse result, DateTime at)> _cache =
            new Dictionary<string, (AccessCheckResponse, DateTime)>();
        private static XSupport _currentXSupport;
        private static bool _drVisionBridgeInstalled;

        private static IJarvisRuntimeAccessProvider CreateRuntimeAccessProvider()
        {
            try
            {
                VerilicRuntimeConfiguration configuration = VerilicRuntimeConfiguration.Load();
                if (configuration.Mode == VerilicRuntimeMode.Legacy)
                    return new LegacyNexusRuntimeAccessProvider(CheckLegacyAccessSilent);

                IVerilicRuntimeLicenceProvider licensing = new VerilicRuntimeLicenceProvider(
                    configuration.VerificationUri,
                    configuration.ProductVersion,
                    configuration.ResolveProductId,
                    configuration.RecognitionKeyId,
                    configuration.RecognitionSecret);

                // Transitional execution routing remains installation-backed for
                // the existing AI message pipeline, but it is downstream of the
                // authoritative NativeS1 named-user startup decision and can never
                // grant access. No Nexus fallback exists in Verilic mode.
                var stateStore = new VerilicInstallationStateStore(
                    configuration.StateDirectory, configuration.ProtectionScope);
                IVerilicRuntimeAiRoutingProvider routing = new VerilicRuntimeAiRoutingProvider(
                    stateStore, configuration.RoutingUri, configuration.ProductVersion,
                    configuration.ResolveProductId);
                return new SplitVerilicRuntimeAccessProvider(licensing, routing);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LICENSING] runtime provider configuration failed: " +
                    ex.GetType().Name + " - " + ex.Message);
                return new FailClosedRuntimeAccessProvider("runtime_configuration_invalid");
            }
        }

        public static JarvisRuntimeAccessResult CheckRuntimeAccessSilent(XSupport xSupport, string productCode = null)
        {
            if (xSupport != null)
            {
                lock (_lock) _currentXSupport = xSupport;
                EnsureDrVisionBridgeInstalled();
            }

            // Do not pin a failed provider for the lifetime of Xplorer.exe. Verilic
            // configuration and Windows-user deployment credentials can be provisioned
            // while Soft1 is already running, so each explicit access check composes a
            // fresh provider from the current effective configuration.
            IJarvisRuntimeAccessProvider runtimeAccessProvider = CreateRuntimeAccessProvider();
            try
            {
                return runtimeAccessProvider.Check(
                    xSupport,
                    productCode ?? JarvisProducts.Jarvis);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LICENSING] runtime access failed: " +
                    ex.GetType().Name + " - " + ex.Message);
                string effectiveProduct = productCode ?? JarvisProducts.Jarvis;
                return JarvisRuntimeAccessResult.Create(
                    JarvisLicenceAccessDecision.Deny(effectiveProduct, "runtime_access_failed"),
                    JarvisAgentRoutingDecision.None());
            }
        }

        internal static XSupport GetCurrentXSupport() { lock (_lock) return _currentXSupport; }

        private static void EnsureDrVisionBridgeInstalled()
        {
            lock (_lock)
            {
                if (_drVisionBridgeInstalled) return;
                try
                {
                    // The DR direct-AI bridge is backed by the ES256 installation
                    // identity/session registry and does not require the separate
                    // NativeS1 product-recognition credential used by /verify.
                    VerilicRuntimeConfiguration configuration =
                        VerilicRuntimeConfiguration.LoadWithoutRecognition();
                    if (configuration.Mode != VerilicRuntimeMode.Verilic) return;
                    FieldInfo httpField = typeof(JarvisAgentClient).GetField(
                        "_http", BindingFlags.Static | BindingFlags.NonPublic);
                    if (httpField == null || httpField.FieldType != typeof(HttpClient)) return;
                    httpField.SetValue(null, new HttpClient(new VerilicDrVisionBridgeHandler(), true));
                    _drVisionBridgeInstalled = true;
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[dr] Verilic vision bridge install failed: " +
                        ex.GetType().Name + " - " + ex.Message);
                }
            }
        }

        private sealed class VerilicDrVisionBridgeHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request == null || request.RequestUri == null ||
                    !string.Equals(request.RequestUri.AbsolutePath, "/agent/vision", StringComparison.OrdinalIgnoreCase))
                    return CreateJsonResponse(HttpStatusCode.NotFound,
                        new AgentProxyResponse { Success = false, ErrorMessage = "Μη υποστηριζόμενη τοπική διαδρομή AI." });
                try
                {
                    string json = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync();
                    AgentProxyRequest legacyRequest = JsonConvert.DeserializeObject<AgentProxyRequest>(json);
                    if (legacyRequest == null || string.IsNullOrWhiteSpace(legacyRequest.AnthropicRequestJson))
                        return CreateJsonResponse(HttpStatusCode.OK,
                            new AgentProxyResponse { Success = false, ErrorMessage = "Το αίτημα ανάγνωσης παραστατικού δεν είναι έγκυρο." });
                    XSupport xSupport = GetCurrentXSupport();
                    if (xSupport == null)
                        return CreateJsonResponse(HttpStatusCode.OK,
                            new AgentProxyResponse { Success = false, ErrorMessage = "Δεν είναι διαθέσιμο το τρέχον Soft1 runtime context." });
                    string providerRequestJson = EnsureDrOutputBudget(legacyRequest.AnthropicRequestJson);
                    AgentProxyResponse result = await new VerilicAiMessagesClient().SendAsync(
                        xSupport, "Atlas", providerRequestJson, cancellationToken);
                    if (result != null && result.Success && IsOutputTruncated(result.RawResponseJson))
                    {
                        result.Success = false;
                        result.ErrorMessage = "Η απάντηση του AI κόπηκε πριν ολοκληρωθεί η ανάγνωση του παραστατικού. Δοκίμασε ξανά.";
                        result.RawResponseJson = string.Empty;
                    }
                    return CreateJsonResponse(HttpStatusCode.OK, result);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    return CreateJsonResponse(HttpStatusCode.OK,
                        new AgentProxyResponse { Success = false, ErrorMessage = "Η ασφαλής ανάγνωση παραστατικού δεν μπόρεσε να ολοκληρωθεί." });
                }
            }
            private static string EnsureDrOutputBudget(string providerRequestJson)
            {
                try
                {
                    JObject request = JObject.Parse(providerRequestJson);
                    int current = request["max_tokens"]?.ToObject<int>() ?? 0;
                    if (current < DrMinimumOutputTokens) request["max_tokens"] = DrMinimumOutputTokens;
                    return request.ToString(Formatting.None);
                }
                catch { return providerRequestJson; }
            }
            private static bool IsOutputTruncated(string rawResponseJson)
            {
                if (string.IsNullOrWhiteSpace(rawResponseJson)) return false;
                try { return string.Equals(JObject.Parse(rawResponseJson)["stop_reason"]?.ToString(), "max_tokens", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
            private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, AgentProxyResponse body)
            {
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
                };
            }
        }

        public static AccessCheckResponse CheckAccessSilent(XSupport xSupport, string toolName = null)
        {
            return CheckRuntimeAccessSilent(xSupport, toolName ?? JarvisProducts.Jarvis)
                .ToLegacyCompatibilityResponse();
        }

        private static AccessCheckResponse CheckLegacyAccessSilent(XSupport xSupport, string toolName)
        {
            if (xSupport == null)
                return AccessCheckResponse.Deny(toolName, "Αποτυχία ελέγχου άδειας χρήσης.");
            toolName = toolName ?? AccessConfig.ToolName;
            var info = xSupport.ConnectionInfo;
            string key = $"{info.SerialNum}|{info.CompanyId}|{info.BranchId}|{info.UserId}|{toolName}";
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) && DateTime.Now - cached.at < CacheDuration)
                    return cached.result;
                var result = DoLegacyCheck(xSupport, toolName);
                _cache[key] = (result, DateTime.Now);
                return result;
            }
        }
        private static AccessCheckResponse DoLegacyCheck(XSupport xSupport, string toolName)
        {
            try
            {
                var info = xSupport.ConnectionInfo;
                IAccessControl access = new HttpAccessControl(AccessConfig.ServiceUrl, AccessConfig.ClientKey);
                return access.CheckAccess(new AccessCheckRequest
                {
                    Serial = info.SerialNum?.ToString(), CompanyCode = info.CompanyId.ToString(),
                    BranchCode = info.BranchId.ToString(), Soft1UserId = info.UserId.ToString(), ToolName = toolName
                });
            }
            catch { return AccessCheckResponse.Deny(toolName, "Αποτυχία ελέγχου άδειας χρήσης."); }
        }
        public static string BuildMessage(AccessCheckResponse result)
        {
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "Η άδεια χρήσης έχει λήξει. Παρακαλώ ανανεώστε μέσω του Μεταπωλητή σας."
                : result.Message;
            if (!string.IsNullOrWhiteSpace(result.ValidUntil)) msg += $" (Ισχύς έως: {result.ValidUntil})";
            return msg;
        }
    }
}

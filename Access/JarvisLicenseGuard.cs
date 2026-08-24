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
    // ══════════════════════════════════════════════════════════════════════
    // JarvisLicenseGuard
    //
    // Runtime access boundary for the Jarvis product family. Legacy mode keeps
    // the existing combined Nexus lookup. Verilic mode composes authoritative
    // licensing plus signed AI-routing resolution with no legacy fallback.
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisLicenseGuard
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const int DrMinimumOutputTokens = 16000;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, (AccessCheckResponse result, DateTime at)> _cache =
            new Dictionary<string, (AccessCheckResponse, DateTime)>();

        // DR one-shot methods were written before the Verilic cutover and do not
        // carry XSupport in their public signature. Keep only the current local
        // Soft1 runtime object so the compatibility bridge below can construct
        // the same signed Verilic messages request as the normal Jarvis chat.
        // No provider credential, licence secret or private key is copied here.
        private static XSupport _currentXSupport;
        private static bool _drVisionBridgeInstalled;

        private static readonly IJarvisRuntimeAccessProvider _runtimeAccessProvider =
            CreateRuntimeAccessProvider();

        private static IJarvisRuntimeAccessProvider CreateRuntimeAccessProvider()
        {
            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();

                if (configuration.Mode == VerilicRuntimeMode.Legacy)
                    return new LegacyNexusRuntimeAccessProvider(
                        CheckLegacyAccessSilent);

                var stateStore = new VerilicInstallationStateStore(
                    configuration.StateDirectory,
                    configuration.ProtectionScope);

                IVerilicRuntimeLicenceProvider licensing =
                    new VerilicRuntimeLicenceProvider(
                        stateStore,
                        configuration.VerificationUri,
                        configuration.ProductVersion,
                        configuration.ResolveProductId);

                IVerilicRuntimeAiRoutingProvider routing =
                    new VerilicRuntimeAiRoutingProvider(
                        stateStore,
                        configuration.RoutingUri,
                        configuration.ProductVersion,
                        configuration.ResolveProductId);

                return new SplitVerilicRuntimeAccessProvider(
                    licensing,
                    routing);
            }
            catch
            {
                // If Verilic mode was explicitly requested but its configuration
                // cannot be composed, never fall back to legacy authorization.
                return new FailClosedRuntimeAccessProvider(
                    "runtime_configuration_invalid");
            }
        }

        public static JarvisRuntimeAccessResult CheckRuntimeAccessSilent(
            XSupport xSupport,
            string productCode = null)
        {
            if (xSupport != null)
            {
                lock (_lock)
                    _currentXSupport = xSupport;

                // The legacy DR code still calls JarvisAgentClient.CallProxyAsync,
                // which targets /agent/vision. In Verilic mode that must never go
                // back to the old Nexus proxy. Install a narrow in-process
                // HttpMessageHandler once, before the DR entitlement returns.
                // The handler intercepts only /agent/vision and forwards the
                // provider-shaped payload through VerilicAiMessagesClient, where
                // licence/proof/routing/account/model are authoritative server-side.
                EnsureDrVisionBridgeInstalled();
            }

            try
            {
                return _runtimeAccessProvider.Check(
                    xSupport,
                    productCode ?? JarvisProducts.Jarvis);
            }
            catch
            {
                string effectiveProduct =
                    productCode ?? JarvisProducts.Jarvis;
                return JarvisRuntimeAccessResult.Create(
                    JarvisLicenceAccessDecision.Deny(
                        effectiveProduct,
                        "runtime_access_failed"),
                    JarvisAgentRoutingDecision.None());
            }
        }

        internal static XSupport GetCurrentXSupport()
        {
            lock (_lock)
                return _currentXSupport;
        }

        private static void EnsureDrVisionBridgeInstalled()
        {
            lock (_lock)
            {
                if (_drVisionBridgeInstalled)
                    return;

                try
                {
                    VerilicRuntimeConfiguration configuration =
                        VerilicRuntimeConfiguration.Load();
                    if (configuration.Mode != VerilicRuntimeMode.Verilic)
                        return;

                    FieldInfo httpField = typeof(JarvisAgentClient).GetField(
                        "_http",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (httpField == null || httpField.FieldType != typeof(HttpClient))
                    {
                        DebugLog.Log("[dr] Verilic vision bridge: JarvisAgentClient._http was not found.");
                        return;
                    }

                    var bridgedClient = new HttpClient(
                        new VerilicDrVisionBridgeHandler(),
                        disposeHandler: true);
                    httpField.SetValue(null, bridgedClient);
                    _drVisionBridgeInstalled = true;
                    DebugLog.Log("[dr] Verilic vision bridge installed.");
                }
                catch (Exception ex)
                {
                    // Licensing itself must remain available even if this
                    // transitional DR transport bridge cannot be installed.
                    DebugLog.Log("[dr] Verilic vision bridge install failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Temporary compatibility boundary for the two mature DR one-shot
        /// methods. They still serialize AgentProxyRequest and call the private
        /// /agent/vision transport in JarvisAgentClient. Rather than re-enable
        /// that legacy server path, intercept it locally and invoke the same
        /// signed Verilic messages client used by normal Jarvis traffic.
        ///
        /// The legacy AgentAccountRef in the envelope is deliberately ignored:
        /// Verilic resolves the authoritative account/provider/model server-side.
        /// DR is not a normal chat helper, so the bridge uses Atlas only as the
        /// neutral default-target selector; the operation remains the DR feature.
        /// </summary>
        private sealed class VerilicDrVisionBridgeHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request == null ||
                    request.RequestUri == null ||
                    !string.Equals(
                        request.RequestUri.AbsolutePath,
                        "/agent/vision",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return CreateJsonResponse(
                        HttpStatusCode.NotFound,
                        new AgentProxyResponse
                        {
                            Success = false,
                            ErrorMessage = "Μη υποστηριζόμενη τοπική διαδρομή AI."
                        });
                }

                try
                {
                    string json = request.Content == null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync();
                    AgentProxyRequest legacyRequest =
                        JsonConvert.DeserializeObject<AgentProxyRequest>(json);

                    if (legacyRequest == null ||
                        string.IsNullOrWhiteSpace(legacyRequest.AnthropicRequestJson))
                    {
                        return CreateJsonResponse(
                            HttpStatusCode.OK,
                            new AgentProxyResponse
                            {
                                Success = false,
                                ErrorMessage = "Το αίτημα ανάγνωσης παραστατικού δεν είναι έγκυρο."
                            });
                    }

                    XSupport xSupport = GetCurrentXSupport();
                    if (xSupport == null)
                    {
                        return CreateJsonResponse(
                            HttpStatusCode.OK,
                            new AgentProxyResponse
                            {
                                Success = false,
                                ErrorMessage = "Δεν είναι διαθέσιμο το τρέχον Soft1 runtime context."
                            });
                    }

                    // DR extraction is intentionally provider-neutral. Thinking
                    // models may consume part of the output budget internally,
                    // while other providers may not. Give every DR one-shot call
                    // the same sufficiently large ceiling instead of adding a
                    // Gemini-only branch. Providers still stop naturally when the
                    // structured response is complete.
                    string providerRequestJson = EnsureDrOutputBudget(
                        legacyRequest.AnthropicRequestJson);

                    DebugLog.Log("[dr] Forwarding document vision through signed Verilic messages using the configured default AI target.");
                    AgentProxyResponse result = await new VerilicAiMessagesClient()
                        .SendAsync(
                            xSupport,
                            "Atlas",
                            providerRequestJson,
                            cancellationToken);

                    // Never feed a response that is known to be truncated into
                    // the JObject parser used by DR. This keeps the failure
                    // deterministic instead of surfacing an "Unexpected end of
                    // content" JSON exception to the operator.
                    if (result != null && result.Success && IsOutputTruncated(result.RawResponseJson))
                    {
                        DebugLog.Log("[dr] Provider stopped at max_tokens before structured extraction completed.");
                        result.Success = false;
                        result.ErrorMessage =
                            "Η απάντηση του AI κόπηκε πριν ολοκληρωθεί η ανάγνωση του παραστατικού. Δοκίμασε ξανά.";
                        result.RawResponseJson = string.Empty;
                    }

                    return CreateJsonResponse(HttpStatusCode.OK, result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[dr] Verilic vision bridge request failed: " + ex.GetType().Name + ": " + ex.Message);
                    return CreateJsonResponse(
                        HttpStatusCode.OK,
                        new AgentProxyResponse
                        {
                            Success = false,
                            ErrorMessage = "Η ασφαλής ανάγνωση παραστατικού δεν μπόρεσε να ολοκληρωθεί."
                        });
                }
            }

            private static string EnsureDrOutputBudget(string providerRequestJson)
            {
                try
                {
                    JObject request = JObject.Parse(providerRequestJson);
                    int current = request["max_tokens"]?.ToObject<int>() ?? 0;
                    if (current < DrMinimumOutputTokens)
                        request["max_tokens"] = DrMinimumOutputTokens;
                    return request.ToString(Formatting.None);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[dr] Could not normalize output budget: " + ex.GetType().Name + ": " + ex.Message);
                    return providerRequestJson;
                }
            }

            private static bool IsOutputTruncated(string rawResponseJson)
            {
                if (string.IsNullOrWhiteSpace(rawResponseJson))
                    return false;

                try
                {
                    JObject response = JObject.Parse(rawResponseJson);
                    return string.Equals(
                        response["stop_reason"]?.ToString(),
                        "max_tokens",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            private static HttpResponseMessage CreateJsonResponse(
                HttpStatusCode statusCode,
                AgentProxyResponse body)
            {
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(body),
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }

        /// <summary>
        /// Compatibility API for the existing JarvisShell call sites. The
        /// response shape stays unchanged, but its licensing Allowed value comes
        /// from the configured runtime access provider.
        /// </summary>
        public static AccessCheckResponse CheckAccessSilent(
            XSupport xSupport,
            string toolName = null)
        {
            return CheckRuntimeAccessSilent(
                    xSupport,
                    toolName ?? JarvisProducts.Jarvis)
                .ToLegacyCompatibilityResponse();
        }

        private static AccessCheckResponse CheckLegacyAccessSilent(
            XSupport xSupport,
            string toolName)
        {
            if (xSupport == null)
                return AccessCheckResponse.Deny(
                    toolName,
                    "Αποτυχία ελέγχου άδειας χρήσης.");

            toolName = toolName ?? AccessConfig.ToolName;
            var info = xSupport.ConnectionInfo;

            string key = $"{info.SerialNum}|{info.CompanyId}|{info.BranchId}|{info.UserId}|{toolName}";

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) &&
                    DateTime.Now - cached.at < CacheDuration)
                {
                    return cached.result;
                }

                var result = DoLegacyCheck(xSupport, toolName);
                _cache[key] = (result, DateTime.Now);
                return result;
            }
        }

        private static AccessCheckResponse DoLegacyCheck(
            XSupport xSupport,
            string toolName)
        {
            try
            {
                var info = xSupport.ConnectionInfo;

                IAccessControl access = new HttpAccessControl(
                    AccessConfig.ServiceUrl,
                    AccessConfig.ClientKey);

                return access.CheckAccess(new AccessCheckRequest
                {
                    Serial = info.SerialNum?.ToString(),
                    CompanyCode = info.CompanyId.ToString(),
                    BranchCode = info.BranchId.ToString(),
                    Soft1UserId = info.UserId.ToString(),
                    ToolName = toolName,
                });
            }
            catch
            {
                // Do not surface transport/internal exception details to the UI.
                return AccessCheckResponse.Deny(
                    toolName,
                    "Αποτυχία ελέγχου άδειας χρήσης.");
            }
        }

        public static string BuildMessage(AccessCheckResponse result)
        {
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "Η άδεια χρήσης έχει λήξει. Παρακαλώ ανανεώστε μέσω του Μεταπωλητή σας."
                : result.Message;

            if (!string.IsNullOrWhiteSpace(result.ValidUntil))
                msg += $" (Ισχύς έως: {result.ValidUntil})";

            return msg;
        }
    }
}

using System;
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
        private const int DrMinimumOutputTokens = 16000;
        private static readonly object _lock = new object();
        private static XSupport _currentXSupport;
        private static bool _drVisionBridgeInstalled;

        private static IJarvisRuntimeAccessProvider CreateRuntimeAccessProvider()
        {
            return new VerilicNativeS1RuntimeAccessProvider(
                new VerilicRuntimeLicenceProvider());
        }

        public static JarvisRuntimeAccessResult CheckRuntimeAccessSilent(
            XSupport xSupport,
            string productCode = null)
        {
            if (xSupport != null)
            {
                lock (_lock)
                    _currentXSupport = xSupport;
                EnsureDrVisionBridgeInstalled();
            }

            try
            {
                return CreateRuntimeAccessProvider().Check(
                    xSupport,
                    productCode ?? JarvisProducts.Jarvis);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LICENSING] Verilic contract runtime verification failed: " +
                    ex.GetType().Name + " - " + ex.Message);
                string effectiveProduct = productCode ?? JarvisProducts.Jarvis;
                return JarvisRuntimeAccessResult.Create(
                    JarvisLicenceAccessDecision.Deny(effectiveProduct, "runtime_access_failed"),
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
                    FieldInfo httpField = typeof(JarvisAgentClient).GetField(
                        "_http",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (httpField == null || httpField.FieldType != typeof(HttpClient))
                        return;

                    httpField.SetValue(
                        null,
                        new HttpClient(new VerilicDrVisionBridgeHandler(), true));
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
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request == null || request.RequestUri == null ||
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

                    string providerRequestJson =
                        EnsureDrOutputBudget(legacyRequest.AnthropicRequestJson);
                    AgentProxyResponse result = await new VerilicAiMessagesClient().SendAsync(
                        xSupport,
                        "Atlas",
                        providerRequestJson,
                        cancellationToken);

                    if (result != null && result.Success &&
                        IsOutputTruncated(result.RawResponseJson))
                    {
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
                catch
                {
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
                catch
                {
                    return providerRequestJson;
                }
            }

            private static bool IsOutputTruncated(string rawResponseJson)
            {
                if (string.IsNullOrWhiteSpace(rawResponseJson))
                    return false;
                try
                {
                    return string.Equals(
                        JObject.Parse(rawResponseJson)["stop_reason"]?.ToString(),
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

        public static AccessCheckResponse CheckAccessSilent(
            XSupport xSupport,
            string toolName = null)
        {
            return CheckRuntimeAccessSilent(
                    xSupport,
                    toolName ?? JarvisProducts.Jarvis)
                .ToLegacyCompatibilityResponse();
        }

        public static string BuildMessage(AccessCheckResponse result)
        {
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "Η άδεια χρήσης δεν είναι διαθέσιμη."
                : result.Message;
            if (!string.IsNullOrWhiteSpace(result.ValidUntil))
                msg += " (Ισχύς έως: " + result.ValidUntil + ")";
            return msg;
        }
    }
}

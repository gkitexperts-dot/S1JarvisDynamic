using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Core
{
    internal sealed class JarvisAgentHealthTargetResult
    {
        public string Agent { get; set; }
        public bool Ready { get; set; }
        public string ReasonCode { get; set; }
        public string AgentAccountRef { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public bool Inherited { get; set; }
        public string DiagnosticCode { get; set; }
        public string DiagnosticMessage { get; set; }
    }

    internal sealed class JarvisAgentHealthResult
    {
        public bool Ready { get; private set; }
        public bool CreditsExhausted { get; private set; }
        public string ReasonCode { get; private set; }
        public string Provider { get; private set; }
        public string Model { get; private set; }
        public string DiagnosticCode { get; private set; }
        public string DiagnosticMessage { get; private set; }
        public IReadOnlyList<JarvisAgentHealthTargetResult> Targets { get; private set; }

        public static JarvisAgentHealthResult Success(
            string provider,
            string model,
            IReadOnlyList<JarvisAgentHealthTargetResult> targets)
        {
            return new JarvisAgentHealthResult
            {
                Ready = true,
                ReasonCode = "provider_ready",
                Provider = Normalize(provider),
                Model = Normalize(model),
                Targets = targets ?? new List<JarvisAgentHealthTargetResult>()
            };
        }

        public static JarvisAgentHealthResult Failure(
            string reasonCode,
            bool creditsExhausted = false,
            string provider = null,
            string model = null,
            IReadOnlyList<JarvisAgentHealthTargetResult> targets = null,
            string diagnosticCode = null,
            string diagnosticMessage = null)
        {
            return new JarvisAgentHealthResult
            {
                Ready = false,
                CreditsExhausted = creditsExhausted,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "provider_unavailable"
                    : reasonCode,
                Provider = Normalize(provider),
                Model = Normalize(model),
                DiagnosticCode = Normalize(diagnosticCode),
                DiagnosticMessage = Normalize(diagnosticMessage),
                Targets = targets ?? new List<JarvisAgentHealthTargetResult>()
            };
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>
    /// BOOT/explicit HEALTH NativeS1 provisioning. This intentionally uses the
    /// same named-user /api/licensing/v1/verify contract as startup licensing.
    /// There is no installation id, device binding, activation state or ES256
    /// installation proof in this path.
    /// </summary>
    internal sealed class JarvisAgentHealthProbe
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private static readonly string[] Agents =
        {
            "Jarvis", "Atlas", "Forge", "Compass", "Echo", "Sprint", "Scout", "Sage"
        };

        static JarvisAgentHealthProbe()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public Task<JarvisAgentHealthResult> ProbeAsync(
            XSupport xSupport,
            string expectedAgentAccountRef)
        {
            return ProbeAsync(xSupport, expectedAgentAccountRef, null);
        }

        public async Task<JarvisAgentHealthResult> ProbeAsync(
            XSupport xSupport,
            string expectedAgentAccountRef,
            string expectedModel)
        {
            if (xSupport == null)
                return JarvisAgentHealthResult.Failure("provider_probe_identity_missing");

            try
            {
                VerilicRuntimeConfiguration configuration = VerilicRuntimeConfiguration.Load();
                if (configuration.Mode != VerilicRuntimeMode.Verilic ||
                    configuration.VerificationUri == null)
                    return JarvisAgentHealthResult.Failure("provider_health_configuration_invalid");

                var info = xSupport.ConnectionInfo;
                if (info == null)
                    return JarvisAgentHealthResult.Failure("provider_probe_identity_missing");

                string productId = configuration.ResolveProductId(JarvisProducts.Jarvis);
                var requestBody = new VerilicVerifyLicenceRequest
                {
                    ProductId = productId,
                    ProductVersion = configuration.ProductVersion,
                    RuntimeContext = new VerilicRuntimeContext
                    {
                        Soft1Serial = info.SerialNum == null ? null : info.SerialNum.ToString(),
                        CompanyCode = info.CompanyId.ToString(),
                        BranchCode = info.BranchId.ToString(),
                        Soft1UserId = info.UserId.ToString()
                    }
                };

                string json = JsonConvert.SerializeObject(requestBody, Formatting.None);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                using (var request = new HttpRequestMessage(HttpMethod.Post, configuration.VerificationUri))
                using (var cts = new CancellationTokenSource(Timeout))
                {
                    request.Content = new ByteArrayContent(bodyBytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    new VerilicRecognitionRequestAuthorizer(
                        configuration.RecognitionKeyId,
                        configuration.RecognitionSecret).Authorize(request, bodyBytes);

                    DebugLog.Log("[AI-SESSION-REGISTRY] NativeS1 verify provisioning request");
                    using (HttpResponseMessage response = await Http.SendAsync(request, cts.Token))
                    {
                        string responseJson = await response.Content.ReadAsStringAsync();
                        VerilicVerifyLicenceResult verification;
                        try
                        {
                            verification = JsonConvert.DeserializeObject<VerilicVerifyLicenceResult>(responseJson);
                        }
                        catch (JsonException)
                        {
                            return JarvisAgentHealthResult.Failure("verification_response_invalid");
                        }

                        if (verification == null)
                            return JarvisAgentHealthResult.Failure("verification_response_invalid");

                        if (!response.IsSuccessStatusCode || !verification.Allowed)
                            return JarvisAgentHealthResult.Failure(
                                string.IsNullOrWhiteSpace(verification.ReasonCode)
                                    ? "verification_http_" + ((int)response.StatusCode).ToString()
                                    : verification.ReasonCode.Trim());

                        if (!string.Equals(verification.ProductId, productId, StringComparison.Ordinal))
                            return JarvisAgentHealthResult.Failure("verification_response_binding_mismatch");

                        VerilicVerifyProductResult product = verification.FindRequestedProduct(productId);
                        if (product == null || !product.Allowed)
                            return JarvisAgentHealthResult.Failure("requested_product_not_entitled");

                        if (!product.RuntimeReady)
                            return JarvisAgentHealthResult.Failure(
                                string.IsNullOrWhiteSpace(product.RuntimeReasonCode)
                                    ? "ai_runtime_not_ready"
                                    : product.RuntimeReasonCode.Trim(),
                                diagnosticMessage: product.RuntimeMessage);

                        List<JObject> usable = new List<JObject>();
                        foreach (JObject candidate in product.AiConfigurations ?? new List<JObject>())
                        {
                            if (candidate != null && ReadObject(candidate, "defaultTarget") != null)
                                usable.Add(candidate);
                        }

                        if (usable.Count == 0)
                            return JarvisAgentHealthResult.Failure(
                                "ai_default_target_unavailable",
                                diagnosticMessage: product.RuntimeMessage);
                        if (usable.Count > 1)
                            return JarvisAgentHealthResult.Failure(
                                "ai_multiple_configurations_available",
                                diagnosticMessage: product.RuntimeMessage);

                        IReadOnlyList<JarvisAgentHealthTargetResult> targets = BuildTargets(
                            usable[0],
                            verification.DecisionId,
                            productId,
                            configuration.RecognitionSecret);

                        string provider = targets.Count == 0 ? null : targets[0].Provider;
                        string model = targets.Count == 0 ? null : targets[0].Model;
                        DebugLog.Log("[AI-SESSION-REGISTRY] NativeS1 verify provisioning accepted; targets=" +
                            targets.Count.ToString());
                        return JarvisAgentHealthResult.Success(provider, model, targets);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return JarvisAgentHealthResult.Failure("provider_timeout");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-SESSION-REGISTRY] NativeS1 verify provisioning exception: " +
                    ex.GetType().Name + " - " + ex.Message);
                return JarvisAgentHealthResult.Failure("provider_health_failed");
            }
        }

        private static IReadOnlyList<JarvisAgentHealthTargetResult> BuildTargets(
            JObject configuration,
            string decisionId,
            string callerProductId,
            string recognitionSecret)
        {
            string contractId = ReadString(configuration, "contractId");
            if (string.IsNullOrWhiteSpace(contractId))
                throw new CryptographicException("NativeS1 AI configuration contract id is missing.");

            JObject defaultTarget = ReadObject(configuration, "defaultTarget");
            if (defaultTarget == null)
                throw new CryptographicException("NativeS1 default AI target is missing.");

            JObject overrides = ReadObject(configuration, "helperOverrides");
            var result = new List<JarvisAgentHealthTargetResult>(Agents.Length);
            foreach (string agent in Agents)
            {
                bool inherited = !string.Equals(agent, "Jarvis", StringComparison.OrdinalIgnoreCase);
                JObject target = defaultTarget;
                if (inherited && overrides != null)
                {
                    JToken overrideToken = GetCaseInsensitive(overrides, agent);
                    if (overrideToken is JObject overrideObject)
                    {
                        target = overrideObject;
                        inherited = false;
                    }
                }

                string accountRef = ReadString(target, "agentAccountRef");
                string provider = ReadString(target, "provider");
                string model = ReadString(target, "model");
                JObject credential = ReadObject(target, "credential");
                if (string.IsNullOrWhiteSpace(accountRef) ||
                    string.IsNullOrWhiteSpace(provider) ||
                    string.IsNullOrWhiteSpace(model) || credential == null)
                    throw new CryptographicException(
                        "NativeS1 AI target is incomplete for agent " + agent + ".");

                string apiKey = VerilicNativeS1CredentialDecryptor.Decrypt(
                    credential,
                    recognitionSecret,
                    decisionId,
                    callerProductId,
                    callerProductId,
                    contractId,
                    accountRef,
                    model);

                result.Add(new JarvisAgentHealthTargetResult
                {
                    Agent = agent,
                    Ready = true,
                    ReasonCode = "provider_ready",
                    AgentAccountRef = accountRef,
                    Provider = provider,
                    Model = model,
                    ApiKey = apiKey,
                    Inherited = inherited
                });
            }
            return result;
        }

        private static JObject ReadObject(JObject value, string name)
        {
            return GetCaseInsensitive(value, name) as JObject;
        }

        private static string ReadString(JObject value, string name)
        {
            JToken token = GetCaseInsensitive(value, name);
            return token == null || token.Type == JTokenType.Null
                ? null
                : token.ToString().Trim();
        }

        private static JToken GetCaseInsensitive(JObject value, string name)
        {
            if (value == null || string.IsNullOrEmpty(name))
                return null;
            JToken direct;
            if (value.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out direct))
                return direct;
            return null;
        }
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Softone;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Core
{
    internal sealed class JarvisAgentHealthResult
    {
        public bool Ready { get; private set; }
        public bool CreditsExhausted { get; private set; }
        public string ReasonCode { get; private set; }
        public string Model { get; private set; }

        public static JarvisAgentHealthResult Success(string model)
        {
            return new JarvisAgentHealthResult
            {
                Ready = true,
                ReasonCode = "provider_ready",
                Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim()
            };
        }

        public static JarvisAgentHealthResult Failure(
            string reasonCode,
            bool creditsExhausted = false,
            string model = null)
        {
            return new JarvisAgentHealthResult
            {
                Ready = false,
                CreditsExhausted = creditsExhausted,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "provider_unavailable"
                    : reasonCode,
                Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim()
            };
        }
    }

    /// <summary>
    /// Performs the startup provider probe through Verilic. The request is
    /// authenticated with the activated installation's ES256 proof and carries
    /// only the same Soft1 identity fields used by authoritative AI routing.
    /// Verilic re-verifies licence, entitlement, AgentAccount and configured
    /// model, decrypts the provider credential server-side, and performs the
    /// provider-specific probe. No provider credential is sent to this client.
    /// </summary>
    internal sealed class JarvisAgentHealthProbe
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        private sealed class ProviderHealthResponse
        {
            public bool Ready { get; set; }
            public string ReasonCode { get; set; }
            public string Model { get; set; }
        }

        static JarvisAgentHealthProbe()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }

        public async Task<JarvisAgentHealthResult> ProbeAsync(
            XSupport xSupport,
            string expectedAgentAccountRef,
            string expectedModel)
        {
            if (xSupport == null)
                return JarvisAgentHealthResult.Failure("provider_probe_identity_missing");
            if (string.IsNullOrWhiteSpace(expectedAgentAccountRef))
                return JarvisAgentHealthResult.Failure("agent_account_missing");
            if (string.IsNullOrWhiteSpace(expectedModel))
                return JarvisAgentHealthResult.Failure("provider_model_missing");

            string selectedModel = expectedModel.Trim();

            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();
                if (configuration.Mode != VerilicRuntimeMode.Verilic ||
                    configuration.ProviderHealthUri == null)
                    return JarvisAgentHealthResult.Failure(
                        "provider_health_configuration_invalid",
                        model: selectedModel);

                string productId = configuration.ResolveProductId(
                    JarvisProducts.Jarvis);

                var stateStore = new VerilicInstallationStateStore(
                    configuration.StateDirectory,
                    configuration.ProtectionScope);
                VerilicInstallationState state = stateStore.Load(
                    JarvisProducts.Jarvis);

                if (state == null || !state.ActivationCompleted ||
                    string.IsNullOrWhiteSpace(state.InstallationId) ||
                    state.InstallationId.StartsWith("pending_", StringComparison.Ordinal) ||
                    !string.Equals(state.VerilicProductId, productId, StringComparison.Ordinal) ||
                    !string.Equals(state.KeyAlgorithm, "ES256", StringComparison.Ordinal) ||
                    state.PrivateKeyMaterial == null || state.PrivateKeyMaterial.Length == 0)
                    return JarvisAgentHealthResult.Failure(
                        "provider_health_installation_invalid",
                        model: selectedModel);

                var info = xSupport.ConnectionInfo;
                if (info == null)
                    return JarvisAgentHealthResult.Failure(
                        "provider_probe_identity_missing",
                        model: selectedModel);

                var requestBody = new VerilicAiRoutingRequest
                {
                    ProductId = productId,
                    InstallationId = state.InstallationId,
                    ProductVersion = configuration.ProductVersion,
                    RequestedFeatures = new string[0],
                    Soft1Serial = info.SerialNum == null
                        ? null
                        : info.SerialNum.ToString(),
                    CompanyCode = info.CompanyId.ToString(),
                    BranchCode = info.BranchId.ToString(),
                    Soft1UserId = info.UserId.ToString()
                };

                string json = JsonConvert.SerializeObject(requestBody);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

                var proofState = new VerilicInstallationState
                {
                    ProductCode = productId,
                    InstallationId = state.InstallationId,
                    KeyAlgorithm = state.KeyAlgorithm,
                    PrivateKeyMaterial = state.PrivateKeyMaterial
                };

                using (var authorizer = new VerilicEs256RequestAuthorizer(proofState))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    configuration.ProviderHealthUri))
                using (var cts = new CancellationTokenSource(Timeout))
                {
                    var content = new ByteArrayContent(bodyBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8"
                    };
                    request.Content = content;
                    authorizer.Authorize(request, bodyBytes);

                    using (HttpResponseMessage response = await Http.SendAsync(request, cts.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                            return JarvisAgentHealthResult.Failure(
                                "provider_health_http_" + ((int)response.StatusCode).ToString(),
                                model: selectedModel);

                        string responseJson = await response.Content.ReadAsStringAsync();
                        ProviderHealthResponse health;
                        try
                        {
                            health = JsonConvert.DeserializeObject<ProviderHealthResponse>(
                                responseJson);
                        }
                        catch (JsonException)
                        {
                            return JarvisAgentHealthResult.Failure(
                                "provider_health_response_invalid",
                                model: selectedModel);
                        }

                        if (health == null || string.IsNullOrWhiteSpace(health.ReasonCode))
                            return JarvisAgentHealthResult.Failure(
                                "provider_health_response_invalid",
                                model: selectedModel);

                        string returnedModel = string.IsNullOrWhiteSpace(health.Model)
                            ? selectedModel
                            : health.Model.Trim();

                        // The shell already resolved an authoritative account/model
                        // immediately before this call. If the server reports a
                        // different model, configuration changed between the two
                        // signed requests; fail closed and let startup retry later.
                        if (!string.Equals(
                                returnedModel,
                                selectedModel,
                                StringComparison.Ordinal))
                            return JarvisAgentHealthResult.Failure(
                                "provider_routing_changed",
                                model: returnedModel);

                        if (health.Ready)
                            return JarvisAgentHealthResult.Success(returnedModel);

                        return JarvisAgentHealthResult.Failure(
                            health.ReasonCode.Trim(),
                            string.Equals(
                                health.ReasonCode.Trim(),
                                "provider_credits_exhausted",
                                StringComparison.Ordinal),
                            returnedModel);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return JarvisAgentHealthResult.Failure(
                    "provider_timeout",
                    model: selectedModel);
            }
            catch
            {
                return JarvisAgentHealthResult.Failure(
                    "provider_health_failed",
                    model: selectedModel);
            }
        }
    }
}

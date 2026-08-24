using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using S1Jarvis.Access;

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
    /// Performs a tiny end-to-end provider probe through the same Nexus proxy
    /// used by normal Jarvis AI calls. The client never receives or stores the
    /// provider API key. The model is supplied by the authoritative Verilic
    /// contract/user AI configuration returned by signed routing; no model is
    /// hardcoded in this probe.
    /// </summary>
    internal sealed class JarvisAgentHealthProbe
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

        static JarvisAgentHealthProbe()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }

        public async Task<JarvisAgentHealthResult> ProbeAsync(
            string agentAccountRef,
            string model)
        {
            if (string.IsNullOrWhiteSpace(agentAccountRef))
                return JarvisAgentHealthResult.Failure("agent_account_missing");
            if (string.IsNullOrWhiteSpace(model))
                return JarvisAgentHealthResult.Failure("provider_model_missing");

            string selectedModel = model.Trim();

            try
            {
                // Use the same provider path as real Jarvis traffic, but keep
                // the probe intentionally tiny. The provider must actually
                // accept both the server-side credential and the configured
                // model and return a valid proxy result.
                var providerRequest = new
                {
                    model = selectedModel,
                    max_tokens = 16,
                    messages = new[]
                    {
                        new { role = "user", content = "Reply with OK only." }
                    }
                };

                var proxyRequest = new AgentProxyRequest
                {
                    AgentAccountRef = agentAccountRef.Trim(),
                    AnthropicRequestJson = JsonConvert.SerializeObject(providerRequest)
                };

                string url = AccessConfig.ServiceUrl.TrimEnd('/') + "/agent/vision";
                string body = JsonConvert.SerializeObject(proxyRequest);

                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                using (var cts = new CancellationTokenSource(Timeout))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrEmpty(AccessConfig.ClientKey))
                        request.Headers.Add("X-Client-Key", AccessConfig.ClientKey);

                    using (HttpResponseMessage response = await Http.SendAsync(request, cts.Token))
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return JarvisAgentHealthResult.Failure(
                                "proxy_unavailable",
                                model: selectedModel);

                        AgentProxyResponse proxy = null;
                        try
                        {
                            proxy = JsonConvert.DeserializeObject<AgentProxyResponse>(json);
                        }
                        catch (JsonException)
                        {
                            return JarvisAgentHealthResult.Failure(
                                "proxy_response_invalid",
                                model: selectedModel);
                        }

                        if (proxy == null)
                            return JarvisAgentHealthResult.Failure(
                                "proxy_response_invalid",
                                model: selectedModel);

                        if (!proxy.Success)
                            return proxy.CreditsExhausted
                                ? JarvisAgentHealthResult.Failure(
                                    "provider_credits_exhausted",
                                    true,
                                    selectedModel)
                                : JarvisAgentHealthResult.Failure(
                                    "provider_unavailable",
                                    model: selectedModel);

                        return JarvisAgentHealthResult.Success(selectedModel);
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
                    "provider_unavailable",
                    model: selectedModel);
            }
        }
    }
}

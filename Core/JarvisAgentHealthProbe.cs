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

        public static JarvisAgentHealthResult Success()
        {
            return new JarvisAgentHealthResult
            {
                Ready = true,
                ReasonCode = "provider_ready"
            };
        }

        public static JarvisAgentHealthResult Failure(string reasonCode, bool creditsExhausted = false)
        {
            return new JarvisAgentHealthResult
            {
                Ready = false,
                CreditsExhausted = creditsExhausted,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "provider_unavailable"
                    : reasonCode
            };
        }
    }

    /// <summary>
    /// Performs a tiny end-to-end provider probe through the same Nexus proxy
    /// used by normal Jarvis AI calls. The client never receives or stores the
    /// provider API key: it sends only the opaque AgentAccountRef and a minimal
    /// provider request. A successful response proves that routing, server-side
    /// credential resolution and provider communication are all working.
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

        public async Task<JarvisAgentHealthResult> ProbeAsync(string agentAccountRef)
        {
            if (string.IsNullOrWhiteSpace(agentAccountRef))
                return JarvisAgentHealthResult.Failure("agent_account_missing");

            try
            {
                // Use the same provider path as real Jarvis traffic, but keep
                // the probe intentionally tiny. This is not a synthetic
                // "credential exists" check: the provider must actually accept
                // the server-side credential and return a valid proxy result.
                var providerRequest = new
                {
                    model = "claude-opus-5",
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
                            return JarvisAgentHealthResult.Failure("proxy_unavailable");

                        AgentProxyResponse proxy = null;
                        try
                        {
                            proxy = JsonConvert.DeserializeObject<AgentProxyResponse>(json);
                        }
                        catch (JsonException)
                        {
                            return JarvisAgentHealthResult.Failure("proxy_response_invalid");
                        }

                        if (proxy == null)
                            return JarvisAgentHealthResult.Failure("proxy_response_invalid");

                        if (!proxy.Success)
                            return proxy.CreditsExhausted
                                ? JarvisAgentHealthResult.Failure("provider_credits_exhausted", true)
                                : JarvisAgentHealthResult.Failure("provider_unavailable");

                        return JarvisAgentHealthResult.Success();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return JarvisAgentHealthResult.Failure("provider_timeout");
            }
            catch
            {
                return JarvisAgentHealthResult.Failure("provider_unavailable");
            }
        }
    }
}

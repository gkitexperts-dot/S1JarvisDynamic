using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace S1Jarvis.Access.Verilic
{
    internal sealed class VerilicAiRoutingRequest
    {
        public string ProductId { get; set; }
        public string InstallationId { get; set; }
        public string ProductVersion { get; set; }
        public string[] RequestedFeatures { get; set; }
        public string Soft1Serial { get; set; }
        public string CompanyCode { get; set; }
        public string BranchCode { get; set; }
        public string Soft1UserId { get; set; }
    }

    internal sealed class VerilicAiRoutingResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; }
        public string AgentAccountRef { get; set; }

        public static VerilicAiRoutingResult Deny(string reasonCode)
        {
            return new VerilicAiRoutingResult
            {
                Success = false,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "routing_unavailable"
                    : reasonCode,
                AgentAccountRef = null
            };
        }
    }

    internal interface IVerilicAiRoutingTransport
    {
        VerilicAiRoutingResult Resolve(VerilicAiRoutingRequest request);
    }

    /// <summary>
    /// .NET Framework 4.8 HTTPS client for the Verilic Jarvis AI routing
    /// endpoint. The ES256 authorizer signs the exact UTF-8 body bytes sent on
    /// the wire. Transport, parsing, and invalid-success failures are fail-closed.
    /// </summary>
    internal sealed class VerilicAiRoutingHttpTransport : IVerilicAiRoutingTransport
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly Uri _routingUri;
        private readonly IVerilicRequestAuthorizer _authorizer;
        private readonly TimeSpan _timeout;

        static VerilicAiRoutingHttpTransport()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public VerilicAiRoutingHttpTransport(
            Uri routingUri,
            IVerilicRequestAuthorizer authorizer,
            int timeoutSeconds = 15)
        {
            if (routingUri == null)
                throw new ArgumentNullException(nameof(routingUri));
            if (!routingUri.IsAbsoluteUri ||
                !string.Equals(
                    routingUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Verilic AI routing transport requires an absolute HTTPS URI.",
                    nameof(routingUri));
            if (authorizer == null)
                throw new ArgumentNullException(nameof(authorizer));
            if (timeoutSeconds <= 0 || timeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            _routingUri = routingUri;
            _authorizer = authorizer;
            _timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        public VerilicAiRoutingResult Resolve(VerilicAiRoutingRequest request)
        {
            if (!IsValidRequest(request))
                return VerilicAiRoutingResult.Deny("routing_request_invalid");

            try
            {
                return Task.Run(() => ResolveCoreAsync(request))
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                return VerilicAiRoutingResult.Deny("routing_transport_failed");
            }
        }

        private async Task<VerilicAiRoutingResult> ResolveCoreAsync(
            VerilicAiRoutingRequest request)
        {
            string json = JsonConvert.SerializeObject(request);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

            using (var message = new HttpRequestMessage(HttpMethod.Post, _routingUri))
            {
                var content = new ByteArrayContent(bodyBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };
                message.Content = content;

                _authorizer.Authorize(message, bodyBytes);

                using (var cts = new CancellationTokenSource(_timeout))
                using (var response = await Http.SendAsync(message, cts.Token))
                {
                    if (!response.IsSuccessStatusCode)
                        return VerilicAiRoutingResult.Deny(
                            "routing_http_" + ((int)response.StatusCode));

                    string responseJson = await response.Content.ReadAsStringAsync();
                    VerilicAiRoutingResult result =
                        JsonConvert.DeserializeObject<VerilicAiRoutingResult>(responseJson);

                    if (result == null)
                        return VerilicAiRoutingResult.Deny("routing_response_invalid");

                    if (result.Success && !IsValidIdentifier(result.AgentAccountRef))
                        return VerilicAiRoutingResult.Deny("routing_response_invalid");

                    if (!result.Success)
                    {
                        result.AgentAccountRef = null;
                        if (string.IsNullOrWhiteSpace(result.ReasonCode))
                            result.ReasonCode = "routing_unavailable";
                    }

                    return result;
                }
            }
        }

        private static bool IsValidRequest(VerilicAiRoutingRequest request)
        {
            return request != null &&
                   IsValidIdentifier(request.ProductId) &&
                   IsValidIdentifier(request.InstallationId) &&
                   IsValidIdentifier(request.ProductVersion) &&
                   IsValidIdentifier(request.Soft1Serial) &&
                   IsValidIdentifier(request.CompanyCode) &&
                   IsValidIdentifier(request.BranchCode) &&
                   IsValidIdentifier(request.Soft1UserId);
        }

        private static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
                return false;

            string normalized = value.Trim();
            for (int index = 0; index < normalized.Length; index++)
            {
                if (char.IsControl(normalized[index]))
                    return false;
            }

            return true;
        }
    }
}

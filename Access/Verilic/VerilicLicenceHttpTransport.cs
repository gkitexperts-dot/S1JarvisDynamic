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
    /// <summary>
    /// Adds the request proof/recognition material required by the Verilic
    /// endpoint. The transport deliberately does not invent or embed a proof
    /// algorithm; Step 12 will provide a dedicated net48 implementation once
    /// the server proof contract is wired.
    /// </summary>
    internal interface IVerilicRequestAuthorizer
    {
        void Authorize(HttpRequestMessage request, byte[] exactBodyBytes);
    }

    internal interface IVerilicLicenceTransport
    {
        VerilicVerifyLicenceResult Verify(VerilicVerifyLicenceRequest request);
    }

    /// <summary>
    /// .NET Framework 4.8 compatible HTTPS transport for the Verilic licence
    /// verification contract. It signs/authorizes the exact byte sequence that
    /// is sent and never converts transport failures into Allowed=true.
    /// </summary>
    internal sealed class VerilicLicenceHttpTransport : IVerilicLicenceTransport
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly Uri _verificationUri;
        private readonly IVerilicRequestAuthorizer _authorizer;
        private readonly TimeSpan _timeout;

        static VerilicLicenceHttpTransport()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public VerilicLicenceHttpTransport(
            Uri verificationUri,
            IVerilicRequestAuthorizer authorizer,
            int timeoutSeconds = 15)
        {
            if (verificationUri == null)
                throw new ArgumentNullException(nameof(verificationUri));
            if (!verificationUri.IsAbsoluteUri)
                throw new ArgumentException(
                    "An absolute Verilic verification URI is required.",
                    nameof(verificationUri));
            if (!string.Equals(
                    verificationUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Verilic licensing transport requires HTTPS.",
                    nameof(verificationUri));
            if (authorizer == null)
                throw new ArgumentNullException(nameof(authorizer));
            if (timeoutSeconds <= 0 || timeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            _verificationUri = verificationUri;
            _authorizer = authorizer;
            _timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        public VerilicVerifyLicenceResult Verify(VerilicVerifyLicenceRequest request)
        {
            if (!IsValidRequest(request))
                return VerilicVerifyLicenceResult.Deny("verification_request_invalid");

            try
            {
                return Task.Run(() => VerifyCoreAsync(request))
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                return VerilicVerifyLicenceResult.Deny("verification_transport_failed");
            }
        }

        private async Task<VerilicVerifyLicenceResult> VerifyCoreAsync(
            VerilicVerifyLicenceRequest request)
        {
            string json = JsonConvert.SerializeObject(request);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

            using (var message = new HttpRequestMessage(HttpMethod.Post, _verificationUri))
            {
                var content = new ByteArrayContent(bodyBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };
                message.Content = content;

                // The authorizer sees the exact bytes sent on the wire. No body
                // serialization is allowed after this point because Verilic PoP
                // binds the signature to the request body.
                _authorizer.Authorize(message, bodyBytes);

                using (var cts = new CancellationTokenSource(_timeout))
                using (var response = await Http.SendAsync(message, cts.Token))
                {
                    if (!response.IsSuccessStatusCode)
                        return VerilicVerifyLicenceResult.Deny(
                            "verification_http_" + ((int)response.StatusCode));

                    string responseJson = await response.Content.ReadAsStringAsync();
                    VerilicVerifyLicenceResult result =
                        JsonConvert.DeserializeObject<VerilicVerifyLicenceResult>(responseJson);

                    if (result == null)
                        return VerilicVerifyLicenceResult.Deny(
                            "verification_response_invalid");

                    // An allowed response must remain bound to the exact product
                    // and installation that were requested. A mismatch fails
                    // closed even if Allowed=true appears in the payload.
                    if (result.Allowed &&
                        (!string.Equals(
                            result.ProductId,
                            request.ProductId,
                            StringComparison.Ordinal) ||
                         !string.Equals(
                            result.InstallationId,
                            request.InstallationId,
                            StringComparison.Ordinal)))
                    {
                        return VerilicVerifyLicenceResult.Deny(
                            "verification_response_binding_mismatch");
                    }

                    return result;
                }
            }
        }

        private static bool IsValidRequest(VerilicVerifyLicenceRequest request)
        {
            if (request == null)
                return false;
            if (string.IsNullOrWhiteSpace(request.ProductId) ||
                string.IsNullOrWhiteSpace(request.InstallationId) ||
                string.IsNullOrWhiteSpace(request.ProductVersion))
                return false;
            if (request.ProductId.Length > 200 ||
                request.InstallationId.Length > 200 ||
                request.ProductVersion.Length > 200)
                return false;

            return true;
        }
    }
}

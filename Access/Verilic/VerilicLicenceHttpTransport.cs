using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Adds product recognition credentials to the public NativeS1 endpoint.
    /// The secret is never serialized into the JSON body and must never be
    /// written to logs.
    /// </summary>
    internal interface IVerilicRequestAuthorizer
    {
        void Authorize(HttpRequestMessage request, byte[] exactBodyBytes);
    }

    internal sealed class VerilicRecognitionRequestAuthorizer :
        IVerilicRequestAuthorizer
    {
        private readonly string _keyId;
        private readonly string _secret;

        public VerilicRecognitionRequestAuthorizer(string keyId, string secret)
        {
            if (string.IsNullOrWhiteSpace(keyId))
                throw new ArgumentException("Recognition key id is required.", nameof(keyId));
            if (string.IsNullOrWhiteSpace(secret))
                throw new ArgumentException("Recognition secret is required.", nameof(secret));

            _keyId = keyId.Trim();
            _secret = secret.Trim();
        }

        public void Authorize(HttpRequestMessage request, byte[] exactBodyBytes)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            request.Headers.Remove("X-Verilic-Recognition-Key-Id");
            request.Headers.Remove("X-Verilic-Recognition-Secret");
            request.Headers.TryAddWithoutValidation("X-Verilic-Recognition-Key-Id", _keyId);
            request.Headers.TryAddWithoutValidation("X-Verilic-Recognition-Secret", _secret);
        }
    }

    internal interface IVerilicLicenceTransport
    {
        VerilicVerifyLicenceResult Verify(VerilicVerifyLicenceRequest request);
    }

    /// <summary>
    /// .NET Framework 4.8 transport for POST /api/licensing/v1/verify.
    /// Transport, HTTP and JSON failures always fail closed. HTTP 200 is never
    /// interpreted as a licence grant without validating the response body.
    /// </summary>
    internal sealed class VerilicLicenceHttpTransport : IVerilicLicenceTransport
    {
        private const int MaximumRequestBytes = 16 * 1024;
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
            if (!verificationUri.IsAbsoluteUri ||
                !string.Equals(verificationUri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "An absolute HTTPS Verilic verification URI is required.",
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
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return VerilicVerifyLicenceResult.Deny("verification_transport_failed");
            }
            catch
            {
                return VerilicVerifyLicenceResult.Deny("verification_transport_failed");
            }
        }

        private async Task<VerilicVerifyLicenceResult> VerifyCoreAsync(
            VerilicVerifyLicenceRequest request)
        {
            string json = JsonConvert.SerializeObject(request, Formatting.None);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            if (bodyBytes.Length > MaximumRequestBytes)
                return VerilicVerifyLicenceResult.Deny("verification_request_too_large", 413);

            using (var message = new HttpRequestMessage(HttpMethod.Post, _verificationUri))
            {
                var content = new ByteArrayContent(bodyBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                message.Content = content;
                _authorizer.Authorize(message, bodyBytes);

                using (var cts = new CancellationTokenSource(_timeout))
                using (var response = await Http.SendAsync(message, cts.Token))
                {
                    string responseJson = await response.Content.ReadAsStringAsync();
                    int status = (int)response.StatusCode;
                    LogVerificationResponse(status, responseJson);

                    VerilicVerifyLicenceResult payload = TryDeserialize(responseJson);
                    int? retryAfter = ReadRetryAfterSeconds(response);

                    if (!response.IsSuccessStatusCode)
                    {
                        string reason = payload == null ||
                            string.IsNullOrWhiteSpace(payload.ReasonCode)
                                ? MapHttpReason(status)
                                : payload.ReasonCode.Trim();
                        return VerilicVerifyLicenceResult.Deny(reason, status, retryAfter);
                    }

                    if (payload == null)
                        return VerilicVerifyLicenceResult.Deny(
                            "verification_response_invalid", status);

                    payload.HttpStatusCode = status;
                    payload.RetryAfterSeconds = retryAfter;

                    // Bind an allowed wrapper to the requested product and require
                    // one matching product entry. This enforces the public startup
                    // decision matrix instead of treating 200 as licensed.
                    if (payload.Allowed &&
                        (!string.Equals(payload.ProductId, request.ProductId,
                            StringComparison.Ordinal) ||
                         payload.FindRequestedProduct(request.ProductId) == null))
                    {
                        return VerilicVerifyLicenceResult.Deny(
                            "verification_response_binding_mismatch", status);
                    }

                    return payload;
                }
            }
        }

        private static void LogVerificationResponse(int status, string responseJson)
        {
            try
            {
                string safeBody = SanitizeVerificationResponse(responseJson);
                S1Jarvis.Core.DebugLog.Log(
                    "[VERILIC-VERIFY-RESPONSE] http=" +
                    status.ToString(CultureInfo.InvariantCulture) +
                    " body=" + safeBody);
            }
            catch
            {
                try
                {
                    S1Jarvis.Core.DebugLog.Log(
                        "[VERILIC-VERIFY-RESPONSE] http=" +
                        status.ToString(CultureInfo.InvariantCulture) +
                        " body=<unavailable>");
                }
                catch { }
            }
        }

        private static string SanitizeVerificationResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
                return "<empty>";

            JToken root;
            try
            {
                root = JToken.Parse(responseJson);
            }
            catch (JsonException)
            {
                return responseJson.Length <= 4096
                    ? responseJson
                    : responseJson.Substring(0, 4096) + "<truncated>";
            }

            foreach (JProperty credential in root.SelectTokens("$..credential")
                         .OfType<JObject>()
                         .SelectMany(o => o.Properties()))
            {
                if (string.Equals(credential.Name, "ciphertext", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(credential.Name, "nonce", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(credential.Name, "tag", StringComparison.OrdinalIgnoreCase))
                {
                    credential.Value = "<redacted>";
                }
            }

            string safe = root.ToString(Formatting.None);
            return safe.Length <= 12000
                ? safe
                : safe.Substring(0, 12000) + "<truncated>";
        }

        private static VerilicVerifyLicenceResult TryDeserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<VerilicVerifyLicenceResult>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int? ReadRetryAfterSeconds(HttpResponseMessage response)
        {
            if (response == null || response.Headers.RetryAfter == null)
                return null;

            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                double seconds = response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                return seconds <= 0 ? 0 : (int)Math.Ceiling(seconds);
            }

            if (response.Headers.RetryAfter.Date.HasValue)
            {
                double seconds = (response.Headers.RetryAfter.Date.Value.UtcDateTime -
                    DateTime.UtcNow).TotalSeconds;
                return seconds <= 0 ? 0 : (int)Math.Ceiling(seconds);
            }

            return null;
        }

        private static string MapHttpReason(int status)
        {
            switch (status)
            {
                case 400: return "verification_request_invalid";
                case 401: return "product_recognition_failed";
                case 413: return "verification_request_too_large";
                case 429: return "rate_limited";
                case 404: return "verification_unavailable";
                default:
                    return status >= 500
                        ? "verification_unavailable"
                        : "verification_http_" + status.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static bool IsValidRequest(VerilicVerifyLicenceRequest request)
        {
            if (request == null || request.RuntimeContext == null)
                return false;

            return IsRequired(request.ProductId, 200) &&
                   IsRequired(request.ProductVersion, 200) &&
                   IsRequired(request.RuntimeContext.Soft1Serial, 200) &&
                   IsRequired(request.RuntimeContext.CompanyCode, 200) &&
                   IsRequired(request.RuntimeContext.BranchCode, 200) &&
                   IsRequired(request.RuntimeContext.Soft1UserId, 200);
        }

        private static bool IsRequired(string value, int maximumLength)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
        }
    }
}

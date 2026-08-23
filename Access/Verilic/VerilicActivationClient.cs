using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Access.Verilic
{
    internal sealed class VerilicActivationRequest
    {
        public string VendorId { get; set; }
        public string ProductCode { get; set; }
        public string ProductId { get; set; }
        public string LicenceId { get; set; }
        public string ProductVersion { get; set; }
        public string DeviceSignalHash { get; set; }
    }

    internal sealed class VerilicActivationResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; }
        public string InstallationId { get; set; }
        public bool WasAlreadyCompleted { get; set; }

        public static VerilicActivationResult Denied(string reasonCode)
        {
            return new VerilicActivationResult
            {
                Success = false,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "activation_failed"
                    : reasonCode
            };
        }
    }

    /// <summary>
    /// .NET Framework 4.8 in-process activation flow for Verilic.
    /// ProductCode is the local Jarvis SKU/state key; ProductId is the
    /// registered Verilic product identity used by the server protocol.
    /// Activation establishes installation identity only and never grants
    /// runtime licence access by itself.
    /// </summary>
    internal sealed class VerilicActivationClient
    {
        private const string Algorithm = "ES256";
        private const string ActivationCanonicalVersion =
            "VERILIC-ACTIVATION-V1";

        private static readonly HttpClient Http = new HttpClient();

        private readonly Uri _challengeUri;
        private readonly Uri _completionUri;
        private readonly VerilicInstallationStateStore _stateStore;
        private readonly TimeSpan _timeout;

        static VerilicActivationClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public VerilicActivationClient(
            Uri licensingOrigin,
            VerilicInstallationStateStore stateStore,
            int timeoutSeconds = 15)
        {
            if (licensingOrigin == null)
                throw new ArgumentNullException(nameof(licensingOrigin));
            if (!licensingOrigin.IsAbsoluteUri ||
                !string.Equals(
                    licensingOrigin.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Verilic activation requires an absolute HTTPS origin.",
                    nameof(licensingOrigin));
            if (stateStore == null)
                throw new ArgumentNullException(nameof(stateStore));
            if (timeoutSeconds <= 0 || timeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            var origin = new Uri(
                licensingOrigin.GetLeftPart(UriPartial.Authority) + "/");
            _challengeUri = new Uri(
                origin,
                "api/licensing/v1/activations/challenge");
            _completionUri = new Uri(
                origin,
                "api/licensing/v1/activations");
            _stateStore = stateStore;
            _timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        public VerilicActivationResult Activate(VerilicActivationRequest request)
        {
            try
            {
                ValidateRequest(request);
                return Task.Run(() => ActivateCoreAsync(request))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (ArgumentException)
            {
                return VerilicActivationResult.Denied(
                    "activation_request_invalid");
            }
            catch (InvalidDataException)
            {
                return VerilicActivationResult.Denied(
                    "activation_state_invalid");
            }
            catch (CryptographicException)
            {
                return VerilicActivationResult.Denied(
                    "activation_key_invalid");
            }
            catch
            {
                return VerilicActivationResult.Denied(
                    "activation_transport_failed");
            }
        }

        private async Task<VerilicActivationResult> ActivateCoreAsync(
            VerilicActivationRequest request)
        {
            VerilicInstallationState state =
                _stateStore.GetOrCreateIdentity(request.ProductCode);

            BindProductId(state, request.ProductId);

            if (state.ActivationCompleted)
            {
                return new VerilicActivationResult
                {
                    Success = true,
                    ReasonCode = "activation_already_completed_local",
                    InstallationId = state.InstallationId,
                    WasAlreadyCompleted = true
                };
            }

            bool stateChanged = EnsurePendingKeyAndIdempotency(state);
            if (stateChanged)
                _stateStore.Save(state);

            string publicJwk =
                VerilicEs256RequestAuthorizer.ExportPublicJwk(
                    state.PrivateKeyMaterial);
            string publicKeyThumbprint =
                ComputePublicKeyThumbprint(publicJwk);

            if (string.IsNullOrEmpty(state.ActivationChallengeId))
            {
                ActivationChallengeResponse challenge =
                    await CreateChallengeAsync(
                        request,
                        state,
                        publicJwk);

                if (challenge == null)
                    return VerilicActivationResult.Denied(
                        "activation_transport_failed");
                if (!challenge.Success)
                    return VerilicActivationResult.Denied(
                        challenge.ReasonCode);
                if (string.IsNullOrWhiteSpace(challenge.ChallengeId) ||
                    string.IsNullOrWhiteSpace(challenge.ChallengeToken))
                    return VerilicActivationResult.Denied(
                        "activation_response_invalid");

                state.ActivationChallengeId = challenge.ChallengeId;
                state.ActivationChallengeToken = challenge.ChallengeToken;
                _stateStore.Save(state);
            }

            string signature = SignActivationCompletion(
                state.PrivateKeyMaterial,
                state.ActivationChallengeToken,
                request.ProductId,
                state.ActivationChallengeId,
                publicKeyThumbprint);

            var completionRequest = new ActivationCompletionRequestBody
            {
                ChallengeToken = state.ActivationChallengeToken,
                Proof = new ActivationCompletionProofBody
                {
                    Algorithm = Algorithm,
                    Signature = signature
                },
                ProductVersion = request.ProductVersion,
                DeviceSignalHash = request.DeviceSignalHash ?? string.Empty
            };

            ActivationCompletionResponse completion =
                await PostJsonAsync<
                    ActivationCompletionRequestBody,
                    ActivationCompletionResponse>(
                        _completionUri,
                        completionRequest,
                        null);

            if (completion == null)
                return VerilicActivationResult.Denied(
                    "activation_transport_failed");
            if (!completion.Success)
                return VerilicActivationResult.Denied(
                    completion.ReasonCode);
            if (!ValidIdentifier(completion.InstallationId, 200))
                return VerilicActivationResult.Denied(
                    "activation_response_invalid");

            // The server-generated installation id is authoritative. Keep the
            // ProductId binding with it and only then clear resumable state.
            state.VerilicProductId = request.ProductId;
            state.InstallationId = completion.InstallationId;
            state.ActivationCompleted = true;
            state.ActivationIdempotencyKey = null;
            state.ActivationChallengeId = null;
            state.ActivationChallengeToken = null;
            _stateStore.Save(state);

            return new VerilicActivationResult
            {
                Success = true,
                ReasonCode = string.IsNullOrWhiteSpace(completion.ReasonCode)
                    ? "activation_completed"
                    : completion.ReasonCode,
                InstallationId = completion.InstallationId,
                WasAlreadyCompleted = completion.WasAlreadyCompleted
            };
        }

        private void BindProductId(
            VerilicInstallationState state,
            string productId)
        {
            if (string.IsNullOrWhiteSpace(state.VerilicProductId))
            {
                state.VerilicProductId = productId;
                _stateStore.Save(state);
                return;
            }

            if (!string.Equals(
                    state.VerilicProductId,
                    productId,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Verilic installation ProductId binding mismatch.");
        }

        private async Task<ActivationChallengeResponse> CreateChallengeAsync(
            VerilicActivationRequest request,
            VerilicInstallationState state,
            string publicJwk)
        {
            var body = new ActivationChallengeRequestBody
            {
                VendorId = request.VendorId,
                ProductId = request.ProductId,
                LicenceId = request.LicenceId,
                PublicKeyJwk = publicJwk,
                ProofAlgorithm = Algorithm
            };

            return await PostJsonAsync<
                ActivationChallengeRequestBody,
                ActivationChallengeResponse>(
                    _challengeUri,
                    body,
                    state.ActivationIdempotencyKey);
        }

        private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            Uri uri,
            TRequest body,
            string idempotencyKey)
            where TResponse : class
        {
            string json = JsonConvert.SerializeObject(body);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Content = new ByteArrayContent(bodyBytes);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8"
                    };

                if (!string.IsNullOrEmpty(idempotencyKey) &&
                    !request.Headers.TryAddWithoutValidation(
                        "Idempotency-Key",
                        idempotencyKey))
                    throw new InvalidOperationException(
                        "Unable to add activation idempotency header.");

                using (var cts = new CancellationTokenSource(_timeout))
                using (HttpResponseMessage response =
                    await Http.SendAsync(request, cts.Token))
                {
                    string responseJson =
                        await response.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(responseJson))
                        return null;

                    try
                    {
                        return JsonConvert.DeserializeObject<TResponse>(
                            responseJson);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }
        }

        private static bool EnsurePendingKeyAndIdempotency(
            VerilicInstallationState state)
        {
            bool hasAlgorithm = !string.IsNullOrEmpty(state.KeyAlgorithm);
            bool hasKey = state.PrivateKeyMaterial != null &&
                          state.PrivateKeyMaterial.Length > 0;

            if (hasAlgorithm != hasKey)
                throw new CryptographicException(
                    "Verilic pending installation key state is incomplete.");
            if (hasAlgorithm &&
                !string.Equals(state.KeyAlgorithm, Algorithm, StringComparison.Ordinal))
                throw new CryptographicException(
                    "Unsupported Verilic installation key algorithm.");

            bool changed = false;
            if (!hasKey)
            {
                string ignoredPublicJwk;
                state.PrivateKeyMaterial =
                    VerilicEs256RequestAuthorizer.GeneratePrivateKeyMaterial(
                        out ignoredPublicJwk);
                state.KeyAlgorithm = Algorithm;
                changed = true;
            }

            if (string.IsNullOrEmpty(state.ActivationIdempotencyKey))
            {
                state.ActivationIdempotencyKey =
                    CreateOpaqueValue("actreq", 24);
                changed = true;
            }

            return changed;
        }

        private static string ComputePublicKeyThumbprint(string publicJwk)
        {
            JObject jwk = JObject.Parse(publicJwk);
            string kty = (string)jwk["kty"];
            string crv = (string)jwk["crv"];
            string x = (string)jwk["x"];
            string y = (string)jwk["y"];

            if (!string.Equals(kty, "EC", StringComparison.Ordinal) ||
                !string.Equals(crv, "P-256", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(x) ||
                string.IsNullOrWhiteSpace(y))
                throw new CryptographicException(
                    "Invalid Verilic P-256 public JWK.");

            string canonical =
                "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" +
                x + "\",\"y\":\"" + y + "\"}";
            return Sha256Base64Url(Encoding.UTF8.GetBytes(canonical));
        }

        private static string SignActivationCompletion(
            byte[] privateKeyMaterial,
            string challengeToken,
            string productId,
            string challengeId,
            string publicKeyThumbprint)
        {
            string canonical = string.Join(
                "\n",
                ActivationCanonicalVersion,
                Sha256Base64Url(Encoding.UTF8.GetBytes(challengeToken)),
                productId.Trim(),
                challengeId.Trim(),
                publicKeyThumbprint.Trim());

            byte[] signature;
            using (CngKey key = CngKey.Import(
                privateKeyMaterial,
                CngKeyBlobFormat.EccPrivateBlob))
            using (var ecdsa = new ECDsaCng(key))
            {
                signature = ecdsa.SignData(
                    Encoding.UTF8.GetBytes(canonical),
                    HashAlgorithmName.SHA256);
            }

            byte[] p1363 = signature.Length == 64
                ? signature
                : DerToP1363(signature);
            if (p1363.Length != 64)
                throw new CryptographicException(
                    "Invalid ES256 activation signature size.");

            return Base64UrlEncode(p1363);
        }

        private static byte[] DerToP1363(byte[] der)
        {
            if (der == null || der.Length < 8)
                throw new CryptographicException(
                    "Invalid DER ECDSA signature.");

            int offset = 0;
            RequireTag(der, ref offset, 0x30);
            int sequenceLength = ReadLength(der, ref offset);
            if (sequenceLength != der.Length - offset)
                throw new CryptographicException(
                    "Invalid DER ECDSA sequence length.");

            byte[] r = ReadInteger(der, ref offset);
            byte[] s = ReadInteger(der, ref offset);
            if (offset != der.Length)
                throw new CryptographicException(
                    "Unexpected DER ECDSA data.");

            var result = new byte[64];
            CopyInteger(r, result, 0);
            CopyInteger(s, result, 32);
            return result;
        }

        private static byte[] ReadInteger(byte[] value, ref int offset)
        {
            RequireTag(value, ref offset, 0x02);
            int length = ReadLength(value, ref offset);
            if (length <= 0 || offset + length > value.Length)
                throw new CryptographicException(
                    "Invalid DER ECDSA integer.");

            var result = new byte[length];
            Buffer.BlockCopy(value, offset, result, 0, length);
            offset += length;
            return result;
        }

        private static void CopyInteger(
            byte[] integer,
            byte[] destination,
            int destinationOffset)
        {
            int sourceOffset = 0;
            while (sourceOffset < integer.Length - 1 &&
                   integer[sourceOffset] == 0)
                sourceOffset++;

            int length = integer.Length - sourceOffset;
            if (length > 32)
                throw new CryptographicException(
                    "ECDSA integer exceeds P-256 size.");

            Buffer.BlockCopy(
                integer,
                sourceOffset,
                destination,
                destinationOffset + 32 - length,
                length);
        }

        private static void RequireTag(
            byte[] value,
            ref int offset,
            byte expected)
        {
            if (offset >= value.Length || value[offset++] != expected)
                throw new CryptographicException(
                    "Invalid DER ECDSA tag.");
        }

        private static int ReadLength(byte[] value, ref int offset)
        {
            if (offset >= value.Length)
                throw new CryptographicException(
                    "Invalid DER ECDSA length.");

            int first = value[offset++];
            if ((first & 0x80) == 0)
                return first;

            int byteCount = first & 0x7F;
            if (byteCount <= 0 || byteCount > 2 ||
                offset + byteCount > value.Length)
                throw new CryptographicException(
                    "Invalid DER ECDSA length.");

            int length = 0;
            for (int index = 0; index < byteCount; index++)
                length = (length << 8) | value[offset++];
            return length;
        }

        private static string Sha256Base64Url(byte[] value)
        {
            using (var sha = SHA256.Create())
                return Base64UrlEncode(sha.ComputeHash(value));
        }

        private static string Base64UrlEncode(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string CreateOpaqueValue(
            string prefix,
            int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var text = new StringBuilder(prefix + "_");
            for (int index = 0; index < bytes.Length; index++)
                text.Append(bytes[index].ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static void ValidateRequest(
            VerilicActivationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            RequireIdentifier(
                request.VendorId,
                nameof(request.VendorId),
                200);
            RequireIdentifier(
                request.ProductCode,
                nameof(request.ProductCode),
                200);
            RequireIdentifier(
                request.ProductId,
                nameof(request.ProductId),
                200);
            RequireIdentifier(
                request.LicenceId,
                nameof(request.LicenceId),
                200);
            RequireIdentifier(
                request.ProductVersion,
                nameof(request.ProductVersion),
                200);
        }

        private static string RequireIdentifier(
            string value,
            string name,
            int maximumLength)
        {
            if (!ValidIdentifier(value, maximumLength))
                throw new ArgumentException(
                    "A valid licensing identifier is required.", name);
            return value.Trim();
        }

        private static bool ValidIdentifier(
            string value,
            int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > maximumLength)
                return false;

            string normalized = value.Trim();
            for (int index = 0; index < normalized.Length; index++)
            {
                if (char.IsControl(normalized[index]) ||
                    char.IsWhiteSpace(normalized[index]))
                    return false;
            }
            return true;
        }

        private sealed class ActivationChallengeRequestBody
        {
            [JsonProperty("vendorId")]
            public string VendorId { get; set; }

            [JsonProperty("productId")]
            public string ProductId { get; set; }

            [JsonProperty("licenceId")]
            public string LicenceId { get; set; }

            [JsonProperty("publicKeyJwk")]
            public string PublicKeyJwk { get; set; }

            [JsonProperty("proofAlgorithm")]
            public string ProofAlgorithm { get; set; }
        }

        private sealed class ActivationChallengeResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("reasonCode")]
            public string ReasonCode { get; set; }

            [JsonProperty("challengeId")]
            public string ChallengeId { get; set; }

            [JsonProperty("challengeToken")]
            public string ChallengeToken { get; set; }

            [JsonProperty("expiresAtUtc")]
            public DateTime? ExpiresAtUtc { get; set; }

            [JsonProperty("wasReused")]
            public bool WasReused { get; set; }
        }

        private sealed class ActivationCompletionRequestBody
        {
            [JsonProperty("challengeToken")]
            public string ChallengeToken { get; set; }

            [JsonProperty("proof")]
            public ActivationCompletionProofBody Proof { get; set; }

            [JsonProperty("productVersion")]
            public string ProductVersion { get; set; }

            [JsonProperty("deviceSignalHash")]
            public string DeviceSignalHash { get; set; }
        }

        private sealed class ActivationCompletionProofBody
        {
            [JsonProperty("algorithm")]
            public string Algorithm { get; set; }

            [JsonProperty("signature")]
            public string Signature { get; set; }
        }

        private sealed class ActivationCompletionResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("reasonCode")]
            public string ReasonCode { get; set; }

            [JsonProperty("installationId")]
            public string InstallationId { get; set; }

            [JsonProperty("wasAlreadyCompleted")]
            public bool WasAlreadyCompleted { get; set; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace S1Jarvis.Access.Verilic
{
    internal static class VerilicProofHeaderNames
    {
        public const string ProductId = "X-Verilic-Product";
        public const string InstallationId = "X-Verilic-Installation";
        public const string Algorithm = "X-Verilic-Algorithm";
        public const string IssuedAt = "X-Verilic-Issued-At";
        public const string Jti = "X-Verilic-Jti";
        public const string Audience = "X-Verilic-Audience";
        public const string Signature = "X-Verilic-Signature";
    }

    internal sealed class VerilicEs256RequestAuthorizer : IVerilicRequestAuthorizer, IDisposable
    {
        private const string AlgorithmName = "ES256";
        private const string DefaultAudience = "verilic-licensing";
        private const string CanonicalVersion = "VERILIC-PROOF-V1";
        private const int CoordinateSize = 32;

        private readonly string _productId;
        private readonly string _installationId;
        private readonly string _audience;
        private byte[] _privateKeyMaterial;
        private bool _disposed;

        public VerilicEs256RequestAuthorizer(
            VerilicInstallationState state,
            string audience = DefaultAudience)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (!string.Equals(state.KeyAlgorithm, AlgorithmName, StringComparison.Ordinal))
                throw new CryptographicException("Verilic installation key algorithm is not ES256.");
            if (state.PrivateKeyMaterial == null || state.PrivateKeyMaterial.Length == 0)
                throw new CryptographicException("Verilic installation private key material is missing.");

            _productId = RequireIdentifier(state.ProductCode, nameof(state.ProductCode), 200);
            _installationId = RequireIdentifier(state.InstallationId, nameof(state.InstallationId), 200);
            _audience = RequireIdentifier(audience, nameof(audience), 100);
            _privateKeyMaterial = (byte[])state.PrivateKeyMaterial.Clone();

            using (CngKey key = ImportPrivateKey(_privateKeyMaterial))
            {
                if (key.KeySize != 256)
                    throw new CryptographicException("Verilic installation key must be P-256.");
            }
        }

        public void Authorize(HttpRequestMessage request, byte[] exactBodyBytes)
        {
            ThrowIfDisposed();
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.RequestUri == null || !request.RequestUri.IsAbsoluteUri)
                throw new ArgumentException("An absolute request URI is required.", nameof(request));
            if (exactBodyBytes == null)
                throw new ArgumentNullException(nameof(exactBodyBytes));

            long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string jti = CreateJti();
            string canonical = CreateCanonical(
                request.Method.Method,
                request.RequestUri,
                exactBodyBytes,
                _productId,
                _installationId,
                issuedAt,
                jti,
                _audience);

            string signature = SignCanonical(canonical, _privateKeyMaterial);

            AddHeader(request, VerilicProofHeaderNames.ProductId, _productId);
            AddHeader(request, VerilicProofHeaderNames.InstallationId, _installationId);
            AddHeader(request, VerilicProofHeaderNames.Algorithm, AlgorithmName);
            AddHeader(request, VerilicProofHeaderNames.IssuedAt,
                issuedAt.ToString(CultureInfo.InvariantCulture));
            AddHeader(request, VerilicProofHeaderNames.Jti, jti);
            AddHeader(request, VerilicProofHeaderNames.Audience, _audience);
            AddHeader(request, VerilicProofHeaderNames.Signature, signature);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_privateKeyMaterial != null)
                Array.Clear(_privateKeyMaterial, 0, _privateKeyMaterial.Length);
            _privateKeyMaterial = null;
            _disposed = true;
        }

        internal static byte[] GeneratePrivateKeyMaterial(out string publicJwk)
        {
            using (CngKey key = CngKey.Create(CngAlgorithm.ECDsaP256))
            {
                byte[] privateMaterial = key.Export(CngKeyBlobFormat.EccPrivateBlob);
                publicJwk = ExportPublicJwk(key);
                return privateMaterial;
            }
        }

        internal static string ExportPublicJwk(byte[] privateKeyMaterial)
        {
            using (CngKey key = ImportPrivateKey(privateKeyMaterial))
                return ExportPublicJwk(key);
        }

        private static string ExportPublicJwk(CngKey key)
        {
            byte[] blob = key.Export(CngKeyBlobFormat.EccPublicBlob);
            if (blob.Length != 8 + (CoordinateSize * 2))
                throw new CryptographicException("Invalid P-256 public key blob.");

            int keyLength = BitConverter.ToInt32(blob, 4);
            if (keyLength != CoordinateSize)
                throw new CryptographicException("Invalid P-256 public key size.");

            byte[] x = new byte[CoordinateSize];
            byte[] y = new byte[CoordinateSize];
            Buffer.BlockCopy(blob, 8, x, 0, CoordinateSize);
            Buffer.BlockCopy(blob, 8 + CoordinateSize, y, 0, CoordinateSize);

            return "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" +
                   Base64UrlEncode(x) + "\",\"y\":\"" + Base64UrlEncode(y) + "\"}";
        }

        private static CngKey ImportPrivateKey(byte[] privateKeyMaterial)
        {
            if (privateKeyMaterial == null || privateKeyMaterial.Length == 0)
                throw new CryptographicException("P-256 private key material is required.");

            CngKey key = CngKey.Import(privateKeyMaterial, CngKeyBlobFormat.EccPrivateBlob);
            if (key.AlgorithmGroup != CngAlgorithmGroup.ECDsa || key.KeySize != 256)
            {
                key.Dispose();
                throw new CryptographicException("Only ECDSA P-256 installation keys are supported.");
            }

            return key;
        }

        private static string SignCanonical(string canonical, byte[] privateKeyMaterial)
        {
            byte[] signature;
            using (CngKey key = ImportPrivateKey(privateKeyMaterial))
            using (var ecdsa = new ECDsaCng(key))
            {
                signature = ecdsa.SignData(
                    Encoding.UTF8.GetBytes(canonical),
                    HashAlgorithmName.SHA256);
            }

            byte[] p1363 = signature.Length == CoordinateSize * 2
                ? signature
                : DerToP1363(signature);
            if (p1363.Length != CoordinateSize * 2)
                throw new CryptographicException("Invalid ES256 signature size.");

            return Base64UrlEncode(p1363);
        }

        private static string CreateCanonical(
            string httpMethod,
            Uri targetUri,
            byte[] body,
            string productId,
            string installationId,
            long issuedAt,
            string jti,
            string audience)
        {
            string canonicalTarget = CanonicalizeTarget(targetUri);
            string bodyHash;
            using (var sha = SHA256.Create())
                bodyHash = Base64UrlEncode(sha.ComputeHash(body ?? new byte[0]));

            return string.Join(
                "\n",
                CanonicalVersion,
                httpMethod.Trim().ToUpperInvariant(),
                canonicalTarget,
                bodyHash,
                productId.Trim(),
                installationId.Trim(),
                issuedAt.ToString(CultureInfo.InvariantCulture),
                jti.Trim(),
                audience.Trim());
        }

        private static string CanonicalizeTarget(Uri targetUri)
        {
            string scheme = targetUri.Scheme.ToLowerInvariant();
            string host = targetUri.IdnHost.ToLowerInvariant();
            bool defaultPort =
                (scheme == "https" && targetUri.Port == 443) ||
                (scheme == "http" && targetUri.Port == 80);
            string authority = defaultPort
                ? host
                : host + ":" + targetUri.Port.ToString(CultureInfo.InvariantCulture);
            string path = string.IsNullOrEmpty(targetUri.AbsolutePath)
                ? "/"
                : targetUri.AbsolutePath;

            string[] parameters = ParseQuery(targetUri.Query)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Value, StringComparer.Ordinal)
                .Select(item => Escape(item.Key) + "=" + Escape(item.Value))
                .ToArray();

            string query = parameters.Length == 0
                ? string.Empty
                : "?" + string.Join("&", parameters);

            return scheme + "://" + authority + path + query;
        }

        private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
        {
            if (string.IsNullOrEmpty(query))
                yield break;

            string value = query[0] == '?' ? query.Substring(1) : query;
            if (value.Length == 0)
                yield break;

            foreach (string part in value.Split('&'))
            {
                int separator = part.IndexOf('=');
                string key = separator < 0 ? part : part.Substring(0, separator);
                string itemValue = separator < 0 ? string.Empty : part.Substring(separator + 1);

                yield return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(key.Replace("+", " ")),
                    Uri.UnescapeDataString(itemValue.Replace("+", " ")));
            }
        }

        private static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty).Replace("%7E", "~");
        }

        private static string CreateJti()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var builder = new StringBuilder("jti_");
            for (int index = 0; index < bytes.Length; index++)
                builder.Append(bytes[index].ToString("x2"));
            return builder.ToString();
        }

        private static string Base64UrlEncode(byte[] value)
        {
            return Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static void AddHeader(HttpRequestMessage request, string name, string value)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new InvalidOperationException("Unable to add Verilic proof header: " + name);
        }

        private static string RequireIdentifier(string value, string name, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
                throw new ArgumentException("A valid licensing identifier is required.", name);

            string normalized = value.Trim();
            for (int index = 0; index < normalized.Length; index++)
            {
                if (char.IsControl(normalized[index]) || char.IsWhiteSpace(normalized[index]))
                    throw new ArgumentException(
                        "Licensing identifiers cannot contain whitespace or control characters.",
                        name);
            }

            return normalized;
        }

        private static byte[] DerToP1363(byte[] der)
        {
            if (der == null || der.Length < 8)
                throw new CryptographicException("Invalid DER ECDSA signature.");

            int offset = 0;
            RequireTag(der, ref offset, 0x30);
            int sequenceLength = ReadLength(der, ref offset);
            if (sequenceLength != der.Length - offset)
                throw new CryptographicException("Invalid DER ECDSA sequence length.");

            byte[] r = ReadInteger(der, ref offset);
            byte[] s = ReadInteger(der, ref offset);
            if (offset != der.Length)
                throw new CryptographicException("Unexpected DER ECDSA data.");

            var result = new byte[CoordinateSize * 2];
            CopyInteger(r, result, 0);
            CopyInteger(s, result, CoordinateSize);
            return result;
        }

        private static byte[] ReadInteger(byte[] value, ref int offset)
        {
            RequireTag(value, ref offset, 0x02);
            int length = ReadLength(value, ref offset);
            if (length <= 0 || offset + length > value.Length)
                throw new CryptographicException("Invalid DER ECDSA integer.");

            var result = new byte[length];
            Buffer.BlockCopy(value, offset, result, 0, length);
            offset += length;
            return result;
        }

        private static void CopyInteger(byte[] integer, byte[] destination, int destinationOffset)
        {
            int sourceOffset = 0;
            while (sourceOffset < integer.Length - 1 && integer[sourceOffset] == 0)
                sourceOffset++;

            int length = integer.Length - sourceOffset;
            if (length > CoordinateSize)
                throw new CryptographicException("ECDSA integer exceeds P-256 size.");

            Buffer.BlockCopy(
                integer,
                sourceOffset,
                destination,
                destinationOffset + CoordinateSize - length,
                length);
        }

        private static void RequireTag(byte[] value, ref int offset, byte expected)
        {
            if (offset >= value.Length || value[offset++] != expected)
                throw new CryptographicException("Invalid DER ECDSA tag.");
        }

        private static int ReadLength(byte[] value, ref int offset)
        {
            if (offset >= value.Length)
                throw new CryptographicException("Invalid DER ECDSA length.");

            int first = value[offset++];
            if ((first & 0x80) == 0)
                return first;

            int byteCount = first & 0x7F;
            if (byteCount <= 0 || byteCount > 2 || offset + byteCount > value.Length)
                throw new CryptographicException("Invalid DER ECDSA length.");

            int length = 0;
            for (int index = 0; index < byteCount; index++)
                length = (length << 8) | value[offset++];
            return length;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VerilicEs256RequestAuthorizer));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;

namespace S1Jarvis.Access.Verilic
{
    internal enum VerilicRuntimeMode
    {
        Verilic = 1
    }

    internal sealed class VerilicProductRecognitionCredential
    {
        public VerilicProductRecognitionCredential(
            string productId,
            string keyId,
            string secret)
        {
            ProductId = productId;
            KeyId = keyId;
            Secret = secret;
        }

        public string ProductId { get; private set; }
        public string KeyId { get; private set; }
        public string Secret { get; private set; }
    }

    /// <summary>
    /// Configuration for the single supported Verilic NativeS1 startup contract.
    /// Recognition credentials are product-level build material: S1JARVIS,
    /// JARVISCOURIER and JARVISDOCREADER each have their own Key ID + Secret.
    /// Customer PCs require no Recognition environment variables or provisioning.
    /// </summary>
    internal sealed class VerilicRuntimeConfiguration
    {
        private const string OriginVariable = "S1JARVIS_VERILIC_ORIGIN";
        private const string DefaultOrigin = "https://verilic.gr";

        private const string JarvisRegisteredProductId =
            "prd_6ece7bc271a54fd6ba945be8a8189e0b";
        private const string JarvisCourierRegisteredProductId =
            "prd_1ee62b4b7a1e4f5f9283da60a9bc1d29";
        private const string JarvisDocReaderRegisteredProductId =
            "prd_dfc9315f4e8242faa679be0aa49b474f";

        private const string JarvisKeyIdMetadata = "VerilicJarvisRecognitionKeyId";
        private const string JarvisSecretMetadata = "VerilicJarvisRecognitionSecret";
        private const string CourierKeyIdMetadata = "VerilicJarvisCourierRecognitionKeyId";
        private const string CourierSecretMetadata = "VerilicJarvisCourierRecognitionSecret";
        private const string DocReaderKeyIdMetadata = "VerilicJarvisDocReaderRecognitionKeyId";
        private const string DocReaderSecretMetadata = "VerilicJarvisDocReaderRecognitionSecret";

        private readonly Dictionary<string, VerilicProductRecognitionCredential> _credentials;

        private VerilicRuntimeConfiguration(
            Uri origin,
            Dictionary<string, VerilicProductRecognitionCredential> credentials,
            string productVersion)
        {
            LicensingOrigin = origin;
            _credentials = credentials;
            ProductVersion = productVersion;
        }

        public VerilicRuntimeMode Mode => VerilicRuntimeMode.Verilic;
        public Uri LicensingOrigin { get; private set; }
        public string ProductVersion { get; private set; }
        public Uri VerificationUri => new Uri(
            new Uri(LicensingOrigin.GetLeftPart(UriPartial.Authority) + "/"),
            "api/licensing/v1/verify");

        public string ResolveProductId(string productCode)
        {
            return ResolveProductCredential(productCode).ProductId;
        }

        public VerilicProductRecognitionCredential ResolveProductCredential(string productCode)
        {
            VerilicProductRecognitionCredential credential;
            if (string.IsNullOrWhiteSpace(productCode) ||
                !_credentials.TryGetValue(productCode, out credential) ||
                credential == null)
            {
                throw new InvalidOperationException(
                    "No Verilic NativeS1 recognition credential is configured for product " +
                    (productCode ?? "<null>") + ".");
            }

            return credential;
        }

        public static VerilicRuntimeConfiguration Load()
        {
            string originText = Environment.GetEnvironmentVariable(OriginVariable);
            if (string.IsNullOrWhiteSpace(originText))
                originText = DefaultOrigin;

            Uri origin = RequireHttpsOrigin(originText);

            var credentials = new Dictionary<string, VerilicProductRecognitionCredential>(
                StringComparer.Ordinal)
            {
                [JarvisProducts.Jarvis] = BuildCredential(
                    JarvisRegisteredProductId,
                    JarvisKeyIdMetadata,
                    JarvisSecretMetadata),
                [JarvisProducts.JarvisCourier] = BuildCredential(
                    JarvisCourierRegisteredProductId,
                    CourierKeyIdMetadata,
                    CourierSecretMetadata),
                [JarvisProducts.JarvisDocReader] = BuildCredential(
                    JarvisDocReaderRegisteredProductId,
                    DocReaderKeyIdMetadata,
                    DocReaderSecretMetadata)
            };

            return new VerilicRuntimeConfiguration(
                origin,
                credentials,
                GetProductVersion());
        }

        private static VerilicProductRecognitionCredential BuildCredential(
            string productId,
            string keyIdMetadata,
            string secretMetadata)
        {
            string keyId = RequireIdentifier(
                ReadBuildMetadata(keyIdMetadata),
                keyIdMetadata);
            string secret = ReadBuildMetadata(secretMetadata);
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096)
            {
                throw new InvalidOperationException(
                    "S1JARVIS was built without a valid Verilic Recognition secret (" +
                    secretMetadata + ").");
            }

            return new VerilicProductRecognitionCredential(
                productId,
                keyId,
                secret.Trim());
        }

        private static string ReadBuildMetadata(string key)
        {
            object[] values = Assembly.GetExecutingAssembly().GetCustomAttributes(
                typeof(AssemblyMetadataAttribute), false);

            for (int i = 0; i < values.Length; i++)
            {
                var metadata = values[i] as AssemblyMetadataAttribute;
                if (metadata != null &&
                    string.Equals(metadata.Key, key, StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(metadata.Value)
                        ? null
                        : metadata.Value.Trim();
                }
            }

            return null;
        }

        private static Uri RequireHttpsOrigin(string value)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "S1Jarvis Verilic origin must be an absolute HTTPS URI.");
            }

            return uri;
        }

        private static string RequireIdentifier(string value, string metadataName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
            {
                throw new InvalidOperationException(
                    "S1JARVIS was built without a valid Verilic Recognition key id (" +
                    metadataName + ").");
            }

            string normalized = value.Trim();
            for (int i = 0; i < normalized.Length; i++)
            {
                if (char.IsControl(normalized[i]) || char.IsWhiteSpace(normalized[i]))
                {
                    throw new InvalidOperationException(
                        "The compiled Verilic Recognition key id is invalid.");
                }
            }

            return normalized;
        }

        private static string GetProductVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "1.0.0.0" : version.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;

namespace S1Jarvis.Access.Verilic
{
    internal enum VerilicRuntimeMode
    {
        Verilic = 1
    }

    /// <summary>
    /// Configuration for the single supported Verilic NativeS1 startup contract.
    /// Product-recognition credentials are compiled into the runtime at build time.
    /// They are product-level credentials shared by every installation of the same
    /// compiled S1JARVIS build; no PC/user environment provisioning is required.
    /// </summary>
    internal sealed class VerilicRuntimeConfiguration
    {
        private const string OriginVariable = "S1JARVIS_VERILIC_ORIGIN";
        private const string RecognitionKeyIdMetadata =
            "VerilicRecognitionKeyId";
        private const string RecognitionSecretMetadata =
            "VerilicRecognitionSecret";

        private const string DefaultOrigin = "https://verilic.gr";

        private const string JarvisRegisteredProductId =
            "prd_6ece7bc271a54fd6ba945be8a8189e0b";
        private const string JarvisCourierRegisteredProductId =
            "prd_1ee62b4b7a1e4f5f9283da60a9bc1d29";
        private const string JarvisDocReaderRegisteredProductId =
            "prd_dfc9315f4e8242faa679be0aa49b474f";

        private readonly Dictionary<string, string> _productIds;

        private VerilicRuntimeConfiguration(
            Uri origin,
            Dictionary<string, string> productIds,
            string productVersion,
            string recognitionKeyId,
            string recognitionSecret)
        {
            LicensingOrigin = origin;
            _productIds = productIds;
            ProductVersion = productVersion;
            RecognitionKeyId = recognitionKeyId;
            RecognitionSecret = recognitionSecret;
        }

        public VerilicRuntimeMode Mode => VerilicRuntimeMode.Verilic;
        public Uri LicensingOrigin { get; private set; }
        public string ProductVersion { get; private set; }
        public string RecognitionKeyId { get; private set; }
        public string RecognitionSecret { get; private set; }
        public Uri VerificationUri => new Uri(
            new Uri(LicensingOrigin.GetLeftPart(UriPartial.Authority) + "/"),
            "api/licensing/v1/verify");

        public string ResolveProductId(string productCode)
        {
            string value;
            if (string.IsNullOrWhiteSpace(productCode) ||
                !_productIds.TryGetValue(productCode, out value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "No Verilic ProductId is configured for the Jarvis product.");
            }

            return value;
        }

        public static VerilicRuntimeConfiguration Load()
        {
            string originText = Environment.GetEnvironmentVariable(OriginVariable);
            if (string.IsNullOrWhiteSpace(originText))
                originText = DefaultOrigin;

            Uri origin = RequireHttpsOrigin(originText);

            // The Recognition credential is deployment/build material, not runtime
            // machine configuration. The build injects it as assembly metadata into
            // S1Jarvis.Runtime.dll; every customer receives the same compiled product
            // credential until the next Verilic Rotate + grace-period rollout.
            string recognitionKeyId = RequireIdentifier(
                ReadBuildMetadata(RecognitionKeyIdMetadata),
                RecognitionKeyIdMetadata);
            string recognitionSecret = ReadBuildMetadata(RecognitionSecretMetadata);
            if (string.IsNullOrWhiteSpace(recognitionSecret) ||
                recognitionSecret.Length > 4096)
            {
                throw new InvalidOperationException(
                    "S1JARVIS was built without a valid Verilic Recognition secret.");
            }

            var productIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JarvisProducts.Jarvis] = JarvisRegisteredProductId,
                [JarvisProducts.JarvisCourier] = JarvisCourierRegisteredProductId,
                [JarvisProducts.JarvisDocReader] = JarvisDocReaderRegisteredProductId
            };

            return new VerilicRuntimeConfiguration(
                origin,
                productIds,
                GetProductVersion(),
                recognitionKeyId,
                recognitionSecret.Trim());
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

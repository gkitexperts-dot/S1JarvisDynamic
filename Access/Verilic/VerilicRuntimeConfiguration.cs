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
    /// Configuration for the single supported Verilic NativeS1 contract.
    /// The runtime exposes only /api/licensing/v1/verify plus product recognition.
    /// There is no Nexus mode, local installation state, activation licence id,
    /// device binding, routing/resolve endpoint or provider-health endpoint here.
    /// </summary>
    internal sealed class VerilicRuntimeConfiguration
    {
        private const string OriginVariable = "S1JARVIS_VERILIC_ORIGIN";
        private const string RecognitionKeyIdVariable =
            "S1JARVIS_VERILIC_RECOGNITION_KEY_ID";
        private const string RecognitionSecretVariable =
            "S1JARVIS_VERILIC_RECOGNITION_SECRET";

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
            string originText = ReadDeploymentValue(OriginVariable);
            if (string.IsNullOrWhiteSpace(originText))
                originText = DefaultOrigin;

            Uri origin = RequireHttpsOrigin(originText);
            string recognitionKeyId = RequireIdentifier(
                ReadDeploymentValue(RecognitionKeyIdVariable),
                RecognitionKeyIdVariable);
            string recognitionSecret = ReadDeploymentValue(RecognitionSecretVariable);
            if (string.IsNullOrWhiteSpace(recognitionSecret) ||
                recognitionSecret.Length > 4096)
            {
                throw new InvalidOperationException(
                    RecognitionSecretVariable +
                    " is required by the Verilic NativeS1 /verify contract.");
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

        /// <summary>
        /// Product-recognition credentials are deployment material for the new
        /// NativeS1 contract. They are intentionally not obtained from any legacy
        /// activation/provisioning/installation store. Process values are preferred;
        /// User/Machine targets are accepted only as normal Windows deployment
        /// locations for the same NativeS1 credential.
        /// </summary>
        private static string ReadDeploymentValue(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

            try
            {
                value = Environment.GetEnvironmentVariable(
                    name,
                    EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            catch { }

            try
            {
                value = Environment.GetEnvironmentVariable(
                    name,
                    EnvironmentVariableTarget.Machine);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            catch { }

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

        private static string RequireIdentifier(string value, string variableName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
            {
                throw new InvalidOperationException(
                    variableName +
                    " must contain the registered Verilic product-recognition key id.");
            }

            string normalized = value.Trim();
            for (int i = 0; i < normalized.Length; i++)
            {
                if (char.IsControl(normalized[i]) || char.IsWhiteSpace(normalized[i]))
                {
                    throw new InvalidOperationException(
                        variableName +
                        " contains an invalid Verilic product-recognition key id.");
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

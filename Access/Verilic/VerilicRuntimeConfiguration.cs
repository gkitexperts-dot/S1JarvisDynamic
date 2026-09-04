using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace S1Jarvis.Access.Verilic
{
    internal enum VerilicRuntimeMode { Legacy = 0, Verilic = 1 }

    internal sealed class VerilicRuntimeConfiguration
    {
        private const string ModeVariable = "S1JARVIS_VERILIC_MODE";
        private const string OriginVariable = "S1JARVIS_VERILIC_ORIGIN";
        private const string StateDirectoryVariable = "S1JARVIS_VERILIC_STATE_DIR";
        private const string DpapiScopeVariable = "S1JARVIS_VERILIC_DPAPI_SCOPE";
        private const string VendorIdVariable = "S1JARVIS_VERILIC_VENDOR_ID";
        private const string RecognitionKeyIdVariable = "S1JARVIS_VERILIC_RECOGNITION_KEY_ID";
        private const string RecognitionSecretVariable = "S1JARVIS_VERILIC_RECOGNITION_SECRET";

        private readonly Dictionary<string, string> _productIds;
        private readonly Dictionary<string, string> _licenceIds;

        private VerilicRuntimeConfiguration(
            VerilicRuntimeMode mode, Uri licensingOrigin, string stateDirectory,
            VerilicInstallationProtectionScope protectionScope,
            Dictionary<string, string> productIds, Dictionary<string, string> licenceIds,
            string vendorId, string productVersion,
            string recognitionKeyId, string recognitionSecret)
        {
            Mode = mode;
            LicensingOrigin = licensingOrigin;
            StateDirectory = stateDirectory;
            ProtectionScope = protectionScope;
            _productIds = productIds ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _licenceIds = licenceIds ?? new Dictionary<string, string>(StringComparer.Ordinal);
            VendorId = vendorId;
            ProductVersion = productVersion;
            RecognitionKeyId = recognitionKeyId;
            RecognitionSecret = recognitionSecret;
        }

        public VerilicRuntimeMode Mode { get; private set; }
        public Uri LicensingOrigin { get; private set; }
        public string StateDirectory { get; private set; }
        public VerilicInstallationProtectionScope ProtectionScope { get; private set; }
        public string VendorId { get; private set; }
        public string ProductVersion { get; private set; }
        public string RecognitionKeyId { get; private set; }
        public string RecognitionSecret { get; private set; }

        public Uri VerificationUri => BuildApiUri("api/licensing/v1/verify");
        public Uri RoutingUri => BuildApiUri("api/jarvis-ai/routing/resolve");
        public Uri ProviderHealthUri => BuildApiUri("api/jarvis-ai/routing/health");

        public string ResolveProductId(string productCode) => ResolveMappedIdentifier(
            _productIds, productCode, "No Verilic ProductId is configured for the Jarvis product.");

        public string ResolveActivationVendorId()
        {
            if (string.IsNullOrWhiteSpace(VendorId))
                throw new InvalidOperationException("No Verilic VendorId is configured for activation.");
            return VendorId;
        }

        public string ResolveLicenceId(string productCode) => ResolveMappedIdentifier(
            _licenceIds, productCode, "No Verilic LicenceId is configured for activation of this Jarvis product.");

        public static VerilicRuntimeConfiguration Load()
        {
            VerilicLocalConfiguration local = VerilicLocalConfigurationStore.Load();
            return local != null ? LoadLocal(local) : LoadProcessEnvironment();
        }

        internal static void ValidateLocalConfiguration(VerilicLocalConfiguration local)
        {
            if (local == null)
                throw new ArgumentNullException(nameof(local));

            Create(local.Mode, local.Origin, local.StateDirectory, local.DpapiScope,
                local.VendorId, local.JarvisProductId, local.JarvisLicenceId,
                local.CourierProductId, local.CourierLicenceId,
                local.DocReaderProductId, local.DocReaderLicenceId,
                null, null, "local Verilic configuration", false);
        }

        private static VerilicRuntimeConfiguration LoadLocal(VerilicLocalConfiguration local)
        {
            return Create(local.Mode, local.Origin, local.StateDirectory, local.DpapiScope,
                local.VendorId, local.JarvisProductId, local.JarvisLicenceId,
                local.CourierProductId, local.CourierLicenceId,
                local.DocReaderProductId, local.DocReaderLicenceId,
                Environment.GetEnvironmentVariable(RecognitionKeyIdVariable),
                Environment.GetEnvironmentVariable(RecognitionSecretVariable),
                "local Verilic configuration + process secret configuration", true);
        }

        private static VerilicRuntimeConfiguration LoadProcessEnvironment()
        {
            return Create(
                Environment.GetEnvironmentVariable(ModeVariable),
                Environment.GetEnvironmentVariable(OriginVariable),
                Environment.GetEnvironmentVariable(StateDirectoryVariable),
                Environment.GetEnvironmentVariable(DpapiScopeVariable),
                Environment.GetEnvironmentVariable(VendorIdVariable),
                Environment.GetEnvironmentVariable("S1JARVIS_VERILIC_PRODUCT_ID"),
                Environment.GetEnvironmentVariable("S1JARVIS_VERILIC_LICENCE_ID"),
                Environment.GetEnvironmentVariable("S1JARVISCOURIER_VERILIC_PRODUCT_ID"),
                Environment.GetEnvironmentVariable("S1JARVISCOURIER_VERILIC_LICENCE_ID"),
                Environment.GetEnvironmentVariable("S1JARVISDOCREADER_VERILIC_PRODUCT_ID"),
                Environment.GetEnvironmentVariable("S1JARVISDOCREADER_VERILIC_LICENCE_ID"),
                Environment.GetEnvironmentVariable(RecognitionKeyIdVariable),
                Environment.GetEnvironmentVariable(RecognitionSecretVariable),
                "process environment", true);
        }

        private static VerilicRuntimeConfiguration Create(
            string modeText, string originText, string stateDirectoryText, string scopeText,
            string vendorIdText, string jarvisProductIdText, string jarvisLicenceIdText,
            string courierProductIdText, string courierLicenceIdText,
            string docReaderProductIdText, string docReaderLicenceIdText,
            string recognitionKeyIdText, string recognitionSecretText,
            string sourceName, bool requireRecognition)
        {
            if (string.IsNullOrWhiteSpace(modeText) ||
                string.Equals(modeText.Trim(), "legacy", StringComparison.OrdinalIgnoreCase))
            {
                return new VerilicRuntimeConfiguration(
                    VerilicRuntimeMode.Legacy, null, null,
                    VerilicInstallationProtectionScope.CurrentUser,
                    null, null, null, GetProductVersion(), null, null);
            }

            if (!string.Equals(modeText.Trim(), "verilic", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "S1Jarvis Verilic runtime mode is invalid in " + sourceName + ".");

            Uri origin = RequireHttpsOrigin(originText);
            string stateDirectory = string.IsNullOrWhiteSpace(stateDirectoryText)
                ? VerilicLocalConfigurationStore.GetDefaultDirectory()
                : stateDirectoryText;
            VerilicInstallationProtectionScope scope = ParseProtectionScope(scopeText);

            // Canonical NativeS1 product ids default to the stable Jarvis product
            // codes. This removes the need to configure separate product-id
            // environment variables for normal deployments. The explicit values
            // are still accepted as overrides for backwards compatibility.
            var productIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JarvisProducts.Jarvis] = ProductIdOrDefault(
                    jarvisProductIdText, JarvisProducts.Jarvis,
                    "S1JARVIS_VERILIC_PRODUCT_ID"),
                [JarvisProducts.JarvisCourier] = ProductIdOrDefault(
                    courierProductIdText, JarvisProducts.JarvisCourier,
                    "S1JARVISCOURIER_VERILIC_PRODUCT_ID"),
                [JarvisProducts.JarvisDocReader] = ProductIdOrDefault(
                    docReaderProductIdText, JarvisProducts.JarvisDocReader,
                    "S1JARVISDOCREADER_VERILIC_PRODUCT_ID")
            };

            var licenceIds = new Dictionary<string, string>(StringComparer.Ordinal);
            AddOptionalIdentifier(licenceIds, JarvisProducts.Jarvis,
                jarvisLicenceIdText, "S1JARVIS_VERILIC_LICENCE_ID");
            AddOptionalIdentifier(licenceIds, JarvisProducts.JarvisCourier,
                courierLicenceIdText, "S1JARVISCOURIER_VERILIC_LICENCE_ID");
            AddOptionalIdentifier(licenceIds, JarvisProducts.JarvisDocReader,
                docReaderLicenceIdText, "S1JARVISDOCREADER_VERILIC_LICENCE_ID");

            string recognitionKeyId = null;
            string recognitionSecret = null;
            if (requireRecognition)
            {
                recognitionKeyId = RequireIdentifier(
                    recognitionKeyIdText, RecognitionKeyIdVariable);
                if (string.IsNullOrWhiteSpace(recognitionSecretText) ||
                    recognitionSecretText.Length > 4096)
                {
                    throw new InvalidOperationException(
                        RecognitionSecretVariable +
                        " is required for NativeS1 startup verification.");
                }
                recognitionSecret = recognitionSecretText.Trim();
            }

            return new VerilicRuntimeConfiguration(
                VerilicRuntimeMode.Verilic,
                origin,
                Path.GetFullPath(stateDirectory),
                scope,
                productIds,
                licenceIds,
                OptionalIdentifier(vendorIdText, VendorIdVariable),
                GetProductVersion(),
                recognitionKeyId,
                recognitionSecret);
        }

        private Uri BuildApiUri(string relativePath)
        {
            if (LicensingOrigin == null)
                return null;
            return new Uri(
                new Uri(LicensingOrigin.GetLeftPart(UriPartial.Authority) + "/"),
                relativePath);
        }

        private static string ProductIdOrDefault(
            string configuredValue,
            string defaultProductId,
            string variableName)
        {
            return string.IsNullOrWhiteSpace(configuredValue)
                ? RequireIdentifier(defaultProductId, variableName)
                : RequireIdentifier(configuredValue, variableName);
        }

        private static void AddOptionalIdentifier(
            Dictionary<string, string> values,
            string productCode,
            string value,
            string variableName)
        {
            string normalized = OptionalIdentifier(value, variableName);
            if (!string.IsNullOrEmpty(normalized))
                values[productCode] = normalized;
        }

        private static string OptionalIdentifier(string value, string variableName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : RequireIdentifier(value, variableName);
        }

        private static string ResolveMappedIdentifier(
            Dictionary<string, string> values,
            string productCode,
            string errorMessage)
        {
            string value;
            if (string.IsNullOrWhiteSpace(productCode) ||
                !values.TryGetValue(productCode, out value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(errorMessage);
            }
            return value;
        }

        private static Uri RequireHttpsOrigin(string value)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "S1Jarvis Verilic origin must be an absolute HTTPS URI.");
            }
            return uri;
        }

        private static VerilicInstallationProtectionScope ParseProtectionScope(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value.Trim(), "CurrentUser",
                    StringComparison.OrdinalIgnoreCase))
            {
                return VerilicInstallationProtectionScope.CurrentUser;
            }

            if (string.Equals(value.Trim(), "LocalMachine",
                    StringComparison.OrdinalIgnoreCase))
            {
                return VerilicInstallationProtectionScope.LocalMachine;
            }

            throw new InvalidOperationException(
                "S1Jarvis Verilic DPAPI scope must be CurrentUser or LocalMachine.");
        }

        private static string RequireIdentifier(string value, string variableName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
                throw new InvalidOperationException(
                    variableName + " must contain a registered Verilic identifier.");

            string normalized = value.Trim();
            for (int i = 0; i < normalized.Length; i++)
            {
                if (char.IsControl(normalized[i]) || char.IsWhiteSpace(normalized[i]))
                {
                    throw new InvalidOperationException(
                        variableName + " contains an invalid Verilic identifier.");
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace S1Jarvis.Access.Verilic
{
    internal enum VerilicRuntimeMode
    {
        Legacy = 0,
        Verilic = 1
    }

    /// <summary>
    /// External, non-secret composition settings for the in-process Verilic
    /// client. Environment variables are used deliberately because S1Jarvis is
    /// loaded by the Soft1 host and cannot safely own the host executable's
    /// app.config. Missing configuration never silently enables Verilic.
    ///
    /// Registered ProductIds are runtime composition data and are required in
    /// Verilic mode. VendorId/LicenceIds are activation-only references: they
    /// may be absent for products that are not being activated on this client.
    /// </summary>
    internal sealed class VerilicRuntimeConfiguration
    {
        private const string ModeVariable = "S1JARVIS_VERILIC_MODE";
        private const string OriginVariable = "S1JARVIS_VERILIC_ORIGIN";
        private const string StateDirectoryVariable = "S1JARVIS_VERILIC_STATE_DIR";
        private const string DpapiScopeVariable = "S1JARVIS_VERILIC_DPAPI_SCOPE";
        private const string VendorIdVariable = "S1JARVIS_VERILIC_VENDOR_ID";

        private readonly Dictionary<string, string> _productIds;
        private readonly Dictionary<string, string> _licenceIds;

        private VerilicRuntimeConfiguration(
            VerilicRuntimeMode mode,
            Uri licensingOrigin,
            string stateDirectory,
            VerilicInstallationProtectionScope protectionScope,
            Dictionary<string, string> productIds,
            Dictionary<string, string> licenceIds,
            string vendorId,
            string productVersion)
        {
            Mode = mode;
            LicensingOrigin = licensingOrigin;
            StateDirectory = stateDirectory;
            ProtectionScope = protectionScope;
            _productIds = productIds ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _licenceIds = licenceIds ?? new Dictionary<string, string>(StringComparer.Ordinal);
            VendorId = vendorId;
            ProductVersion = productVersion;
        }

        public VerilicRuntimeMode Mode { get; private set; }
        public Uri LicensingOrigin { get; private set; }
        public string StateDirectory { get; private set; }
        public VerilicInstallationProtectionScope ProtectionScope { get; private set; }
        public string VendorId { get; private set; }
        public string ProductVersion { get; private set; }

        public Uri VerificationUri
        {
            get { return BuildApiUri("api/licensing/v1/verify"); }
        }

        public Uri RoutingUri
        {
            get { return BuildApiUri("api/jarvis-ai/routing/resolve"); }
        }

        public string ResolveProductId(string productCode)
        {
            return ResolveMappedIdentifier(
                _productIds,
                productCode,
                "No Verilic ProductId is configured for the Jarvis product.");
        }

        public string ResolveActivationVendorId()
        {
            if (string.IsNullOrWhiteSpace(VendorId))
                throw new InvalidOperationException(
                    "No Verilic VendorId is configured for activation.");

            return VendorId;
        }

        public string ResolveLicenceId(string productCode)
        {
            return ResolveMappedIdentifier(
                _licenceIds,
                productCode,
                "No Verilic LicenceId is configured for activation of this Jarvis product.");
        }

        public static VerilicRuntimeConfiguration Load()
        {
            string modeText = Environment.GetEnvironmentVariable(ModeVariable);
            if (string.IsNullOrWhiteSpace(modeText) ||
                string.Equals(modeText.Trim(), "legacy", StringComparison.OrdinalIgnoreCase))
            {
                return new VerilicRuntimeConfiguration(
                    VerilicRuntimeMode.Legacy,
                    null,
                    null,
                    VerilicInstallationProtectionScope.CurrentUser,
                    null,
                    null,
                    null,
                    GetProductVersion());
            }

            if (!string.Equals(modeText.Trim(), "verilic", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "S1Jarvis Verilic runtime mode is invalid.");

            Uri origin = RequireHttpsOrigin(
                Environment.GetEnvironmentVariable(OriginVariable));

            string stateDirectory = Environment.GetEnvironmentVariable(
                StateDirectoryVariable);
            if (string.IsNullOrWhiteSpace(stateDirectory))
            {
                stateDirectory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "S1Jarvis",
                    "Verilic");
            }

            VerilicInstallationProtectionScope scope = ParseProtectionScope(
                Environment.GetEnvironmentVariable(DpapiScopeVariable));

            var productIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [JarvisProducts.Jarvis] = RequireIdentifier(
                    Environment.GetEnvironmentVariable(
                        "S1JARVIS_VERILIC_PRODUCT_ID"),
                    "S1JARVIS_VERILIC_PRODUCT_ID"),
                [JarvisProducts.JarvisCourier] = RequireIdentifier(
                    Environment.GetEnvironmentVariable(
                        "S1JARVISCOURIER_VERILIC_PRODUCT_ID"),
                    "S1JARVISCOURIER_VERILIC_PRODUCT_ID"),
                [JarvisProducts.JarvisDocReader] = RequireIdentifier(
                    Environment.GetEnvironmentVariable(
                        "S1JARVISDOCREADER_VERILIC_PRODUCT_ID"),
                    "S1JARVISDOCREADER_VERILIC_PRODUCT_ID")
            };

            var licenceIds = new Dictionary<string, string>(StringComparer.Ordinal);
            AddOptionalIdentifier(
                licenceIds,
                JarvisProducts.Jarvis,
                Environment.GetEnvironmentVariable(
                    "S1JARVIS_VERILIC_LICENCE_ID"),
                "S1JARVIS_VERILIC_LICENCE_ID");
            AddOptionalIdentifier(
                licenceIds,
                JarvisProducts.JarvisCourier,
                Environment.GetEnvironmentVariable(
                    "S1JARVISCOURIER_VERILIC_LICENCE_ID"),
                "S1JARVISCOURIER_VERILIC_LICENCE_ID");
            AddOptionalIdentifier(
                licenceIds,
                JarvisProducts.JarvisDocReader,
                Environment.GetEnvironmentVariable(
                    "S1JARVISDOCREADER_VERILIC_LICENCE_ID"),
                "S1JARVISDOCREADER_VERILIC_LICENCE_ID");

            string vendorId = OptionalIdentifier(
                Environment.GetEnvironmentVariable(VendorIdVariable),
                VendorIdVariable);

            return new VerilicRuntimeConfiguration(
                VerilicRuntimeMode.Verilic,
                origin,
                Path.GetFullPath(stateDirectory),
                scope,
                productIds,
                licenceIds,
                vendorId,
                GetProductVersion());
        }

        private Uri BuildApiUri(string relativePath)
        {
            if (LicensingOrigin == null)
                return null;

            var origin = new Uri(
                LicensingOrigin.GetLeftPart(UriPartial.Authority) + "/");
            return new Uri(origin, relativePath);
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
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return RequireIdentifier(value, variableName);
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
                throw new InvalidOperationException(errorMessage);

            return value;
        }

        private static Uri RequireHttpsOrigin(string value)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "S1Jarvis Verilic origin must be an absolute HTTPS URI.");

            return uri;
        }

        private static VerilicInstallationProtectionScope ParseProtectionScope(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value.Trim(), "CurrentUser", StringComparison.OrdinalIgnoreCase))
                return VerilicInstallationProtectionScope.CurrentUser;

            if (string.Equals(value.Trim(), "LocalMachine", StringComparison.OrdinalIgnoreCase))
                return VerilicInstallationProtectionScope.LocalMachine;

            throw new InvalidOperationException(
                "S1Jarvis Verilic DPAPI scope must be CurrentUser or LocalMachine.");
        }

        private static string RequireIdentifier(string value, string variableName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
                throw new InvalidOperationException(
                    variableName + " must contain a registered Verilic identifier.");

            string normalized = value.Trim();
            for (int index = 0; index < normalized.Length; index++)
            {
                if (char.IsControl(normalized[index]) ||
                    char.IsWhiteSpace(normalized[index]))
                    throw new InvalidOperationException(
                        variableName + " contains an invalid Verilic identifier.");
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

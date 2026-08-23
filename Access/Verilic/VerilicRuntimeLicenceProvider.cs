using System;
using System.Collections.Generic;

namespace S1Jarvis.Access.Verilic
{
    internal interface IVerilicRuntimeLicenceProvider
    {
        JarvisLicenceAccessDecision Check(string productCode);
    }

    /// <summary>
    /// Authoritative Verilic runtime licensing check for an already activated
    /// installation. The commercial Jarvis product code is mapped explicitly to
    /// the registered Verilic ProductId; the two identities are never assumed to
    /// be equal.
    /// </summary>
    internal sealed class VerilicRuntimeLicenceProvider :
        IVerilicRuntimeLicenceProvider
    {
        private readonly VerilicInstallationStateStore _stateStore;
        private readonly Uri _verificationUri;
        private readonly string _productVersion;
        private readonly Func<string, string> _productIdResolver;

        public VerilicRuntimeLicenceProvider(
            VerilicInstallationStateStore stateStore,
            Uri verificationUri,
            string productVersion,
            Func<string, string> productIdResolver)
        {
            _stateStore = stateStore ??
                throw new ArgumentNullException(nameof(stateStore));

            if (verificationUri == null ||
                !verificationUri.IsAbsoluteUri ||
                !string.Equals(
                    verificationUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "An absolute HTTPS Verilic verification URI is required.",
                    nameof(verificationUri));

            if (string.IsNullOrWhiteSpace(productVersion) ||
                productVersion.Length > 200)
                throw new ArgumentException(
                    "A product version is required.",
                    nameof(productVersion));
            if (productIdResolver == null)
                throw new ArgumentNullException(nameof(productIdResolver));

            _verificationUri = verificationUri;
            _productVersion = productVersion.Trim();
            _productIdResolver = productIdResolver;
        }

        public JarvisLicenceAccessDecision Check(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_request_invalid");

            try
            {
                string productId = _productIdResolver(productCode);
                if (string.IsNullOrWhiteSpace(productId))
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "product_binding_invalid");

                VerilicInstallationState state =
                    _stateStore.Load(productCode);

                if (state == null || !state.ActivationCompleted)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_not_activated");

                if (!string.Equals(
                        state.VerilicProductId,
                        productId,
                        StringComparison.Ordinal))
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "product_binding_invalid");

                if (string.IsNullOrWhiteSpace(state.InstallationId) ||
                    state.InstallationId.StartsWith(
                        "pending_",
                        StringComparison.Ordinal))
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_binding_invalid");

                if (!string.Equals(
                        state.KeyAlgorithm,
                        "ES256",
                        StringComparison.Ordinal) ||
                    state.PrivateKeyMaterial == null ||
                    state.PrivateKeyMaterial.Length == 0)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_key_invalid");

                // The existing net48 authorizer snapshots its proof ProductId
                // from ProductCode. Give it a proof-only view containing the
                // registered Verilic ProductId while keeping persistent state
                // keyed by the stable Jarvis commercial code.
                var proofState = new VerilicInstallationState
                {
                    ProductCode = productId,
                    InstallationId = state.InstallationId,
                    KeyAlgorithm = state.KeyAlgorithm,
                    PrivateKeyMaterial = state.PrivateKeyMaterial
                };

                using (var authorizer =
                    new VerilicEs256RequestAuthorizer(proofState))
                {
                    IVerilicLicenceTransport transport =
                        new VerilicLicenceHttpTransport(
                            _verificationUri,
                            authorizer);

                    VerilicVerifyLicenceResult result =
                        transport.Verify(
                            new VerilicVerifyLicenceRequest
                            {
                                ProductId = productId,
                                InstallationId = state.InstallationId,
                                ProductVersion = _productVersion,
                                RequestedFeatures = new List<string>()
                            });

                    return JarvisLicenceAccessDecision.FromVerilic(
                        productCode,
                        result);
                }
            }
            catch
            {
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_failed");
            }
        }
    }
}

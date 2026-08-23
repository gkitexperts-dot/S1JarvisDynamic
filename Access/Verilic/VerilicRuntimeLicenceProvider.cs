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
    /// installation. Missing/incomplete local activation state fails closed
    /// before any network request is attempted.
    /// </summary>
    internal sealed class VerilicRuntimeLicenceProvider :
        IVerilicRuntimeLicenceProvider
    {
        private readonly VerilicInstallationStateStore _stateStore;
        private readonly Uri _verificationUri;
        private readonly string _productVersion;

        public VerilicRuntimeLicenceProvider(
            VerilicInstallationStateStore stateStore,
            Uri verificationUri,
            string productVersion)
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

            _verificationUri = verificationUri;
            _productVersion = productVersion.Trim();
        }

        public JarvisLicenceAccessDecision Check(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_request_invalid");

            try
            {
                VerilicInstallationState state =
                    _stateStore.Load(productCode);

                if (state == null)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_not_activated");

                if (!state.ActivationCompleted)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_not_activated");

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

                using (var authorizer =
                    new VerilicEs256RequestAuthorizer(state))
                {
                    IVerilicLicenceTransport transport =
                        new VerilicLicenceHttpTransport(
                            _verificationUri,
                            authorizer);

                    VerilicVerifyLicenceResult result =
                        transport.Verify(
                            new VerilicVerifyLicenceRequest
                            {
                                ProductId = productCode,
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

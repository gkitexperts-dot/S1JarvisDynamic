using System;
using Softone;

namespace S1Jarvis.Access.Verilic
{
    internal interface IVerilicRuntimeAiRoutingProvider
    {
        VerilicAiRoutingResult Resolve(XSupport xSupport, string productCode);
    }

    /// <summary>
    /// Resolves only the opaque AI account reference for an activated Jarvis
    /// installation. The server re-authenticates the ES256 installation proof
    /// and re-verifies the authoritative licence before returning routing data.
    /// This provider never creates or changes the licensing decision.
    /// </summary>
    internal sealed class VerilicRuntimeAiRoutingProvider :
        IVerilicRuntimeAiRoutingProvider
    {
        private readonly VerilicInstallationStateStore _stateStore;
        private readonly Uri _routingUri;
        private readonly string _productVersion;
        private readonly Func<string, string> _productIdResolver;

        public VerilicRuntimeAiRoutingProvider(
            VerilicInstallationStateStore stateStore,
            Uri routingUri,
            string productVersion,
            Func<string, string> productIdResolver)
        {
            _stateStore = stateStore ??
                throw new ArgumentNullException(nameof(stateStore));

            if (routingUri == null ||
                !routingUri.IsAbsoluteUri ||
                !string.Equals(
                    routingUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "An absolute HTTPS Verilic routing URI is required.",
                    nameof(routingUri));

            if (string.IsNullOrWhiteSpace(productVersion) ||
                productVersion.Length > 200)
                throw new ArgumentException(
                    "A product version is required.",
                    nameof(productVersion));
            if (productIdResolver == null)
                throw new ArgumentNullException(nameof(productIdResolver));

            _routingUri = routingUri;
            _productVersion = productVersion.Trim();
            _productIdResolver = productIdResolver;
        }

        public VerilicAiRoutingResult Resolve(
            XSupport xSupport,
            string productCode)
        {
            if (xSupport == null || string.IsNullOrWhiteSpace(productCode))
                return VerilicAiRoutingResult.Deny("routing_request_invalid");

            try
            {
                string productId = _productIdResolver(productCode);
                if (string.IsNullOrWhiteSpace(productId))
                    return VerilicAiRoutingResult.Deny(
                        "product_binding_invalid");

                VerilicInstallationState state =
                    _stateStore.Load(productCode);

                if (state == null || !state.ActivationCompleted)
                    return VerilicAiRoutingResult.Deny(
                        "installation_not_activated");

                if (!string.Equals(
                        state.VerilicProductId,
                        productId,
                        StringComparison.Ordinal))
                    return VerilicAiRoutingResult.Deny(
                        "product_binding_invalid");

                if (string.IsNullOrWhiteSpace(state.InstallationId) ||
                    state.InstallationId.StartsWith(
                        "pending_",
                        StringComparison.Ordinal))
                    return VerilicAiRoutingResult.Deny(
                        "installation_binding_invalid");

                if (!string.Equals(
                        state.KeyAlgorithm,
                        "ES256",
                        StringComparison.Ordinal) ||
                    state.PrivateKeyMaterial == null ||
                    state.PrivateKeyMaterial.Length == 0)
                    return VerilicAiRoutingResult.Deny(
                        "installation_key_invalid");

                var info = xSupport.ConnectionInfo;
                if (info == null)
                    return VerilicAiRoutingResult.Deny(
                        "routing_request_invalid");

                // The net48 authorizer snapshots its proof ProductId from
                // ProductCode. Use a proof-only view with the registered
                // Verilic ProductId; persistent state remains keyed by the
                // stable Jarvis commercial code.
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
                    IVerilicAiRoutingTransport transport =
                        new VerilicAiRoutingHttpTransport(
                            _routingUri,
                            authorizer);

                    return transport.Resolve(
                        new VerilicAiRoutingRequest
                        {
                            ProductId = productId,
                            InstallationId = state.InstallationId,
                            ProductVersion = _productVersion,
                            RequestedFeatures = new string[0],
                            Soft1Serial = info.SerialNum == null
                                ? null
                                : info.SerialNum.ToString(),
                            CompanyCode = info.CompanyId.ToString(),
                            BranchCode = info.BranchId.ToString(),
                            Soft1UserId = info.UserId.ToString()
                        });
                }
            }
            catch
            {
                return VerilicAiRoutingResult.Deny("routing_failed");
            }
        }
    }
}

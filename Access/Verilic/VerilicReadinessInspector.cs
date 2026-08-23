using System;

namespace S1Jarvis.Access.Verilic
{
    internal sealed class VerilicProductReadiness
    {
        public bool ProductConfigured { get; set; }
        public bool ActivationReferencesConfigured { get; set; }
        public bool StatePresent { get; set; }
        public bool ActivationCompleted { get; set; }
        public bool ProductBindingMatches { get; set; }
        public bool RuntimeReady { get; set; }
        public string ReasonCode { get; set; }
    }

    /// <summary>
    /// Read-only local readiness inspection. It never creates installation
    /// state, performs activation, verifies a licence over the network, or
    /// exposes identifiers, paths, keys, challenge material or tokens.
    /// </summary>
    internal sealed class VerilicReadinessInspector
    {
        private readonly VerilicRuntimeConfiguration _configuration;
        private readonly VerilicInstallationStateStore _stateStore;

        public VerilicReadinessInspector(
            VerilicRuntimeConfiguration configuration)
        {
            _configuration = configuration ??
                throw new ArgumentNullException(nameof(configuration));

            if (_configuration.Mode != VerilicRuntimeMode.Verilic)
                throw new InvalidOperationException(
                    "Verilic readiness inspection requires verilic runtime mode.");

            _stateStore = new VerilicInstallationStateStore(
                _configuration.StateDirectory,
                _configuration.ProtectionScope);
        }

        public VerilicProductReadiness Inspect(string productCode)
        {
            var result = new VerilicProductReadiness
            {
                ReasonCode = "not_ready"
            };

            string configuredProductId;
            try
            {
                configuredProductId =
                    _configuration.ResolveProductId(productCode);
                result.ProductConfigured = true;
            }
            catch
            {
                result.ReasonCode = "product_not_configured";
                return result;
            }

            try
            {
                _configuration.ResolveActivationVendorId();
                _configuration.ResolveLicenceId(productCode);
                result.ActivationReferencesConfigured = true;
            }
            catch
            {
                result.ActivationReferencesConfigured = false;
            }

            VerilicInstallationState state;
            try
            {
                state = _stateStore.Load(productCode);
            }
            catch
            {
                result.ReasonCode = "state_invalid";
                return result;
            }

            if (state == null)
            {
                result.ReasonCode = "state_missing";
                return result;
            }

            result.StatePresent = true;
            result.ActivationCompleted = state.ActivationCompleted;
            result.ProductBindingMatches =
                !string.IsNullOrWhiteSpace(state.VerilicProductId) &&
                string.Equals(
                    state.VerilicProductId,
                    configuredProductId,
                    StringComparison.Ordinal);

            if (!result.ActivationCompleted)
            {
                result.ReasonCode = "activation_incomplete";
                return result;
            }

            if (!result.ProductBindingMatches)
            {
                result.ReasonCode = "product_binding_mismatch";
                return result;
            }

            if (string.IsNullOrWhiteSpace(state.InstallationId) ||
                state.InstallationId.StartsWith(
                    "pending_",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    state.KeyAlgorithm,
                    "ES256",
                    StringComparison.Ordinal) ||
                state.PrivateKeyMaterial == null ||
                state.PrivateKeyMaterial.Length == 0)
            {
                result.ReasonCode = "installation_state_incomplete";
                return result;
            }

            result.RuntimeReady = true;
            result.ReasonCode = "ready";
            return result;
        }
    }
}

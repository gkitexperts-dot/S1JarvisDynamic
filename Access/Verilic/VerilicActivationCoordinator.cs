using System;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Explicit operator-controlled activation composition. This class does not
    /// run during Jarvis startup and activation success is never treated as a
    /// runtime licence authorization decision.
    /// </summary>
    internal sealed class VerilicActivationCoordinator
    {
        private readonly VerilicRuntimeConfiguration _configuration;
        private readonly VerilicActivationClient _client;

        public VerilicActivationCoordinator(
            VerilicRuntimeConfiguration configuration)
        {
            _configuration = configuration ??
                throw new ArgumentNullException(nameof(configuration));

            if (_configuration.Mode != VerilicRuntimeMode.Verilic)
                throw new InvalidOperationException(
                    "Verilic activation requires explicit verilic runtime mode.");

            var stateStore = new VerilicInstallationStateStore(
                _configuration.StateDirectory,
                _configuration.ProtectionScope);

            _client = new VerilicActivationClient(
                _configuration.LicensingOrigin,
                stateStore);
        }

        public VerilicActivationResult Activate(
            string productCode,
            string deviceSignalHash = null)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return VerilicActivationResult.Denied(
                    "activation_request_invalid");

            try
            {
                return _client.Activate(
                    new VerilicActivationRequest
                    {
                        VendorId = _configuration.VendorId,
                        ProductCode = productCode,
                        ProductId = _configuration.ResolveProductId(productCode),
                        LicenceId = _configuration.ResolveLicenceId(productCode),
                        ProductVersion = _configuration.ProductVersion,
                        DeviceSignalHash = deviceSignalHash ?? string.Empty
                    });
            }
            catch
            {
                return VerilicActivationResult.Denied(
                    "activation_configuration_invalid");
            }
        }
    }
}

using System;
using Softone;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Access
{
    internal interface IJarvisRuntimeAccessProvider
    {
        JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode);
    }

    internal sealed class LegacyNexusRuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly Func<XSupport, string, AccessCheckResponse> _legacyCheck;
        public LegacyNexusRuntimeAccessProvider(Func<XSupport, string, AccessCheckResponse> legacyCheck)
        {
            _legacyCheck = legacyCheck ?? throw new ArgumentNullException(nameof(legacyCheck));
        }
        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));
            return JarvisRuntimeAccessResult.FromLegacy(_legacyCheck(xSupport, productCode));
        }
    }

    internal sealed class FailClosedRuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly string _reasonCode;
        public FailClosedRuntimeAccessProvider(string reasonCode)
        {
            _reasonCode = string.IsNullOrWhiteSpace(reasonCode)
                ? "runtime_configuration_invalid" : reasonCode;
        }
        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            return JarvisRuntimeAccessResult.Create(
                JarvisLicenceAccessDecision.Deny(productCode, _reasonCode),
                JarvisAgentRoutingDecision.None());
        }
    }

    /// <summary>
    /// Verilic is the only licensing authority. NativeS1 verification receives
    /// the active Soft1 identity. For a NativeS1 verification result, AI runtime
    /// material is supplied by /api/licensing/v1/verify and loaded into the
    /// session registry at BOOT/explicit HEALTH. The old installation routing
    /// provider is therefore never consulted for a NativeS1 decision.
    /// </summary>
    internal sealed class SplitVerilicRuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly IVerilicRuntimeLicenceProvider _licensing;
        private readonly IVerilicRuntimeAiRoutingProvider _routing;

        public SplitVerilicRuntimeAccessProvider(
            IVerilicRuntimeLicenceProvider licensing,
            IVerilicRuntimeAiRoutingProvider routing)
        {
            _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
            _routing = routing;
        }

        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));

            JarvisLicenceAccessDecision licence = _licensing.Check(xSupport, productCode);
            if (licence == null || !licence.Allowed ||
                (string.Equals(productCode, JarvisProducts.Jarvis, StringComparison.Ordinal) &&
                 !licence.RuntimeReady))
            {
                return JarvisRuntimeAccessResult.Create(
                    licence ?? JarvisLicenceAccessDecision.Deny(
                        productCode, "verification_failed"),
                    JarvisAgentRoutingDecision.None(
                        licence == null ? null : licence.RuntimeReasonCode));
            }

            // NativeS1 /verify is authoritative and already carries the encrypted
            // AI configuration. Do not make an installation-bound /routing/resolve
            // request after a NativeS1 licence decision.
            if (licence.Verification != null)
                return JarvisRuntimeAccessResult.Create(
                    licence, JarvisAgentRoutingDecision.None());

            // Compatibility only for non-NativeS1/older composition callers.
            if (!string.Equals(productCode, JarvisProducts.Jarvis, StringComparison.Ordinal) ||
                _routing == null)
                return JarvisRuntimeAccessResult.Create(
                    licence, JarvisAgentRoutingDecision.None());

            VerilicAiRoutingResult routingResult;
            try
            {
                routingResult = _routing.Resolve(xSupport, productCode);
            }
            catch
            {
                routingResult = null;
            }

            return JarvisRuntimeAccessResult.Create(
                licence, JarvisAgentRoutingDecision.FromVerilic(routingResult));
        }
    }
}

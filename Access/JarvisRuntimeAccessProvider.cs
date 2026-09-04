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
    /// the active Soft1 identity. The older routing provider is retained only as
    /// a compatibility layer for existing agent execution until the encrypted
    /// NativeS1 AI envelope is consumed directly; it can never turn a licence or
    /// runtime-readiness denial into an allow.
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

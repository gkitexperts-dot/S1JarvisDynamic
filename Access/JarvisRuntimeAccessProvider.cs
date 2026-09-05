using System;
using Softone;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Access
{
    internal interface IJarvisRuntimeAccessProvider
    {
        JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode);
    }

    internal sealed class FailClosedRuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly string _reasonCode;

        public FailClosedRuntimeAccessProvider(string reasonCode)
        {
            _reasonCode = string.IsNullOrWhiteSpace(reasonCode)
                ? "runtime_configuration_invalid"
                : reasonCode;
        }

        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            return JarvisRuntimeAccessResult.Create(
                JarvisLicenceAccessDecision.Deny(productCode, _reasonCode),
                JarvisAgentRoutingDecision.None());
        }
    }

    /// <summary>
    /// NativeS1-only runtime access. Verilic /api/licensing/v1/verify is the sole
    /// licensing and runtime-readiness authority. No Nexus fallback, installation
    /// identity, activation state, routing/resolve call or ES256 proof participates
    /// in the startup decision.
    /// </summary>
    internal sealed class VerilicNativeS1RuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly IVerilicRuntimeLicenceProvider _licensing;

        public VerilicNativeS1RuntimeAccessProvider(IVerilicRuntimeLicenceProvider licensing)
        {
            _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
        }

        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null)
                throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));

            JarvisLicenceAccessDecision licence = _licensing.Check(xSupport, productCode);
            if (licence == null)
            {
                licence = JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_failed");
            }

            return JarvisRuntimeAccessResult.Create(
                licence,
                JarvisAgentRoutingDecision.None(licence.RuntimeReasonCode));
        }
    }
}

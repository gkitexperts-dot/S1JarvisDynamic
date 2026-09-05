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
    /// Verilic NativeS1 runtime access. Each product uses its own build-time
    /// Recognition Key ID + Secret and verifies the active Soft1 named-user
    /// identity through POST /api/licensing/v1/verify. No workstation or
    /// per-customer credential provisioning is required.
    /// </summary>
    internal sealed class VerilicContractRuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly IVerilicRuntimeLicenceProvider _licensing;

        public VerilicContractRuntimeAccessProvider(IVerilicRuntimeLicenceProvider licensing)
        {
            _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
        }

        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null)
                throw new ArgumentNullException(nameof(xSupport));

            JarvisLicenceAccessDecision licence = _licensing.Check(xSupport, productCode);
            if (licence == null)
                licence = JarvisLicenceAccessDecision.Deny(productCode, "verification_failed");

            return JarvisRuntimeAccessResult.Create(
                licence,
                JarvisAgentRoutingDecision.None(licence.RuntimeReasonCode));
        }
    }
}

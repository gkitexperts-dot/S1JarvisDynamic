using System;
using Softone;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Access
{
    internal interface IJarvisRuntimeAccessProvider
    {
        JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode);
    }

    /// <summary>
    /// Transitional provider used before the Verilic cutover is explicitly
    /// enabled. It preserves the existing combined Nexus behaviour.
    /// </summary>
    internal sealed class LegacyNexusRuntimeAccessProvider :
        IJarvisRuntimeAccessProvider
    {
        private readonly Func<XSupport, string, AccessCheckResponse> _legacyCheck;

        public LegacyNexusRuntimeAccessProvider(
            Func<XSupport, string, AccessCheckResponse> legacyCheck)
        {
            _legacyCheck = legacyCheck ??
                throw new ArgumentNullException(nameof(legacyCheck));
        }

        public JarvisRuntimeAccessResult Check(
            XSupport xSupport,
            string productCode)
        {
            if (xSupport == null)
                throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException(
                    "Product code is required.",
                    nameof(productCode));

            AccessCheckResponse legacy =
                _legacyCheck(xSupport, productCode);
            return JarvisRuntimeAccessResult.FromLegacy(legacy);
        }
    }

    /// <summary>
    /// Used when Verilic mode was explicitly requested but its composition is
    /// invalid. This is deliberately different from legacy mode: explicit
    /// Verilic configuration failure never falls back to Nexus authorization.
    /// </summary>
    internal sealed class FailClosedRuntimeAccessProvider :
        IJarvisRuntimeAccessProvider
    {
        private readonly string _reasonCode;

        public FailClosedRuntimeAccessProvider(string reasonCode)
        {
            _reasonCode = string.IsNullOrWhiteSpace(reasonCode)
                ? "runtime_configuration_invalid"
                : reasonCode;
        }

        public JarvisRuntimeAccessResult Check(
            XSupport xSupport,
            string productCode)
        {
            return JarvisRuntimeAccessResult.Create(
                JarvisLicenceAccessDecision.Deny(
                    productCode,
                    _reasonCode),
                JarvisAgentRoutingDecision.None());
        }
    }

    /// <summary>
    /// Cutover provider: Verilic is the only licensing authority. The legacy
    /// Nexus call is made only after Verilic Allowed=true and is consumed only
    /// as an opaque AI-routing lookup. A Verilic deny never falls back to the
    /// legacy entitlement decision.
    /// </summary>
    internal sealed class SplitVerilicRuntimeAccessProvider :
        IJarvisRuntimeAccessProvider
    {
        private readonly IVerilicRuntimeLicenceProvider _licensing;
        private readonly Func<XSupport, string, AccessCheckResponse>
            _legacyRoutingLookup;

        public SplitVerilicRuntimeAccessProvider(
            IVerilicRuntimeLicenceProvider licensing,
            Func<XSupport, string, AccessCheckResponse> legacyRoutingLookup)
        {
            _licensing = licensing ??
                throw new ArgumentNullException(nameof(licensing));
            _legacyRoutingLookup = legacyRoutingLookup ??
                throw new ArgumentNullException(nameof(legacyRoutingLookup));
        }

        public JarvisRuntimeAccessResult Check(
            XSupport xSupport,
            string productCode)
        {
            if (xSupport == null)
                throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException(
                    "Product code is required.",
                    nameof(productCode));

            JarvisLicenceAccessDecision licence =
                _licensing.Check(productCode);

            if (licence == null || !licence.Allowed)
            {
                return JarvisRuntimeAccessResult.Create(
                    licence ?? JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "verification_failed"),
                    JarvisAgentRoutingDecision.None());
            }

            AccessCheckResponse legacyRouting;
            try
            {
                legacyRouting =
                    _legacyRoutingLookup(xSupport, productCode);
            }
            catch
            {
                legacyRouting = null;
            }

            JarvisAgentRoutingDecision routing =
                legacyRouting == null
                    ? JarvisAgentRoutingDecision.None()
                    : JarvisAgentRoutingDecision.FromLegacy(
                        legacyRouting);

            return JarvisRuntimeAccessResult.Create(
                licence,
                routing);
        }
    }
}

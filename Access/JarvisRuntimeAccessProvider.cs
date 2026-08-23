using System;
using Softone;

namespace S1Jarvis.Access
{
    internal interface IJarvisRuntimeAccessProvider
    {
        JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode);
    }

    /// <summary>
    /// Transitional Step 12 provider. It preserves the current Nexus behaviour
    /// behind the new runtime-access boundary so the Jarvis shell no longer
    /// needs to care where licensing and AI-routing decisions originate.
    ///
    /// A later batch will replace the licensing side with Verilic while AI
    /// account routing remains a separate concern.
    /// </summary>
    internal sealed class LegacyNexusRuntimeAccessProvider : IJarvisRuntimeAccessProvider
    {
        private readonly Func<XSupport, string, AccessCheckResponse> _legacyCheck;

        public LegacyNexusRuntimeAccessProvider(
            Func<XSupport, string, AccessCheckResponse> legacyCheck)
        {
            _legacyCheck = legacyCheck ?? throw new ArgumentNullException(nameof(legacyCheck));
        }

        public JarvisRuntimeAccessResult Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null)
                throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));

            AccessCheckResponse legacy = _legacyCheck(xSupport, productCode);
            return JarvisRuntimeAccessResult.FromLegacy(legacy);
        }
    }
}

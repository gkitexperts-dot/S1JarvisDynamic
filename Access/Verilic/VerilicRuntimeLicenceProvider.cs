using System;
using Softone;
using S1Jarvis.Access;

namespace S1Jarvis.Access.Verilic
{
    internal interface IVerilicRuntimeLicenceProvider
    {
        JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode);
    }

    internal sealed class VerilicRuntimeLicenceProvider : IVerilicRuntimeLicenceProvider
    {
        public JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null || string.IsNullOrWhiteSpace(productCode))
                return JarvisLicenceAccessDecision.Deny(productCode, "verification_request_invalid");

            try
            {
                VerilicRuntimeAuthorization authorization =
                    VerilicRuntimeSession.AuthorizeAsync(xSupport, productCode)
                        .GetAwaiter().GetResult();

                return JarvisLicenceAccessDecision.FromAccessCheck(
                    productCode,
                    authorization == null ? null : authorization.Access);
            }
            catch (Exception ex)
            {
                try { S1Jarvis.Core.DebugLog.Log("[VERILIC-AUTH] access verification failed: " + ex.Message); }
                catch { }
                return JarvisLicenceAccessDecision.Deny(productCode, "verification_failed");
            }
        }
    }
}

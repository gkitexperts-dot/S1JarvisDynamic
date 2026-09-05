using System;
using Softone;

namespace S1Jarvis.Access.Verilic
{
    internal interface IVerilicRuntimeLicenceProvider
    {
        JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode);
    }

    /// <summary>
    /// Runtime authorization is deliberately workstation-independent:
    /// 1) authenticate ApiUsername/ApiValue from Soft1 cccParams and receive a
    ///    short-lived clientKey from Verilic;
    /// 2) call /access/check with that clientKey plus the active Soft1
    ///    Serial + Company + Branch + User + ToolName.
    /// </summary>
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

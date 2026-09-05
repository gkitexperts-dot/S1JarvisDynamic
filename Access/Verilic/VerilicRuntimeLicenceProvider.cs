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
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_request_invalid");

            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();
                VerilicProductRecognitionCredential credential =
                    configuration.ResolveProductCredential(productCode);

                var connection = xSupport.ConnectionInfo;
                if (connection == null)
                {
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "verification_request_invalid");
                }

                var request = new VerilicVerifyLicenceRequest
                {
                    ProductId = credential.ProductId,
                    ProductVersion = configuration.ProductVersion,
                    RuntimeContext = new VerilicRuntimeContext
                    {
                        Soft1Serial = connection.SerialNum == null
                            ? string.Empty
                            : connection.SerialNum.ToString(),
                        CompanyCode = connection.CompanyId.ToString(),
                        BranchCode = connection.BranchId.ToString(),
                        Soft1UserId = connection.UserId.ToString()
                    }
                };

                var authorizer = new VerilicRecognitionRequestAuthorizer(
                    credential.KeyId,
                    credential.Secret);
                var transport = new VerilicLicenceHttpTransport(
                    configuration.VerificationUri,
                    authorizer);

                VerilicVerifyLicenceResult verification = transport.Verify(request);
                return JarvisLicenceAccessDecision.FromVerilic(
                    productCode,
                    credential.ProductId,
                    verification);
            }
            catch (Exception ex)
            {
                try
                {
                    S1Jarvis.Core.DebugLog.Log(
                        "[LICENSING] NativeS1 verification failed for product=" +
                        (productCode ?? "-") + ": " +
                        ex.GetType().Name + " - " + ex.Message);
                }
                catch { }

                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_failed");
            }
        }
    }
}

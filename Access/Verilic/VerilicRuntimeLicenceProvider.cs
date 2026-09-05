using System;
using Softone;
using S1Jarvis.Access;

namespace S1Jarvis.Access.Verilic
{
    internal sealed class VerilicNativeS1VerificationSession
    {
        internal VerilicRuntimeConfiguration Configuration { get; set; }
        internal VerilicProductRecognitionCredential Credential { get; set; }
        internal VerilicVerifyLicenceResult Verification { get; set; }
        internal VerilicVerifyProductResult Product { get; set; }
    }

    internal interface IVerilicRuntimeLicenceProvider
    {
        JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode);
    }

    internal sealed class VerilicRuntimeLicenceProvider : IVerilicRuntimeLicenceProvider
    {
        private static readonly object CompatibilityCacheSync = new object();
        private static PendingVerification _pendingVerification;

        public JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null || string.IsNullOrWhiteSpace(productCode))
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_request_invalid");

            try
            {
                VerilicNativeS1VerificationSession session;
                if (!TryTakeBootVerification(xSupport, productCode, out session))
                    session = Verify(xSupport, productCode);

                return JarvisLicenceAccessDecision.FromVerilic(
                    productCode,
                    session.Credential.ProductId,
                    session.Verification);
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

        internal static VerilicNativeS1VerificationSession Verify(
            XSupport xSupport,
            string productCode)
        {
            if (xSupport == null)
                throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));

            VerilicRuntimeConfiguration configuration =
                VerilicRuntimeConfiguration.Load();
            VerilicProductRecognitionCredential credential =
                configuration.ResolveProductCredential(productCode);

            var connection = xSupport.ConnectionInfo;
            if (connection == null)
                throw new InvalidOperationException("Soft1 connection identity is unavailable.");

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
            if (verification == null)
                throw new InvalidOperationException("verification_response_invalid");

            var session = new VerilicNativeS1VerificationSession
            {
                Configuration = configuration,
                Credential = credential,
                Verification = verification,
                Product = verification.FindRequestedProduct(credential.ProductId)
            };

            RememberBootVerification(xSupport, productCode, session);
            return session;
        }

        private static void RememberBootVerification(
            XSupport xSupport,
            string productCode,
            VerilicNativeS1VerificationSession session)
        {
            var connection = xSupport == null ? null : xSupport.ConnectionInfo;
            if (connection == null || session == null)
                return;

            var pending = new PendingVerification
            {
                ProductCode = productCode == null ? null : productCode.Trim(),
                Soft1Serial = connection.SerialNum == null ? string.Empty : connection.SerialNum.ToString(),
                CompanyCode = connection.CompanyId.ToString(),
                BranchCode = connection.BranchId.ToString(),
                Soft1UserId = connection.UserId.ToString(),
                Session = session
            };

            lock (CompatibilityCacheSync)
                _pendingVerification = pending;
        }

        private static bool TryTakeBootVerification(
            XSupport xSupport,
            string productCode,
            out VerilicNativeS1VerificationSession session)
        {
            session = null;
            var connection = xSupport == null ? null : xSupport.ConnectionInfo;
            if (connection == null)
                return false;

            lock (CompatibilityCacheSync)
            {
                PendingVerification pending = _pendingVerification;
                if (pending == null)
                    return false;

                bool matches =
                    string.Equals(pending.ProductCode, productCode == null ? null : productCode.Trim(), StringComparison.Ordinal) &&
                    string.Equals(pending.Soft1Serial, connection.SerialNum == null ? string.Empty : connection.SerialNum.ToString(), StringComparison.Ordinal) &&
                    string.Equals(pending.CompanyCode, connection.CompanyId.ToString(), StringComparison.Ordinal) &&
                    string.Equals(pending.BranchCode, connection.BranchId.ToString(), StringComparison.Ordinal) &&
                    string.Equals(pending.Soft1UserId, connection.UserId.ToString(), StringComparison.Ordinal);

                if (!matches)
                    return false;

                session = pending.Session;
                _pendingVerification = null;
            }

            try
            {
                S1Jarvis.Core.DebugLog.Log(
                    "[LICENSING] reused boot /verify result for compatibility access check; product=" +
                    (productCode ?? "-"));
            }
            catch { }

            return session != null;
        }

        private sealed class PendingVerification
        {
            internal string ProductCode { get; set; }
            internal string Soft1Serial { get; set; }
            internal string CompanyCode { get; set; }
            internal string BranchCode { get; set; }
            internal string Soft1UserId { get; set; }
            internal VerilicNativeS1VerificationSession Session { get; set; }
        }
    }
}

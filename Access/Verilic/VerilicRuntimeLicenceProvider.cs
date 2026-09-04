using System;
using Softone;

namespace S1Jarvis.Access.Verilic
{
    internal interface IVerilicRuntimeLicenceProvider
    {
        JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode);
    }

    /// <summary>
    /// NativeS1 named-user verification. Authorization is evaluated for the
    /// active Soft1 Serial + Company + Branch + User at call time. There is no
    /// installation/device binding and no local allow cache: the server decision
    /// is authoritative for every startup/explicit verification.
    /// </summary>
    internal sealed class VerilicRuntimeLicenceProvider :
        IVerilicRuntimeLicenceProvider
    {
        private readonly Uri _verificationUri;
        private readonly string _productVersion;
        private readonly Func<string, string> _productIdResolver;
        private readonly string _recognitionKeyId;
        private readonly string _recognitionSecret;

        public VerilicRuntimeLicenceProvider(
            Uri verificationUri,
            string productVersion,
            Func<string, string> productIdResolver,
            string recognitionKeyId,
            string recognitionSecret)
        {
            if (verificationUri == null || !verificationUri.IsAbsoluteUri ||
                !string.Equals(verificationUri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "An absolute HTTPS Verilic verification URI is required.",
                    nameof(verificationUri));
            if (string.IsNullOrWhiteSpace(productVersion) || productVersion.Length > 200)
                throw new ArgumentException("A product version is required.", nameof(productVersion));
            if (productIdResolver == null)
                throw new ArgumentNullException(nameof(productIdResolver));
            if (string.IsNullOrWhiteSpace(recognitionKeyId))
                throw new ArgumentException("Recognition key id is required.", nameof(recognitionKeyId));
            if (string.IsNullOrWhiteSpace(recognitionSecret))
                throw new ArgumentException("Recognition secret is required.", nameof(recognitionSecret));

            _verificationUri = verificationUri;
            _productVersion = productVersion.Trim();
            _productIdResolver = productIdResolver;
            _recognitionKeyId = recognitionKeyId.Trim();
            _recognitionSecret = recognitionSecret.Trim();
        }

        public JarvisLicenceAccessDecision Check(XSupport xSupport, string productCode)
        {
            if (xSupport == null || string.IsNullOrWhiteSpace(productCode))
                return JarvisLicenceAccessDecision.Deny(
                    productCode, "verification_request_invalid");

            try
            {
                var info = xSupport.ConnectionInfo;
                if (info == null)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode, "verification_request_invalid");

                string productId = _productIdResolver(productCode);
                var request = new VerilicVerifyLicenceRequest
                {
                    ProductId = productId,
                    ProductVersion = _productVersion,
                    RuntimeContext = new VerilicRuntimeContext
                    {
                        Soft1Serial = info.SerialNum == null ? null : info.SerialNum.ToString(),
                        CompanyCode = info.CompanyId.ToString(),
                        BranchCode = info.BranchId.ToString(),
                        Soft1UserId = info.UserId.ToString()
                    }
                };

                IVerilicLicenceTransport transport = new VerilicLicenceHttpTransport(
                    _verificationUri,
                    new VerilicRecognitionRequestAuthorizer(
                        _recognitionKeyId,
                        _recognitionSecret));

                VerilicVerifyLicenceResult result = transport.Verify(request);
                return JarvisLicenceAccessDecision.FromVerilic(
                    productCode,
                    productId,
                    result);
            }
            catch
            {
                return JarvisLicenceAccessDecision.Deny(
                    productCode, "verification_failed");
            }
        }
    }
}

using System;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Access
{
    internal sealed class JarvisLicenceAccessDecision
    {
        public bool Allowed { get; private set; }
        public bool RuntimeReady { get; private set; }
        public string RuntimeReasonCode { get; private set; }
        public string ToolName { get; private set; }
        public string Message { get; private set; }
        public string ValidUntil { get; private set; }
        public string AgentAccountRef { get; private set; }

        // Retained only so older in-process callers compile while the new
        // contract-auth/access-check flow is rolled out.
        public VerilicVerifyLicenceResult Verification { get; private set; }
        public VerilicVerifyProductResult Product { get; private set; }

        public static JarvisLicenceAccessDecision FromAccessCheck(
            string productCode,
            AccessCheckResponse result)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));
            if (result == null)
                return Deny(productCode, "verification_response_invalid");

            return new JarvisLicenceAccessDecision
            {
                Allowed = result.Allowed,
                RuntimeReady = result.Allowed,
                RuntimeReasonCode = result.Allowed ? "runtime_ready" : "access_denied",
                ToolName = string.IsNullOrWhiteSpace(result.ToolName) ? productCode : result.ToolName,
                Message = result.Message,
                ValidUntil = result.ValidUntil,
                AgentAccountRef = result.AgentAccountRef
            };
        }

        // Compatibility parser for historical /verify DTOs. No current Jarvis
        // boot path calls this method.
        public static JarvisLicenceAccessDecision FromVerilic(
            string productCode,
            string requestedProductId,
            VerilicVerifyLicenceResult result)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException("Product code is required.", nameof(productCode));
            if (string.IsNullOrWhiteSpace(requestedProductId))
                throw new ArgumentException("Product id is required.", nameof(requestedProductId));
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            VerilicVerifyProductResult product = result.FindRequestedProduct(requestedProductId);
            bool allowed = result.Allowed &&
                string.Equals(result.ProductId, requestedProductId, StringComparison.Ordinal) &&
                product != null && product.Allowed;

            string message = null;
            if (!allowed)
                message = SafeReason(result.ReasonCode);
            else if (!product.RuntimeReady)
                message = string.IsNullOrWhiteSpace(product.RuntimeMessage)
                    ? "Το AI runtime δεν είναι διαθέσιμο. Επικοινωνήστε με τον διαχειριστή του Verilic."
                    : product.RuntimeMessage.Trim();

            DateTime? validUntil = product != null && product.ValidUntilUtc.HasValue
                ? product.ValidUntilUtc
                : result.ValidUntilUtc;

            return new JarvisLicenceAccessDecision
            {
                Allowed = allowed,
                RuntimeReady = allowed && product.RuntimeReady,
                RuntimeReasonCode = product == null ? null : product.RuntimeReasonCode,
                ToolName = productCode,
                Message = message,
                ValidUntil = validUntil.HasValue ? validUntil.Value.ToUniversalTime().ToString("o") : null,
                Verification = result,
                Product = product
            };
        }

        public static JarvisLicenceAccessDecision Deny(string productCode, string reasonCode)
        {
            return new JarvisLicenceAccessDecision
            {
                Allowed = false,
                RuntimeReady = false,
                RuntimeReasonCode = reasonCode,
                ToolName = productCode,
                Message = SafeReason(reasonCode)
            };
        }

        private static string SafeReason(string reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                return "Η άδεια χρήσης δεν είναι διαθέσιμη.";

            switch (reasonCode.Trim())
            {
                case "verification_request_invalid":
                    return "Δεν είναι διαθέσιμα τα στοιχεία επαλήθευσης του Jarvis.";
                case "verification_failed":
                case "runtime_auth_failed":
                    return "Ο Jarvis δεν μπόρεσε να πιστοποιηθεί στο Verilic.";
                case "contract_inactive":
                    return "Το συμβόλαιο Verilic δεν είναι ενεργό.";
                case "contract_expired":
                    return "Το συμβόλαιο Verilic έχει λήξει.";
                case "runtime_client_key_rejected":
                    return "Η προσωρινή συνεδρία Verilic έληξε. Απαιτείται νέα πιστοποίηση.";
                default:
                    return "Η άδεια χρήσης δεν είναι διαθέσιμη (" + reasonCode.Trim() + ").";
            }
        }
    }

    internal sealed class JarvisAgentRoutingDecision
    {
        public string ReasonCode { get; private set; }
        public bool Available => false;

        public static JarvisAgentRoutingDecision None(string reasonCode = null)
        {
            return new JarvisAgentRoutingDecision
            {
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim()
            };
        }
    }

    internal sealed class JarvisRuntimeAccessResult
    {
        public JarvisLicenceAccessDecision Licence { get; private set; }
        public JarvisAgentRoutingDecision AgentRouting { get; private set; }

        public static JarvisRuntimeAccessResult Create(
            JarvisLicenceAccessDecision licence,
            JarvisAgentRoutingDecision agentRouting)
        {
            if (licence == null)
                throw new ArgumentNullException(nameof(licence));

            return new JarvisRuntimeAccessResult
            {
                Licence = licence,
                AgentRouting = agentRouting ?? JarvisAgentRoutingDecision.None()
            };
        }

        public AccessCheckResponse ToLegacyCompatibilityResponse()
        {
            if (Licence == null)
                return AccessCheckResponse.Deny(JarvisProducts.Jarvis, "Η άδεια χρήσης δεν είναι διαθέσιμη.");

            bool baseJarvis = string.Equals(Licence.ToolName, JarvisProducts.Jarvis, StringComparison.Ordinal);
            bool operationallyAllowed = Licence.Allowed && (!baseJarvis || Licence.RuntimeReady);

            return new AccessCheckResponse
            {
                Allowed = operationallyAllowed,
                ToolName = Licence.ToolName,
                Message = Licence.Message,
                ValidUntil = Licence.ValidUntil,
                AgentAccountRef = Licence.AgentAccountRef,
                AiModel = null
            };
        }
    }
}

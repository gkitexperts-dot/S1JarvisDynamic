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
        public VerilicVerifyLicenceResult Verification { get; private set; }
        public VerilicVerifyProductResult Product { get; private set; }

        public static JarvisLicenceAccessDecision FromLegacy(AccessCheckResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            return new JarvisLicenceAccessDecision
            {
                Allowed = response.Allowed,
                RuntimeReady = response.Allowed,
                ToolName = response.ToolName,
                Message = response.Message,
                ValidUntil = response.ValidUntil
            };
        }

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

            VerilicVerifyProductResult product =
                result.FindRequestedProduct(requestedProductId);
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
                ValidUntil = validUntil.HasValue
                    ? validUntil.Value.ToUniversalTime().ToString("o")
                    : null,
                Verification = result,
                Product = product
            };
        }

        public static JarvisLicenceAccessDecision Deny(
            string productCode,
            string reasonCode)
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
                case "version_unsupported":
                    return "Η έκδοση του Jarvis δεν υποστηρίζεται πλέον. Απαιτείται αναβάθμιση.";
                case "product_recognition_failed":
                    return "Ο Jarvis δεν μπόρεσε να πιστοποιηθεί στο Verilic. Επικοινωνήστε με τον διαχειριστή.";
                case "rate_limited":
                    return "Το Verilic περιόρισε προσωρινά τον έλεγχο άδειας. Δοκιμάστε ξανά αργότερα.";
                case "verification_unavailable":
                case "verification_transport_failed":
                    return "Το Verilic δεν είναι διαθέσιμο αυτή τη στιγμή. Η εκκίνηση του Jarvis αποκλείστηκε για λόγους ασφαλείας.";
                default:
                    return "Η άδεια χρήσης δεν είναι διαθέσιμη (" + reasonCode.Trim() + ").";
            }
        }
    }

    internal sealed class JarvisAgentRoutingDecision
    {
        public string AgentAccountRef { get; private set; }
        public string Model { get; private set; }
        public string ReasonCode { get; private set; }
        public bool Available => !string.IsNullOrWhiteSpace(AgentAccountRef);

        public static JarvisAgentRoutingDecision None(string reasonCode = null)
        {
            return new JarvisAgentRoutingDecision
            {
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim()
            };
        }

        public static JarvisAgentRoutingDecision FromLegacy(AccessCheckResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));
            return new JarvisAgentRoutingDecision
            {
                AgentAccountRef = response.Allowed ? response.AgentAccountRef : null,
                Model = response.Allowed ? response.AiModel : null
            };
        }

        public static JarvisAgentRoutingDecision FromVerilic(VerilicAiRoutingResult result)
        {
            if (result == null || !result.Success ||
                string.IsNullOrWhiteSpace(result.AgentAccountRef))
                return None(result == null ? "routing_result_missing" : result.ReasonCode);

            return new JarvisAgentRoutingDecision
            {
                AgentAccountRef = result.AgentAccountRef.Trim(),
                Model = string.IsNullOrWhiteSpace(result.Model) ? null : result.Model.Trim(),
                ReasonCode = string.IsNullOrWhiteSpace(result.ReasonCode) ? null : result.ReasonCode.Trim()
            };
        }
    }

    internal sealed class JarvisRuntimeAccessResult
    {
        public JarvisLicenceAccessDecision Licence { get; private set; }
        public JarvisAgentRoutingDecision AgentRouting { get; private set; }

        public static JarvisRuntimeAccessResult FromLegacy(AccessCheckResponse response)
        {
            return new JarvisRuntimeAccessResult
            {
                Licence = JarvisLicenceAccessDecision.FromLegacy(response),
                AgentRouting = JarvisAgentRoutingDecision.FromLegacy(response)
            };
        }

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
                return AccessCheckResponse.Deny(
                    JarvisProducts.Jarvis, "Η άδεια χρήσης δεν είναι διαθέσιμη.");

            bool baseJarvis = string.Equals(
                Licence.ToolName, JarvisProducts.Jarvis, StringComparison.Ordinal);
            bool routingAvailable = AgentRouting != null && AgentRouting.Available;

            // NativeS1 AI readiness is an independent startup gate. For legacy
            // mode RuntimeReady mirrors Allowed; for NativeS1 the server decides.
            bool operationallyAllowed = Licence.Allowed &&
                (!baseJarvis || Licence.RuntimeReady) &&
                (!baseJarvis || routingAvailable || Licence.Verification != null);

            return new AccessCheckResponse
            {
                Allowed = operationallyAllowed,
                ToolName = Licence.ToolName,
                Message = Licence.Message,
                ValidUntil = Licence.ValidUntil,
                AgentAccountRef = operationallyAllowed && routingAvailable
                    ? AgentRouting.AgentAccountRef : null,
                AiModel = operationallyAllowed && routingAvailable
                    ? AgentRouting.Model : null
            };
        }
    }
}

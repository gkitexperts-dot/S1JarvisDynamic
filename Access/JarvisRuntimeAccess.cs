using System;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Access
{
    internal sealed class JarvisLicenceAccessDecision
    {
        public bool Allowed { get; private set; }
        public string ToolName { get; private set; }
        public string Message { get; private set; }
        public string ValidUntil { get; private set; }

        public static JarvisLicenceAccessDecision FromLegacy(AccessCheckResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            return new JarvisLicenceAccessDecision
            {
                Allowed = response.Allowed,
                ToolName = response.ToolName,
                Message = response.Message,
                ValidUntil = response.ValidUntil
            };
        }

        public static JarvisLicenceAccessDecision FromVerilic(
            string productCode,
            VerilicVerifyLicenceResult result)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                throw new ArgumentException(
                    "Product code is required.",
                    nameof(productCode));
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            return new JarvisLicenceAccessDecision
            {
                Allowed = result.Allowed,
                ToolName = productCode,
                Message = result.Allowed
                    ? null
                    : SafeReason(result.ReasonCode),
                ValidUntil = result.ValidUntilUtc.HasValue
                    ? result.ValidUntilUtc.Value.ToUniversalTime().ToString("o")
                    : null
            };
        }

        public static JarvisLicenceAccessDecision Deny(
            string productCode,
            string reasonCode)
        {
            return new JarvisLicenceAccessDecision
            {
                Allowed = false,
                ToolName = productCode,
                Message = SafeReason(reasonCode),
                ValidUntil = null
            };
        }

        private static string SafeReason(string reasonCode)
        {
            return string.IsNullOrWhiteSpace(reasonCode)
                ? "Η άδεια χρήσης δεν είναι διαθέσιμη."
                : "Η άδεια χρήσης δεν είναι διαθέσιμη (" + reasonCode.Trim() + ").";
        }
    }

    internal sealed class JarvisAgentRoutingDecision
    {
        public string AgentAccountRef { get; private set; }

        public bool Available => !string.IsNullOrWhiteSpace(AgentAccountRef);

        public static JarvisAgentRoutingDecision None()
        {
            return new JarvisAgentRoutingDecision();
        }

        public static JarvisAgentRoutingDecision FromLegacy(AccessCheckResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            return new JarvisAgentRoutingDecision
            {
                AgentAccountRef = response.Allowed
                    ? response.AgentAccountRef
                    : null
            };
        }
    }

    internal sealed class JarvisRuntimeAccessResult
    {
        public JarvisLicenceAccessDecision Licence { get; private set; }
        public JarvisAgentRoutingDecision AgentRouting { get; private set; }

        public static JarvisRuntimeAccessResult FromLegacy(AccessCheckResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

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
                    JarvisProducts.Jarvis,
                    "Η άδεια χρήσης δεν είναι διαθέσιμη.");

            return new AccessCheckResponse
            {
                Allowed = Licence.Allowed,
                ToolName = Licence.ToolName,
                Message = Licence.Message,
                ValidUntil = Licence.ValidUntil,
                AgentAccountRef = Licence.Allowed && AgentRouting != null
                    ? AgentRouting.AgentAccountRef
                    : null
            };
        }
    }
}

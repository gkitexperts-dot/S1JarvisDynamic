using System;

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
    }

    internal sealed class JarvisAgentRoutingDecision
    {
        public string AgentAccountRef { get; private set; }

        public bool Available => !string.IsNullOrWhiteSpace(AgentAccountRef);

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
    }
}

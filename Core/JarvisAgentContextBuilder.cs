using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Single context-policy entry point for every logical-agent dispatch.
    /// It derives tools/domains from the authoritative task/tool registries,
    /// then resolves the applicable central policies. Executors must not carry
    /// their own lists of policy scopes.
    ///
    /// Knowledge/schema companions will be composed through this same boundary;
    /// policy resolution is centralized here first.
    /// </summary>
    internal static class JarvisAgentContextBuilder
    {
        internal static string BuildTrainingPolicyContext(string agentName, string taskType)
        {
            string[] tools;
            string[] domains;
            ResolveTaskScope(taskType, out tools, out domains);
            return JarvisPolicyRegistry.BuildTrainingContext(agentName, taskType, domains, tools);
        }

        internal static string BuildDeterministicPolicyIds(string agentName, string taskType)
        {
            string[] tools;
            string[] domains;
            ResolveTaskScope(taskType, out tools, out domains);
            return JarvisPolicyRegistry.BuildDeterministicPolicyContext(agentName, taskType, domains, tools);
        }

        internal static string BuildDecompositionPolicyContext()
        {
            return JarvisPolicyRegistry.BuildTrainingContext(
                "Jarvis", "__decomposition", new string[0], new string[0]);
        }

        internal static string BuildPresentationPolicyContext()
        {
            return JarvisPolicyRegistry.BuildTrainingContext(
                "Jarvis", "__presentation", new string[0], new string[0]);
        }

        internal static void ResolveTaskScope(string taskType, out string[] tools, out string[] domains)
        {
            JarvisTaskDescriptor task = JarvisTaskRegistry.Find(taskType);
            tools = task == null
                ? new string[0]
                : (task.Tools ?? new string[0])
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            var resolvedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (task != null && !string.IsNullOrWhiteSpace(task.Capability))
                resolvedDomains.Add(task.Capability.Trim());

            foreach (string toolName in tools)
            {
                JarvisToolDescriptor tool = JarvisToolRegistry.Find(toolName);
                if (tool != null && !string.IsNullOrWhiteSpace(tool.Domain))
                    resolvedDomains.Add(tool.Domain.Trim());
            }

            domains = resolvedDomains.ToArray();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Single policy-context entry point for every logical-agent dispatch and
    /// Jarvis internal planning/presentation stage. It derives scope only from
    /// authoritative registries; callers never maintain local policy lists.
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
            return BuildInternalStagePolicyContext("__decomposition");
        }

        internal static string BuildPlanningPolicyContext()
        {
            return BuildInternalStagePolicyContext("__planning");
        }

        internal static string BuildPresentationPolicyContext()
        {
            return BuildInternalStagePolicyContext("__presentation");
        }

        internal static string BuildHelpPolicyContext()
        {
            return JarvisPolicyRegistry.BuildTrainingContext(
                "Sage", "__help", new string[] { "Help" }, new string[0]);
        }

        /// <summary>
        /// Resolves the authoritative training policy context for any AI request.
        /// This is intentionally agent-generic: Atlas, Forge, Compass, Echo,
        /// Sprint, Scout, Sage and Jarvis all pass through the same resolver.
        /// When the request maps to several registered tasks (legacy multi-tool
        /// mode), their applicable policy sets are unioned and de-duplicated.
        /// </summary>
        internal static string BuildPolicyContextForRequest(string agentName, string providerRequestJson)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return string.Empty;

            JObject request = TryParse(providerRequestJson);
            string[] requestTools = ReadToolNames(request);
            string explicitTask = ReadExplicitTask(request);

            if (!string.IsNullOrWhiteSpace(explicitTask) && JarvisTaskRegistry.Find(explicitTask) != null)
                return BuildTrainingPolicyContext(agentName, explicitTask);

            JarvisTaskDescriptor[] candidateTasks = JarvisTaskRegistry.ForAgent(agentName)
                .Where(task => task != null &&
                    (requestTools.Length == 0 || (task.Tools ?? new string[0]).Any(t => requestTools.Contains(t, StringComparer.OrdinalIgnoreCase))))
                .ToArray();

            if (candidateTasks.Length == 1)
                return BuildTrainingPolicyContext(agentName, candidateTasks[0].TaskType);

            var policies = new Dictionary<string, JarvisPolicyDescriptor>(StringComparer.OrdinalIgnoreCase);

            foreach (JarvisPolicyDescriptor policy in JarvisPolicyRegistry.Resolve(
                agentName, null, ResolveDomains(requestTools), requestTools, null))
            {
                if (policy.Enforcement == JarvisPolicyEnforcement.Training ||
                    policy.Enforcement == JarvisPolicyEnforcement.Both)
                    policies[policy.PolicyId] = policy;
            }

            foreach (JarvisTaskDescriptor task in candidateTasks)
            {
                string[] tools;
                string[] domains;
                ResolveTaskScope(task.TaskType, out tools, out domains);
                foreach (JarvisPolicyDescriptor policy in JarvisPolicyRegistry.Resolve(
                    agentName, task.TaskType, domains, tools, null))
                {
                    if (policy.Enforcement == JarvisPolicyEnforcement.Training ||
                        policy.Enforcement == JarvisPolicyEnforcement.Both)
                        policies[policy.PolicyId] = policy;
                }
            }

            // HelpLookup is the registered Sage task while __help is the
            // internal conversational stage. They intentionally share policy.
            if (string.Equals(agentName, "Sage", StringComparison.OrdinalIgnoreCase) &&
                candidateTasks.Any(x => string.Equals(x.TaskType, "HelpLookup", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (JarvisPolicyDescriptor policy in JarvisPolicyRegistry.Resolve(
                    "Sage", "__help", new string[] { "Help" }, new string[0], null))
                {
                    if (policy.Enforcement == JarvisPolicyEnforcement.Training ||
                        policy.Enforcement == JarvisPolicyEnforcement.Both)
                        policies[policy.PolicyId] = policy;
                }
            }

            return FormatTrainingContext(policies.Values);
        }

        /// <summary>
        /// Every registered task must receive centralized training and
        /// deterministic policy context. This is deliberately checked for all
        /// owners, not only currently promoted executors.
        /// </summary>
        internal static string[] ValidateCoverage()
        {
            var issues = new List<string>();
            foreach (JarvisTaskDescriptor task in JarvisTaskRegistry.AllTasks)
            {
                if (task == null) continue;

                string training = BuildTrainingPolicyContext(task.OwnerAgent, task.TaskType);
                string deterministic = BuildDeterministicPolicyIds(task.OwnerAgent, task.TaskType);
                if (string.IsNullOrWhiteSpace(training))
                    issues.Add("Registered task has no centralized training policy context: " + task.TaskType + " owner=" + task.OwnerAgent);
                if (string.IsNullOrWhiteSpace(deterministic))
                    issues.Add("Registered task has no centralized deterministic policy context: " + task.TaskType + " owner=" + task.OwnerAgent);
            }
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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

        private static string BuildInternalStagePolicyContext(string taskType)
        {
            return JarvisPolicyRegistry.BuildTrainingContext(
                "Jarvis", taskType, new string[0], new string[0]);
        }

        private static JObject TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JObject.Parse(json); }
            catch { return null; }
        }

        private static string ReadExplicitTask(JObject request)
        {
            if (request == null) return null;
            JObject metadata = request["metadata"] as JObject;
            string value = metadata == null ? null : (string)metadata["jarvis_task"];
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string[] ReadToolNames(JObject request)
        {
            if (request == null) return new string[0];
            JArray tools = request["tools"] as JArray;
            if (tools == null) return new string[0];
            return tools.OfType<JObject>()
                .Select(x => (string)x["name"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string[] ResolveDomains(IEnumerable<string> tools)
        {
            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string toolName in tools ?? new string[0])
            {
                JarvisToolDescriptor tool = JarvisToolRegistry.Find(toolName);
                if (tool != null && !string.IsNullOrWhiteSpace(tool.Domain))
                    domains.Add(tool.Domain.Trim());
            }
            return domains.ToArray();
        }

        private static string FormatTrainingContext(IEnumerable<JarvisPolicyDescriptor> policies)
        {
            JarvisPolicyDescriptor[] applicable = (policies ?? Enumerable.Empty<JarvisPolicyDescriptor>())
                .Where(x => x != null)
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.PolicyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (applicable.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[JARVIS_POLICY_CONTEXT]");
            foreach (JarvisPolicyDescriptor policy in applicable)
                sb.Append("- ").Append(policy.PolicyId).Append(": ").AppendLine(policy.Rule);
            return sb.ToString().TrimEnd();
        }
    }
}

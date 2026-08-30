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
        /// Resolves authoritative policy for any outbound AI request. In modern
        /// orchestration an explicit registered task is enough. In legacy
        /// multi-tool requests we inspect every attached registered tool and
        /// union the policies of ALL task owners represented by that surface,
        /// so a request spanning Forge+Echo+Compass, for example, cannot lose
        /// policies merely because one model/agent was chosen for transport.
        /// </summary>
        internal static string BuildPolicyContextForRequest(string agentName, string providerRequestJson)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return string.Empty;

            JObject request = TryParse(providerRequestJson);
            string[] requestTools = ReadToolNames(request);
            string explicitTask = ReadExplicitTask(request);

            if (!string.IsNullOrWhiteSpace(explicitTask))
            {
                JarvisTaskDescriptor registeredTask = JarvisTaskRegistry.Find(explicitTask);
                if (registeredTask != null)
                    return BuildTrainingPolicyContext(registeredTask.OwnerAgent, registeredTask.TaskType);
            }

            JarvisTaskDescriptor[] candidateTasks = requestTools.Length == 0
                ? JarvisTaskRegistry.ForAgent(agentName).Where(x => x != null).ToArray()
                : JarvisTaskRegistry.AllTasks
                    .Where(task => task != null &&
                        (task.Tools ?? new string[0]).Any(t => requestTools.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    .ToArray();

            var policies = new Dictionary<string, JarvisPolicyDescriptor>(StringComparer.OrdinalIgnoreCase);

            // Global rules applicable to the physical request surface.
            AddTrainingPolicies(
                policies,
                JarvisPolicyRegistry.Resolve(agentName, null, ResolveDomains(requestTools), requestTools, null));

            // Task/owner/domain/tool rules for every registered task represented
            // by the attached tools, not only the transport-selected agent.
            foreach (JarvisTaskDescriptor task in candidateTasks)
            {
                string[] tools;
                string[] domains;
                ResolveTaskScope(task.TaskType, out tools, out domains);
                AddTrainingPolicies(
                    policies,
                    JarvisPolicyRegistry.Resolve(task.OwnerAgent, task.TaskType, domains, tools, null));
            }

            // HelpLookup is the registered Sage task while __help is the
            // internal conversational stage. They intentionally share policy.
            if (candidateTasks.Any(x => string.Equals(x.TaskType, "HelpLookup", StringComparison.OrdinalIgnoreCase)))
            {
                AddTrainingPolicies(
                    policies,
                    JarvisPolicyRegistry.Resolve("Sage", "__help", new string[] { "Help" }, new string[0], null));
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

        private static void AddTrainingPolicies(
            IDictionary<string, JarvisPolicyDescriptor> destination,
            IEnumerable<JarvisPolicyDescriptor> source)
        {
            if (destination == null) return;
            foreach (JarvisPolicyDescriptor policy in source ?? Enumerable.Empty<JarvisPolicyDescriptor>())
            {
                if (policy == null) continue;
                if (policy.Enforcement == JarvisPolicyEnforcement.Training ||
                    policy.Enforcement == JarvisPolicyEnforcement.Both)
                    destination[policy.PolicyId] = policy;
            }
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

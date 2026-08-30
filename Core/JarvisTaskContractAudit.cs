using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Validates the business task catalog against authoritative tool and policy contracts.
    /// Task metadata describes business outcomes. Native prerequisites/outputs live in
    /// JarvisToolRegistry; behavioral rules live in JarvisPolicyRegistry. Neither may
    /// drift into a second truth inside executor prompts.
    /// </summary>
    internal static class JarvisTaskContractAudit
    {
        internal static string[] Validate()
        {
            var issues = new List<string>();

            foreach (string inventoryIssue in JarvisToolRegistry.ValidateInventory())
            {
                // A routing capability may intentionally be semantic-only (for example,
                // a help/knowledge route) and therefore need no tool advertising the exact
                // same capability. It is valid only when the capability resolves through
                // the canonical routing registry to an agent that actually owns tools.
                if (!IsValidSemanticRouteWithoutDedicatedTool(inventoryIssue))
                    issues.Add(inventoryIssue);
            }
            issues.AddRange(JarvisPolicyRegistry.ValidateInventory());

            foreach (JarvisTaskDescriptor task in JarvisTaskRegistry.AllTasks)
            {
                ValidateNames(task, issues);
                ValidateOutputs(task, issues);
                ValidateDependencyContracts(task, issues);
                ValidateTerminalToolContract(task, issues);
            }

            return issues
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static JarvisToolDescriptor FindTerminalStateChangingTool(JarvisTaskDescriptor task)
        {
            JarvisToolDescriptor[] changing = GetStateChangingTools(task);
            return changing.Length == 1 ? changing[0] : null;
        }

        private static bool IsValidSemanticRouteWithoutDedicatedTool(string issue)
        {
            const string prefix = "Route capability without registered tool:";
            if (string.IsNullOrWhiteSpace(issue) ||
                !issue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string capability = issue.Substring(prefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(capability)) return false;

            string owner = JarvisCapabilityResolver.ResolveOwner(capability);
            if (string.IsNullOrWhiteSpace(owner)) return false;

            return JarvisToolRegistry.ForAgent(owner).Any();
        }

        private static void ValidateTerminalToolContract(JarvisTaskDescriptor task, List<string> issues)
        {
            bool stateChangingTask = task.Operation == JarvisTaskOperation.Write ||
                                     task.Operation == JarvisTaskOperation.ExternalAction;

            if (!stateChangingTask)
                return;

            JarvisToolDescriptor[] changing = GetStateChangingTools(task);

            if (changing.Length != 1)
            {
                issues.Add("State-changing atomic task must expose exactly one terminal state-changing tool: " +
                    task.TaskType + " count=" + changing.Length);
                return;
            }

            JarvisToolPrerequisiteDescriptor contract = JarvisToolRegistry.FindPrerequisites(changing[0].Name);
            if (contract == null)
            {
                issues.Add("Terminal tool has no authoritative prerequisite contract: " +
                    task.TaskType + " -> " + changing[0].Name);
                return;
            }

            foreach (string produced in Normalize(contract.Produces))
            {
                if (!(task.Produces ?? new string[0]).Contains(produced, StringComparer.OrdinalIgnoreCase))
                    issues.Add("Task output contract does not include terminal tool output: " +
                        task.TaskType + " -> " + changing[0].Name + "." + produced);
            }
        }

        private static JarvisToolDescriptor[] GetStateChangingTools(JarvisTaskDescriptor task)
        {
            if (task == null) return new JarvisToolDescriptor[0];
            return (task.Tools ?? new string[0])
                .Select(JarvisToolRegistry.Find)
                .Where(x => x != null && x.Operation != JarvisToolOperation.Read)
                .ToArray();
        }

        private static void ValidateNames(JarvisTaskDescriptor task, List<string> issues)
        {
            string[] required = Normalize(task.RequiredInputs);
            string[] optional = Normalize(task.OptionalInputs);
            string[] produces = Normalize(task.Produces);

            AddDuplicateIssues(task.TaskType, "required input", task.RequiredInputs, issues);
            AddDuplicateIssues(task.TaskType, "optional input", task.OptionalInputs, issues);
            AddDuplicateIssues(task.TaskType, "output", task.Produces, issues);

            foreach (string name in required.Intersect(optional, StringComparer.OrdinalIgnoreCase))
                issues.Add("Task input is both required and optional: " + task.TaskType + " -> " + name);

            foreach (string name in required.Concat(optional))
            {
                if (!IsContractName(name))
                    issues.Add("Task has invalid input contract name: " + task.TaskType + " -> " + name);
            }

            foreach (string name in produces)
            {
                if (!IsContractName(name))
                    issues.Add("Task has invalid output contract name: " + task.TaskType + " -> " + name);
            }
        }

        private static void ValidateOutputs(JarvisTaskDescriptor task, List<string> issues)
        {
            if (task.Produces == null || task.Produces.Length == 0)
                issues.Add("Task has no declared outputs: " + task.TaskType);
        }

        private static void ValidateDependencyContracts(JarvisTaskDescriptor task, List<string> issues)
        {
            foreach (string capability in Normalize(task.DependencyCapabilities))
            {
                JarvisTaskDescriptor[] producers = JarvisTaskRegistry.ForCapability(capability).ToArray();
                if (producers.Length == 0)
                {
                    if (!JarvisToolRegistry.ForCapability(capability).Any())
                        issues.Add("Task dependency has no task/tool producer: " + task.TaskType + " -> " + capability);
                    continue;
                }

                if (producers.All(x => x.Produces == null || x.Produces.Length == 0))
                    issues.Add("Task dependency producers declare no outputs: " + task.TaskType + " -> " + capability);
            }
        }

        private static void AddDuplicateIssues(
            string taskType,
            string label,
            IEnumerable<string> values,
            List<string> issues)
        {
            if (values == null) return;

            foreach (IGrouping<string, string> group in values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 1)
                    issues.Add("Duplicate task " + label + ": " + taskType + " -> " + group.Key);
            }
        }

        private static string[] Normalize(IEnumerable<string> values)
        {
            return (values ?? new string[0])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray();
        }

        private static bool IsContractName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return false;
            }
            return true;
        }
    }
}

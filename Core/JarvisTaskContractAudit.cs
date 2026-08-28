using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Validates the semantic contract exposed to the orchestration planner.
    ///
    /// This deliberately audits business-level task inputs/outputs rather than
    /// pretending that task contracts are identical to one tool JSON schema.
    /// A task may use several tools and may translate a business input into a
    /// lower-level tool argument during execution.
    ///
    /// The goal is to catch metadata that would make planning impossible or
    /// unsafe before a provider call is allowed to consume TASK_CATALOG.
    /// </summary>
    internal static class JarvisTaskContractAudit
    {
        internal static string[] Validate()
        {
            var issues = new List<string>();

            foreach (JarvisTaskDescriptor task in JarvisTaskRegistry.AllTasks)
            {
                ValidateNames(task, issues);
                ValidateOutputs(task, issues);
                ValidateDependencyContracts(task, issues);
            }

            return issues
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
                    // Some dependencies are helper capabilities supplied by tools
                    // inside the same atomic task (for example Contacts). Those
                    // are valid when the capability resolves in the tool registry.
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
            return (values ?? Enumerable.Empty<string>())
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

using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Cross-checks orchestration task metadata against canonical tool/routing,
    /// policy, knowledge and durable-state invariants before semantic planning is
    /// allowed to become authoritative.
    /// </summary>
    internal static class JarvisTaskRegistryAudit
    {
        internal static string[] Validate()
        {
            var issues = new List<string>();
            issues.AddRange(JarvisTaskRegistry.ValidateAgainstToolRegistry());
            issues.AddRange(JarvisTaskContractAudit.Validate());
            issues.AddRange(JarvisPolicyRegistry.ValidateInventory());
            issues.AddRange(JarvisAgentContextBuilder.ValidateCoverage());
            issues.AddRange(JarvisArchitectureRegressionAudit.Validate());

            foreach (JarvisTaskDescriptor task in JarvisTaskRegistry.AllTasks)
            {
                ValidateCapabilityRoute(task, issues);
                ValidateToolOwnership(task, issues);
                ValidateToolCapabilities(task, issues);
                ValidateAtomicBoundary(task, issues);
                ValidateDependencies(task, issues);
            }

            return issues
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void ValidateCapabilityRoute(JarvisTaskDescriptor task, List<string> issues)
        {
            string routedOwner = JarvisCapabilityResolver.ResolveOwner(task.Capability);
            if (string.IsNullOrWhiteSpace(routedOwner))
            {
                string reason = JarvisCapabilityResolver.IsAmbiguous(task.Capability)
                    ? "ambiguous owner"
                    : "no owner";
                issues.Add("Task capability cannot resolve a canonical owner: " + task.TaskType +
                    " -> " + task.Capability + " (" + reason + ")");
                return;
            }

            if (!string.Equals(routedOwner, task.OwnerAgent, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("Task owner differs from capability route: " + task.TaskType +
                    " capability=" + task.Capability +
                    " taskOwner=" + task.OwnerAgent +
                    " routedOwner=" + routedOwner);
            }
        }

        private static void ValidateToolOwnership(JarvisTaskDescriptor task, List<string> issues)
        {
            foreach (string toolName in task.Tools)
            {
                JarvisToolDescriptor tool = JarvisToolRegistry.Find(toolName);
                if (tool == null)
                    continue;

                bool allowed = tool.AllowedAgents.Any(x => string.Equals(
                    x,
                    task.OwnerAgent,
                    StringComparison.OrdinalIgnoreCase));

                if (!allowed)
                {
                    issues.Add("Task owner is not allowed to use tool: " + task.TaskType +
                        " owner=" + task.OwnerAgent + " tool=" + toolName);
                }
            }
        }

        private static void ValidateToolCapabilities(JarvisTaskDescriptor task, List<string> issues)
        {
            bool anyToolMatchesPrimaryCapability = task.Tools
                .Select(JarvisToolRegistry.Find)
                .Where(x => x != null)
                .Any(tool => tool.Capabilities.Any(capability => string.Equals(
                    capability,
                    task.Capability,
                    StringComparison.OrdinalIgnoreCase)));

            if (anyToolMatchesPrimaryCapability)
                return;

            string explicitOwner = JarvisToolRegistry.ResolveOwnerForCapability(task.Capability);
            if (string.IsNullOrWhiteSpace(explicitOwner))
            {
                issues.Add("No task tool advertises the task primary capability: " +
                    task.TaskType + " -> " + task.Capability);
            }
        }

        private static void ValidateAtomicBoundary(JarvisTaskDescriptor task, List<string> issues)
        {
            JarvisToolDescriptor[] tools = task.Tools
                .Select(JarvisToolRegistry.Find)
                .Where(x => x != null)
                .ToArray();

            bool hasConfirmingTool = tools.Any(x => x.RequiresConfirmation);

            if (hasConfirmingTool && !task.RequiresConfirmation)
                issues.Add("Task exposes a confirming tool without task confirmation: " + task.TaskType);

            if (task.Operation == JarvisTaskOperation.Read && hasConfirmingTool)
                issues.Add("Read task contains a state-changing tool: " + task.TaskType);

            if (task.Operation == JarvisTaskOperation.Mixed)
                issues.Add("Mixed-operation task must be split into atomic read/write tasks: " + task.TaskType);
        }

        private static void ValidateDependencies(JarvisTaskDescriptor task, List<string> issues)
        {
            foreach (string capability in task.DependencyCapabilities)
            {
                if (string.IsNullOrWhiteSpace(capability))
                    continue;

                bool taskProducerExists = JarvisTaskRegistry.AllTasks.Any(x =>
                    string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase));
                bool ownerResolves = !string.IsNullOrWhiteSpace(
                    JarvisCapabilityResolver.ResolveOwner(capability));
                bool toolCapabilityExists = JarvisToolRegistry.ForCapability(capability).Any();

                if (!taskProducerExists && !ownerResolves && !toolCapabilityExists)
                {
                    issues.Add("Task dependency capability is unknown to both registries: " +
                        task.TaskType + " -> " + capability);
                }
            }
        }
    }
}

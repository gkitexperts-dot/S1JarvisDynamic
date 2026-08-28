using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Phase 2 planner baseline.
    /// Builds a candidate orchestration plan from the declarative task registry.
    /// It is intentionally deterministic and side-effect free: no provider call,
    /// no tool execution and no change to the mature Main Chat routing path yet.
    /// </summary>
    internal static class JarvisPlanner
    {
        internal static JarvisPlan BuildCandidatePlan(string userPrompt)
        {
            var plan = new JarvisPlan(Guid.NewGuid().ToString("N"), userPrompt ?? string.Empty);
            if (string.IsNullOrWhiteSpace(userPrompt))
                return plan;

            string normalized = userPrompt.Trim();
            List<JarvisTaskDescriptor> matches = JarvisTaskRegistry.AllTasks
                .Where(x => MatchesIntent(x, normalized))
                .ToList();

            foreach (JarvisTaskDescriptor descriptor in matches)
                plan.AddTask(JarvisPlannedTask.FromDescriptor(descriptor));

            BindDependencies(plan);
            return plan;
        }

        private static bool MatchesIntent(JarvisTaskDescriptor descriptor, string prompt)
        {
            if (descriptor == null || string.IsNullOrWhiteSpace(prompt))
                return false;

            return descriptor.IntentHints.Any(hint =>
                !string.IsNullOrWhiteSpace(hint) &&
                prompt.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void BindDependencies(JarvisPlan plan)
        {
            if (plan == null || plan.Tasks.Count < 2)
                return;

            foreach (JarvisPlannedTask consumer in plan.Tasks)
            {
                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(consumer.TaskType);
                if (descriptor == null || descriptor.DependencyCapabilities.Length == 0)
                    continue;

                foreach (string capability in descriptor.DependencyCapabilities)
                {
                    JarvisPlannedTask producer = plan.Tasks.FirstOrDefault(x =>
                        !string.Equals(x.TaskId, consumer.TaskId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Capability, capability, StringComparison.OrdinalIgnoreCase));

                    if (producer != null)
                        consumer.AddDependency(producer.TaskId);
                }
            }
        }
    }
}

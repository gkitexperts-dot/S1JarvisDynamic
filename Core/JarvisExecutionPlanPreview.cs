using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisExecutionPlanReadiness
    {
        Invalid,
        NeedsLookupResolution,
        NeedsConfirmation,
        StructurallyReady
    }

    internal sealed class JarvisExecutionPlanEntry
    {
        public JarvisExecutionPlanEntry()
        {
            DependsOnObjectIds = new List<string>();
            LookupInputs = new List<string>();
            BoundInputs = new List<string>();
        }

        public int Wave { get; set; }
        public int Ordinal { get; set; }
        public string ObjectId { get; set; }
        public string TaskType { get; set; }
        public string OwnerAgent { get; set; }
        public JarvisTaskExecutionPolicy ExecutionPolicy { get; set; }
        public bool RequiresConfirmation { get; set; }
        public List<string> DependsOnObjectIds { get; private set; }
        public List<string> LookupInputs { get; private set; }
        public List<string> BoundInputs { get; private set; }
    }

    internal sealed class JarvisExecutionPlanPreview
    {
        public JarvisExecutionPlanPreview()
        {
            Entries = new List<JarvisExecutionPlanEntry>();
            ValidationIssues = new List<string>();
        }

        public List<JarvisExecutionPlanEntry> Entries { get; private set; }
        public List<string> ValidationIssues { get; private set; }

        public bool IsValid
        {
            get { return ValidationIssues.Count == 0; }
        }

        public bool HasLookupWork
        {
            get { return Entries.Any(x => x != null && x.LookupInputs.Count > 0); }
        }

        public bool RequiresConfirmation
        {
            get { return Entries.Any(x => x != null && x.RequiresConfirmation); }
        }

        public JarvisExecutionPlanReadiness Readiness
        {
            get
            {
                if (!IsValid)
                    return JarvisExecutionPlanReadiness.Invalid;
                if (HasLookupWork)
                    return JarvisExecutionPlanReadiness.NeedsLookupResolution;
                if (RequiresConfirmation)
                    return JarvisExecutionPlanReadiness.NeedsConfirmation;
                return JarvisExecutionPlanReadiness.StructurallyReady;
            }
        }
    }

    /// <summary>
    /// Final side-effect-free validation stage before any future execution
    /// engine is allowed to consume the new orchestration path.
    ///
    /// Responsibilities:
    /// - validate every node and prerequisite resolution state;
    /// - require exactly one local dependency binding for every
    ///   DependencyPending input;
    /// - validate lookup definitions against the registered tool inventory;
    /// - validate graph edges and reject cycles/unknown nodes;
    /// - create deterministic topological execution waves;
    /// - never execute lookups, tools, confirmations or business actions.
    ///
    /// Parallelism is deliberately conservative: only ParallelSafe tasks may
    /// share a wave. DependsOnInputs and Sequential tasks are isolated even when
    /// they have no graph dependency, so later live execution cannot accidentally
    /// gain concurrency merely because the preview layer was permissive.
    /// </summary>
    internal static class JarvisWholePlanValidator
    {
        internal static JarvisExecutionPlanPreview BuildPreview(JarvisDependencyGraph graph)
        {
            var preview = new JarvisExecutionPlanPreview();
            if (graph == null)
            {
                preview.ValidationIssues.Add("Dependency graph is null.");
                return preview;
            }

            foreach (string issue in graph.ValidationIssues ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(issue))
                    preview.ValidationIssues.Add("Dependency graph: " + issue);
            }

            JarvisValidatedTaskNode[] nodes = (graph.Nodes ?? new List<JarvisValidatedTaskNode>())
                .Where(x => x != null)
                .ToArray();

            ValidateWholePlan(nodes, graph, preview.ValidationIssues);
            if (preview.ValidationIssues.Count > 0)
                return preview;

            BuildExecutionWaves(nodes, graph, preview);
            ValidatePreview(preview, nodes, graph);
            return preview;
        }

        private static void ValidateWholePlan(
            JarvisValidatedTaskNode[] nodes,
            JarvisDependencyGraph graph,
            List<string> issues)
        {
            if (nodes == null || nodes.Length == 0)
            {
                issues.Add("Whole plan contains no validated task nodes.");
                return;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JarvisValidatedTaskNode node in nodes)
            {
                if (string.IsNullOrWhiteSpace(node.ObjectId))
                {
                    issues.Add("Whole plan contains a task node without ObjectId.");
                    continue;
                }

                if (!ids.Add(node.ObjectId))
                    issues.Add("Duplicate ObjectId in whole plan: " + node.ObjectId);

                JarvisTaskDescriptor registered = JarvisTaskRegistry.Find(node.TaskType);
                if (registered == null || node.Descriptor == null)
                {
                    issues.Add("Object " + node.ObjectId + " has no registered task descriptor.");
                    continue;
                }

                if (!string.Equals(registered.TaskType, node.Descriptor.TaskType, StringComparison.OrdinalIgnoreCase))
                    issues.Add("Task descriptor mismatch for object " + node.ObjectId + ".");

                if (!node.IsStructurallyValid)
                    issues.Add("Object " + node.ObjectId + " is not structurally valid.");

                if (node.NeedsUserInput)
                    issues.Add("Object " + node.ObjectId + " still needs user input.");

                ValidatePrerequisites(node, graph, issues);
            }

            foreach (JarvisDependencyBinding binding in graph.Bindings ?? new List<JarvisDependencyBinding>())
            {
                if (binding == null)
                {
                    issues.Add("Whole plan contains a null dependency binding.");
                    continue;
                }

                if (!ids.Contains(binding.SourceObjectId ?? string.Empty))
                    issues.Add("Dependency source object does not exist: " + (binding.SourceObjectId ?? "<null>"));
                if (!ids.Contains(binding.TargetObjectId ?? string.Empty))
                    issues.Add("Dependency target object does not exist: " + (binding.TargetObjectId ?? "<null>"));
            }
        }

        private static void ValidatePrerequisites(
            JarvisValidatedTaskNode node,
            JarvisDependencyGraph graph,
            List<string> issues)
        {
            foreach (JarvisPrerequisiteResolutionItem prerequisite in node.Prerequisites)
            {
                if (prerequisite == null)
                {
                    issues.Add("Object " + node.ObjectId + " contains a null prerequisite.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(prerequisite.InputName))
                {
                    issues.Add("Object " + node.ObjectId + " contains a prerequisite without input name.");
                    continue;
                }

                switch (prerequisite.Kind)
                {
                    case JarvisPrerequisiteResolutionKind.ResolvedFromIntent:
                    case JarvisPrerequisiteResolutionKind.ResolvedFromRouting:
                        if (prerequisite.Value == null)
                            issues.Add("Resolved prerequisite has no value: " + node.ObjectId + "." + prerequisite.InputName);
                        break;

                    case JarvisPrerequisiteResolutionKind.LookupPlanned:
                        ValidateLookup(node, prerequisite, issues);
                        break;

                    case JarvisPrerequisiteResolutionKind.DependencyPending:
                        ValidateDependencyPending(node, prerequisite, graph, issues);
                        break;

                    case JarvisPrerequisiteResolutionKind.NeedsUserInput:
                        issues.Add("Required input still needs user input: " + node.ObjectId + "." + prerequisite.InputName);
                        break;

                    default:
                        issues.Add("Invalid prerequisite state: " + node.ObjectId + "." + prerequisite.InputName);
                        break;
                }
            }
        }

        private static void ValidateLookup(
            JarvisValidatedTaskNode node,
            JarvisPrerequisiteResolutionItem prerequisite,
            List<string> issues)
        {
            JarvisPrerequisiteLookupDefinition lookup = prerequisite.Lookup;
            if (lookup == null || string.IsNullOrWhiteSpace(lookup.Strategy) || string.IsNullOrWhiteSpace(lookup.Output))
            {
                issues.Add("Incomplete lookup definition for " + node.ObjectId + "." + prerequisite.InputName);
                return;
            }

            string[] tools = lookup.Tools ?? new string[0];
            if (tools.Length == 0)
            {
                // Tool-less lookup is permitted only for deterministic local
                // business-rule resolution. It is not a license to ask an LLM
                // to invent a value.
                if (!string.Equals(lookup.Source, "business_rule", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(
                        "Tool-less lookup is not an approved business-rule resolver: " +
                        node.ObjectId + "." + prerequisite.InputName);
                }
                return;
            }

            foreach (string toolName in tools)
            {
                if (JarvisToolRegistry.Find(toolName) == null)
                {
                    issues.Add(
                        "Lookup references unregistered tool '" + toolName + "' for " +
                        node.ObjectId + "." + prerequisite.InputName);
                }
            }
        }

        private static void ValidateDependencyPending(
            JarvisValidatedTaskNode node,
            JarvisPrerequisiteResolutionItem prerequisite,
            JarvisDependencyGraph graph,
            List<string> issues)
        {
            JarvisDependencyBinding[] bindings = (graph.Bindings ?? new List<JarvisDependencyBinding>())
                .Where(x => x != null &&
                    string.Equals(x.TargetObjectId, node.ObjectId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.TargetInput, prerequisite.InputName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (bindings.Length == 0)
            {
                issues.Add(
                    "Unbound dependency input: " + node.ObjectId + "." + prerequisite.InputName);
                return;
            }

            if (bindings.Length > 1)
            {
                issues.Add(
                    "Dependency input has multiple bindings: " + node.ObjectId + "." + prerequisite.InputName);
            }
        }

        private static void BuildExecutionWaves(
            JarvisValidatedTaskNode[] nodes,
            JarvisDependencyGraph graph,
            JarvisExecutionPlanPreview preview)
        {
            var remaining = new List<JarvisValidatedTaskNode>(nodes);
            var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var originalOrdinal = nodes
                .Select((node, index) => new { node.ObjectId, Index = index })
                .ToDictionary(x => x.ObjectId, x => x.Index, StringComparer.OrdinalIgnoreCase);

            int wave = 1;
            int ordinal = 1;

            while (remaining.Count > 0)
            {
                JarvisValidatedTaskNode[] eligible = remaining
                    .Where(node => graph.GetDependencies(node.ObjectId).All(id => scheduled.Contains(id)))
                    .OrderBy(node => originalOrdinal[node.ObjectId])
                    .ToArray();

                if (eligible.Length == 0)
                {
                    preview.ValidationIssues.Add("Whole plan cannot be topologically scheduled; dependency cycle or missing predecessor detected.");
                    return;
                }

                JarvisValidatedTaskNode[] selected = SelectWave(eligible);
                foreach (JarvisValidatedTaskNode node in selected)
                {
                    JarvisExecutionPlanEntry entry = CreateEntry(node, graph, wave, ordinal++);
                    preview.Entries.Add(entry);
                    scheduled.Add(node.ObjectId);
                    remaining.Remove(node);
                }

                wave++;
            }
        }

        private static JarvisValidatedTaskNode[] SelectWave(JarvisValidatedTaskNode[] eligible)
        {
            if (eligible == null || eligible.Length == 0)
                return new JarvisValidatedTaskNode[0];

            JarvisValidatedTaskNode first = eligible[0];
            if (first.Descriptor == null || first.Descriptor.ExecutionPolicy != JarvisTaskExecutionPolicy.ParallelSafe)
                return new[] { first };

            return eligible
                .Where(x => x.Descriptor != null && x.Descriptor.ExecutionPolicy == JarvisTaskExecutionPolicy.ParallelSafe)
                .ToArray();
        }

        private static JarvisExecutionPlanEntry CreateEntry(
            JarvisValidatedTaskNode node,
            JarvisDependencyGraph graph,
            int wave,
            int ordinal)
        {
            var entry = new JarvisExecutionPlanEntry
            {
                Wave = wave,
                Ordinal = ordinal,
                ObjectId = node.ObjectId,
                TaskType = node.TaskType,
                OwnerAgent = node.Descriptor == null ? string.Empty : node.Descriptor.OwnerAgent,
                ExecutionPolicy = node.Descriptor == null
                    ? JarvisTaskExecutionPolicy.Sequential
                    : node.Descriptor.ExecutionPolicy,
                RequiresConfirmation = node.Descriptor != null && node.Descriptor.RequiresConfirmation
            };

            entry.DependsOnObjectIds.AddRange(graph.GetDependencies(node.ObjectId));

            foreach (JarvisPrerequisiteResolutionItem prerequisite in node.Prerequisites)
            {
                if (prerequisite == null)
                    continue;

                if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.LookupPlanned)
                    entry.LookupInputs.Add(prerequisite.InputName);
                else if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.DependencyPending)
                    entry.BoundInputs.Add(prerequisite.InputName);
            }

            return entry;
        }

        private static void ValidatePreview(
            JarvisExecutionPlanPreview preview,
            JarvisValidatedTaskNode[] nodes,
            JarvisDependencyGraph graph)
        {
            if (preview.Entries.Count != nodes.Length)
            {
                preview.ValidationIssues.Add(
                    "Execution preview task count does not match validated graph node count.");
                return;
            }

            var byObject = preview.Entries.ToDictionary(
                x => x.ObjectId,
                StringComparer.OrdinalIgnoreCase);

            foreach (JarvisDependencyBinding binding in graph.Bindings)
            {
                JarvisExecutionPlanEntry source;
                JarvisExecutionPlanEntry target;
                if (!byObject.TryGetValue(binding.SourceObjectId, out source) ||
                    !byObject.TryGetValue(binding.TargetObjectId, out target))
                    continue;

                if (source.Wave >= target.Wave)
                {
                    preview.ValidationIssues.Add(
                        "Execution ordering violates dependency " + binding.SourceObjectId +
                        " -> " + binding.TargetObjectId + ".");
                }
            }
        }
    }
}

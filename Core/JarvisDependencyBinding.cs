using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    internal sealed class JarvisDependencyBinding
    {
        public string SourceObjectId { get; set; }
        public string SourceTaskType { get; set; }
        public string SourceOutput { get; set; }
        public string TargetObjectId { get; set; }
        public string TargetTaskType { get; set; }
        public string TargetInput { get; set; }
        public string Rule { get; set; }
    }

    internal sealed class JarvisDependencyGraph
    {
        public JarvisDependencyGraph()
        {
            Nodes = new List<JarvisValidatedTaskNode>();
            Bindings = new List<JarvisDependencyBinding>();
            ValidationIssues = new List<string>();
        }

        public List<JarvisValidatedTaskNode> Nodes { get; private set; }
        public List<JarvisDependencyBinding> Bindings { get; private set; }
        public List<string> ValidationIssues { get; private set; }

        public bool IsValid
        {
            get { return ValidationIssues.Count == 0 && !HasCycle(); }
        }

        internal string[] GetDependencies(string targetObjectId)
        {
            return Bindings
                .Where(x => string.Equals(x.TargetObjectId, targetObjectId, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SourceObjectId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private bool HasCycle()
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JarvisValidatedTaskNode node in Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ObjectId))
                    continue;
                if (Visit(node.ObjectId, visiting, visited))
                    return true;
            }

            return false;
        }

        private bool Visit(string objectId, HashSet<string> visiting, HashSet<string> visited)
        {
            if (visited.Contains(objectId))
                return false;
            if (!visiting.Add(objectId))
                return true;

            foreach (string dependencyId in GetDependencies(objectId))
            {
                if (Visit(dependencyId, visiting, visited))
                    return true;
            }

            visiting.Remove(objectId);
            visited.Add(objectId);
            return false;
        }
    }

    internal sealed class JarvisDependencyBindingRule
    {
        public JarvisDependencyBindingRule(
            string sourceTaskType,
            string sourceOutput,
            string targetTaskType,
            string targetInput,
            string reason)
        {
            SourceTaskType = sourceTaskType ?? string.Empty;
            SourceOutput = sourceOutput ?? string.Empty;
            TargetTaskType = targetTaskType ?? string.Empty;
            TargetInput = targetInput ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public string SourceTaskType { get; private set; }
        public string SourceOutput { get; private set; }
        public string TargetTaskType { get; private set; }
        public string TargetInput { get; private set; }
        public string Reason { get; private set; }
    }

    /// <summary>
    /// Deterministic cross-object dependency builder.
    ///
    /// The LLM is not allowed to invent graph edges. A dependency is created only
    /// when either:
    /// 1. producer output and consumer pending input have the same registered
    ///    contract name, or
    /// 2. an explicit local whitelist rule maps the producer output to the
    ///    consumer input.
    ///
    /// Ambiguous producer selection is never guessed. The graph remains invalid
    /// until the ambiguity is resolved by business context or clarification.
    /// </summary>
    internal static class JarvisDependencyBinder
    {
        private static readonly JarvisDependencyBindingRule[] Rules =
        {
            R("FindTrader", "trdrId", "CreateOrder", "trdrId",
                "Resolved trader id feeds the order trader input."),

            R("OpenDocument", "findoc", "ResolveDocumentConversion", "findoc",
                "Resolved document FINDOC feeds conversion target lookup."),

            R("OpenDocument", "findoc", "CreateCourierVoucher", "findocId",
                "Resolved source document feeds courier voucher creation."),

            R("CreateOrder", "findocId", "CreateCourierVoucher", "findocId",
                "Newly created order FINDOC may feed a requested courier action."),

            R("ExportData", "file_artifact", "SendEmail", "artifact_reference",
                "Export artifact feeds a requested email attachment."),

            R("ExportData", "path", "SendEmail", "attachmentFilePath",
                "Export path feeds a requested email attachment path."),

            R("ReadInbox", "messageId", "ReplyEmail", "messageId",
                "Resolved inbox message id feeds reply action.")
        };

        internal static IReadOnlyList<JarvisDependencyBindingRule> AllRules
        {
            get { return Rules; }
        }

        internal static JarvisDependencyGraph Build(IEnumerable<JarvisValidatedTaskNode> nodes)
        {
            var graph = new JarvisDependencyGraph();
            JarvisValidatedTaskNode[] candidates = (nodes ?? Enumerable.Empty<JarvisValidatedTaskNode>())
                .Where(x => x != null)
                .ToArray();

            graph.Nodes.AddRange(candidates);
            ValidateNodes(candidates, graph.ValidationIssues);
            if (graph.ValidationIssues.Count > 0)
                return graph;

            foreach (JarvisValidatedTaskNode target in candidates)
            {
                JarvisPrerequisiteResolutionItem[] pending = target.Prerequisites
                    .Where(x => x != null && x.Kind == JarvisPrerequisiteResolutionKind.DependencyPending)
                    .ToArray();

                foreach (JarvisPrerequisiteResolutionItem input in pending)
                    BindPendingInput(candidates, target, input, graph);
            }

            ValidateBindings(graph);
            return graph;
        }

        private static void BindPendingInput(
            JarvisValidatedTaskNode[] nodes,
            JarvisValidatedTaskNode target,
            JarvisPrerequisiteResolutionItem input,
            JarvisDependencyGraph graph)
        {
            JarvisDependencyBinding[] matches = nodes
                .Where(source => !ReferenceEquals(source, target))
                .SelectMany(source => GetBindings(source, target, input))
                .ToArray();

            if (matches.Length == 0)
            {
                graph.ValidationIssues.Add(
                    "No registered producer can satisfy dependency input '" + input.InputName +
                    "' for object " + target.ObjectId + " (" + target.TaskType + ").");
                return;
            }

            string[] producerIds = matches
                .Select(x => x.SourceObjectId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (producerIds.Length > 1)
            {
                graph.ValidationIssues.Add(
                    "Ambiguous dependency for input '" + input.InputName + "' on object " +
                    target.ObjectId + ": multiple producer objects match (" +
                    string.Join(", ", producerIds) + ").");
                return;
            }

            JarvisDependencyBinding selected = matches
                .OrderByDescending(x => string.Equals(x.SourceOutput, input.InputName, StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.SourceOutput, StringComparer.OrdinalIgnoreCase)
                .First();

            graph.Bindings.Add(selected);
        }

        private static IEnumerable<JarvisDependencyBinding> GetBindings(
            JarvisValidatedTaskNode source,
            JarvisValidatedTaskNode target,
            JarvisPrerequisiteResolutionItem input)
        {
            if (source == null || target == null || input == null ||
                source.Descriptor == null || target.Descriptor == null)
                yield break;

            foreach (string output in source.Descriptor.Produces ?? new string[0])
            {
                if (string.Equals(output, input.InputName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return B(source, output, target, input.InputName,
                        "Exact registered producer-output/consumer-input contract match.");
                }
            }

            foreach (JarvisDependencyBindingRule rule in Rules)
            {
                if (!string.Equals(rule.SourceTaskType, source.TaskType, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(rule.TargetTaskType, target.TaskType, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(rule.TargetInput, input.InputName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!(source.Descriptor.Produces ?? new string[0])
                    .Contains(rule.SourceOutput, StringComparer.OrdinalIgnoreCase))
                    continue;

                yield return B(source, rule.SourceOutput, target, input.InputName, rule.Reason);
            }
        }

        private static JarvisDependencyBinding B(
            JarvisValidatedTaskNode source,
            string sourceOutput,
            JarvisValidatedTaskNode target,
            string targetInput,
            string rule)
        {
            return new JarvisDependencyBinding
            {
                SourceObjectId = source.ObjectId,
                SourceTaskType = source.TaskType,
                SourceOutput = sourceOutput,
                TargetObjectId = target.ObjectId,
                TargetTaskType = target.TaskType,
                TargetInput = targetInput,
                Rule = rule
            };
        }

        private static void ValidateNodes(
            IEnumerable<JarvisValidatedTaskNode> nodes,
            List<string> issues)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JarvisValidatedTaskNode node in nodes)
            {
                if (string.IsNullOrWhiteSpace(node.ObjectId))
                    issues.Add("Validated task node without ObjectId.");
                else if (!ids.Add(node.ObjectId))
                    issues.Add("Duplicate validated task ObjectId: " + node.ObjectId);

                if (!node.IsStructurallyValid)
                    issues.Add("Object " + (node.ObjectId ?? "<null>") + " is not structurally valid.");
                if (node.NeedsUserInput)
                    issues.Add("Object " + (node.ObjectId ?? "<null>") + " still needs user input and cannot enter dependency binding.");
                if (node.Descriptor == null || JarvisTaskRegistry.Find(node.TaskType) == null)
                    issues.Add("Object " + (node.ObjectId ?? "<null>") + " references an unknown task contract.");
            }
        }

        private static void ValidateBindings(JarvisDependencyGraph graph)
        {
            foreach (JarvisDependencyBinding binding in graph.Bindings)
            {
                if (string.Equals(binding.SourceObjectId, binding.TargetObjectId, StringComparison.OrdinalIgnoreCase))
                    graph.ValidationIssues.Add("Dependency binding cannot reference the same source and target object: " + binding.SourceObjectId);

                JarvisValidatedTaskNode source = graph.Nodes.FirstOrDefault(x =>
                    string.Equals(x.ObjectId, binding.SourceObjectId, StringComparison.OrdinalIgnoreCase));
                JarvisValidatedTaskNode target = graph.Nodes.FirstOrDefault(x =>
                    string.Equals(x.ObjectId, binding.TargetObjectId, StringComparison.OrdinalIgnoreCase));

                if (source == null || target == null)
                {
                    graph.ValidationIssues.Add("Dependency binding references a missing graph node.");
                    continue;
                }

                if (source.Descriptor == null || !(source.Descriptor.Produces ?? new string[0])
                    .Contains(binding.SourceOutput, StringComparer.OrdinalIgnoreCase))
                {
                    graph.ValidationIssues.Add(
                        "Binding source output is not registered: " + binding.SourceTaskType + "." + binding.SourceOutput);
                }

                if (!target.Prerequisites.Any(x =>
                    x != null &&
                    string.Equals(x.InputName, binding.TargetInput, StringComparison.OrdinalIgnoreCase) &&
                    x.Kind == JarvisPrerequisiteResolutionKind.DependencyPending))
                {
                    graph.ValidationIssues.Add(
                        "Binding target input is not dependency-pending: " + binding.TargetTaskType + "." + binding.TargetInput);
                }
            }

            if (HasCycle(graph))
                graph.ValidationIssues.Add("Dependency graph contains a cycle.");
        }

        private static bool HasCycle(JarvisDependencyGraph graph)
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JarvisValidatedTaskNode node in graph.Nodes)
            {
                if (node == null || string.IsNullOrWhiteSpace(node.ObjectId))
                    continue;
                if (Visit(graph, node.ObjectId, visiting, visited))
                    return true;
            }
            return false;
        }

        private static bool Visit(
            JarvisDependencyGraph graph,
            string objectId,
            HashSet<string> visiting,
            HashSet<string> visited)
        {
            if (visited.Contains(objectId))
                return false;
            if (!visiting.Add(objectId))
                return true;

            foreach (string dependencyId in graph.GetDependencies(objectId))
            {
                if (Visit(graph, dependencyId, visiting, visited))
                    return true;
            }

            visiting.Remove(objectId);
            visited.Add(objectId);
            return false;
        }

        private static JarvisDependencyBindingRule R(
            string sourceTaskType,
            string sourceOutput,
            string targetTaskType,
            string targetInput,
            string reason)
        {
            return new JarvisDependencyBindingRule(
                sourceTaskType,
                sourceOutput,
                targetTaskType,
                targetInput,
                reason);
        }
    }
}

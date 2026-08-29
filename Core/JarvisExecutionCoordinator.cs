using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisExecutionStepState
    {
        Planned,
        WaitingForDependencies,
        WaitingForConfirmation,
        ReadyForDispatch,
        Running,
        Succeeded,
        Failed,
        Blocked
    }

    internal sealed class JarvisTaskExecutionResult
    {
        public JarvisTaskExecutionResult()
        {
            Outputs = new JObject();
            Issues = new List<string>();
        }

        public string ObjectId { get; set; }
        public string TaskType { get; set; }
        public string OwnerAgent { get; set; }
        public bool Success { get; set; }
        public JObject Outputs { get; private set; }
        public List<string> Issues { get; private set; }
    }

    internal sealed class JarvisExecutionStepSnapshot
    {
        public JarvisExecutionStepSnapshot()
        {
            DependsOn = new List<string>();
            BoundInputs = new List<string>();
            MaterializedInputs = new JObject();
            ValidationIssues = new List<string>();
        }

        public int Wave { get; set; }
        public int Ordinal { get; set; }
        public string ObjectId { get; set; }
        public string TaskType { get; set; }
        public string OwnerAgent { get; set; }
        public JarvisExecutionStepState State { get; set; }
        public bool RequiresConfirmation { get; set; }
        public bool ConfirmationGranted { get; set; }
        public List<string> DependsOn { get; private set; }
        public List<string> BoundInputs { get; private set; }
        public JObject MaterializedInputs { get; private set; }
        public List<string> ValidationIssues { get; private set; }
    }

    internal sealed class JarvisExecutionControlSnapshot
    {
        public JarvisExecutionControlSnapshot()
        {
            Steps = new List<JarvisExecutionStepSnapshot>();
            ValidationIssues = new List<string>();
        }

        public List<JarvisExecutionStepSnapshot> Steps { get; private set; }
        public List<string> ValidationIssues { get; private set; }
        public bool IsValid { get { return ValidationIssues.Count == 0 && Steps.All(x => x.ValidationIssues.Count == 0); } }
    }

    /// <summary>
    /// Jarvis-owned execution control plane. Executors never advance the graph
    /// and never pass results directly to another executor. Jarvis validates
    /// dispatch, validates returned results, stores them, materializes registered
    /// dependency bindings, then decides which object may run next.
    /// </summary>
    internal sealed class JarvisExecutionCoordinator
    {
        private readonly JarvisDependencyGraph _graph;
        private readonly JarvisExecutionPlanPreview _preview;
        private readonly Dictionary<string, JarvisTaskExecutionResult> _results;
        private readonly HashSet<string> _confirmed;
        private readonly HashSet<string> _running;

        internal JarvisExecutionCoordinator(JarvisDependencyGraph graph, JarvisExecutionPlanPreview preview)
        {
            _graph = graph;
            _preview = preview;
            _results = new Dictionary<string, JarvisTaskExecutionResult>(StringComparer.OrdinalIgnoreCase);
            _confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal JarvisExecutionControlSnapshot Inspect()
        {
            var snapshot = new JarvisExecutionControlSnapshot();
            ValidateControlPlane(snapshot.ValidationIssues);
            if (snapshot.ValidationIssues.Count > 0) return snapshot;
            foreach (JarvisExecutionPlanEntry entry in _preview.Entries.OrderBy(x => x.Ordinal))
                snapshot.Steps.Add(BuildStepSnapshot(entry));
            return snapshot;
        }

        internal string[] GetDispatchableObjectIds()
        {
            JarvisExecutionControlSnapshot snapshot = Inspect();
            if (!snapshot.IsValid) return new string[0];
            return snapshot.Steps.Where(x => x.State == JarvisExecutionStepState.ReadyForDispatch)
                .OrderBy(x => x.Ordinal).Select(x => x.ObjectId).ToArray();
        }

        internal bool TryGetDispatchInputs(string objectId, out JObject inputs, out string[] issues)
        {
            inputs = new JObject();
            var localIssues = new List<string>();
            JarvisExecutionPlanEntry entry = FindEntry(objectId);
            if (entry == null)
            {
                localIssues.Add("Cannot materialize inputs for unknown execution object: " + (objectId ?? "<null>"));
            }
            else
            {
                JarvisValidatedTaskNode node = FindNode(entry.ObjectId);
                if (node == null || node.Descriptor == null)
                    localIssues.Add("Execution object has no authoritative task node: " + entry.ObjectId);
                else
                {
                    foreach (JarvisPrerequisiteResolutionItem prerequisite in node.Prerequisites.Where(x => x != null))
                    {
                        if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.ResolvedFromIntent ||
                            prerequisite.Kind == JarvisPrerequisiteResolutionKind.ResolvedFromRouting)
                        {
                            if (prerequisite.Value != null)
                                inputs[prerequisite.InputName] = prerequisite.Value.DeepClone();
                        }
                        else if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.DependencyPending)
                        {
                            MaterializeBoundInput(entry, prerequisite.InputName, inputs, localIssues);
                        }
                        else if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.LookupPlanned)
                            localIssues.Add("Lookup prerequisite remains unresolved for " + entry.ObjectId + "." + prerequisite.InputName + ".");
                        else if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.NeedsUserInput)
                            localIssues.Add("User input remains unresolved for " + entry.ObjectId + "." + prerequisite.InputName + ".");
                        else if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.Invalid)
                            localIssues.Add("Invalid prerequisite for " + entry.ObjectId + "." + prerequisite.InputName + ".");
                    }

                    foreach (string required in node.Descriptor.RequiredInputs ?? new string[0])
                        if (inputs[required] == null)
                            localIssues.Add("Required dispatch input is not materialized: " + entry.ObjectId + "." + required + ".");
                }
            }
            issues = localIssues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return issues.Length == 0;
        }

        internal bool GrantConfirmation(string objectId, out string[] issues)
        {
            var localIssues = new List<string>();
            JarvisExecutionPlanEntry entry = FindEntry(objectId);
            if (entry == null) localIssues.Add("Cannot confirm unknown execution object: " + (objectId ?? "<null>"));
            else if (!entry.RequiresConfirmation) localIssues.Add("Execution object does not require confirmation: " + entry.ObjectId);
            else if (!DependenciesSucceeded(entry)) localIssues.Add("Confirmation cannot unlock a task before all dependencies succeed: " + entry.ObjectId);
            else
            {
                JObject ignored;
                string[] materializationIssues;
                if (!TryGetDispatchInputs(entry.ObjectId, out ignored, out materializationIssues))
                    localIssues.AddRange(materializationIssues);
            }
            if (localIssues.Count == 0) _confirmed.Add(entry.ObjectId);
            issues = localIssues.ToArray();
            return issues.Length == 0;
        }

        internal bool TryBeginDispatch(string objectId, out string[] issues)
        {
            var localIssues = new List<string>();
            JarvisExecutionPlanEntry entry = FindEntry(objectId);
            ValidateBeforeDispatch(entry, localIssues);
            if (localIssues.Count == 0) _running.Add(entry.ObjectId);
            issues = localIssues.ToArray();
            return issues.Length == 0;
        }

        internal bool TryAcceptResult(JarvisTaskExecutionResult result, out string[] issues)
        {
            var localIssues = new List<string>();
            ValidateAfterExecution(result, localIssues);
            if (localIssues.Count == 0)
            {
                _running.Remove(result.ObjectId);
                _results[result.ObjectId] = result;
            }
            issues = localIssues.ToArray();
            return issues.Length == 0;
        }

        private JarvisExecutionStepSnapshot BuildStepSnapshot(JarvisExecutionPlanEntry entry)
        {
            var step = new JarvisExecutionStepSnapshot
            {
                Wave = entry.Wave, Ordinal = entry.Ordinal, ObjectId = entry.ObjectId,
                TaskType = entry.TaskType, OwnerAgent = entry.OwnerAgent,
                RequiresConfirmation = entry.RequiresConfirmation,
                ConfirmationGranted = _confirmed.Contains(entry.ObjectId)
            };
            step.DependsOn.AddRange(entry.DependsOnObjectIds);
            step.BoundInputs.AddRange(entry.BoundInputs);

            JarvisTaskExecutionResult result;
            if (_results.TryGetValue(entry.ObjectId, out result))
            {
                step.State = result.Success ? JarvisExecutionStepState.Succeeded : JarvisExecutionStepState.Failed;
                return step;
            }
            if (_running.Contains(entry.ObjectId)) { step.State = JarvisExecutionStepState.Running; return step; }
            if (DependencyFailed(entry)) { step.State = JarvisExecutionStepState.Blocked; step.ValidationIssues.Add("A required upstream task failed."); return step; }
            if (!DependenciesSucceeded(entry)) { step.State = JarvisExecutionStepState.WaitingForDependencies; return step; }

            JObject inputs;
            string[] inputIssues;
            if (!TryGetDispatchInputs(entry.ObjectId, out inputs, out inputIssues))
            {
                step.State = JarvisExecutionStepState.Blocked;
                step.ValidationIssues.AddRange(inputIssues);
                return step;
            }
            foreach (JProperty property in inputs.Properties())
                step.MaterializedInputs[property.Name] = property.Value.DeepClone();

            if (entry.RequiresConfirmation && !_confirmed.Contains(entry.ObjectId))
            {
                step.State = JarvisExecutionStepState.WaitingForConfirmation;
                return step;
            }

            var dispatchIssues = new List<string>();
            ValidateBeforeDispatch(entry, dispatchIssues);
            if (dispatchIssues.Count > 0) { step.State = JarvisExecutionStepState.Blocked; step.ValidationIssues.AddRange(dispatchIssues); return step; }
            step.State = JarvisExecutionStepState.ReadyForDispatch;
            return step;
        }

        private void ValidateControlPlane(List<string> issues)
        {
            if (_graph == null) { issues.Add("Execution coordinator requires a dependency graph."); return; }
            if (_preview == null) { issues.Add("Execution coordinator requires a validated execution preview."); return; }
            if (!_graph.IsValid) issues.Add("Execution coordinator refuses an invalid dependency graph.");
            if (!_preview.IsValid) issues.Add("Execution coordinator refuses an invalid execution preview.");
            if (_preview.Entries.Count != _graph.Nodes.Count) issues.Add("Execution preview and dependency graph task counts differ.");
        }

        private void ValidateBeforeDispatch(JarvisExecutionPlanEntry entry, List<string> issues)
        {
            if (entry == null) { issues.Add("Cannot dispatch an unknown execution object."); return; }
            JarvisValidatedTaskNode node = FindNode(entry.ObjectId);
            if (node == null || node.Descriptor == null) { issues.Add("Dispatch object has no validated task node: " + entry.ObjectId); return; }
            JarvisTaskDescriptor registered = JarvisTaskRegistry.Find(entry.TaskType);
            if (registered == null) issues.Add("Dispatch task is not registered: " + entry.TaskType);
            else
            {
                if (!string.Equals(registered.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) issues.Add("Owner agent mismatch for " + entry.ObjectId + ".");
                if (!string.Equals(registered.TaskType, node.TaskType, StringComparison.OrdinalIgnoreCase)) issues.Add("Task contract mismatch for " + entry.ObjectId + ".");
            }
            if (!DependenciesSucceeded(entry)) issues.Add("Dependencies are not complete for " + entry.ObjectId + ".");
            JObject ignored;
            string[] materializationIssues;
            if (!TryGetDispatchInputs(entry.ObjectId, out ignored, out materializationIssues)) issues.AddRange(materializationIssues);
            if (entry.RequiresConfirmation && !_confirmed.Contains(entry.ObjectId)) issues.Add("Confirmation is required before dispatch of " + entry.ObjectId + ".");
            if (_running.Contains(entry.ObjectId)) issues.Add("Execution object is already running: " + entry.ObjectId);
            if (_results.ContainsKey(entry.ObjectId)) issues.Add("Execution object already has an accepted result: " + entry.ObjectId);
        }

        private void ValidateAfterExecution(JarvisTaskExecutionResult result, List<string> issues)
        {
            if (result == null) { issues.Add("Executor returned a null result."); return; }
            JarvisExecutionPlanEntry entry = FindEntry(result.ObjectId);
            if (entry == null) { issues.Add("Executor returned a result for an unknown object: " + (result.ObjectId ?? "<null>")); return; }
            if (!_running.Contains(entry.ObjectId)) issues.Add("Jarvis cannot accept a result for a task it did not dispatch: " + entry.ObjectId);
            if (!string.Equals(result.TaskType, entry.TaskType, StringComparison.OrdinalIgnoreCase)) issues.Add("Executor result task type mismatch for " + entry.ObjectId + ".");
            if (!string.Equals(result.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) issues.Add("Executor result owner mismatch for " + entry.ObjectId + ".");
            JarvisValidatedTaskNode node = FindNode(entry.ObjectId);
            if (node == null || node.Descriptor == null) { issues.Add("Accepted result has no authoritative task descriptor: " + entry.ObjectId); return; }
            if (result.Success)
            {
                foreach (string outputName in node.Descriptor.Produces ?? new string[0])
                    if (result.Outputs == null || result.Outputs[outputName] == null)
                        issues.Add("Successful executor result is missing registered output '" + outputName + "' for " + entry.ObjectId + ".");
            }
            else if (result.Issues == null || result.Issues.Count == 0)
                issues.Add("Failed executor result must contain a diagnostic issue for " + entry.ObjectId + ".");
        }

        private void MaterializeBoundInput(JarvisExecutionPlanEntry entry, string inputName, JObject inputs, List<string> issues)
        {
            JarvisDependencyBinding[] bindings = _graph.Bindings.Where(x => x != null &&
                string.Equals(x.TargetObjectId, entry.ObjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TargetInput, inputName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bindings.Length != 1) { issues.Add("Jarvis requires exactly one dependency binding for " + entry.ObjectId + "." + inputName + "."); return; }
            JarvisDependencyBinding binding = bindings[0];
            JarvisTaskExecutionResult sourceResult;
            if (!_results.TryGetValue(binding.SourceObjectId, out sourceResult) || !sourceResult.Success)
            {
                issues.Add("Bound input is waiting for validated upstream result: " + entry.ObjectId + "." + inputName + ".");
                return;
            }
            JToken value = sourceResult.Outputs == null ? null : sourceResult.Outputs[binding.SourceOutput];
            if (value == null) { issues.Add("Validated upstream result does not contain bound output '" + binding.SourceOutput + "' for " + entry.ObjectId + "." + inputName + "."); return; }
            inputs[inputName] = value.DeepClone();
        }

        private bool DependenciesSucceeded(JarvisExecutionPlanEntry entry)
        {
            foreach (string dependencyId in entry.DependsOnObjectIds)
            {
                JarvisTaskExecutionResult result;
                if (!_results.TryGetValue(dependencyId, out result) || !result.Success) return false;
            }
            return true;
        }

        private bool DependencyFailed(JarvisExecutionPlanEntry entry)
        {
            foreach (string dependencyId in entry.DependsOnObjectIds)
            {
                JarvisTaskExecutionResult result;
                if (_results.TryGetValue(dependencyId, out result) && !result.Success) return true;
            }
            return false;
        }

        private JarvisExecutionPlanEntry FindEntry(string objectId)
        {
            if (_preview == null || string.IsNullOrWhiteSpace(objectId)) return null;
            return _preview.Entries.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
        }

        private JarvisValidatedTaskNode FindNode(string objectId)
        {
            if (_graph == null || string.IsNullOrWhiteSpace(objectId)) return null;
            return _graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
        }
    }
}

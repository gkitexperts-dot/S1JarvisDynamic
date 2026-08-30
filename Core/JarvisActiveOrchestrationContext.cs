using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Shell-scoped durable state for one active multi-turn orchestration run.
    /// It preserves semantic facts and verified state; it never decides routing,
    /// authorization or tool execution and contains no phrase/keyword heuristics.
    /// </summary>
    internal sealed class JarvisActiveOrchestrationContext
    {
        private readonly object _sync = new object();
        private string _runId;
        private string _originalIntent;
        private string _latestMessage;
        private bool _open;
        private readonly JObject _explicitFacts = new JObject();
        private readonly JObject _verifiedOutputs = new JObject();
        private readonly JArray _graph = new JArray();
        private readonly HashSet<string> _completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _invalidated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _pendingObjectId;
        private JObject _pendingPayload;
        private string _pendingPayloadHash;

        internal bool HasOpenRun
        {
            get { lock (_sync) return _open && !string.IsNullOrWhiteSpace(_runId); }
        }

        internal string RunId
        {
            get { lock (_sync) return _runId; }
        }

        internal void Begin(string originalIntent)
        {
            lock (_sync)
            {
                ResetCore();
                _runId = Guid.NewGuid().ToString("N");
                _originalIntent = originalIntent ?? string.Empty;
                _latestMessage = _originalIntent;
                _open = true;
            }
        }

        /// <summary>
        /// Closed contexts are transparent so prompts that fall back to legacy chat
        /// do not accidentally create stale orchestration state. An active context
        /// contributes structured state to semantic decomposition without deciding
        /// whether the current message is a follow-up by words or phrases.
        /// </summary>
        internal string PreparePrompt(string currentUserMessage)
        {
            lock (_sync)
            {
                string current = currentUserMessage ?? string.Empty;
                if (!_open)
                    return current;

                _latestMessage = current;
                JObject context = BuildSnapshotUnsafe();
                return "[JARVIS_ACTIVE_ORCHESTRATION_CONTEXT]\n" +
                       context.ToString(Formatting.None) +
                       "\n[CURRENT_OPERATOR_MESSAGE]\n" + current;
            }
        }

        internal void CapturePlanning(JarvisShadowOrchestrationResult planning)
        {
            if (planning == null || planning.Graph == null) return;
            lock (_sync)
            {
                _graph.RemoveAll();
                foreach (JarvisValidatedTaskNode node in planning.Graph.Nodes.Where(x => x != null))
                {
                    var item = new JObject
                    {
                        ["objectId"] = node.ObjectId ?? string.Empty,
                        ["taskType"] = node.TaskType ?? string.Empty,
                        ["intentFragment"] = node.IntentFragment ?? string.Empty,
                        ["prerequisites"] = new JArray()
                    };
                    JArray prerequisites = (JArray)item["prerequisites"];
                    foreach (JarvisPrerequisiteResolutionItem prerequisite in node.Prerequisites.Where(x => x != null))
                    {
                        prerequisites.Add(new JObject
                        {
                            ["name"] = prerequisite.InputName ?? string.Empty,
                            ["kind"] = prerequisite.Kind.ToString(),
                            ["value"] = prerequisite.Value == null ? JValue.CreateNull() : prerequisite.Value.DeepClone()
                        });

                        if (prerequisite.Kind == JarvisPrerequisiteResolutionKind.ResolvedFromIntent && prerequisite.Value != null)
                            CaptureFactUnsafe(node.TaskType, prerequisite.InputName, prerequisite.Value);
                    }
                    _graph.Add(item);
                }
            }
        }

        internal void CaptureVerifiedResult(JarvisTaskExecutionResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.ObjectId)) return;
            lock (_sync)
            {
                _verifiedOutputs[result.ObjectId] = new JObject
                {
                    ["taskType"] = result.TaskType ?? string.Empty,
                    ["ownerAgent"] = result.OwnerAgent ?? string.Empty,
                    ["outputs"] = result.Outputs == null ? new JObject() : result.Outputs.DeepClone()
                };
                _completed.Add(result.ObjectId);
                _invalidated.Remove(result.ObjectId);
            }
        }

        internal void CapturePendingConfirmation(JarvisPendingConfirmationSession session)
        {
            lock (_sync)
            {
                if (session == null || !session.HasPending)
                {
                    ClearPendingUnsafe();
                    return;
                }
                _pendingObjectId = session.PendingObjectId;
                _pendingPayload = session.FrozenPayload;
                _pendingPayloadHash = session.PayloadHash;
            }
        }

        internal void ClearPendingConfirmation()
        {
            lock (_sync) ClearPendingUnsafe();
        }

        internal void Invalidate(string objectId)
        {
            if (string.IsNullOrWhiteSpace(objectId)) return;
            lock (_sync)
            {
                _invalidated.Add(objectId);
                _completed.Remove(objectId);
                _verifiedOutputs.Remove(objectId);
                if (string.Equals(_pendingObjectId, objectId, StringComparison.OrdinalIgnoreCase))
                    ClearPendingUnsafe();
            }
        }

        internal void Complete()
        {
            lock (_sync)
            {
                _open = false;
                ClearPendingUnsafe();
            }
        }

        internal void Clear()
        {
            lock (_sync) ResetCore();
        }

        internal JObject Snapshot()
        {
            lock (_sync) return BuildSnapshotUnsafe();
        }

        private void CaptureFactUnsafe(string taskType, string inputName, JToken value)
        {
            if (string.IsNullOrWhiteSpace(inputName) || value == null) return;
            string key = (taskType ?? string.Empty) + "." + inputName;
            _explicitFacts[key] = value.DeepClone();
        }

        private JObject BuildSnapshotUnsafe()
        {
            return new JObject
            {
                ["runId"] = _runId ?? string.Empty,
                ["originalIntent"] = _originalIntent ?? string.Empty,
                ["latestMessage"] = _latestMessage ?? string.Empty,
                ["explicitFacts"] = _explicitFacts.DeepClone(),
                ["graph"] = _graph.DeepClone(),
                ["verifiedOutputs"] = _verifiedOutputs.DeepClone(),
                ["completedObjectIds"] = new JArray(_completed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["invalidatedObjectIds"] = new JArray(_invalidated.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["pendingConfirmation"] = string.IsNullOrWhiteSpace(_pendingObjectId)
                    ? JValue.CreateNull()
                    : (JToken)new JObject
                    {
                        ["objectId"] = _pendingObjectId,
                        ["payload"] = _pendingPayload == null ? new JObject() : _pendingPayload.DeepClone(),
                        ["payloadHash"] = _pendingPayloadHash ?? string.Empty
                    }
            };
        }

        private void ClearPendingUnsafe()
        {
            _pendingObjectId = null;
            _pendingPayload = null;
            _pendingPayloadHash = null;
        }

        private void ResetCore()
        {
            _runId = null;
            _originalIntent = null;
            _latestMessage = null;
            _open = false;
            _explicitFacts.RemoveAll();
            _verifiedOutputs.RemoveAll();
            _graph.RemoveAll();
            _completed.Clear();
            _invalidated.Clear();
            ClearPendingUnsafe();
        }
    }
}

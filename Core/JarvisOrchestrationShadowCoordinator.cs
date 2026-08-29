using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisShadowOrchestrationResult
    {
        public JarvisShadowOrchestrationResult()
        {
            Issues = new List<string>();
        }

        public bool GateEnabled { get; set; }
        public bool DecompositionSucceeded { get; set; }
        public JarvisIntentObjectSet IntentObjects { get; set; }
        public IReadOnlyList<JarvisValidatedTaskNode> Nodes { get; set; }
        public JarvisDependencyGraph Graph { get; set; }
        public JarvisExecutionPlanPreview Preview { get; set; }
        public List<string> Issues { get; private set; }
    }

    /// <summary>
    /// Pilot-only shadow pipeline. It may call the AI decomposer and read
    /// routing knowledge, but it never executes business tools, lookups,
    /// confirmations or task actions. Any failure is diagnostic-only and must
    /// never affect the mature Main Chat path.
    /// </summary>
    internal static class JarvisOrchestrationShadowCoordinator
    {
        internal static async Task<JarvisShadowOrchestrationResult> RunAsync(
            XSupport xSupport,
            string userPrompt,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new JarvisShadowOrchestrationResult();

            if (!JarvisRoutingFeatureGate.UseNewRouting(xSupport))
                return result;

            result.GateEnabled = true;

            try
            {
                // Deliberately keep the caller synchronization context here.
                // After the no-tools HTTP call completes, Pass 2 may read
                // Soft1 routing knowledge through XSupport and must resume on
                // the host/UI context instead of a ThreadPool continuation.
                string decomposerJson = await JarvisShadowSemanticClient.DecomposeAsync(
                    xSupport,
                    userPrompt,
                    cancellationToken);

                JarvisIntentObjectSet objectSet;
                string[] parseIssues;
                bool parsed = JarvisIntentOrchestration.TryParsePass1(
                    decomposerJson,
                    userPrompt,
                    out objectSet,
                    out parseIssues);

                result.IntentObjects = objectSet;
                if (!parsed)
                {
                    result.Issues.AddRange(parseIssues ?? new string[0]);
                    LogResult(result, userPrompt);
                    return result;
                }

                result.DecompositionSucceeded = true;

                // Pass 2 runs independently only for objects that need it.
                JarvisIntentOrchestration.ApplyDynamicPass(objectSet, xSupport);

                JarvisIntentObject[] unresolved = objectSet.Objects
                    .Where(x => x == null || !x.IsResolved)
                    .ToArray();
                if (unresolved.Length > 0)
                {
                    foreach (JarvisIntentObject item in unresolved.Where(x => x != null))
                    {
                        result.Issues.Add(
                            "Object " + (item.ObjectId ?? "<null>") +
                            " unresolved after routing; status=" + item.Status +
                            "; diagnostic=" + JarvisIntentOrchestration.BuildClarificationDiagnostic(item));
                    }

                    LogResult(result, userPrompt);
                    return result;
                }

                IReadOnlyList<JarvisValidatedTaskNode> nodes =
                    JarvisPrerequisiteResolution.BuildNodes(objectSet);
                result.Nodes = nodes;

                JarvisDependencyGraph graph = JarvisDependencyBinder.Build(nodes);
                result.Graph = graph;
                if (!graph.IsValid)
                {
                    result.Issues.AddRange(graph.ValidationIssues);
                    LogResult(result, userPrompt);
                    return result;
                }

                JarvisExecutionPlanPreview preview = JarvisExecutionPlanPreviewBuilder.Build(graph);
                result.Preview = preview;
                if (!preview.IsValid)
                    result.Issues.AddRange(preview.ValidationIssues);

                LogResult(result, userPrompt);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Issues.Add("Shadow orchestration cancelled.");
                LogResult(result, userPrompt);
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add("Shadow orchestration exception: " + ex.Message);
                DebugLog.Log("[ORCH-SHADOW] EXCEPTION (legacy chat unaffected): " + ex);
                return result;
            }
        }

        internal static async Task RunAndLogSafeAsync(XSupport xSupport, string userPrompt)
        {
            try
            {
                // Do not ConfigureAwait(false): XSupport-backed Pass 2 must
                // continue on the Soft1/WPF synchronization context.
                await RunAsync(xSupport, userPrompt);
            }
            catch (Exception ex)
            {
                // Absolute safety boundary for fire-and-forget Main Chat hook.
                DebugLog.Log("[ORCH-SHADOW] UNHANDLED SUPPRESSED: " + ex);
            }
        }

        private static void LogResult(JarvisShadowOrchestrationResult result, string prompt)
        {
            try
            {
                var root = new JObject
                {
                    ["gate"] = result != null && result.GateEnabled,
                    ["decompositionSucceeded"] = result != null && result.DecompositionSucceeded,
                    ["prompt"] = prompt ?? string.Empty,
                    ["objects"] = SerializeObjects(result == null ? null : result.IntentObjects),
                    ["preview"] = SerializePreview(result == null ? null : result.Preview),
                    ["issues"] = new JArray(result == null ? new string[0] : result.Issues)
                };

                DebugLog.Log("[ORCH-SHADOW] " + root.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] diagnostic serialization failed: " + ex.Message);
            }
        }

        private static JArray SerializeObjects(JarvisIntentObjectSet set)
        {
            var array = new JArray();
            if (set == null)
                return array;

            foreach (JarvisIntentObject item in set.Objects.Where(x => x != null))
            {
                array.Add(new JObject
                {
                    ["id"] = item.ObjectId ?? string.Empty,
                    ["fragment"] = item.IntentFragment ?? string.Empty,
                    ["status"] = item.Status.ToString(),
                    ["pass"] = item.RoutingPass.ToString(),
                    ["taskType"] = item.ResolvedTaskType ?? string.Empty,
                    ["confidence"] = item.RoutingDecision != null && item.RoutingDecision.Winner != null
                        ? (JToken)new JValue(item.RoutingDecision.Winner.Score)
                        : JValue.CreateNull()
                });
            }
            return array;
        }

        private static JObject SerializePreview(JarvisExecutionPlanPreview preview)
        {
            if (preview == null)
                return null;

            return new JObject
            {
                ["readiness"] = preview.Readiness.ToString(),
                ["valid"] = preview.IsValid,
                ["entries"] = new JArray(preview.Entries.Select(x => new JObject
                {
                    ["wave"] = x.Wave,
                    ["ordinal"] = x.Ordinal,
                    ["objectId"] = x.ObjectId ?? string.Empty,
                    ["taskType"] = x.TaskType ?? string.Empty,
                    ["owner"] = x.OwnerAgent ?? string.Empty,
                    ["requiresConfirmation"] = x.RequiresConfirmation,
                    ["dependsOn"] = new JArray(x.DependsOnObjectIds),
                    ["lookupInputs"] = new JArray(x.LookupInputs),
                    ["boundInputs"] = new JArray(x.BoundInputs)
                })),
                ["issues"] = new JArray(preview.ValidationIssues)
            };
        }
    }

    /// <summary>
    /// One-shot no-tools semantic call for the shadow decomposer. Uses the same
    /// signed Verilic AI transport as Main Chat but provides no tool definitions,
    /// therefore it cannot execute any Jarvis business action.
    /// </summary>
    internal static class JarvisShadowSemanticClient
    {
        private const string Model = "claude-opus-5";
        private const int MaxTokens = 6000;

        internal static async Task<string> DecomposeAsync(
            XSupport xSupport,
            string userPrompt,
            CancellationToken cancellationToken)
        {
            if (xSupport == null)
                throw new ArgumentNullException("xSupport");

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = JarvisIntentOrchestration.BuildDecomposerUserPayload(userPrompt)
                }
            };

            var requestBody = new JObject
            {
                ["model"] = Model,
                ["max_tokens"] = MaxTokens,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = JarvisIntentOrchestration.BuildDecomposerSystemPrompt()
                    }
                },
                ["tools"] = new JArray(),
                ["messages"] = messages
            };

            string anthropicJson = requestBody.ToString(Formatting.None);
            var proxyResp = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                .SendAsync(xSupport, anthropicJson, cancellationToken)
                .ConfigureAwait(false);

            if (!proxyResp.Success)
                throw new InvalidOperationException(
                    proxyResp.CreditsExhausted
                        ? "AI credits exhausted during shadow decomposition."
                        : (proxyResp.ErrorMessage ?? "Shadow semantic call failed."));

            JObject anthropicResponse = JObject.Parse(proxyResp.RawResponseJson);
            string stopReason = anthropicResponse["stop_reason"] == null
                ? string.Empty
                : anthropicResponse["stop_reason"].ToString();
            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shadow semantic decomposer refused the request.");

            JArray content = anthropicResponse["content"] as JArray ?? new JArray();
            JToken text = content.FirstOrDefault(x =>
                string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
            string raw = text == null || text["text"] == null
                ? string.Empty
                : text["text"].ToString().Trim();

            return ExtractJsonObject(raw);
        }

        private static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLineEnd = trimmed.IndexOf('\n');
                int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                    trimmed = trimmed.Substring(firstLineEnd + 1, lastFence - firstLineEnd - 1).Trim();
            }

            int firstBrace = trimmed.IndexOf('{');
            int lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);

            return trimmed;
        }
    }
}

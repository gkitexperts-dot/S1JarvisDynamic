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
                string decomposerJson = await JarvisShadowSemanticClient.DecomposeAsync(
                    xSupport, userPrompt, cancellationToken);

                JarvisIntentObjectSet objectSet;
                string[] parseIssues;
                bool parsed = JarvisIntentOrchestration.TryParsePass1(
                    decomposerJson, userPrompt, out objectSet, out parseIssues);

                result.IntentObjects = objectSet;
                if (!parsed)
                {
                    result.Issues.AddRange(parseIssues ?? new string[0]);
                    LogResult(result, userPrompt);
                    return result;
                }

                result.DecompositionSucceeded = true;
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

                IReadOnlyList<JarvisValidatedTaskNode> nodes = JarvisPrerequisiteResolution.BuildNodes(objectSet);
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
                await RunAsync(xSupport, userPrompt);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] UNHANDLED SUPPRESSED: " + ex);
            }
        }

        private static void LogResult(JarvisShadowOrchestrationResult result, string prompt)
        {
            try
            {
                JArray issueArray = result == null ? new JArray() : new JArray(result.Issues);
                var root = new JObject
                {
                    ["gate"] = result != null && result.GateEnabled,
                    ["decompositionSucceeded"] = result != null && result.DecompositionSucceeded,
                    ["prompt"] = prompt ?? string.Empty,
                    ["objects"] = SerializeObjects(result == null ? null : result.IntentObjects),
                    ["preview"] = SerializePreview(result == null ? null : result.Preview),
                    ["issues"] = issueArray
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
    /// Shadow-only structured semantic decomposer. The synthetic tool is an output
    /// envelope, not a Jarvis business tool, and is never dispatched or executed.
    /// </summary>
    internal static class JarvisShadowSemanticClient
    {
        private const string Model = "claude-opus-5";
        private const string StructuredToolName = "emit_intent_objects";
        private const int MaxTokens = 6000;

        internal static async Task<string> DecomposeAsync(
            XSupport xSupport,
            string userPrompt,
            CancellationToken cancellationToken)
        {
            if (xSupport == null)
                throw new ArgumentNullException("xSupport");

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
                        ["text"] = JarvisIntentOrchestration.BuildDecomposerSystemPrompt() +
                            " Η απάντηση πρέπει να δοθεί αποκλειστικά καλώντας το emit_intent_objects. " +
                            "Το εργαλείο είναι μόνο structured output envelope και δεν εκτελεί business action."
                    }
                },
                ["tools"] = new JArray(BuildStructuredOutputTool()),
                ["tool_choice"] = new JObject
                {
                    ["type"] = "tool",
                    ["name"] = StructuredToolName
                },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = JarvisIntentOrchestration.BuildDecomposerUserPayload(userPrompt)
                    }
                }
            };

            string providerRequestJson = requestBody.ToString(Formatting.None);

            // Atlas is an already configured read/planning-capable route in the
            // current runtime. The synthetic tool itself is never executed.
            var proxyResp = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                .SendAsync(xSupport, "Atlas", providerRequestJson, cancellationToken)
                .ConfigureAwait(false);

            if (!proxyResp.Success)
                throw new InvalidOperationException(
                    proxyResp.CreditsExhausted
                        ? "AI credits exhausted during shadow decomposition."
                        : (proxyResp.ErrorMessage ?? "Shadow semantic call failed."));

            string structuredJson = ExtractStructuredToolInput(proxyResp.RawResponseJson);
            if (!string.IsNullOrWhiteSpace(structuredJson))
            {
                DebugLog.Log(
                    "[ORCH-SHADOW] structured decomposition received; runtimeAgent=" +
                    (proxyResp.RuntimeAgent ?? string.Empty) + " provider=" +
                    (proxyResp.RuntimeProvider ?? string.Empty) + " model=" +
                    (proxyResp.RuntimeModel ?? string.Empty));
                return structuredJson;
            }

            // Diagnostic compatibility fallback only. It does not weaken the
            // structured contract, but preserves visibility for older adapters.
            string raw = ExtractTextFromNormalizedResponse(proxyResp.RawResponseJson);
            if (string.IsNullOrWhiteSpace(raw))
                raw = (proxyResp.ResponseText ?? string.Empty).Trim();

            DebugLog.Log(
                "[ORCH-SHADOW] structured tool output missing; runtimeAgent=" +
                (proxyResp.RuntimeAgent ?? string.Empty) + " provider=" +
                (proxyResp.RuntimeProvider ?? string.Empty) + " model=" +
                (proxyResp.RuntimeModel ?? string.Empty) + " rawChars=" +
                (proxyResp.RawResponseJson ?? string.Empty).Length.ToString());

            return ExtractJsonObject(raw);
        }

        private static JObject BuildStructuredOutputTool()
        {
            return new JObject
            {
                ["name"] = StructuredToolName,
                ["description"] = "Return the decomposed autonomous intent objects. This is a structured output envelope only; it performs no action.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["intentObjects"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["properties"] = new JObject
                                {
                                    ["id"] = new JObject { ["type"] = "string" },
                                    ["intentFragment"] = new JObject { ["type"] = "string" },
                                    ["inputs"] = new JObject
                                    {
                                        ["type"] = "object",
                                        ["additionalProperties"] = true
                                    },
                                    ["candidates"] = new JObject
                                    {
                                        ["type"] = "array",
                                        ["items"] = new JObject
                                        {
                                            ["type"] = "object",
                                            ["additionalProperties"] = false,
                                            ["properties"] = new JObject
                                            {
                                                ["taskType"] = new JObject { ["type"] = "string" },
                                                ["confidence"] = new JObject
                                                {
                                                    ["type"] = "number",
                                                    ["minimum"] = 0.0,
                                                    ["maximum"] = 1.0
                                                }
                                            },
                                            ["required"] = new JArray("taskType", "confidence")
                                        }
                                    }
                                },
                                ["required"] = new JArray("id", "intentFragment", "inputs", "candidates")
                            }
                        }
                    },
                    ["required"] = new JArray("intentObjects")
                }
            };
        }

        private static string ExtractStructuredToolInput(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
                return string.Empty;

            JObject response = JObject.Parse(responseJson);
            string stopReason = response["stop_reason"] == null
                ? string.Empty
                : response["stop_reason"].ToString();
            if (string.Equals(stopReason, "refusal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shadow semantic decomposer refused the request.");

            JArray content = response["content"] as JArray ?? new JArray();
            JObject toolUse = content
                .OfType<JObject>()
                .FirstOrDefault(x =>
                    string.Equals((string)x["type"], "tool_use", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)x["name"], StructuredToolName, StringComparison.OrdinalIgnoreCase));

            if (toolUse == null || toolUse["input"] == null)
                return string.Empty;

            JObject input = toolUse["input"] as JObject;
            if (input == null)
                throw new InvalidOperationException("Structured decomposer tool input is not a JSON object.");

            return input.ToString(Formatting.None);
        }

        private static string ExtractTextFromNormalizedResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
                return string.Empty;

            JObject response = JObject.Parse(responseJson);
            JArray content = response["content"] as JArray ?? new JArray();
            IEnumerable<string> textParts = content
                .OfType<JObject>()
                .Where(x => string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase))
                .Select(x => x["text"] == null ? string.Empty : x["text"].ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join("\n", textParts).Trim();
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

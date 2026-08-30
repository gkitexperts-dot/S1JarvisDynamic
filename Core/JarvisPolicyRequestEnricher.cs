using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Last-mile centralized context injection for every logical AI request.
    /// Optimizers may compact protocol envelopes, but they cannot bypass the
    /// authoritative policy registry or the fact-only knowledge companion.
    /// </summary>
    internal static class JarvisPolicyRequestEnricher
    {
        private const string PolicyMarker = "[JARVIS_POLICY_CONTEXT]";
        private const string KnowledgeMarker = "[JARVIS_KNOWLEDGE_CONTEXT]";
        private const string OrchestrationInvariantMarker = "[JARVIS_ORCHESTRATION_INVARIANTS]";

        internal static string Apply(string agentName, string providerRequestJson)
        {
            if (string.IsNullOrWhiteSpace(providerRequestJson) || string.IsNullOrWhiteSpace(agentName))
                return providerRequestJson;

            JObject request = JObject.Parse(providerRequestJson);
            JArray system = NormalizeSystem(request);

            if (!ContainsMarker(system, PolicyMarker))
            {
                string policy = JarvisAgentContextBuilder.BuildPolicyContextForRequest(
                    agentName,
                    request.ToString(Formatting.None));
                if (!string.IsNullOrWhiteSpace(policy))
                    AddContextBlock(system, policy);
            }

            // Product-wide orchestration invariants are injected independently of
            // task/domain selection. They therefore cannot disappear because a
            // request was routed through a different logical agent or compact mode.
            if (!ContainsMarker(system, OrchestrationInvariantMarker))
                AddContextBlock(system, JarvisPolicySettings.Orchestration.BuildPolicyEnvelope());

            if (!ContainsMarker(system, KnowledgeMarker))
            {
                string knowledge = JarvisKnowledgeCompanion.BuildForRequest(
                    agentName,
                    request.ToString(Formatting.None));
                if (!string.IsNullOrWhiteSpace(knowledge))
                    AddContextBlock(system, knowledge);
            }

            return request.ToString(Formatting.None);
        }

        private static void AddContextBlock(JArray system, string text)
        {
            if (system == null || string.IsNullOrWhiteSpace(text)) return;
            system.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = new JObject { ["type"] = "ephemeral" }
            });
        }

        private static JArray NormalizeSystem(JObject request)
        {
            JArray blocks = request["system"] as JArray;
            if (blocks != null) return blocks;

            blocks = new JArray();
            JToken existing = request["system"];
            if (existing != null && existing.Type != JTokenType.Null)
            {
                string text = existing.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    blocks.Add(new JObject { ["type"] = "text", ["text"] = text });
            }
            request["system"] = blocks;
            return blocks;
        }

        private static bool ContainsMarker(JToken system, string marker)
        {
            if (system == null || string.IsNullOrWhiteSpace(marker)) return false;
            if (system.Type == JTokenType.String)
                return system.ToString().IndexOf(marker, StringComparison.Ordinal) >= 0;

            JArray blocks = system as JArray;
            if (blocks == null) return system.ToString().IndexOf(marker, StringComparison.Ordinal) >= 0;
            return blocks.OfType<JObject>().Any(block =>
                ((string)block["text"] ?? string.Empty).IndexOf(marker, StringComparison.Ordinal) >= 0);
        }
    }
}

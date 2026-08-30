using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Last-mile policy injection for every logical AI agent request. This sits
    /// above provider adapters, so no agent or legacy mode can bypass the
    /// centralized policy registry by carrying its own prompt only.
    /// </summary>
    internal static class JarvisPolicyRequestEnricher
    {
        private const string Marker = "[JARVIS_POLICY_CONTEXT]";

        internal static string Apply(string agentName, string providerRequestJson)
        {
            if (string.IsNullOrWhiteSpace(providerRequestJson) || string.IsNullOrWhiteSpace(agentName))
                return providerRequestJson;

            JObject request = JObject.Parse(providerRequestJson);
            string authoritative = JarvisAgentContextBuilder.BuildPolicyContextForRequest(
                agentName,
                providerRequestJson);
            if (string.IsNullOrWhiteSpace(authoritative))
                return providerRequestJson;

            // Requests already built by the orchestration control plane carry
            // the same authoritative marker in their system context. Do not
            // duplicate it. Legacy requests without the marker get it here.
            if (ContainsPolicyMarker(request["system"]))
                return providerRequestJson;

            JArray system = NormalizeSystem(request);
            system.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = authoritative,
                ["cache_control"] = new JObject { ["type"] = "ephemeral" }
            });

            return request.ToString(Formatting.None);
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

        private static bool ContainsPolicyMarker(JToken system)
        {
            if (system == null) return false;
            if (system.Type == JTokenType.String)
                return system.ToString().IndexOf(Marker, StringComparison.Ordinal) >= 0;

            JArray blocks = system as JArray;
            if (blocks == null) return system.ToString().IndexOf(Marker, StringComparison.Ordinal) >= 0;
            return blocks.OfType<JObject>().Any(block =>
                ((string)block["text"] ?? string.Empty).IndexOf(Marker, StringComparison.Ordinal) >= 0);
        }
    }
}

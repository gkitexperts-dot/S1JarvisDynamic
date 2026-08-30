using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic validation of proposed native tool arguments against the
    /// authoritative JarvisToolRegistry prerequisite contract.
    /// No tool-specific required-field lists are allowed here.
    /// </summary>
    internal static class JarvisToolContractValidator
    {
        internal static string[] ValidateProposedInput(string toolName, JObject input)
        {
            var issues = new List<string>();
            JarvisToolPrerequisiteDescriptor contract = JarvisToolRegistry.FindPrerequisites(toolName);
            if (contract == null)
            {
                issues.Add("No authoritative prerequisite contract is registered for tool: " + (toolName ?? "<null>"));
                return issues.ToArray();
            }

            JObject values = input ?? new JObject();

            foreach (string hardInput in contract.HardInputs ?? new string[0])
            {
                if (!HasValue(values[hardInput]))
                    issues.Add("Missing required tool input: " + hardInput);
            }

            foreach (string resolution in contract.ResolutionInputs ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(resolution))
                    continue;

                string token = resolution.Trim();
                bool optional = token.EndsWith("_optional", StringComparison.OrdinalIgnoreCase);
                if (optional)
                    token = token.Substring(0, token.Length - "_optional".Length);

                string[] alternatives = token
                    .Split(new[] { "_or_" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToArray();

                if (alternatives.Length == 0 || optional)
                    continue;

                if (!alternatives.Any(name => HasValue(values[name])))
                    issues.Add("Missing required resolution input: " + string.Join(" or ", alternatives));
            }

            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static string[] ValidateProducedOutputs(string toolName, JObject outputs)
        {
            var issues = new List<string>();
            JarvisToolPrerequisiteDescriptor contract = JarvisToolRegistry.FindPrerequisites(toolName);
            if (contract == null)
            {
                issues.Add("No authoritative prerequisite contract is registered for tool: " + (toolName ?? "<null>"));
                return issues.ToArray();
            }

            JObject values = outputs ?? new JObject();
            foreach (string produced in contract.Produces ?? new string[0])
            {
                if (!HasValue(values[produced]))
                    issues.Add("Missing registered tool output: " + produced);
            }
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool HasValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return false;
            if (token.Type == JTokenType.String)
                return !string.IsNullOrWhiteSpace(token.ToString());
            if (token.Type == JTokenType.Array)
                return ((JArray)token).Count > 0;
            return true;
        }
    }
}

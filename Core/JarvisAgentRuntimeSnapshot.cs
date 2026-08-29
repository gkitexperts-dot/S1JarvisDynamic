using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Session-scoped AI routing snapshot populated exactly once from the
    /// startup Verilic Health result. Runtime AI calls MUST use this snapshot
    /// and MUST NOT resolve provider/model independently per prompt.
    ///
    /// Architectural invariant:
    /// - Verilic is read at Jarvis startup to obtain effective Agent/Provider/Model.
    /// - The snapshot remains immutable for the lifetime of the opened Jarvis shell.
    /// - Changes made in Verilic become visible on the next Jarvis startup.
    /// - No desktop module may hardcode an AI model name.
    /// </summary>
    internal static class JarvisAgentRuntimeSnapshot
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, JarvisAgentRuntimeTarget> _targets =
            new Dictionary<string, JarvisAgentRuntimeTarget>(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        internal static bool IsInitialized
        {
            get { lock (Sync) return _initialized; }
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                _targets = new Dictionary<string, JarvisAgentRuntimeTarget>(
                    StringComparer.OrdinalIgnoreCase);
                _initialized = false;
            }
        }

        internal static bool TryInitialize(
            IReadOnlyList<JarvisAgentHealthTargetResult> healthTargets,
            out string issue)
        {
            issue = null;

            lock (Sync)
            {
                // Startup snapshot is intentionally immutable. A later HEALTH
                // command or any other code path must never replace routing in
                // the middle of an open Jarvis session.
                if (_initialized)
                    return true;
            }

            if (healthTargets == null || healthTargets.Count == 0)
            {
                issue = "startup health returned no agent targets";
                return false;
            }

            var next = new Dictionary<string, JarvisAgentRuntimeTarget>(
                StringComparer.OrdinalIgnoreCase);

            foreach (JarvisAgentHealthTargetResult source in healthTargets)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Agent))
                    continue;

                string agent = source.Agent.Trim();
                if (!source.Ready)
                {
                    issue = "startup agent is not ready: " + agent +
                            " (" + (source.ReasonCode ?? "provider_unavailable") + ")";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(source.Model))
                {
                    issue = "startup agent has no model: " + agent;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(source.Provider))
                {
                    issue = "startup agent has no provider: " + agent;
                    return false;
                }

                next[agent] = new JarvisAgentRuntimeTarget
                {
                    Agent = agent,
                    Provider = source.Provider.Trim(),
                    Model = source.Model.Trim(),
                    Inherited = source.Inherited
                };
            }

            string[] required =
            {
                "Jarvis", "Atlas", "Forge", "Compass",
                "Echo", "Sprint", "Scout", "Sage"
            };
            string missing = required.FirstOrDefault(x => !next.ContainsKey(x));
            if (!string.IsNullOrWhiteSpace(missing))
            {
                issue = "startup health is missing required agent: " + missing;
                return false;
            }

            lock (Sync)
            {
                if (_initialized)
                    return true;

                _targets = next;
                _initialized = true;
            }

            DebugLog.Log("[AI-STARTUP-SNAPSHOT] loaded agents=" +
                string.Join(",", next.Values
                    .OrderBy(x => x.Agent, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Agent + "=" + x.Provider + "/" + x.Model +
                        (x.Inherited ? "/Inherited" : "/Dedicated"))));
            return true;
        }

        internal static bool TryGet(string agentName, out JarvisAgentRuntimeTarget target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(agentName))
                return false;

            lock (Sync)
            {
                JarvisAgentRuntimeTarget found;
                if (!_initialized || !_targets.TryGetValue(agentName.Trim(), out found))
                    return false;

                target = found.Clone();
                return true;
            }
        }

        internal static IReadOnlyList<JarvisAgentRuntimeTarget> GetAll()
        {
            lock (Sync)
            {
                return _targets.Values
                    .OrderBy(x => x.Agent, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Clone())
                    .ToList();
            }
        }

        internal static string ResolveModel(string agentName)
        {
            JarvisAgentRuntimeTarget target;
            return TryGet(agentName, out target) ? target.Model : null;
        }

        internal static string ApplyModelToProviderRequest(
            string agentName,
            string providerRequestJson)
        {
            JarvisAgentRuntimeTarget target;
            if (!TryGet(agentName, out target))
                throw new InvalidOperationException(
                    "AI startup snapshot is unavailable for agent " +
                    (agentName ?? "<null>") + ". Restart Jarvis after a successful Health check.");

            JObject request = JObject.Parse(providerRequestJson ?? string.Empty);
            request["model"] = target.Model;
            return request.ToString(Formatting.None);
        }

        internal static bool MatchesRuntime(
            string agentName,
            string provider,
            string model,
            out string issue)
        {
            issue = null;
            JarvisAgentRuntimeTarget expected;
            if (!TryGet(agentName, out expected))
            {
                issue = "startup snapshot missing for " + (agentName ?? "<null>");
                return false;
            }

            if (!string.Equals(expected.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                issue = "model changed during Jarvis session; expected=" +
                        expected.Model + " actual=" + (model ?? "<null>");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expected.Provider) &&
                !string.IsNullOrWhiteSpace(provider) &&
                !string.Equals(expected.Provider, provider, StringComparison.OrdinalIgnoreCase))
            {
                issue = "provider changed during Jarvis session; expected=" +
                        expected.Provider + " actual=" + provider;
                return false;
            }

            return true;
        }
    }

    internal sealed class JarvisAgentRuntimeTarget
    {
        internal string Agent { get; set; }
        internal string Provider { get; set; }
        internal string Model { get; set; }
        internal bool Inherited { get; set; }

        internal JarvisAgentRuntimeTarget Clone()
        {
            return new JarvisAgentRuntimeTarget
            {
                Agent = Agent,
                Provider = Provider,
                Model = Model,
                Inherited = Inherited
            };
        }
    }
}

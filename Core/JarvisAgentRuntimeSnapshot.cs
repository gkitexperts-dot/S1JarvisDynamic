using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Session-scoped AI execution registry.
    ///
    /// Authoritative lifecycle:
    /// - BOOT loads Agent + Provider + Model + API credential from Verilic once.
    /// - Normal prompts never call Verilic for routing, models or credentials.
    /// - Explicit HEALTH may atomically replace the complete registry.
    /// - Reset/shutdown clears the in-memory credential buffers.
    ///
    /// No desktop module may persist an API credential or hardcode a provider/model.
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
            Dictionary<string, JarvisAgentRuntimeTarget> old;
            lock (Sync)
            {
                old = _targets;
                _targets = new Dictionary<string, JarvisAgentRuntimeTarget>(
                    StringComparer.OrdinalIgnoreCase);
                _initialized = false;
            }

            ClearTargets(old);
        }

        internal static bool TryInitialize(
            IReadOnlyList<JarvisAgentHealthTargetResult> healthTargets,
            out string issue)
        {
            lock (Sync)
            {
                if (_initialized)
                {
                    issue = null;
                    return true;
                }
            }

            return TryReplaceCore(healthTargets, false, out issue);
        }

        /// <summary>
        /// Explicit HEALTH refresh only. Builds and validates a complete next
        /// registry first, then swaps it atomically. If validation fails the
        /// currently working registry remains untouched.
        /// </summary>
        internal static bool TryRefresh(
            IReadOnlyList<JarvisAgentHealthTargetResult> healthTargets,
            out string issue)
        {
            return TryReplaceCore(healthTargets, true, out issue);
        }

        private static bool TryReplaceCore(
            IReadOnlyList<JarvisAgentHealthTargetResult> healthTargets,
            bool allowReplace,
            out string issue)
        {
            issue = null;

            Dictionary<string, JarvisAgentRuntimeTarget> next;
            if (!TryBuildTargets(healthTargets, out next, out issue))
                return false;

            Dictionary<string, JarvisAgentRuntimeTarget> old = null;
            lock (Sync)
            {
                if (_initialized && !allowReplace)
                {
                    ClearTargets(next);
                    return true;
                }

                old = _targets;
                _targets = next;
                _initialized = true;
            }

            ClearTargets(old);

            DebugLog.Log("[AI-SESSION-REGISTRY] " +
                (allowReplace ? "refreshed" : "loaded") + " agents=" +
                string.Join(",", next.Values
                    .OrderBy(x => x.Agent, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Agent + "=" + x.Provider + "/" + x.Model +
                        (x.Inherited ? "/Inherited" : "/Dedicated") + "/Credential=Loaded")));
            return true;
        }

        private static bool TryBuildTargets(
            IReadOnlyList<JarvisAgentHealthTargetResult> healthTargets,
            out Dictionary<string, JarvisAgentRuntimeTarget> next,
            out string issue)
        {
            issue = null;
            next = new Dictionary<string, JarvisAgentRuntimeTarget>(
                StringComparer.OrdinalIgnoreCase);

            if (healthTargets == null || healthTargets.Count == 0)
            {
                issue = "provisioning returned no agent targets";
                return false;
            }

            foreach (JarvisAgentHealthTargetResult source in healthTargets)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Agent))
                    continue;

                string agent = source.Agent.Trim();
                if (!source.Ready)
                {
                    issue = "startup agent is not ready: " + agent +
                            " (" + (source.ReasonCode ?? "provider_unavailable") + ")";
                    ClearTargets(next);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(source.Provider))
                {
                    issue = "startup agent has no provider: " + agent;
                    ClearTargets(next);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(source.Model))
                {
                    issue = "startup agent has no model: " + agent;
                    ClearTargets(next);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(source.ApiKey))
                {
                    issue = "startup agent has no API credential: " + agent;
                    ClearTargets(next);
                    return false;
                }

                var target = new JarvisAgentRuntimeTarget
                {
                    Agent = agent,
                    Provider = source.Provider.Trim(),
                    Model = source.Model.Trim(),
                    Inherited = source.Inherited
                };
                target.SetApiKey(source.ApiKey);
                next[agent] = target;
            }

            string[] required =
            {
                "Jarvis", "Atlas", "Forge", "Compass",
                "Echo", "Sprint", "Scout", "Sage"
            };
            string missing = required.FirstOrDefault(x => !next.ContainsKey(x));
            if (!string.IsNullOrWhiteSpace(missing))
            {
                issue = "startup provisioning is missing required agent: " + missing;
                ClearTargets(next);
                return false;
            }

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
                // Do not copy credentials for display/health reporting.
                return _targets.Values
                    .OrderBy(x => x.Agent, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.CloneWithoutSecret())
                    .ToList();
            }
        }

        internal static string ResolveModel(string agentName)
        {
            lock (Sync)
            {
                JarvisAgentRuntimeTarget target;
                return _initialized &&
                       !string.IsNullOrWhiteSpace(agentName) &&
                       _targets.TryGetValue(agentName.Trim(), out target)
                    ? target.Model
                    : null;
            }
        }

        internal static string ApplyModelToProviderRequest(
            string agentName,
            string providerRequestJson)
        {
            string model = ResolveModel(agentName);
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException(
                    "AI session registry is unavailable for agent " +
                    (agentName ?? "<null>") + ". Run HEALTH or restart Jarvis.");

            JObject request = JObject.Parse(providerRequestJson ?? string.Empty);
            request["model"] = model;
            return request.ToString(Formatting.None);
        }

        internal static bool MatchesRuntime(
            string agentName,
            string provider,
            string model,
            out string issue)
        {
            issue = null;
            lock (Sync)
            {
                JarvisAgentRuntimeTarget expected;
                if (!_initialized || string.IsNullOrWhiteSpace(agentName) ||
                    !_targets.TryGetValue(agentName.Trim(), out expected))
                {
                    issue = "session registry missing for " + (agentName ?? "<null>");
                    return false;
                }

                if (!string.Equals(expected.Model, model, StringComparison.OrdinalIgnoreCase))
                {
                    issue = "model differs from session registry; expected=" +
                            expected.Model + " actual=" + (model ?? "<null>");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(expected.Provider) &&
                    !string.IsNullOrWhiteSpace(provider) &&
                    !string.Equals(expected.Provider, provider, StringComparison.OrdinalIgnoreCase))
                {
                    issue = "provider differs from session registry; expected=" +
                            expected.Provider + " actual=" + provider;
                    return false;
                }

                return true;
            }
        }

        private static void ClearTargets(
            IDictionary<string, JarvisAgentRuntimeTarget> targets)
        {
            if (targets == null)
                return;

            foreach (JarvisAgentRuntimeTarget target in targets.Values)
            {
                try { target?.ClearSecret(); }
                catch { }
            }
        }
    }

    internal sealed class JarvisAgentRuntimeTarget : IDisposable
    {
        private byte[] _apiKeyUtf8;

        internal string Agent { get; set; }
        internal string Provider { get; set; }
        internal string Model { get; set; }
        internal bool Inherited { get; set; }

        internal bool HasApiKey
        {
            get { return _apiKeyUtf8 != null && _apiKeyUtf8.Length > 0; }
        }

        internal void SetApiKey(string apiKey)
        {
            ClearSecret();
            if (string.IsNullOrWhiteSpace(apiKey))
                return;

            _apiKeyUtf8 = Encoding.UTF8.GetBytes(apiKey.Trim());
        }

        /// <summary>
        /// Creates the short-lived managed string required by HttpClient headers.
        /// Callers must never log or persist the returned value.
        /// </summary>
        internal string GetApiKey()
        {
            return HasApiKey ? Encoding.UTF8.GetString(_apiKeyUtf8) : null;
        }

        internal JarvisAgentRuntimeTarget Clone()
        {
            var clone = new JarvisAgentRuntimeTarget
            {
                Agent = Agent,
                Provider = Provider,
                Model = Model,
                Inherited = Inherited
            };

            if (HasApiKey)
            {
                clone._apiKeyUtf8 = new byte[_apiKeyUtf8.Length];
                Buffer.BlockCopy(_apiKeyUtf8, 0, clone._apiKeyUtf8, 0, _apiKeyUtf8.Length);
            }
            return clone;
        }

        internal JarvisAgentRuntimeTarget CloneWithoutSecret()
        {
            return new JarvisAgentRuntimeTarget
            {
                Agent = Agent,
                Provider = Provider,
                Model = Model,
                Inherited = Inherited
            };
        }

        internal void ClearSecret()
        {
            if (_apiKeyUtf8 == null)
                return;

            Array.Clear(_apiKeyUtf8, 0, _apiKeyUtf8.Length);
            _apiKeyUtf8 = null;
        }

        public void Dispose()
        {
            ClearSecret();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Softone;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Core
{
    internal sealed class JarvisAgentHealthTargetResult
    {
        public string Agent { get; set; }
        public bool Ready { get; set; }
        public string ReasonCode { get; set; }
        public string AgentAccountRef { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public bool Inherited { get; set; }
        public string DiagnosticCode { get; set; }
        public string DiagnosticMessage { get; set; }
    }

    internal sealed class JarvisAgentHealthResult
    {
        public bool Ready { get; private set; }
        public bool CreditsExhausted { get; private set; }
        public string ReasonCode { get; private set; }
        public string Provider { get; private set; }
        public string Model { get; private set; }
        public string DiagnosticCode { get; private set; }
        public string DiagnosticMessage { get; private set; }
        public IReadOnlyList<JarvisAgentHealthTargetResult> Targets { get; private set; }

        public static JarvisAgentHealthResult Success(string provider, string model, IReadOnlyList<JarvisAgentHealthTargetResult> targets)
        {
            return new JarvisAgentHealthResult
            {
                Ready = true,
                ReasonCode = "provider_ready",
                Provider = Normalize(provider),
                Model = Normalize(model),
                Targets = targets ?? new List<JarvisAgentHealthTargetResult>()
            };
        }

        public static JarvisAgentHealthResult Failure(
            string reasonCode,
            bool creditsExhausted = false,
            string provider = null,
            string model = null,
            IReadOnlyList<JarvisAgentHealthTargetResult> targets = null,
            string diagnosticCode = null,
            string diagnosticMessage = null)
        {
            return new JarvisAgentHealthResult
            {
                Ready = false,
                CreditsExhausted = creditsExhausted,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? "provider_unavailable" : reasonCode,
                Provider = Normalize(provider),
                Model = Normalize(model),
                DiagnosticCode = Normalize(diagnosticCode),
                DiagnosticMessage = Normalize(diagnosticMessage),
                Targets = targets ?? new List<JarvisAgentHealthTargetResult>()
            };
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>
    /// Boot provisioning is exactly two remote steps:
    /// 1) contract ApiUsername/ApiValue -> short-lived Verilic clientKey;
    /// 2) clientKey + active Soft1 Serial/Company/Branch/User/Tool -> access approval
    ///    and the AI runtime information required for this in-memory session.
    /// No PC, Windows user, installation id or device binding is involved.
    /// </summary>
    internal sealed class JarvisAgentHealthProbe
    {
        private static readonly string[] Agents =
        {
            "Jarvis", "Atlas", "Forge", "Compass", "Echo", "Sprint", "Scout", "Sage"
        };

        public Task<JarvisAgentHealthResult> ProbeAsync(XSupport xSupport, string expectedAgentAccountRef)
        {
            return ProbeAsync(xSupport, expectedAgentAccountRef, null);
        }

        public async Task<JarvisAgentHealthResult> ProbeAsync(
            XSupport xSupport,
            string expectedAgentAccountRef,
            string expectedModel)
        {
            if (xSupport == null)
                return JarvisAgentHealthResult.Failure("provider_probe_identity_missing");

            try
            {
                DebugLog.Log("[AI-SESSION-REGISTRY] Verilic step-1 contract authentication start");
                VerilicRuntimeAuthorization auth = await VerilicRuntimeSession.AuthorizeAsync(
                    xSupport,
                    JarvisProducts.Jarvis);

                AccessCheckResponse access = auth == null ? null : auth.Access;
                if (access == null)
                    return JarvisAgentHealthResult.Failure("access_check_failed");
                if (!access.Allowed)
                    return JarvisAgentHealthResult.Failure("access_denied", diagnosticMessage: access.Message);
                if (string.IsNullOrWhiteSpace(access.AgentAccountRef))
                    return JarvisAgentHealthResult.Failure("agent_account_unavailable");
                if (string.IsNullOrWhiteSpace(access.RuntimeProvider))
                    return JarvisAgentHealthResult.Failure("provider_model_missing", diagnosticMessage: "Verilic did not return runtimeProvider.");
                if (string.IsNullOrWhiteSpace(access.RuntimeCredential))
                    return JarvisAgentHealthResult.Failure("provider_credential_unavailable");

                DebugLog.Log("[AI-SESSION-REGISTRY] Verilic step-2 entitlement approved; agent=" + access.AgentAccountRef);

                var targets = new List<JarvisAgentHealthTargetResult>(Agents.Length);
                foreach (string agent in Agents)
                {
                    string model = ResolveAgentModel(xSupport, agent, expectedModel);
                    targets.Add(new JarvisAgentHealthTargetResult
                    {
                        Agent = agent,
                        Ready = true,
                        ReasonCode = "provider_ready",
                        AgentAccountRef = access.AgentAccountRef.Trim(),
                        Provider = access.RuntimeProvider.Trim(),
                        Model = model,
                        ApiKey = access.RuntimeCredential,
                        Inherited = !string.Equals(agent, "Jarvis", StringComparison.OrdinalIgnoreCase)
                    });
                }

                DebugLog.Log("[AI-SESSION-REGISTRY] Verilic boot provisioning accepted; targets=" + targets.Count);
                return JarvisAgentHealthResult.Success(
                    access.RuntimeProvider,
                    targets[0].Model,
                    targets);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-SESSION-REGISTRY] Verilic contract provisioning exception: " +
                    ex.GetType().Name + " - " + ex.Message);
                return JarvisAgentHealthResult.Failure("provider_health_failed", diagnosticMessage: ex.Message);
            }
        }

        private static string ResolveAgentModel(XSupport xSupport, string agent, string expectedModel)
        {
            if (!string.IsNullOrWhiteSpace(expectedModel))
                return expectedModel.Trim();

            int code;
            switch (agent)
            {
                case "Forge": code = 500030; break;
                case "Compass": code = 500031; break;
                case "Echo": code = 500032; break;
                case "Sprint": code = 500033; break;
                case "Scout": code = 500034; break;
                case "Sage": code = 500035; break;
                default: code = 500029; break;
            }

            try
            {
                XTable table = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValueString FROM cccParams WHERE ParamCode=:1", code);
                if (table != null && table.Count > 0 &&
                    table.Current["ParamValueString"] != null &&
                    table.Current["ParamValueString"] != DBNull.Value)
                {
                    string value = Convert.ToString(table.Current["ParamValueString"]);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }
            catch { }

            return "claude-opus-5";
        }
    }
}

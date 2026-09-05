using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
        public string RuntimeTransport { get; set; }
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
    /// Boot/HEALTH provisioning uses the same canonical NativeS1 /verify contract
    /// as licensing. The response owns provider/model/transport selection and carries
    /// the provider credential only as an AES-GCM envelope derived from the compiled
    /// product recognition secret. No ApiUsername/clientKey/access-check path is used.
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

        public Task<JarvisAgentHealthResult> ProbeAsync(
            XSupport xSupport,
            string expectedAgentAccountRef,
            string expectedModel)
        {
            if (xSupport == null)
                return Task.FromResult(JarvisAgentHealthResult.Failure("provider_probe_identity_missing"));

            try
            {
                DebugLog.Log("[AI-SESSION-REGISTRY] NativeS1 /verify provisioning start");
                VerilicNativeS1VerificationSession session =
                    VerilicRuntimeLicenceProvider.Verify(xSupport, JarvisProducts.Jarvis);

                if (session == null || session.Verification == null || session.Credential == null)
                    return Task.FromResult(JarvisAgentHealthResult.Failure("verification_response_invalid"));

                VerilicVerifyLicenceResult verification = session.Verification;
                VerilicVerifyProductResult product = session.Product;
                if (!verification.Allowed || product == null || !product.Allowed)
                {
                    string reason = string.IsNullOrWhiteSpace(verification.ReasonCode)
                        ? "access_denied"
                        : verification.ReasonCode.Trim();
                    return Task.FromResult(JarvisAgentHealthResult.Failure(reason));
                }

                if (!product.RuntimeReady)
                {
                    string reason = string.IsNullOrWhiteSpace(product.RuntimeReasonCode)
                        ? "provider_credential_unavailable"
                        : product.RuntimeReasonCode.Trim();
                    return Task.FromResult(JarvisAgentHealthResult.Failure(
                        reason,
                        diagnosticMessage: product.RuntimeMessage));
                }

                JObject configuration;
                JObject defaultTarget;
                string configurationError;
                if (!TryResolveSingleConfiguration(product, out configuration, out defaultTarget, out configurationError))
                {
                    return Task.FromResult(JarvisAgentHealthResult.Failure(
                        configurationError,
                        diagnosticMessage: product.RuntimeMessage));
                }

                string contractId = ReadRequired(configuration, "contractId");
                JObject helperOverrides = configuration["helperOverrides"] as JObject;
                var targets = new List<JarvisAgentHealthTargetResult>(Agents.Length);

                foreach (string agent in Agents)
                {
                    bool inherited = true;
                    JObject target = defaultTarget;
                    if (!string.Equals(agent, "Jarvis", StringComparison.OrdinalIgnoreCase) &&
                        helperOverrides != null)
                    {
                        JProperty helper = helperOverrides.Property(agent, StringComparison.OrdinalIgnoreCase);
                        JObject overrideTarget = helper == null ? null : helper.Value as JObject;
                        if (overrideTarget != null)
                        {
                            target = overrideTarget;
                            inherited = false;
                        }
                    }
                    else if (string.Equals(agent, "Jarvis", StringComparison.OrdinalIgnoreCase))
                    {
                        inherited = false;
                    }

                    string agentAccountRef = ReadRequired(target, "agentAccountRef");
                    string provider = ReadRequired(target, "provider");
                    string model = ReadRequired(target, "model");
                    string runtimeTransport = ReadOptional(target, "runtimeTransport");
                    JObject encryptedCredential = target["credential"] as JObject;
                    if (encryptedCredential == null)
                        throw new InvalidOperationException("provider_credential_envelope_missing");

                    if (!string.IsNullOrWhiteSpace(expectedAgentAccountRef) &&
                        string.Equals(agent, "Jarvis", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(agentAccountRef, expectedAgentAccountRef.Trim(), StringComparison.Ordinal))
                    {
                        return Task.FromResult(JarvisAgentHealthResult.Failure("agent_account_mismatch"));
                    }

                    string apiKey = VerilicNativeS1CredentialDecryptor.Decrypt(
                        encryptedCredential,
                        session.Credential.Secret,
                        verification.DecisionId,
                        session.Credential.ProductId,
                        product.ProductId,
                        contractId,
                        agentAccountRef,
                        model);

                    targets.Add(new JarvisAgentHealthTargetResult
                    {
                        Agent = agent,
                        Ready = true,
                        ReasonCode = "provider_ready",
                        AgentAccountRef = agentAccountRef,
                        Provider = provider,
                        Model = model,
                        RuntimeTransport = runtimeTransport,
                        ApiKey = apiKey,
                        Inherited = inherited
                    });
                }

                string defaultProvider = ReadRequired(defaultTarget, "provider");
                string defaultModel = ReadRequired(defaultTarget, "model");
                DebugLog.Log("[AI-SESSION-REGISTRY] NativeS1 /verify provisioning accepted; targets=" +
                    targets.Count.ToString());
                return Task.FromResult(JarvisAgentHealthResult.Success(
                    defaultProvider,
                    defaultModel,
                    targets));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-SESSION-REGISTRY] NativeS1 /verify provisioning exception: " +
                    ex.GetType().Name + " - " + ex.Message);
                return Task.FromResult(JarvisAgentHealthResult.Failure(
                    "provider_health_failed",
                    diagnosticMessage: ex.Message));
            }
        }

        private static bool TryResolveSingleConfiguration(
            VerilicVerifyProductResult product,
            out JObject configuration,
            out JObject defaultTarget,
            out string reasonCode)
        {
            configuration = null;
            defaultTarget = null;
            reasonCode = null;

            if (product == null || product.AiConfigurations == null)
            {
                reasonCode = "ai_default_target_unavailable";
                return false;
            }

            int usable = 0;
            foreach (JObject candidate in product.AiConfigurations)
            {
                if (candidate == null)
                    continue;
                JObject candidateDefault = candidate["defaultTarget"] as JObject;
                if (candidateDefault == null)
                    continue;

                usable++;
                configuration = candidate;
                defaultTarget = candidateDefault;
            }

            if (usable == 0)
            {
                reasonCode = "ai_default_target_unavailable";
                return false;
            }
            if (usable > 1)
            {
                configuration = null;
                defaultTarget = null;
                reasonCode = "ai_multiple_configurations_available";
                return false;
            }

            return true;
        }

        private static string ReadRequired(JObject source, string propertyName)
        {
            string value = ReadOptional(source, propertyName);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("runtime_configuration_missing_" + propertyName);
            return value;
        }

        private static string ReadOptional(JObject source, string propertyName)
        {
            if (source == null)
                throw new InvalidOperationException("runtime_configuration_invalid");

            JToken value = source[propertyName];
            string text = value == null ? null : value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }
}

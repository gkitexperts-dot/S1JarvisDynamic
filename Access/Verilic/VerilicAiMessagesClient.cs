using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Compatibility name for the Jarvis runtime AI dispatcher.
    ///
    /// Verilic is NOT in the normal prompt path anymore. Boot/HEALTH populate
    /// JarvisAgentRuntimeSnapshot with Agent + Provider + Model + API key.
    /// Every normal AI call resolves the logical agent locally and dispatches
    /// directly to that provider through JarvisDirectAiTransport.
    /// </summary>
    internal sealed class VerilicAiMessagesClient
    {
        private static readonly HashSet<string> AllowedAgents = new HashSet<string>(
            new[] { "Jarvis", "Atlas", "Forge", "Compass", "Echo", "Sprint", "Scout", "Sage" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly AsyncLocal<string> ActiveAgentContext =
            new AsyncLocal<string>();

        private static readonly object CompanyNameCacheLock = new object();
        private static readonly Dictionary<string, string> CompanyNameCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static string LastRuntimeAgent { get; private set; }
        internal static string LastRuntimeProvider { get; private set; }
        internal static string LastRuntimeModel { get; private set; }
        internal static string LastRuntimeRouting { get; private set; }

        internal static void ResetRuntimeTargetSnapshot()
        {
            LastRuntimeAgent = null;
            LastRuntimeProvider = null;
            LastRuntimeModel = null;
            LastRuntimeRouting = null;
        }

        public Task<AgentProxyResponse> SendAsync(
            XSupport xSupport,
            string providerRequestJson,
            CancellationToken cancellationToken)
        {
            string agentName = ResolveAgentForCurrentAsyncFlow(providerRequestJson);
            return SendAsync(xSupport, agentName, providerRequestJson, cancellationToken);
        }

        public async Task<AgentProxyResponse> SendAsync(
            XSupport xSupport,
            string agentName,
            string providerRequestJson,
            CancellationToken cancellationToken)
        {
            if (xSupport == null)
                return Failure("messages_identity_missing");
            if (string.IsNullOrWhiteSpace(providerRequestJson))
                return Failure("provider_request_invalid");
            if (string.IsNullOrWhiteSpace(agentName))
                return Failure("routing_agent_invalid");

            agentName = agentName.Trim();
            if (!AllowedAgents.Contains(agentName))
                return Failure("routing_agent_invalid");

            JarvisAgentRuntimeTarget target;
            if (!JarvisAgentRuntimeSnapshot.TryGet(agentName, out target) ||
                target == null || !target.HasApiKey)
            {
                try { target?.Dispose(); } catch { }
                DebugLog.Log("[AI-SESSION-REGISTRY] request blocked; agent=" + agentName +
                    " reason=session_target_missing");
                return Failure("startup_agent_snapshot_missing");
            }

            try
            {
                // Caller-provided model values are never authoritative. The model
                // is overwritten from the boot/HEALTH session registry.
                providerRequestJson = JarvisAgentRuntimeSnapshot.ApplyModelToProviderRequest(
                    agentName,
                    providerRequestJson);

                providerRequestJson = ApplyCurrentCompanyContext(
                    xSupport,
                    providerRequestJson);

                providerRequestJson = VerilicProviderRequestOptimizer.TryOptimize(
                    agentName,
                    providerRequestJson);

                providerRequestJson = ApplyProductIdentityPolicy(
                    agentName,
                    providerRequestJson);
            }
            catch (Exception ex)
            {
                target.Dispose();
                DebugLog.Log("[AI-SESSION-REGISTRY] request preparation blocked; agent=" +
                    agentName + " reason=" + ex.Message);
                return Failure("provider_request_invalid");
            }

            string usageRequestId = Guid.NewGuid().ToString("N");
            AgentProxyResponse result;
            try
            {
                result = await JarvisDirectAiTransport.SendAsync(
                    agentName,
                    target,
                    providerRequestJson,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { target.Dispose(); } catch { }
                DebugLog.Log("[AI-DIRECT] unhandled dispatcher failure agent=" +
                    agentName + " error=" + ex.Message);
                result = Failure("provider_upstream_error");
            }

            if (result == null)
                result = Failure("provider_upstream_error");

            // JARVIS SUPERVISORY RECOVERY
            // ---------------------------
            // A logical agent is not authoritative about its own runtime
            // capabilities. The request/tool registry is authoritative. If an
            // otherwise successful model answer claims that a required tool or
            // access is unavailable while this exact request carried registered
            // tools, reject that answer and give the SAME logical agent one
            // corrective retry. This is provider/model-neutral and happens above
            // Google/OpenAI/Anthropic adapters. A real tool_result error is never
            // masked or retried by this rule.
            if (ShouldCorrectFalseCapabilityDenial(providerRequestJson, result))
            {
                HashSet<string> attachedTools = ReadToolNamesFromRequest(providerRequestJson);
                DebugLog.Log("[JARVIS-SUPERVISOR] rejected false capability denial; agent=" +
                    agentName + " tools=" + string.Join(",", attachedTools.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

                string correctedRequest = BuildCapabilityCorrectionRequest(
                    providerRequestJson,
                    result.ResponseText,
                    attachedTools);

                JarvisAgentRuntimeTarget retryTarget;
                if (JarvisAgentRuntimeSnapshot.TryGet(agentName, out retryTarget) &&
                    retryTarget != null && retryTarget.HasApiKey)
                {
                    AgentProxyResponse retryResult = null;
                    try
                    {
                        retryResult = await JarvisDirectAiTransport.SendAsync(
                            agentName,
                            retryTarget,
                            correctedRequest,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        try { retryTarget.Dispose(); } catch { }
                        DebugLog.Log("[JARVIS-SUPERVISOR] corrective retry failed; agent=" +
                            agentName + " error=" + ex.Message);
                    }

                    if (retryResult != null)
                    {
                        retryResult.UsageInputTokens += result.UsageInputTokens;
                        retryResult.UsageOutputTokens += result.UsageOutputTokens;
                        result = retryResult;
                        DebugLog.Log("[JARVIS-SUPERVISOR] corrective retry completed; agent=" +
                            agentName + " success=" + result.Success.ToString());
                    }
                }
                else
                {
                    try { retryTarget?.Dispose(); } catch { }
                    DebugLog.Log("[JARVIS-SUPERVISOR] corrective retry skipped; agent=" +
                        agentName + " reason=session_target_missing");
                }
            }

            if (result.Success)
            {
                LastRuntimeAgent = string.IsNullOrWhiteSpace(result.RuntimeAgent)
                    ? agentName
                    : result.RuntimeAgent;
                LastRuntimeProvider = result.RuntimeProvider;
                LastRuntimeModel = result.RuntimeModel;
                LastRuntimeRouting = result.RuntimeRouting;
            }

            JarvisAiUsageLogger.TryWrite(
                xSupport,
                usageRequestId,
                string.IsNullOrWhiteSpace(result.RuntimeAgent) ? agentName : result.RuntimeAgent,
                result.RuntimeProvider,
                result.RuntimeModel,
                result.UsageInputTokens,
                result.UsageOutputTokens,
                result.Success,
                result.Success ? null : "direct_provider_failed");

            DebugLog.Log("[AI-DIRECT] agent=" + agentName +
                " provider=" + (result.RuntimeProvider ?? "-") +
                " model=" + (result.RuntimeModel ?? "-") +
                " success=" + result.Success.ToString() +
                " usage=" + result.UsageInputTokens.ToString() + "/" +
                result.UsageOutputTokens.ToString());

            return result;
        }

        private static bool ShouldCorrectFalseCapabilityDenial(
            string providerRequestJson,
            AgentProxyResponse result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                return false;

            HashSet<string> tools = ReadToolNamesFromRequest(providerRequestJson);
            if (tools.Count == 0 || RequestContainsActualToolError(providerRequestJson))
                return false;

            string text = (result.ResponseText ?? string.Empty).ToLowerInvariant();
            bool capabilityNoun =
                text.Contains("εργαλ") || text.Contains("tool") ||
                text.Contains("πρόσβα") || text.Contains("προσβα") ||
                text.Contains("access") || text.Contains("capabil");
            if (!capabilityNoun)
                return false;

            return text.Contains("δεν διαθέτω") || text.Contains("δεν διαθετω") ||
                   text.Contains("δεν έχω") || text.Contains("δεν εχω") ||
                   text.Contains("δεν μπορώ") || text.Contains("δεν μπορω") ||
                   text.Contains("δεν είναι διαθέ") || text.Contains("δεν ειναι διαθε") ||
                   text.Contains("do not have") || text.Contains("don't have") ||
                   text.Contains("cannot access") || text.Contains("can't access") ||
                   text.Contains("not available") || text.Contains("unavailable");
        }

        private static HashSet<string> ReadToolNamesFromRequest(string providerRequestJson)
        {
            try
            {
                JObject request = JObject.Parse(providerRequestJson ?? "{}");
                return ReadToolNames(request["tools"]);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool RequestContainsActualToolError(string providerRequestJson)
        {
            try
            {
                JObject request = JObject.Parse(providerRequestJson ?? "{}");
                JArray messages = request["messages"] as JArray;
                if (messages == null)
                    return false;

                foreach (JObject message in messages.OfType<JObject>())
                {
                    JArray content = message["content"] as JArray;
                    if (content == null)
                        continue;

                    foreach (JObject block in content.OfType<JObject>())
                    {
                        if (!string.Equals((string)block["type"], "tool_result", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if ((bool?)block["is_error"] == true)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string BuildCapabilityCorrectionRequest(
            string providerRequestJson,
            string rejectedText,
            IEnumerable<string> attachedTools)
        {
            JObject request = JObject.Parse(providerRequestJson ?? "{}");
            JArray messages = request["messages"] as JArray;
            if (messages == null)
            {
                messages = new JArray();
                request["messages"] = messages;
            }

            if (!string.IsNullOrWhiteSpace(rejectedText))
                messages.Add(new JObject { ["role"] = "assistant", ["content"] = rejectedText });

            string toolList = string.Join(", ", (attachedTools ?? Enumerable.Empty<string>())
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = "[JARVIS_SUPERVISORY_CORRECTION] Policy=GLOBAL.REGISTRY_IS_AUTHORITY; attachedTools=" + toolList
            });

            return request.ToString(Formatting.None);
        }

        private static string ApplyCurrentCompanyContext(
            XSupport xSupport,
            string providerRequestJson)
        {
            try
            {
                var info = xSupport == null ? null : xSupport.ConnectionInfo;
                if (info == null || string.IsNullOrWhiteSpace(providerRequestJson))
                    return providerRequestJson;

                string companyName = ResolveCurrentCompanyName(
                    xSupport,
                    info.SerialNum == null ? null : info.SerialNum.ToString(),
                    info.CompanyId);
                string safeCompanyName = SanitizeCompanyName(companyName);
                string companyContextValue = string.IsNullOrWhiteSpace(safeCompanyName)
                    ? "UNKNOWN"
                    : safeCompanyName;

                JObject request = JObject.Parse(providerRequestJson);
                RewriteSystemCompanyContext(
                    request,
                    info.CompanyId,
                    companyContextValue);
                RewriteCompanySpecificToolDescriptions(request["tools"] as JArray);

                return request.ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                try
                {
                    DebugLog.Log("[COMPANY-CONTEXT] normalization skipped: " + ex.Message);
                }
                catch { }
                return providerRequestJson;
            }
        }

        private static string ApplyProductIdentityPolicy(
            string internalAgentName,
            string providerRequestJson)
        {
            // Product identity is a centralized global policy injected by
            // JarvisPolicyRequestEnricher. Keep this compatibility hook free of
            // independent policy prose.
            return providerRequestJson;
        }

        private static string ResolveCurrentCompanyName(
            XSupport xSupport,
            string serial,
            int companyId)
        {
            string cacheKey = (serial ?? string.Empty) + "|" + companyId.ToString();
            lock (CompanyNameCacheLock)
            {
                string cached;
                if (CompanyNameCache.TryGetValue(cacheKey, out cached))
                    return cached;
            }

            try
            {
                XTable table = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 NAME FROM COMPANY WHERE COMPANY=" + companyId.ToString());
                if (table == null || table.Count == 0)
                    return null;

                object raw = table.Current["NAME"];
                string name = raw == null ? null : raw.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                name = name.Trim();
                lock (CompanyNameCacheLock)
                    CompanyNameCache[cacheKey] = name;
                return name;
            }
            catch (Exception ex)
            {
                try
                {
                    DebugLog.Log(
                        "[COMPANY-CONTEXT] COMPANY lookup failed for CompanyId=" +
                        companyId.ToString() + ": " + ex.Message);
                }
                catch { }
                return null;
            }
        }

        private static string SanitizeCompanyName(string companyName)
        {
            if (string.IsNullOrWhiteSpace(companyName))
                return null;

            string value = companyName
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Trim();
            if (value.Length > 200)
                value = value.Substring(0, 200);
            return value;
        }

        private static void RewriteSystemCompanyContext(
            JObject request,
            int companyId,
            string companyName)
        {
            if (request == null || request["system"] == null)
                return;

            JToken system = request["system"];
            if (system.Type == JTokenType.String)
            {
                request["system"] = RewriteCompanySpecificText(
                    system.ToString(),
                    companyId,
                    companyName);
                return;
            }

            JArray blocks = system as JArray;
            if (blocks == null)
                return;

            foreach (JObject block in blocks.OfType<JObject>())
            {
                JToken text = block["text"];
                if (text == null || text.Type != JTokenType.String)
                    continue;

                block["text"] = RewriteCompanySpecificText(
                    text.ToString(),
                    companyId,
                    companyName);
            }
        }

        private static string RewriteCompanySpecificText(
            string text,
            int companyId,
            string companyName)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = text.Replace(
                "Είσαι ο Jarvis, ο ψηφιακός βοηθός μέσα στο Soft1 της Jetoil (εταιρία διανομής καυσίμων/πετρελαιοειδών).",
                "Είσαι ο Jarvis, ο ψηφιακός βοηθός μέσα στο Soft1 της ενεργής εταιρείας.");

            text = RemoveLineContaining(text, "Φορτώσεις καυσίμων:");

            string contextPrefix =
                "Τρέχον context: Company=" + companyId.ToString() + ", Branch=";
            string contextWithCompany =
                "Τρέχον context: Company=" + companyId.ToString() +
                ", CompanyName=" + companyName + ", Branch=";

            if (text.IndexOf(contextPrefix, StringComparison.Ordinal) >= 0)
                text = text.Replace(contextPrefix, contextWithCompany);
            else
                text = contextWithCompany + "UNKNOWN\n" + text;

            return text;
        }

        private static string RemoveLineContaining(string text, string marker)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker))
                return text;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var kept = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                if ((line ?? string.Empty).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                kept.Add(line);
            }
            return string.Join("\n", kept.ToArray());
        }

        private static void RewriteCompanySpecificToolDescriptions(JArray tools)
        {
            if (tools == null)
                return;

            foreach (JObject tool in tools.OfType<JObject>())
            {
                JToken description = tool["description"];
                if (description == null || description.Type != JTokenType.String)
                    continue;

                string value = description.ToString();
                value = value.Replace(
                    "πελάτες, φορτώσεις, τιμές, δεξαμενές, παραστατικά",
                    "πελάτες, προμηθευτές, είδη, παραστατικά, κινήσεις");
                tool["description"] = value;
            }
        }

        private static string ResolveAgentForCurrentAsyncFlow(string providerRequestJson)
        {
            try
            {
                JObject request = JObject.Parse(providerRequestJson);
                HashSet<string> tools = ReadToolNames(request["tools"]);

                if (tools.Count > 0)
                {
                    string resolved = ResolveFromCapabilities(tools, request["system"]);
                    ActiveAgentContext.Value = resolved;
                    return resolved;
                }

                if (!string.IsNullOrWhiteSpace(ActiveAgentContext.Value))
                    return ActiveAgentContext.Value;

                return ResolveDedicatedModeHeading(request["system"]) ?? "Atlas";
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveFromCapabilities(
            HashSet<string> tools,
            JToken systemToken)
        {
            if (tools.Contains("open_url") && tools.Contains("read_page_content"))
                return "Scout";
            if (tools.Contains("show_courier_documents") ||
                tools.Contains("get_courier_voucher_data") ||
                tools.Contains("create_courier_voucher"))
                return "Sprint";
            if (tools.Contains("get_item_template") || tools.Contains("create_item"))
                return "Forge";
            if (tools.Contains("find_trader_by_afm") ||
                tools.Contains("get_aade_data") ||
                tools.Contains("create_trader_from_aade"))
                return "Compass";
            if (tools.Contains("filter_email_inbox") ||
                tools.Contains("filter_calendar") ||
                tools.Contains("show_calendar_entries") ||
                tools.Contains("read_calendar"))
                return "Echo";

            string dedicated = ResolveDedicatedModeHeading(systemToken);
            if (string.Equals(dedicated, "Sage", StringComparison.Ordinal))
                return "Sage";

            return "Atlas";
        }

        private static string ResolveDedicatedModeHeading(JToken systemToken)
        {
            string text = ReadSystemText(systemToken);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.StartsWith("HELP MODE", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("🆘 HELP MODE", StringComparison.OrdinalIgnoreCase))
                    return "Sage";
                if (line.StartsWith("BROWSER MODE", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("🌐 BROWSER MODE", StringComparison.OrdinalIgnoreCase))
                    return "Scout";
                if (line.StartsWith("COURIER MODE", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("📦 COURIER MODE", StringComparison.OrdinalIgnoreCase))
                    return "Sprint";
                if (line.StartsWith("EMAIL MODE", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("📧 EMAIL MODE", StringComparison.OrdinalIgnoreCase))
                    return "Echo";
                if (line.StartsWith("- ΑΝΟΙΓΜΑ/ΔΗΜΙΟΥΡΓΙΑ ΕΙΔΟΥΣ", StringComparison.OrdinalIgnoreCase))
                    return "Forge";
                if (line.StartsWith("- ΑΝΟΙΓΜΑ/ΔΗΜΙΟΥΡΓΙΑ ΣΥΝΑΛΛΑΣΣΟΜΕΝΟΥ ΜΕ ΑΦΜ", StringComparison.OrdinalIgnoreCase))
                    return "Compass";
            }
            return null;
        }

        private static HashSet<string> ReadToolNames(JToken toolsToken)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JArray tools = toolsToken as JArray;
            if (tools == null)
                return names;

            foreach (JToken tool in tools)
            {
                string name = tool?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.Trim());
            }
            return names;
        }

        private static string ReadSystemText(JToken system)
        {
            if (system == null)
                return string.Empty;
            if (system.Type == JTokenType.String)
                return system.ToString();

            var builder = new StringBuilder();
            JArray blocks = system as JArray;
            if (blocks == null)
                return system.ToString();

            foreach (JToken block in blocks)
            {
                string text = block?["text"]?.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    if (builder.Length > 0)
                        builder.Append('\n');
                    builder.Append(text);
                }
            }
            return builder.ToString();
        }

        private static AgentProxyResponse Failure(string reasonCode)
        {
            string message;
            switch (reasonCode)
            {
                case "routing_agent_invalid":
                    message = "Ο λογικός AI agent δεν είναι έγκυρος.";
                    break;
                case "startup_agent_snapshot_missing":
                    message = "Το AI session registry δεν είναι διαθέσιμο. Εκτέλεσε HEALTH ή άνοιξε ξανά τον Jarvis.";
                    break;
                case "provider_request_invalid":
                    message = "Το αίτημα προς τον AI provider δεν είναι έγκυρο.";
                    break;
                case "messages_identity_missing":
                    message = "Δεν είναι διαθέσιμο το ενεργό Soft1 session.";
                    break;
                default:
                    message = "Η κλήση προς τον AI provider απέτυχε (" + reasonCode + ").";
                    break;
            }

            return new AgentProxyResponse
            {
                Success = false,
                CreditsExhausted = false,
                ErrorMessage = message,
                RawResponseJson = string.Empty
            };
        }
    }
}

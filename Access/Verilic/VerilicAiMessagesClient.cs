using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Sends Jarvis provider requests through the signed Verilic messages
    /// endpoint. Provider credentials and authoritative AgentAccountRef remain
    /// server-side. Effective agent models are loaded once by the startup Health
    /// check into JarvisAgentRuntimeSnapshot and are reused for the entire open
    /// Jarvis session; this client never performs per-prompt model discovery.
    ///
    /// The preferred overload accepts the logical Jarvis agent explicitly.
    /// The compatibility overload used by the mature Jarvis tool loop resolves
    /// the same logical role from structural request capabilities on the first
    /// iteration and keeps that role in AsyncLocal state for later iterations
    /// (including the final no-tools iteration). It never lets descriptive
    /// Browser/Item/Trader/Email text steal another agent's route.
    /// </summary>
    internal sealed class VerilicAiMessagesClient
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
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

        private static void CaptureRuntimeTarget(MessagesResponse result)
        {
            if (result == null || !result.Success)
                return;

            LastRuntimeAgent = result.Agent;
            LastRuntimeProvider = result.Provider;
            LastRuntimeModel = result.Model;
            LastRuntimeRouting = result.Routing;
        }

        private sealed class MessagesRequest
        {
            public string ProductId { get; set; }
            public string InstallationId { get; set; }
            public string ProductVersion { get; set; }
            public string[] RequestedFeatures { get; set; }
            public string Soft1Serial { get; set; }
            public string CompanyCode { get; set; }
            public string BranchCode { get; set; }
            public string Soft1UserId { get; set; }
            public string AgentName { get; set; }
            public string ProviderRequestJson { get; set; }
        }

        private sealed class MessagesResponse
        {
            public bool Success { get; set; }
            public string ReasonCode { get; set; }
            public string ResponseText { get; set; }
            public bool CreditsExhausted { get; set; }
            public int UsageInputTokens { get; set; }
            public int UsageOutputTokens { get; set; }
            public string RawResponseJson { get; set; }
            public string Agent { get; set; }
            public string Provider { get; set; }
            public string Model { get; set; }
            public string Routing { get; set; }
        }

        static VerilicAiMessagesClient()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
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

            // NON-NEGOTIABLE routing invariant: the model comes only from the
            // immutable startup Health snapshot. Any model value a caller may
            // have placed in its JSON is overwritten here. No per-prompt route,
            // schema or model lookup is permitted at this boundary.
            try
            {
                providerRequestJson = JarvisAgentRuntimeSnapshot.ApplyModelToProviderRequest(
                    agentName,
                    providerRequestJson);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[AI-STARTUP-SNAPSHOT] request blocked; agent=" +
                    agentName + " reason=" + ex.Message);
                return Failure("startup_agent_snapshot_missing");
            }

            // Normalize company awareness at the final desktop -> Verilic boundary.
            // XSupport.ConnectionInfo.CompanyId is authoritative for the active
            // Soft1 company. The display name is resolved from COMPANY and the
            // request is made company-neutral before any provider sees it.
            providerRequestJson = ApplyCurrentCompanyContext(
                xSupport,
                providerRequestJson);

            // Conservative fast path for clearly read-only Atlas/reporting turns.
            // Dedicated agents and action requests remain byte-for-byte unchanged
            // apart from the common company-context normalization above.
            providerRequestJson = VerilicProviderRequestOptimizer.TryOptimize(
                agentName,
                providerRequestJson);

            // Product identity is a boundary invariant, independent of whichever
            // internal execution role was selected. Apply it AFTER optimization so
            // compact role prompts can never redefine Atlas/Forge/etc. as the visible
            // assistant identity.
            providerRequestJson = ApplyProductIdentityPolicy(
                agentName,
                providerRequestJson);

            // Local-only correlation id for Soft1 usage telemetry. It contains
            // no prompt/response content and is not sent to the provider.
            string usageRequestId = Guid.NewGuid().ToString("N");

            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();
                if (configuration.Mode != VerilicRuntimeMode.Verilic ||
                    configuration.LicensingOrigin == null)
                    return Failure("messages_configuration_invalid");

                string productId = configuration.ResolveProductId(
                    JarvisProducts.Jarvis);

                var stateStore = new VerilicInstallationStateStore(
                    configuration.StateDirectory,
                    configuration.ProtectionScope);
                VerilicInstallationState state = stateStore.Load(
                    JarvisProducts.Jarvis);

                if (state == null || !state.ActivationCompleted ||
                    string.IsNullOrWhiteSpace(state.InstallationId) ||
                    state.InstallationId.StartsWith("pending_", StringComparison.Ordinal) ||
                    !string.Equals(state.VerilicProductId, productId, StringComparison.Ordinal) ||
                    !string.Equals(state.KeyAlgorithm, "ES256", StringComparison.Ordinal) ||
                    state.PrivateKeyMaterial == null || state.PrivateKeyMaterial.Length == 0)
                    return Failure("messages_installation_invalid");

                var info = xSupport.ConnectionInfo;
                if (info == null)
                    return Failure("messages_identity_missing");

                var body = new MessagesRequest
                {
                    ProductId = productId,
                    InstallationId = state.InstallationId,
                    ProductVersion = configuration.ProductVersion,
                    RequestedFeatures = new string[0],
                    Soft1Serial = info.SerialNum == null ? null : info.SerialNum.ToString(),
                    CompanyCode = info.CompanyId.ToString(),
                    BranchCode = info.BranchId.ToString(),
                    Soft1UserId = info.UserId.ToString(),
                    AgentName = agentName,
                    ProviderRequestJson = providerRequestJson
                };

                string json = JsonConvert.SerializeObject(body);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

                var origin = new Uri(
                    configuration.LicensingOrigin.GetLeftPart(UriPartial.Authority) + "/");
                var messagesUri = new Uri(origin, "api/jarvis-ai/messages");

                var proofState = new VerilicInstallationState
                {
                    ProductCode = productId,
                    InstallationId = state.InstallationId,
                    KeyAlgorithm = state.KeyAlgorithm,
                    PrivateKeyMaterial = state.PrivateKeyMaterial
                };

                using (var authorizer = new VerilicEs256RequestAuthorizer(proofState))
                using (var request = new HttpRequestMessage(HttpMethod.Post, messagesUri))
                using (var timeout = new CancellationTokenSource(Timeout))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token))
                {
                    var content = new ByteArrayContent(bodyBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8"
                    };
                    request.Content = content;
                    authorizer.Authorize(request, bodyBytes);

                    using (HttpResponseMessage response = await Http.SendAsync(
                        request,
                        linked.Token))
                    {
                        string responseJson = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return Failure(
                                "messages_http_" + ((int)response.StatusCode).ToString());

                        MessagesResponse result;
                        try
                        {
                            result = JsonConvert.DeserializeObject<MessagesResponse>(
                                responseJson);
                        }
                        catch (JsonException)
                        {
                            return Failure("messages_response_invalid");
                        }

                        if (result == null)
                            return Failure("messages_response_invalid");

                        CaptureRuntimeTarget(result);

                        if (!result.Success)
                            LogProviderFailure(result, agentName);

                        // One raw event per parsed Verilic/provider response.
                        // This is deliberately best-effort: the logger catches
                        // all SQL errors and never blocks the AI response.
                        JarvisAiUsageLogger.TryWrite(
                            xSupport,
                            usageRequestId,
                            string.IsNullOrWhiteSpace(result.Agent) ? agentName : result.Agent,
                            result.Provider,
                            result.Model,
                            result.UsageInputTokens,
                            result.UsageOutputTokens,
                            result.Success,
                            result.Success ? null : GetBaseReasonCode(result.ReasonCode));

                        if (result.Success)
                        {
                            string runtimeIssue;
                            string runtimeAgent = string.IsNullOrWhiteSpace(result.Agent)
                                ? agentName
                                : result.Agent;
                            if (!JarvisAgentRuntimeSnapshot.MatchesRuntime(
                                runtimeAgent,
                                result.Provider,
                                result.Model,
                                out runtimeIssue))
                            {
                                DebugLog.Log("[AI-STARTUP-SNAPSHOT] runtime drift blocked; agent=" +
                                    runtimeAgent + " reason=" + (runtimeIssue ?? "unknown"));
                                return new AgentProxyResponse
                                {
                                    Success = false,
                                    CreditsExhausted = false,
                                    ErrorMessage = BuildSafeErrorMessage("startup_agent_snapshot_changed"),
                                    RawResponseJson = string.Empty,
                                    UsageInputTokens = result.UsageInputTokens,
                                    UsageOutputTokens = result.UsageOutputTokens,
                                    RuntimeAgent = result.Agent,
                                    RuntimeProvider = result.Provider,
                                    RuntimeModel = result.Model,
                                    RuntimeRouting = result.Routing
                                };
                            }
                        }

                        return new AgentProxyResponse
                        {
                            Success = result.Success,
                            CreditsExhausted = result.CreditsExhausted,
                            ErrorMessage = result.Success
                                ? null
                                : BuildSafeErrorMessage(GetBaseReasonCode(result.ReasonCode)),
                            ResponseText = result.ResponseText,
                            RawResponseJson = result.RawResponseJson ?? string.Empty,
                            UsageInputTokens = result.UsageInputTokens,
                            UsageOutputTokens = result.UsageOutputTokens,
                            RuntimeAgent = result.Agent,
                            RuntimeProvider = result.Provider,
                            RuntimeModel = result.Model,
                            RuntimeRouting = result.Routing
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;

                return Failure("provider_timeout");
            }
            catch
            {
                return Failure("messages_transport_failed");
            }
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
                    ? "UNKNOWN (δεν ανακτήθηκε από COMPANY - μην υποθέσεις όνομα/κλάδο)"
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
            if (string.IsNullOrWhiteSpace(providerRequestJson))
                return providerRequestJson;

            try
            {
                JObject request = JObject.Parse(providerRequestJson);
                string role = string.IsNullOrWhiteSpace(internalAgentName)
                    ? "internal"
                    : internalAgentName.Trim();

                const string identityRule =
                    "PRODUCT IDENTITY — AUTHORITATIVE: Είσαι ο Jarvis, ο ψηφιακός βοηθός μέσα στο Soft1. " +
                    "Ο χειριστής μιλά πάντα με τον Jarvis. Τα ονόματα Atlas, Forge, Compass, Echo, Sprint, Scout και Sage είναι μόνο εσωτερικοί execution roles και ΔΕΝ αποτελούν ξεχωριστές ορατές ταυτότητες. " +
                    "Μην αυτοσυστήνεσαι ποτέ ως εσωτερικός agent και μην λες ότι είσαι ο Atlas/Forge/Compass/Echo/Sprint/Scout/Sage. " +
                    "Αν ο χειριστής ρωτήσει ποιος είσαι ή πώς σε λένε, απάντησε ότι είσαι ο Jarvis. " +
                    "Αν ρωτήσει για εσωτερικούς agents/αρχιτεκτονική, μην επινοήσεις ονόματα, πλήθος ή ρόλους· εξήγησε μόνο ότι ο Jarvis χρησιμοποιεί εσωτερική δρομολόγηση σε εξειδικευμένες δυνατότητες και ότι αυτό είναι implementation detail.";

                JArray blocks = request["system"] as JArray;
                if (blocks == null)
                {
                    string existing = request["system"] == null
                        ? string.Empty
                        : request["system"].ToString();
                    blocks = new JArray();
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        blocks.Add(new JObject
                        {
                            ["type"] = "text",
                            ["text"] = existing
                        });
                    }
                    request["system"] = blocks;
                }

                // Remove any compact prompt phrasing that explicitly promotes the
                // internal role to visible identity. Role remains available through
                // AgentName for routing/telemetry, not as assistant persona.
                foreach (JObject block in blocks.OfType<JObject>())
                {
                    JToken textToken = block["text"];
                    if (textToken == null || textToken.Type != JTokenType.String)
                        continue;

                    string text = textToken.ToString();
                    if (!string.Equals(role, "Jarvis", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text.Replace(
                            "Είσαι ο " + role + " του Jarvis μέσα στο Soft1.",
                            "Είσαι ο Jarvis μέσα στο Soft1.");
                        text = text.Replace(
                            "Είσαι ο " + role + ",",
                            "Είσαι ο Jarvis,");
                    }
                    block["text"] = text;
                }

                blocks.Insert(0, new JObject
                {
                    ["type"] = "text",
                    ["text"] = identityRule,
                    ["cache_control"] = new JObject { ["type"] = "ephemeral" }
                });

                return request.ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                try { DebugLog.Log("[JARVIS-IDENTITY] provider policy skipped: " + ex.Message); }
                catch { }
                return providerRequestJson;
            }
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
                // Runs synchronously before the first await in SendAsync, so the
                // Soft1 SDK call stays on the caller/integration thread.
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

            // Remove the legacy company-specific fuel-loading schema from the
            // common prompt. Such custom schema belongs to company-specific
            // knowledge, not to the product-wide Jarvis contract.
            text = RemoveLineContaining(text, "Φορτώσεις καυσίμων:");

            string contextPrefix =
                "Τρέχον context: Company=" + companyId.ToString() + ", Branch=";
            string contextWithCompany =
                "Τρέχον context: Company=" + companyId.ToString() +
                ", CompanyName=" + companyName + ", Branch=";

            if (text.IndexOf(contextPrefix, StringComparison.Ordinal) >= 0)
            {
                text = text.Replace(contextPrefix, contextWithCompany);
            }
            else
            {
                text = contextWithCompany + "UNKNOWN\n" + text;
            }

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

        private static void LogProviderFailure(MessagesResponse result, string fallbackAgent)
        {
            if (result == null || result.Success)
                return;

            string reason = GetBaseReasonCode(result.ReasonCode);
            string diagnostic = GetProviderDiagnostic(result.ReasonCode);
            string code;
            string param;
            string message;
            ParseProviderDiagnostic(diagnostic, out code, out param, out message);

            var log = new StringBuilder();
            log.Append("[AI-PROVIDER-ERROR]");
            log.Append(" agent=").Append(SafeLogValue(
                string.IsNullOrWhiteSpace(result.Agent) ? fallbackAgent : result.Agent, 64));
            log.Append(" provider=").Append(SafeLogValue(result.Provider, 64));
            log.Append(" model=").Append(SafeLogValue(result.Model, 128));
            log.Append(" reason=").Append(SafeLogValue(reason, 128));

            if (!string.IsNullOrWhiteSpace(code))
                log.Append(" code=").Append(SafeLogValue(code, 128));
            if (!string.IsNullOrWhiteSpace(param))
                log.Append(" param=").Append(SafeLogValue(param, 128));
            if (!string.IsNullOrWhiteSpace(message))
                log.Append(" message=").Append(SafeLogValue(message, 512));

            DebugLog.Log(log.ToString());
        }

        private static string GetProviderDiagnostic(string reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                return string.Empty;

            int separator = reasonCode.IndexOf('|');
            return separator < 0 || separator + 1 >= reasonCode.Length
                ? string.Empty
                : reasonCode.Substring(separator + 1).Trim();
        }

        private static void ParseProviderDiagnostic(
            string diagnostic,
            out string code,
            out string param,
            out string message)
        {
            code = string.Empty;
            param = string.Empty;
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(diagnostic))
                return;

            string remaining = diagnostic.Trim();
            int dot = remaining.IndexOf('·');
            if (dot >= 0)
            {
                code = remaining.Substring(0, dot).Trim();
                remaining = dot + 1 < remaining.Length
                    ? remaining.Substring(dot + 1).Trim()
                    : string.Empty;
            }

            const string paramPrefix = "param=";
            if (remaining.StartsWith(paramPrefix, StringComparison.OrdinalIgnoreCase))
            {
                int colon = remaining.IndexOf(':');
                if (colon >= 0)
                {
                    param = remaining.Substring(paramPrefix.Length, colon - paramPrefix.Length).Trim();
                    message = colon + 1 < remaining.Length
                        ? remaining.Substring(colon + 1).Trim()
                        : string.Empty;
                }
                else
                {
                    param = remaining.Substring(paramPrefix.Length).Trim();
                }
            }
            else
            {
                message = remaining;
            }

            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
                message = diagnostic.Trim();
        }

        private static string SafeLogValue(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            string safe = value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Trim();

            if (safe.Length > maxLength)
                safe = safe.Substring(0, maxLength);

            return safe;
        }

        private static string GetBaseReasonCode(string reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                return "provider_unavailable";

            string trimmed = reasonCode.Trim();
            int separator = trimmed.IndexOf('|');
            return separator < 0
                ? trimmed
                : trimmed.Substring(0, separator).Trim();
        }

        private static AgentProxyResponse Failure(string reasonCode)
        {
            return new AgentProxyResponse
            {
                Success = false,
                CreditsExhausted = string.Equals(
                    reasonCode,
                    "provider_credits_exhausted",
                    StringComparison.Ordinal),
                ErrorMessage = BuildSafeErrorMessage(GetBaseReasonCode(reasonCode)),
                RawResponseJson = string.Empty
            };
        }

        private static string BuildSafeErrorMessage(string reasonCode)
        {
            string reason = GetBaseReasonCode(reasonCode);

            switch (reason)
            {
                case "provider_auth_failed":
                    return "Ο AI provider απέρριψε τα διαπιστευτήρια.";
                case "provider_model_or_request_invalid":
                    return "Το επιλεγμένο AI model ή το αίτημα δεν είναι έγκυρο.";
                case "provider_credits_exhausted":
                    return "Το AI account αυτής της άδειας έχει εξαντλήσει τα credits του.";
                case "provider_rate_limited":
                    return "Ο AI provider έχει προσωρινό όριο κλήσεων. Δοκίμασε ξανά σε λίγο.";
                case "provider_timeout":
                    return "Ο AI provider δεν απάντησε εγκαίρως.";
                case "provider_chat_adapter_unavailable":
                    return "Ο provider δεν υποστηρίζεται ακόμη από το Jarvis chat.";
                case "provider_credential_unavailable":
                    return "Τα διαπιστευτήρια του AI provider δεν είναι διαθέσιμα.";
                case "provider_model_missing":
                    return "Δεν έχει οριστεί AI model για αυτή τη ρύθμιση.";
                case "agent_account_unavailable":
                    return "Το AI agent account δεν είναι διαθέσιμο.";
                case "provider_customer_mismatch":
                    return "Το AI agent account δεν ανήκει στον πελάτη της ενεργής άδειας.";
                case "routing_agent_invalid":
                    return "Ο λογικός AI agent δεν είναι έγκυρος για αυτή τη δρομολόγηση.";
                case "startup_agent_snapshot_missing":
                    return "Το AI routing snapshot της εκκίνησης δεν είναι διαθέσιμο. Κλείσε και άνοιξε ξανά τον Jarvis.";
                case "startup_agent_snapshot_changed":
                    return "Το AI routing άλλαξε μετά την εκκίνηση. Κλείσε και άνοιξε ξανά τον Jarvis για να φορτωθεί το νέο schema.";
                case "provider_upstream_error":
                    return "Ο AI provider επέστρεψε προσωρινό σφάλμα.";
                default:
                    if (reason.StartsWith("routing_", StringComparison.Ordinal) ||
                        reason.StartsWith("licence_", StringComparison.Ordinal) ||
                        reason.StartsWith("proof_", StringComparison.Ordinal) ||
                        reason.StartsWith("messages_", StringComparison.Ordinal))
                        return "Η ασφαλής δρομολόγηση AI δεν είναι διαθέσιμη (" + reason + ").";

                    return "Η κλήση προς τον AI provider απέτυχε (" + reason + ").";
            }
        }
    }
}

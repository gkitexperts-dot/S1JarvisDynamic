using System;
using System.Collections.Generic;
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
    /// endpoint. Provider credentials, authoritative AgentAccountRef and the
    /// configured model remain server-side.
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

                        if (!result.Success && HasProviderDiagnostic(result.ReasonCode))
                        {
                            DebugLog.Log("[VERILIC] provider diagnostic=" + result.ReasonCode);
                        }

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

        private static bool HasProviderDiagnostic(string reasonCode)
        {
            return !string.IsNullOrWhiteSpace(reasonCode) &&
                reasonCode.IndexOf('|') >= 0;
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
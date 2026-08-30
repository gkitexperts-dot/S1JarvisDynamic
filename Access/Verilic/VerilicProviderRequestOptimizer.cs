using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Provider-neutral request optimization at the final desktop -> Verilic boundary.
    ///
    /// Principles:
    /// - Never changes provider/model/agent routing authority.
    /// - Current-turn tool_use/tool_result state is preserved exactly.
    /// - Completed OLD tool traces are removed, but durable facts such as exported
    ///   file paths and successful tool executions are retained in compact system context.
    /// - Tool schemas are selected per intent, not merely per broad agent domain.
    /// - Cross-turn clarification answers inherit the active protocol intent when safe.
    /// - Ambiguous business requests fail open to the mature/full request.
    /// - Obvious greetings use a no-tools fast path.
    /// </summary>
    internal static class VerilicProviderRequestOptimizer
    {
        private const int ConversationalMaxOutputTokens = 512;
        private const int ConversationalHistoryMessages = 6;
        private const int ToolBudgetTopCount = 10;

        private static readonly object ToolBudgetLock = new object();
        private static readonly HashSet<string> LoggedToolBudgetSignatures =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<string> DirectExportTools = Set(
            "query_data", "export_query_to_file", "export_shown_table");

        private static readonly HashSet<string> LatestDocumentTools = Set(
            "query_data");

        private static readonly HashSet<string> ReadTools = Set(
            "query_data", "export_query_to_file", "open_document",
            "get_conversion_targets", "export_shown_table");

        private static readonly HashSet<string> ForgeTools = Set(
            "query_data", "open_document", "get_item_template", "create_item",
            "export_query_to_file", "export_shown_table");

        private static readonly HashSet<string> CompassTools = Set(
            "query_data", "open_document", "find_trader_by_afm", "get_aade_data",
            "create_trader_from_aade", "export_query_to_file", "export_shown_table");

        private static readonly HashSet<string> EchoContactTools = Set(
            "query_data", "search_outlook_contacts", "show_contact_results");

        private static readonly HashSet<string> EchoInboxTools = Set(
            "filter_email_inbox", "read_email", "download_email_attachment");

        private static readonly HashSet<string> EchoCalendarTools = Set(
            "filter_calendar", "show_calendar_entries", "read_calendar",
            "create_outlook_event", "create_crm_task");

        private static readonly HashSet<string> EchoDraftTools = Set(
            "query_data", "search_outlook_contacts", "show_contact_results");

        private static readonly HashSet<string> EchoSendTools = Set(
            "query_data", "send_email", "reply_email", "search_outlook_contacts");

        private static readonly HashSet<string> EchoExportTools = Set(
            "query_data", "export_query_to_file", "export_shown_table",
            "open_document", "search_outlook_contacts", "send_email");

        private static readonly HashSet<string> EchoAllTools = Set(
            "query_data", "open_document", "export_query_to_file", "export_shown_table",
            "create_crm_task", "read_email", "download_email_attachment", "read_calendar",
            "filter_email_inbox", "filter_calendar", "show_calendar_entries",
            "send_email", "reply_email", "show_contact_results",
            "search_outlook_contacts", "create_outlook_event");

        private static readonly HashSet<string> SprintTools = Set(
            "query_data", "open_document", "show_courier_documents",
            "cancel_courier_voucher", "get_courier_voucher_data",
            "create_courier_voucher");

        private static readonly HashSet<string> ScoutTools = Set(
            "open_url", "read_page_content", "extract_page_tables",
            "query_data", "export_query_to_file", "open_document",
            "get_conversion_targets", "create_crm_task", "create_order",
            "read_email", "download_email_attachment", "send_email", "reply_email",
            "show_contact_results", "search_outlook_contacts", "create_outlook_event",
            "get_item_template", "create_item");

        private static readonly HashSet<string> SageTools = Set(
            "query_data", "open_document", "export_query_to_file");

        private static readonly string[] ReadSignals =
        {
            "φερε", "δωσε", "δειξε", "βρες", "βγαλε", "ποια", "ποιο", "ποσο",
            "λιστα", "παραστατικ", "κινησ", "υπολοιπ", "τζιρ", "πωλησ", "αγορ",
            "αναφορ", "report", "στοιχεια", "καρτελα", "φορτωσ", "γραφημα", "chart",
            "top ", "στατιστικ", "συνολο", "μεσο", "μεση", "ημερομην", "count"
        };

        private static readonly string[] ActionSignals =
        {
            "στειλ", "στελν", "απαντησ", "reply",
            "δημιουργ", "καταχωρ", "περασε", "φτιαξε", "ακυρω", "voucher", "courier",
            "υπενθυμ", "ραντεβου", "εργασ", "task", "μετατρεψ", "μετατροπ",
            "νεο ειδος", "νεο πελατ", "νεο προμηθευτ", "εισαγωγ", "import"
        };

        private static readonly string[] BusinessSignals =
        {
            "soft1", "πελατ", "προμηθευτ", "συναλλασσομεν", "αφμ", "ειδος", "ειδη",
            "τιμολογ", "παραστατικ", "σειρα", "findoc", "trdr", "κωδικ", "ποσο",
            "υπολοιπ", "τζιρ", "πωλησ", "αγορ", "crm", "calendar", "ημερολογ",
            "courier", "voucher", "browser", "σελιδα", "url", "excel", "pdf",
            "csv", "xlsx", "export", "sql", "query", "ααδε", "mydata", "παραγγελι",
            "αποθηκ", "εισερχομεν", "inbox", "outlook", "email", "mail"
        };

        private static readonly string[] ConversationalExact =
        {
            "γεια", "γεια σου", "καλημερα", "καλησπερα", "καληνυχτα",
            "εισαι εδω", "εισαι εδω;", "με ακους", "με ακους;",
            "τι κανεις", "τι κανεις;", "πως εισαι", "πως εισαι;",
            "ευχαριστω", "ευχαριστω πολυ",
            "ποιος εισαι", "ποιος εισαι;", "τι εισαι", "τι εισαι;"
        };

        internal static string TryOptimize(string agentName, string providerRequestJson)
        {
            if (string.IsNullOrWhiteSpace(agentName) ||
                string.IsNullOrWhiteSpace(providerRequestJson))
                return providerRequestJson;

            try
            {
                JObject request = JObject.Parse(providerRequestJson);
                JArray tools = request["tools"] as JArray;
                JArray messages = request["messages"] as JArray;

                int originalChars = providerRequestJson.Length;
                string originalSystem = ReadSystemText(request["system"]);
                int originalSystemChars = originalSystem.Length;
                int originalToolCount = tools == null ? 0 : tools.Count;
                int originalMessageCount = messages == null ? 0 : messages.Count;

                LogToolSchemaBudget(agentName, tools);

                HistoryCompactionStats history = CompactCompletedToolHistory(request);
                messages = request["messages"] as JArray;

                string userText = FindLatestHumanText(messages);
                int latestHumanIndex = FindLatestHumanTextMessageIndex(messages);
                string previousAssistantText = FindPreviousAssistantText(messages, latestHumanIndex);
                string previousHumanText = FindPreviousHumanText(messages, latestHumanIndex);
                string role = (agentName ?? string.Empty).Trim().ToLowerInvariant();
                string contextLine = ExtractContextLine(ReadSystemText(request["system"]));
                string durableContext = BuildDurableContext(history.DurableFilePaths, history.SuccessfulTools);

                if (IsQueryProvenanceQuestion(userText) &&
                    !string.IsNullOrWhiteSpace(history.LastSuccessfulQuerySql))
                {
                    request["tools"] = new JArray();
                    request["system"] = CompactSystem(
                        BuildQueryProvenancePrompt(agentName, contextLine,
                            history.LastSuccessfulQuerySql));
                    CompactPlainTextHistory(request, 4);
                    return Finish(request, agentName, "query-provenance", originalChars,
                        originalSystemChars, originalToolCount, originalMessageCount, history);
                }

                if (IsClearlyConversational(userText) && !HasStructuredCurrentUserContent(messages))
                {
                    request["tools"] = new JArray();
                    request["system"] = CompactSystem(
                        BuildConversationalPrompt(agentName, contextLine, durableContext));
                    request["max_tokens"] = ConversationalMaxOutputTokens;
                    CompactPlainTextHistory(request, ConversationalHistoryMessages);
                    return Finish(request, agentName, "conversation", originalChars,
                        originalSystemChars, originalToolCount, originalMessageCount, history);
                }

                HashSet<string> allowed = null;
                string compactPrompt = null;
                string mode = null;

                if (tools == null || tools.Count == 0)
                {
                    compactPrompt = BuildDedicatedPrompt(role, contextLine, durableContext);
                    if (!string.IsNullOrWhiteSpace(compactPrompt))
                    {
                        request["system"] = CompactSystem(compactPrompt);
                        return Finish(request, agentName, "final-role", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, history);
                    }

                    if (history.Changed)
                    {
                        AppendDurableContextToSystem(request, durableContext);
                        return Finish(request, agentName, "history-only", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, history);
                    }
                    return providerRequestJson;
                }

                bool explicitDirectExport = IsExplicitDirectExportRequest(userText);
                string activeExportRequest = explicitDirectExport
                    ? userText
                    : FindActiveExportRequest(messages, latestHumanIndex, previousAssistantText);
                bool inheritedDirectExport = !explicitDirectExport &&
                    !string.IsNullOrWhiteSpace(activeExportRequest);

                if (explicitDirectExport || inheritedDirectExport)
                {
                    allowed = DirectExportTools;
                    compactPrompt = BuildDirectExportPrompt(
                        string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim(),
                        contextLine, durableContext,
                        inheritedDirectExport ? activeExportRequest : null);
                    mode = inheritedDirectExport ? "direct-export-followup" : "direct-export";
                }

                if (allowed == null && IsLatestDocumentByCurrentUserRequest(userText))
                {
                    allowed = LatestDocumentTools;
                    compactPrompt = BuildLatestDocumentPrompt(
                        string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim(),
                        contextLine, durableContext);
                    mode = "latest-user-document";
                }

                if (allowed == null && (role == "jarvis" || role == "echo"))
                {
                    ResolveEchoOrMainIntent(role, userText, previousAssistantText, tools,
                        contextLine, durableContext, out allowed, out compactPrompt, out mode);
                }

                if (allowed == null)
                {
                    switch (role)
                    {
                        case "atlas":
                            if (IsClearlyReadOnly(userText))
                            {
                                allowed = ReadTools;
                                compactPrompt = BuildReadPrompt("Atlas", contextLine, durableContext);
                                mode = "atlas-read";
                            }
                            break;
                        case "forge":
                            allowed = ForgeTools;
                            compactPrompt = BuildForgePrompt(contextLine, durableContext);
                            mode = "forge";
                            break;
                        case "compass":
                            allowed = CompassTools;
                            compactPrompt = BuildCompassPrompt(contextLine, durableContext);
                            mode = "compass";
                            break;
                        case "sprint":
                            allowed = SprintTools;
                            compactPrompt = BuildSprintPrompt(contextLine, durableContext);
                            mode = "sprint";
                            break;
                        case "scout":
                            allowed = ScoutTools;
                            compactPrompt = BuildScoutPrompt(contextLine, durableContext);
                            mode = "scout";
                            break;
                        case "sage":
                            allowed = SageTools;
                            compactPrompt = BuildSagePrompt(contextLine, durableContext);
                            mode = "sage";
                            break;
                    }
                }

                if (allowed == null || string.IsNullOrWhiteSpace(compactPrompt))
                {
                    if (history.Changed)
                    {
                        AppendDurableContextToSystem(request, durableContext);
                        return Finish(request, agentName, "history-only", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, history);
                    }
                    return providerRequestJson;
                }

                JArray filtered = FilterTools(tools, allowed);
                if (filtered.Count == 0)
                {
                    if (history.Changed)
                    {
                        AppendDurableContextToSystem(request, durableContext);
                        return Finish(request, agentName, "history-only", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, history);
                    }
                    return providerRequestJson;
                }

                if (mode == "direct-export" || mode == "direct-export-followup")
                    HardenDirectExportTools(filtered);

                if (filtered[filtered.Count - 1] is JObject lastTool)
                    lastTool["cache_control"] = new JObject { ["type"] = "ephemeral" };

                request["tools"] = filtered;
                request["system"] = CompactSystem(compactPrompt);

                return Finish(request, agentName, mode ?? "role", originalChars,
                    originalSystemChars, originalToolCount, originalMessageCount, history);
            }
            catch (Exception ex)
            {
                try { DebugLog.Log("[AI-CONTEXT] optimizer skipped: " + ex.Message); }
                catch { }
                return providerRequestJson;
            }
        }

        private static void ResolveEchoOrMainIntent(
            string role,
            string userText,
            string previousAssistantText,
            JArray tools,
            string contextLine,
            string durableContext,
            out HashSet<string> allowed,
            out string prompt,
            out string mode)
        {
            allowed = null;
            prompt = null;
            mode = null;

            string n = NormalizeGreek(userText);
            bool isEchoRole = string.Equals(role, "echo", StringComparison.Ordinal);
            string pendingDomain = InferPendingDomain(previousAssistantText);

            if (IsShortConfirmation(userText))
            {
                if (pendingDomain == "email-send")
                {
                    allowed = EchoSendTools;
                    prompt = BuildEchoSendPrompt(contextLine, durableContext);
                    mode = isEchoRole ? "echo-send-followup" : "jarvis-echo-send-followup";
                    return;
                }
                if (pendingDomain == "courier")
                {
                    allowed = SprintTools;
                    prompt = BuildSprintPrompt(contextLine, durableContext);
                    mode = "jarvis-sprint-followup";
                    return;
                }
            }

            bool explicitContact =
                ContainsAnyNormalized(n, "επαφη", "contact") &&
                ContainsAnyNormalized(n, "βρες", "αναζητ", "email", "mail", "τηλεφων");
            bool contactFollowup = isEchoRole && pendingDomain == "contact" &&
                !ContainsAnyNormalized(n, "στειλ", "στελν", "reply", "απαντησ", "inbox", "εισερχομεν", "calendar", "ημερολογ");

            if (explicitContact || contactFollowup)
            {
                allowed = EchoContactTools;
                prompt = BuildEchoContactPrompt(contextLine, durableContext);
                mode = isEchoRole ? "echo-contact" : "jarvis-echo-contact";
                return;
            }

            bool explicitInboxRead = ContainsAnyNormalized(n,
                "εισερχομεν", "εισερχομενα", "inbox", "email απο", "emails απο",
                "πιο προσφατο email", "τελευταιο email", "διαβασε email",
                "διαβασε το email", "δειξε email", "δειξε μου το email",
                "μηνυμα απο", "μηνυματα απο");

            if (explicitInboxRead)
            {
                allowed = EchoInboxTools;
                prompt = BuildEchoInboxPrompt(contextLine, durableContext);
                mode = isEchoRole ? "echo-inbox" : "jarvis-echo-inbox";
                return;
            }

            if (ContainsAnyNormalized(n, "calendar", "ημερολογ", "ραντεβου"))
            {
                allowed = EchoCalendarTools;
                prompt = BuildEchoCalendarPrompt(contextLine, durableContext);
                mode = isEchoRole ? "echo-calendar" : "jarvis-echo-calendar";
                return;
            }

            bool asksExport = ContainsAnyNormalized(n,
                "αρχει", "csv", "xlsx", "excel", "pdf", "export", "εξαγωγ");
            bool mentionsEmail = ContainsAnyNormalized(n, "email", "mail");
            bool asksSend = ContainsAnyNormalized(n,
                "στειλ", "στελν", "reply", "απαντησ", "αποστειλ",
                "γραψε email", "ετοιμασε email", "συνταξε email");
            bool asksPreview = ContainsAnyNormalized(n,
                "δειξε μου πρωτα", "δειξε πρωτα", "πρωτα τι", "draft", "προσχεδ");

            if (asksExport)
            {
                allowed = EchoExportTools;
                prompt = BuildEchoExportPrompt(contextLine, durableContext, mentionsEmail && asksSend);
                mode = isEchoRole ? "echo-export" : "jarvis-echo-export";
                return;
            }

            if (asksSend && asksPreview)
            {
                allowed = EchoDraftTools;
                prompt = BuildEchoDraftPrompt(contextLine, durableContext);
                mode = isEchoRole ? "echo-draft" : "jarvis-echo-draft";
                return;
            }

            if (asksSend)
            {
                allowed = EchoSendTools;
                prompt = BuildEchoSendPrompt(contextLine, durableContext);
                mode = isEchoRole ? "echo-send" : "jarvis-echo-send";
                return;
            }

            if (!isEchoRole && IsClearlyReadOnly(userText) && !ContainsAny(userText, ActionSignals))
            {
                allowed = ReadTools;
                prompt = BuildReadPrompt("Jarvis", contextLine, durableContext);
                mode = "jarvis-read";
                return;
            }

            if (isEchoRole)
            {
                allowed = EchoAllTools;
                prompt = BuildEchoGeneralPrompt(contextLine, durableContext);
                mode = "echo-general";
            }
        }

        private static HistoryCompactionStats CompactCompletedToolHistory(JObject request)
        {
            var stats = new HistoryCompactionStats();
            JArray messages = request["messages"] as JArray;
            if (messages == null || messages.Count < 2)
                return stats;

            int latestHuman = FindLatestHumanTextMessageIndex(messages);
            if (latestHuman <= 0)
                return stats;

            var compacted = new JArray();
            var durablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var successfulTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toolNamesById = new Dictionary<string, string>(StringComparer.Ordinal);
            var querySqlById = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < messages.Count; i++)
            {
                JObject message = messages[i] as JObject;
                if (message == null)
                {
                    compacted.Add(messages[i].DeepClone());
                    continue;
                }

                if (i >= latestHuman)
                {
                    compacted.Add(message.DeepClone());
                    continue;
                }

                JToken content = message["content"];
                JArray blocks = content as JArray;
                if (blocks == null)
                {
                    compacted.Add(message.DeepClone());
                    continue;
                }

                var kept = new JArray();
                foreach (JToken block in blocks)
                {
                    string type = block?["type"]?.ToString() ?? string.Empty;
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        kept.Add(block.DeepClone());
                        continue;
                    }

                    if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = block?["id"]?.ToString();
                        string name = block?["name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        {
                            toolNamesById[id] = name;
                            if (string.Equals(name, "query_data", StringComparison.OrdinalIgnoreCase))
                            {
                                string sql = block?["input"]?["sql"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(sql))
                                    querySqlById[id] = sql;
                            }
                        }
                    }

                    if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        string raw = block?["content"]?.ToString();
                        CollectDurableFilePaths(raw, durablePaths);

                        string toolUseId = block?["tool_use_id"]?.ToString();
                        string toolName;
                        if (!string.IsNullOrWhiteSpace(toolUseId) &&
                            toolNamesById.TryGetValue(toolUseId, out toolName) &&
                            IsSuccessfulToolResult(block))
                        {
                            successfulTools.Add(toolName);
                            if (string.Equals(toolName, "query_data", StringComparison.OrdinalIgnoreCase))
                            {
                                string sql;
                                if (querySqlById.TryGetValue(toolUseId, out sql) &&
                                    !string.IsNullOrWhiteSpace(sql))
                                    stats.LastSuccessfulQuerySql = sql;
                            }
                        }
                    }

                    if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type, "redacted_thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.RemovedBlocks++;
                        stats.RemovedChars += block.ToString(Formatting.None).Length;
                        continue;
                    }

                    kept.Add(block.DeepClone());
                }

                if (kept.Count > 0)
                {
                    JObject clone = (JObject)message.DeepClone();
                    clone["content"] = kept;
                    compacted.Add(clone);
                }
            }

            stats.DurableFilePaths.AddRange(durablePaths.Take(4));
            stats.SuccessfulTools.AddRange(successfulTools.Take(12));
            if (stats.RemovedBlocks > 0)
            {
                request["messages"] = compacted;
                stats.Changed = true;
            }

            return stats;
        }

        private static bool IsSuccessfulToolResult(JToken block)
        {
            if (block == null || (bool?)block["is_error"] == true)
                return false;

            string raw = block["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return true;

            try
            {
                JToken token = JToken.Parse(raw);
                JObject obj = token as JObject;
                if (obj == null) return true;

                JToken success = obj["success"];
                if (success != null && success.Type == JTokenType.Boolean)
                    return (bool)success;

                JToken error = obj["error"];
                if (error != null && error.Type == JTokenType.String &&
                    !string.IsNullOrWhiteSpace(error.ToString()))
                    return false;
            }
            catch
            {
            }

            return true;
        }

        private static void CollectDurableFilePaths(string raw, HashSet<string> output)
        {
            if (string.IsNullOrWhiteSpace(raw) || output == null)
                return;

            var candidates = new List<string>();
            candidates.Add(raw.Trim().Trim('"'));

            try
            {
                JToken token = JToken.Parse(raw);
                foreach (JValue value in token.DescendantsAndSelf().OfType<JValue>())
                {
                    if (value.Type == JTokenType.String && value.Value != null)
                        candidates.Add(value.Value.ToString());
                }
            }
            catch { }

            foreach (string candidateRaw in candidates)
            {
                string candidate = (candidateRaw ?? string.Empty).Trim().Trim('"');
                if (candidate.Length < 5 || candidate.Length > 1000)
                    continue;

                int drive = FindDrivePathStart(candidate);
                if (drive >= 0)
                    candidate = candidate.Substring(drive).Trim();

                int newline = candidate.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0)
                    candidate = candidate.Substring(0, newline).Trim();

                candidate = candidate.Trim('"', '\'', ' ', ']', '}');
                if (LooksLikeDurableFilePath(candidate))
                    output.Add(candidate);
            }
        }

        private static int FindDrivePathStart(string value)
        {
            if (string.IsNullOrEmpty(value)) return -1;
            for (int i = 0; i + 2 < value.Length; i++)
            {
                if (char.IsLetter(value[i]) && value[i + 1] == ':' &&
                    (value[i + 2] == '\\' || value[i + 2] == '/'))
                    return i;
            }
            return -1;
        }

        private static bool LooksLikeDurableFilePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string lower = value.ToLowerInvariant();
            bool extension = lower.EndsWith(".csv") || lower.EndsWith(".xlsx") ||
                lower.EndsWith(".xls") || lower.EndsWith(".pdf") ||
                lower.EndsWith(".docx") || lower.EndsWith(".txt");
            if (!extension) return false;
            return FindDrivePathStart(value) >= 0 || value.StartsWith("\\\\", StringComparison.Ordinal);
        }

        private static string BuildDurableContext(List<string> paths, List<string> successfulTools)
        {
            bool hasPaths = paths != null && paths.Count > 0;
            bool hasSuccessfulTools = successfulTools != null && successfulTools.Count > 0;
            if (!hasPaths && !hasSuccessfulTools) return string.Empty;

            return "[VERIFIED_DURABLE_CONTEXT] " + new JObject
            {
                ["successfulTools"] = new JArray((successfulTools ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase)),
                ["filePaths"] = new JArray((paths ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            }.ToString(Formatting.None);
        }

        private static void AppendDurableContextToSystem(JObject request, string durableContext)
        {
            if (string.IsNullOrWhiteSpace(durableContext)) return;
            JArray system = request["system"] as JArray;
            if (system == null)
            {
                system = new JArray();
                string existing = ReadSystemText(request["system"]);
                if (!string.IsNullOrWhiteSpace(existing))
                    system.Add(new JObject { ["type"] = "text", ["text"] = existing });
                request["system"] = system;
            }
            system.Add(new JObject
            {
                ["type"] = "text",
                ["text"] = durableContext,
                ["cache_control"] = new JObject { ["type"] = "ephemeral" }
            });
        }

        private static void LogToolSchemaBudget(string agentName, JArray tools)
        {
            if (tools == null || tools.Count == 0) return;
            try
            {
                var sizes = new List<KeyValuePair<string, int>>();
                int total = 0;
                foreach (JToken tool in tools)
                {
                    string name = tool?["name"]?.ToString() ?? "?";
                    int chars = tool.ToString(Formatting.None).Length;
                    total += chars;
                    sizes.Add(new KeyValuePair<string, int>(name, chars));
                }

                string signature = agentName + "|" + string.Join(",",
                    sizes.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value));
                lock (ToolBudgetLock)
                {
                    if (!LoggedToolBudgetSignatures.Add(signature)) return;
                }

                string top = string.Join(", ", sizes.OrderByDescending(x => x.Value)
                    .Take(ToolBudgetTopCount).Select(x => x.Key + ":" + x.Value));
                DebugLog.Log("[AI-TOOL-BUDGET] agent=" + agentName +
                    " tools=" + tools.Count + " schemaChars=" + total +
                    " estTokens~" + Math.Max(1, total / 4) + " top=" + top);
            }
            catch { }
        }

        private static string Finish(
            JObject request, string agentName, string mode,
            int originalChars, int originalSystemChars,
            int originalToolCount, int originalMessageCount,
            HistoryCompactionStats history)
        {
            string optimized = request.ToString(Formatting.None);
            int newSystemChars = ReadSystemText(request["system"]).Length;
            JArray newTools = request["tools"] as JArray;
            JArray newMessages = request["messages"] as JArray;

            try
            {
                DebugLog.Log("[AI-CONTEXT] protocol agent=" + agentName +
                    " mode=" + mode +
                    " requestChars=" + originalChars + "->" + optimized.Length +
                    " systemChars=" + originalSystemChars + "->" + newSystemChars +
                    " tools=" + originalToolCount + "->" + (newTools == null ? 0 : newTools.Count) +
                    " messages=" + originalMessageCount + "->" + (newMessages == null ? 0 : newMessages.Count) +
                    " oldTraceBlocksRemoved=" + history.RemovedBlocks +
                    " oldTraceCharsRemoved=" + history.RemovedChars +
                    " durablePaths=" + history.DurableFilePaths.Count +
                    " durableToolSuccess=" + history.SuccessfulTools.Count);
            }
            catch { }
            return optimized;
        }

        private static JArray FilterTools(JArray tools, HashSet<string> allowed)
        {
            var filtered = new JArray();
            if (tools == null || allowed == null) return filtered;
            foreach (JToken tool in tools)
            {
                string name = tool?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && allowed.Contains(name))
                    filtered.Add(tool.DeepClone());
            }
            return filtered;
        }

        private static void HardenDirectExportTools(JArray tools)
        {
            if (tools == null) return;
            foreach (JObject tool in tools.OfType<JObject>())
            {
                string name = (string)tool["name"];
                if (string.Equals(name, "query_data", StringComparison.OrdinalIgnoreCase))
                    tool["description"] = "Read-only SQL SELECT for a narrow lookup, count or schema check needed by the direct-export protocol.";
                else if (string.Equals(name, "export_query_to_file", StringComparison.OrdinalIgnoreCase))
                    tool["description"] = "Execute the final SELECT directly to an export file and return path, rowsWritten and totalFound.";
                else if (string.Equals(name, "export_shown_table", StringComparison.OrdinalIgnoreCase))
                    tool["description"] = "Export the already-visible table through the registered visible-table artifact flow.";
            }
        }

        private static JArray CompactSystem(string prompt)
        {
            return new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = prompt ?? string.Empty,
                    ["cache_control"] = new JObject { ["type"] = "ephemeral" }
                }
            };
        }

        private static void CompactPlainTextHistory(JObject request, int keepLast)
        {
            JArray messages = request["messages"] as JArray;
            if (messages == null || messages.Count <= keepLast || keepLast < 1) return;
            foreach (JToken token in messages)
            {
                JObject message = token as JObject;
                if (message == null) return;
                JToken content = message["content"];
                if (content == null || content.Type == JTokenType.String) continue;
                JArray blocks = content as JArray;
                if (blocks == null) return;
                foreach (JToken block in blocks)
                    if (!string.Equals(block?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                        return;
            }

            var compacted = new JArray();
            for (int i = Math.Max(0, messages.Count - keepLast); i < messages.Count; i++)
                compacted.Add(messages[i].DeepClone());
            request["messages"] = compacted;
        }

        private static int FindLatestHumanMessageIndex(JArray messages)
        {
            if (messages == null) return -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if (string.Equals(messages[i]?["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static int FindLatestHumanTextMessageIndex(JArray messages)
        {
            if (messages == null) return -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = ReadMessageText(message);
                if (!string.IsNullOrWhiteSpace(text))
                    return i;
            }
            return -1;
        }

        private static string FindLatestHumanText(JArray messages)
        {
            int index = FindLatestHumanTextMessageIndex(messages);
            return index < 0 ? null : ReadMessageText(messages[index] as JObject);
        }

        private static string FindPreviousHumanText(JArray messages, int beforeIndex)
        {
            if (messages == null) return null;
            int start = beforeIndex < 0 ? messages.Count - 1 : beforeIndex - 1;
            for (int i = start; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = ReadMessageText(message);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }

        private static string FindPreviousAssistantText(JArray messages, int beforeIndex)
        {
            if (messages == null) return null;
            int start = beforeIndex < 0 ? messages.Count - 1 : beforeIndex - 1;
            for (int i = start; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = ReadMessageText(message);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }

        private static string ReadMessageText(JObject message)
        {
            if (message == null) return null;
            JToken content = message["content"];
            if (content == null) return null;
            if (content.Type == JTokenType.String) return content.ToString();
            JArray blocks = content as JArray;
            if (blocks == null) return null;
            var sb = new StringBuilder();
            foreach (JToken block in blocks)
            {
                if (!string.Equals(block?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = block?["text"]?.ToString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(text);
            }
            return sb.ToString();
        }

        private static bool HasStructuredCurrentUserContent(JArray messages)
        {
            int index = FindLatestHumanMessageIndex(messages);
            if (index < 0) return false;
            JArray blocks = messages[index]?["content"] as JArray;
            if (blocks == null) return false;
            foreach (JToken block in blocks)
                if (!string.Equals(block?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool IsClearlyConversational(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string n = NormalizeGreek(text).Trim().TrimEnd('?', '!', '.').Trim();
            if (n.Length == 0 || n.Length > 80) return false;
            if (ContainsAnyNormalized(n, BusinessSignals) ||
                ContainsAnyNormalized(n, ReadSignals) ||
                ContainsAnyNormalized(n, ActionSignals)) return false;
            foreach (string phrase in ConversationalExact)
            {
                string p = NormalizeGreek(phrase).Trim().TrimEnd('?', '!', '.').Trim();
                if (string.Equals(n, p, StringComparison.Ordinal)) return true;
            }
            return n.StartsWith("γεια ", StringComparison.Ordinal) ||
                   n.StartsWith("καλημερα ", StringComparison.Ordinal) ||
                   n.StartsWith("καλησπερα ", StringComparison.Ordinal) ||
                   n.StartsWith("ευχαριστω ", StringComparison.Ordinal);
        }

        private static bool IsShortConfirmation(string text)
        {
            string n = NormalizeGreek(text).Trim().TrimEnd('?', '!', '.').Trim();
            return n == "ναι" || n == "ναι στειλτο" || n == "στειλτο" ||
                   n == "οκ στειλτο" || n == "προχωρα" || n == "καντο" ||
                   n == "ναι προχωρα" || n == "yes" || n == "send it";
        }

        private static bool IsExplicitDirectExportRequest(string text)
        {
            string n = NormalizeGreek(text);
            if (string.IsNullOrWhiteSpace(n)) return false;

            if (ContainsAnyNormalized(n, "στειλ", "στελν", "email", "mail", "συνημ"))
                return false;

            bool formatOrFile = ContainsAnyNormalized(n,
                "excel", "xlsx", "csv", "pdf", "αρχει", "export", "εξαγωγ");
            bool action = ContainsAnyNormalized(n,
                "φτιαξε", "κανε", "δημιουργ", "εξαγ", "export", "αποθηκευ", "βγαλε");
            return formatOrFile && action;
        }

        private static string FindActiveExportRequest(
            JArray messages, int latestHumanIndex, string previousAssistantText)
        {
            if (messages == null || latestHumanIndex <= 0 ||
                !LooksLikeClarificationPrompt(previousAssistantText))
                return null;

            int humanTurnsSeen = 0;
            for (int i = latestHumanIndex - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = ReadMessageText(message);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                humanTurnsSeen++;
                if (IsExplicitDirectExportRequest(text))
                    return text;

                if (humanTurnsSeen >= 4)
                    break;
            }

            return null;
        }

        private static bool LooksLikeClarificationPrompt(string assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
                return false;

            string a = NormalizeGreek(assistantText);
            return assistantText.Contains("❓") ||
                a.Contains("ποιον") || a.Contains("ποια") || a.Contains("ποιο ") ||
                a.Contains("εννοεις") || a.Contains("διαλεξε") || a.Contains("επιλεξε") ||
                a.Contains("συμπεριληφ") || a.Contains("πιστωτικ") ||
                a.Contains("τι να κρατησω");
        }

        private static bool IsExportClarificationContinuation(string previousHumanText, string previousAssistantText)
        {
            if (!IsExplicitDirectExportRequest(previousHumanText) ||
                string.IsNullOrWhiteSpace(previousAssistantText))
                return false;

            return LooksLikeClarificationPrompt(previousAssistantText);
        }

        private static string InferPendingDomain(string assistantText)
        {
            string n = NormalizeGreek(assistantText);
            if (ContainsAnyNormalized(n,
                "δεν βρηκα επαφη", "δεν βρεθηκε επαφη", "επαφη", "contact"))
                return "contact";
            if (ContainsAnyNormalized(n,
                "να το στειλω", "να το στειλω;", "να στειλω", "να αποστειλω",
                "επιβεβαιωνεις την αποστολη", "draft", "προσχεδ", "προς:", "θεμα:"))
                return "email-send";
            if (ContainsAnyNormalized(n, "voucher", "courier", "να εκδωσω", "να ακυρωσω"))
                return "courier";
            return null;
        }

        private static bool IsClearlyReadOnly(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string n = NormalizeGreek(text);
            foreach (string action in ActionSignals)
                if (n.Contains(NormalizeGreek(action))) return false;
            foreach (string read in ReadSignals)
                if (n.Contains(NormalizeGreek(read))) return true;
            return false;
        }

        private static bool IsLatestDocumentByCurrentUserRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string n = NormalizeGreek(text);
            bool document = ContainsAnyNormalized(n, "παραστατικ", "findoc");
            bool latest = ContainsAnyNormalized(n, "τελευται", "πιο προσφατ", "προσφατο");
            bool byMe = ContainsAnyNormalized(n,
                "καταχωρησα", "καταχωρισα", "περασα", "εβαλα εγω", "εχω καταχωρησει",
                "που εβαλα", "που περασα", "απο εμενα", "δικο μου");
            return document && latest && byMe;
        }

        private static bool IsQueryProvenanceQuestion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string n = NormalizeGreek(text);
            bool asksQuery = ContainsAnyNormalized(n, "query", "sql", "ερωτημα");
            bool asksUsed = ContainsAnyNormalized(n,
                "χρησιμοποιησ", "εκτελεσ", "ετρεξ", "βρηκες", "βρηκαμε", "με ποιο");
            return asksQuery && asksUsed;
        }

        private static bool ContainsAny(string text, string[] signals)
        {
            return ContainsAnyNormalized(NormalizeGreek(text), signals);
        }

        private static bool ContainsAnyNormalized(string normalized, params string[] signals)
        {
            if (string.IsNullOrWhiteSpace(normalized) || signals == null) return false;
            foreach (string signal in signals)
            {
                if (string.IsNullOrWhiteSpace(signal)) continue;
                if (normalized.Contains(NormalizeGreek(signal))) return true;
            }
            return false;
        }

        private static string ReadSystemText(JToken system)
        {
            if (system == null) return string.Empty;
            if (system.Type == JTokenType.String) return system.ToString();
            JArray blocks = system as JArray;
            if (blocks == null) return system.ToString();
            var sb = new StringBuilder();
            foreach (JToken block in blocks)
            {
                string text = block?["text"]?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text);
            }
            return sb.ToString();
        }

        private static string ExtractContextLine(string systemText)
        {
            if (string.IsNullOrWhiteSpace(systemText)) return string.Empty;
            string[] lines = systemText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.StartsWith("Τρέχον context:", StringComparison.OrdinalIgnoreCase))
                    return line;
            }
            return string.Empty;
        }

        private static StringBuilder PromptBase(string agent, string role, string contextLine, string durableContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[JARVIS_OPTIMIZER_PROTOCOL] logicalAgent=" + (agent ?? string.Empty) +
                "; mode=" + (role ?? string.Empty) +
                "; localDate=" + DateTime.Now.ToString("yyyy-MM-dd") + ".");
            sb.AppendLine("Behavioral rules are supplied exclusively by JARVIS_POLICY_CONTEXT; business/schema facts by JARVIS_KNOWLEDGE_CONTEXT.");
            if (!string.IsNullOrWhiteSpace(contextLine)) sb.AppendLine(contextLine);
            if (!string.IsNullOrWhiteSpace(durableContext)) sb.AppendLine(durableContext);
            return sb;
        }

        private static string BuildConversationalPrompt(string agentName, string contextLine, string durableContext)
        {
            string name = string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim();
            return PromptBase(name, "conversation_no_tools", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildQueryProvenancePrompt(string agentName, string contextLine, string sql)
        {
            string name = string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim();
            StringBuilder sb = PromptBase(name, "query_provenance", contextLine, string.Empty);
            sb.AppendLine("actualPreviousQuery=" + (sql ?? string.Empty));
            return sb.ToString().Trim();
        }

        private static string BuildLatestDocumentPrompt(string agent, string contextLine, string durableContext)
        {
            return PromptBase(agent, "latest_document_by_current_operator", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildDirectExportPrompt(
            string agent, string contextLine, string durableContext, string inheritedRequest)
        {
            StringBuilder sb = PromptBase(agent, "direct_export", contextLine, durableContext);
            if (!string.IsNullOrWhiteSpace(inheritedRequest))
                sb.AppendLine("inheritedExportRequest=" + inheritedRequest.Trim());
            return sb.ToString().Trim();
        }

        private static string BuildReadPrompt(string agent, string contextLine, string durableContext)
        {
            return PromptBase(agent, "read_reporting", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildEchoContactPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Echo", "contact_lookup", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildEchoInboxPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Echo", "inbox_read", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildEchoCalendarPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Echo", "calendar", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildEchoDraftPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Echo", "email_draft", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildEchoSendPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Echo", "email_send", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildEchoExportPrompt(string contextLine, string durableContext, bool emailMentioned)
        {
            StringBuilder sb = PromptBase("Echo", "report_export", contextLine, durableContext);
            sb.AppendLine("emailMentioned=" + emailMentioned.ToString().ToLowerInvariant());
            return sb.ToString().Trim();
        }

        private static string BuildEchoGeneralPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Echo", "email_calendar_contacts", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildForgePrompt(string contextLine, string durableContext)
        {
            return PromptBase("Forge", "items", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildCompassPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Compass", "traders", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildSprintPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Sprint", "courier", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildScoutPrompt(string contextLine, string durableContext)
        {
            return PromptBase("Scout", "browser_research", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildSagePrompt(string contextLine, string durableContext)
        {
            return PromptBase("Sage", "help_support", contextLine, durableContext).ToString().Trim();
        }

        private static string BuildDedicatedPrompt(string role, string contextLine, string durableContext)
        {
            switch (role)
            {
                case "atlas": return BuildReadPrompt("Atlas", contextLine, durableContext);
                case "forge": return BuildForgePrompt(contextLine, durableContext);
                case "compass": return BuildCompassPrompt(contextLine, durableContext);
                case "echo": return BuildEchoGeneralPrompt(contextLine, durableContext);
                case "sprint": return BuildSprintPrompt(contextLine, durableContext);
                case "scout": return BuildScoutPrompt(contextLine, durableContext);
                case "sage": return BuildSagePrompt(contextLine, durableContext);
                default: return null;
            }
        }

        private static HashSet<string> Set(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeGreek(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string s = value.ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case 'ά': sb.Append('α'); break;
                    case 'έ': sb.Append('ε'); break;
                    case 'ή': sb.Append('η'); break;
                    case 'ί': case 'ϊ': case 'ΐ': sb.Append('ι'); break;
                    case 'ό': sb.Append('ο'); break;
                    case 'ύ': case 'ϋ': case 'ΰ': sb.Append('υ'); break;
                    case 'ώ': sb.Append('ω'); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private sealed class HistoryCompactionStats
        {
            internal bool Changed;
            internal int RemovedBlocks;
            internal int RemovedChars;
            internal string LastSuccessfulQuerySql;
            internal readonly List<string> DurableFilePaths = new List<string>();
            internal readonly List<string> SuccessfulTools = new List<string>();
        }
    }
}

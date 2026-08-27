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
            "στειλ", "στελν", "email", "mail", "απαντησ", "reply",
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
                bool inheritedDirectExport = !explicitDirectExport &&
                    IsExportClarificationContinuation(previousHumanText, previousAssistantText);

                // Protocol-level intent: explicit file exports are identical for every
                // agent/provider. A clarification answer must inherit the original export
                // intent instead of falling back to the broad role prompt.
                if (explicitDirectExport || inheritedDirectExport)
                {
                    allowed = DirectExportTools;
                    compactPrompt = BuildDirectExportPrompt(
                        string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim(),
                        contextLine, durableContext,
                        inheritedDirectExport ? previousHumanText : null);
                    mode = inheritedDirectExport ? "direct-export-followup" : "direct-export";
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

            if (ContainsAnyNormalized(n, "εισερχομεν", "inbox", "μηνυμα", "emails", "email απο"))
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
            bool asksEmail = ContainsAnyNormalized(n, "email", "mail", "στειλ", "στελν");
            bool asksPreview = ContainsAnyNormalized(n,
                "δειξε μου πρωτα", "δειξε πρωτα", "πρωτα τι", "draft", "προσχεδ");

            if (asksExport)
            {
                allowed = EchoExportTools;
                prompt = BuildEchoExportPrompt(contextLine, durableContext, asksEmail);
                mode = isEchoRole ? "echo-export" : "jarvis-echo-export";
                return;
            }

            if (asksEmail && asksPreview)
            {
                allowed = EchoDraftTools;
                prompt = BuildEchoDraftPrompt(contextLine, durableContext);
                mode = isEchoRole ? "echo-draft" : "jarvis-echo-draft";
                return;
            }

            if (asksEmail)
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
                            toolNamesById[id] = name;
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

            var sb = new StringBuilder();
            sb.AppendLine("Durable context από ολοκληρωμένα tools:");

            if (hasSuccessfulTools)
            {
                foreach (string tool in successfulTools)
                    sb.AppendLine("- Επιβεβαιωμένη επιτυχής εκτέλεση tool: " + tool + ".");
                sb.AppendLine("Οι παραπάνω εκτελέσεις είναι πραγματικά επιβεβαιωμένες από tool_result. Μην ισχυριστείς ότι δεν εκτελέστηκαν μόνο επειδή τα παλιά raw tool traces συμπτύχθηκαν από το context.");
            }

            if (hasPaths)
            {
                foreach (string path in paths)
                    sb.AppendLine("- Διαθέσιμο αρχείο: " + path);
                sb.AppendLine("Αν ο χρήστης αναφέρεται σε «το αρχείο που μόλις έφτιαξες», χρησιμοποίησε ακριβώς αυτό το path. Μην ξανακάνεις export αν το path υπάρχει ήδη.");
                sb.AppendLine("Όταν ΕΜΦΑΝΙΖΕΙΣ αρχείο στον χειριστή, ΠΟΤΕ raw path ή code block: γράψε Markdown link [όνομα_αρχείου](πλήρες_path). Το Jarvis UI μετατρέπει αυτή τη μορφή σε clickable link που ανοίγει το αρχείο.");
            }

            return sb.ToString().Trim();
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
            foreach (JToken token in tools)
            {
                JObject tool = token as JObject;
                if (tool == null) continue;
                string name = tool["name"]?.ToString();
                if (string.Equals(name, "query_data", StringComparison.OrdinalIgnoreCase))
                {
                    tool["description"] = "Για direct-export flow: χρησιμοποίησέ το ΜΟΝΟ για μικρό/narrow lookup ταυτότητας, COUNT ή απολύτως αναγκαίο schema check. ΠΟΤΕ μην τραβήξεις τις γραμμές του export ως preview (ούτε TOP 100/200) και ΠΟΤΕ μεγάλο dataset. Τα πραγματικά export rows πρέπει να πάνε απευθείας SQL -> export_query_to_file, όχι μέσω LLM context.";
                }
                else if (string.Equals(name, "export_query_to_file", StringComparison.OrdinalIgnoreCase))
                {
                    tool["description"] = "Ο χειριστής έχει ήδη ζητήσει ρητά αρχείο. Εκτέλεσε το τελικό SELECT ΑΠΕΥΘΕΙΑΣ στη βάση και γράψε Excel/CSV χωρίς preview και χωρίς να περάσουν οι γραμμές από το LLM context. Κάλεσέ το μία φορά μόλις λυθούν τα απαραίτητα φίλτρα/οντότητες. Επιστρέφει path, rowsWritten και totalFound.";
                }
                else if (string.Equals(name, "export_shown_table", StringComparison.OrdinalIgnoreCase))
                {
                    tool["description"] = "Αν ο χρήστης αναφέρεται ρητά στον πίνακα που μόλις εμφανίστηκε (π.χ. «κάν' το Excel/PDF»), εξήγαγε εκείνον τον ήδη ορατό πίνακα μία φορά. Μην ξανατρέξεις query_data για να ξαναφέρεις τις ίδιες γραμμές.";
                }
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

            // Combined export+email belongs to the richer email flow; this lane is for
            // the deterministic act of creating/opening a local file only.
            if (ContainsAnyNormalized(n, "στειλ", "στελν", "email", "mail", "συνημ"))
                return false;

            bool formatOrFile = ContainsAnyNormalized(n,
                "excel", "xlsx", "csv", "pdf", "αρχει", "export", "εξαγωγ");
            bool action = ContainsAnyNormalized(n,
                "φτιαξε", "κανε", "δημιουργ", "εξαγ", "export", "αποθηκευ", "βγαλε");
            return formatOrFile && action;
        }

        private static bool IsExportClarificationContinuation(string previousHumanText, string previousAssistantText)
        {
            if (!IsExplicitDirectExportRequest(previousHumanText) ||
                string.IsNullOrWhiteSpace(previousAssistantText))
                return false;

            string a = NormalizeGreek(previousAssistantText);
            return previousAssistantText.Contains("❓") ||
                a.Contains("ποιον") || a.Contains("ποια") || a.Contains("ποιο ") ||
                a.Contains("εννοεις") || a.Contains("διαλεξε") || a.Contains("επιλεξε");
        }

        private static string InferPendingDomain(string assistantText)
        {
            string n = NormalizeGreek(assistantText);
            if (ContainsAnyNormalized(n,
                "δεν βρηκα επαφη", "δεν βρεθηκε επαφη", "επαφη", "contact"))
                return "contact";
            if (ContainsAnyNormalized(n,
                "να το στειλω", "να το στειλω;", "draft", "προσχεδ", "προς:",
                "θεμα:", "email", "mail", "συνημμενο"))
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
            sb.AppendLine("Είσαι ο " + agent + ", " + role + " του Jarvis μέσα στο Soft1. Απαντάς στα ελληνικά, σύντομα και συγκεκριμένα.");
            sb.AppendLine("Σημερινή τοπική ημερομηνία: " + DateTime.Now.ToString("yyyy-MM-dd") + ". Για λέξεις όπως σήμερα/χθες/αύριο/τελευταία εβδομάδα/τελευταίος μήνας/προηγούμενο έτος, υπολόγισε το εύρος από αυτή την ημερομηνία και μην κάνεις query στη βάση μόνο για να μάθεις την τρέχουσα ημερομηνία.");
            sb.AppendLine("Χρησιμοποίησε μόνο τα tools που δίνονται. Μην ισχυρίζεσαι ότι εκτέλεσες ενέργεια χωρίς επιτυχημένο tool result. Οι φράσεις «το ξανάτρεξα», «το επιβεβαίωσα από τη βάση» ή ισοδύναμες είναι ισχυρισμός ΝΕΑΣ εκτέλεσης και επιτρέπονται μόνο αν υπάρχει αντίστοιχο επιτυχημένο tool_result στο ΤΡΕΧΟΝ turn· παλιό durable success δεν σημαίνει ότι το ξανάτρεξες τώρα. Μόλις έχεις αρκετά δεδομένα, σταμάτα τα περιττά tool calls και απάντησε.");
            if (!string.IsNullOrWhiteSpace(contextLine)) sb.AppendLine(contextLine);
            if (!string.IsNullOrWhiteSpace(durableContext)) sb.AppendLine(durableContext);
            return sb;
        }

        private static string BuildConversationalPrompt(string agentName, string contextLine, string durableContext)
        {
            string name = string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim();
            var sb = new StringBuilder();
            sb.AppendLine("Είσαι ο " + name + " του Jarvis μέσα στο Soft1. Απάντησε φυσικά στα ελληνικά, σύντομα και φιλικά.");
            sb.AppendLine("Σημερινή τοπική ημερομηνία: " + DateTime.Now.ToString("yyyy-MM-dd") + ".");
            sb.AppendLine("Αυτό το turn είναι απλή συνομιλία: δεν έχεις tools και δεν πρέπει να ισχυριστείς ότι διάβασες ή άλλαξες δεδομένα Soft1.");
            if (!string.IsNullOrWhiteSpace(contextLine)) sb.AppendLine(contextLine);
            if (!string.IsNullOrWhiteSpace(durableContext)) sb.AppendLine(durableContext);
            return sb.ToString().Trim();
        }

        private static string BuildDirectExportPrompt(
            string agent, string contextLine, string durableContext, string inheritedRequest)
        {
            StringBuilder sb = PromptBase(agent, "direct export agent", contextLine, durableContext);
            if (!string.IsNullOrWhiteSpace(inheritedRequest))
            {
                sb.AppendLine("Το τρέχον μήνυμα είναι απάντηση σε διευκρίνιση. Συνέχισε το προηγούμενο export αίτημα, μην το αντιμετωπίσεις ως νέο ανεξάρτητο read request:");
                sb.AppendLine("Προηγούμενο export αίτημα: " + inheritedRequest.Trim());
            }
            sb.AppendLine("Ο χειριστής έχει ήδη ζητήσει ΡΗΤΑ αρχείο. Αυτό είναι direct-export flow: ΜΗΝ εμφανίσεις preview 100/200 γραμμών και ΜΗΝ κάνεις query_data που επιστρέφει το dataset του export. Οι γραμμές πρέπει να ταξιδέψουν SQL -> export tool -> αρχείο, ποτέ SQL -> LLM -> export.");
            sb.AppendLine("query_data επιτρέπεται μόνο για μικρό lookup/validation που χρειάζεται για να χτιστεί το τελικό SELECT (π.χ. TOP 5 για TRDR, COUNT, ή ένα στοχευμένο INFORMATION_SCHEMA). Μόλις λυθούν τα φίλτρα, κάλεσε export_query_to_file ΜΙΑ φορά με το τελικό SELECT. Αν ο χρήστης αναφέρεται σε πίνακα που ήδη φαίνεται, χρησιμοποίησε export_shown_table αντί να ξανατρέξεις query.");
            sb.AppendLine("Μην κάνεις SELECT GETDATE()/YEAR(GETDATE()) για σχετικές ημερομηνίες: χρησιμοποίησε τη σημερινή ημερομηνία του system context. Για «προηγούμενο έτος» σήμερα σημαίνει " + (DateTime.Now.Year - 1) + ".");
            sb.AppendLine("Γνωστό schema: TRDR(TRDR,CODE,NAME,AFM,SODTYPE,COMPANY), FINDOC(FINDOC,TRDR,TRNDATE,FINCODE,SUMAMNT,SERIES,SOSOURCE,COMPANY), SERIES join ΜΟΝΟ με COMPANY+SERIES+SOSOURCE. ΜΗΝ μαντέψεις CUSTOMER, FINDOCID, FULLFINCODE, TRDTYPE ή SERIES.SODTYPE. Αν χρειάζεται άγνωστο πεδίο, κάνε ΕΝΑ στοχευμένο INFORMATION_SCHEMA lookup, όχι διαδοχικές εικασίες.");
            sb.AppendLine("Μετά από successful export, σταμάτα αμέσως και απάντησε σύντομα με πλήθος γραμμών και clickable Markdown link [όνομα_αρχείου.ext](πλήρες_path). ΜΗΝ ξανακάνεις query/export μόνο για επιβεβαίωση.");
            return sb.ToString().Trim();
        }

        private static string BuildReadPrompt(string agent, string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase(agent, "read/reporting agent", contextLine, durableContext);
            sb.AppendLine("Για Soft1 χρησιμοποίησε query_data μόνο για SELECT. Μην μαντεύεις schema: INFORMATION_SCHEMA μόνο όταν λείπει πραγματικά πληροφορία.");
            sb.AppendLine("Γνωστό schema: TRDR(TRDR,CODE,NAME,AFM,SODTYPE), FINDOC(FINDOC,TRDR,TRNDATE,FINCODE,SUMAMNT,SERIES,SOSOURCE,COMPANY), SERIES join σε COMPANY+SERIES+SOSOURCE, TRDBALSHEET(TRDR,FISCPRD,LDEBIT,LCREDIT). Δεν υπάρχει FINTRD.");
            sb.AppendLine("Πίνακες σε Markdown. Αν totalRowCount>100, preview και πρότεινε export.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoContactPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Echo", "contact lookup agent", contextLine, durableContext);
            sb.AppendLine("Στόχος αυτού του turn είναι μόνο εύρεση επαφής. Πρώτα αναζήτησε στο Soft1 με query_data στον PRSN και μετά συμπληρωματικά στο Outlook με search_outlook_contacts. Για PRSN χρησιμοποίησε μόνο γνωστά πεδία NAME, NAME2, EMAIL, EMAIL1 εκτός αν πρώτα επιβεβαιώσεις άλλο πεδίο από INFORMATION_SCHEMA. Αν ο χρήστης έδωσε ακριβές email, χρησιμοποίησέ το ΑΥΤΟΥΣΙΟ στο PRSN (EMAIL/EMAIL1) και στο Outlook: ΜΗΝ το μετατρέψεις σε επώνυμο, ΜΗΝ κάνεις transliteration και ΜΗΝ δοκιμάζεις εναλλακτικές γραφές. Για όνομα, χρησιμοποίησε το ίδιο κριτήριο που έδωσε ο χρήστης. Μετά κάλεσε show_contact_results με τα αποτελέσματα και των δύο πηγών. Αν δεν βρεθεί τίποτα, σταμάτα μετά από αυτά τα δύο lookups αντί να επαναλαμβάνεις παραλλαγές.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoInboxPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Echo", "inbox agent", contextLine, durableContext);
            sb.AppendLine("Για αναζήτηση λίστας emails προτίμησε filter_email_inbox ώστε το αποτέλεσμα να εμφανιστεί στην Email κουρτίνα. read_email μόνο όταν ζητείται το πλήρες περιεχόμενο συγκεκριμένου μηνύματος. Μην επαναλάβεις το ίδιο φίλτρο αν επέστρεψε επιτυχώς.");
            sb.AppendLine("Για σχετικές περιόδους (π.χ. τελευταία εβδομάδα/τελευταίος μήνας) υπολόγισε το sinceDate από τη σημερινή τοπική ημερομηνία που δίνεται στο system context. Μην ζητάς διευκρίνιση για το ποια ημερομηνία είναι σήμερα.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoCalendarPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Echo", "calendar agent", contextLine, durableContext);
            sb.AppendLine("Χρησιμοποίησε filter_calendar/show_calendar_entries/read_calendar ανάλογα με το αίτημα. Δημιουργία event/task μόνο με σαφή πρόθεση του χρήστη.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoDraftPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Echo", "email drafting agent", contextLine, durableContext);
            sb.AppendLine("Αυτό είναι draft/preview turn. ΜΗΝ στείλεις email. Αν λείπει διεύθυνση, αναζήτησε πρώτα query_data στον PRSN (NAME/NAME2, με EMAIL συμπληρωμένο) και μετά συμπληρωματικά search_outlook_contacts. Παρουσίασε σύντομα Προς/Κοιν/Θέμα/Κείμενο και ζήτησε επιβεβαίωση.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoSendPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Echo", "email sending agent", contextLine, durableContext);
            sb.AppendLine("Αν υπάρχει ήδη επιβεβαιωμένο draft στο αμέσως προηγούμενο context, μην το ξαναγράψεις και μην ξανακάνεις lookup χωρίς λόγο: κάλεσε send_email/reply_email μία φορά με τα ήδη γνωστά στοιχεία. Αν πρόκειται για νέο αίτημα αποστολής που δίνει μόνο όνομα παραλήπτη, χρησιμοποίησε query_data στον PRSN πριν από Outlook lookup και μη μαντέψεις email.");
            sb.AppendLine("Αν υπάρχει durable file path, χρησιμοποίησέ το αυτούσιο ως attachmentFilePath. Μην δημιουργήσεις ή εξάγεις ξανά το ίδιο αρχείο.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoExportPrompt(string contextLine, string durableContext, bool emailMentioned)
        {
            StringBuilder sb = PromptBase("Echo", "report/export agent", contextLine, durableContext);
            sb.AppendLine("Για δεδομένα Soft1 χρησιμοποίησε query_data και μετά ΕΝΑ export tool. Μόλις export tool επιστρέψει μη κενό path, θεώρησε το αρχείο έτοιμο και ΜΗΝ ξανακάνεις export στο ίδιο user request.");
            sb.AppendLine("Export schema guardrail: χρησιμοποίησε ως γνωστά TRDR(TRDR,CODE,NAME,AFM,SODTYPE,COMPANY), FINDOC(FINDOC,TRDR,TRNDATE,FINCODE,SUMAMNT,SERIES,SOSOURCE,COMPANY) και SERIES join ΜΟΝΟ με COMPANY+SERIES+SOSOURCE. ΜΗΝ χρησιμοποιήσεις/μαντέψεις CUSTOMER, FINDOCID, FULLFINCODE, TRDTYPE ή SERIES.SODTYPE. Αν χρειάζεσαι πεδίο πέρα από τα γνωστά, κάνε ΕΝΑ στοχευμένο INFORMATION_SCHEMA lookup και μετά χρησιμοποίησέ το· όχι διαδοχικές εικασίες schema. Αν ο συναλλασσόμενος έχει ήδη λυθεί στο κοντινό context, επαναχρησιμοποίησε το γνωστό TRDR/CODE αντί να τον ξαναανακαλύψεις.");
            sb.AppendLine("Μετά από επιτυχημένο export, η τελική απάντηση ΠΡΕΠΕΙ να εμφανίζει το αρχείο ως clickable Markdown link: [όνομα_αρχείου.xlsx](C:\\πλήρες\\path\\όνομα_αρχείου.xlsx). ΜΗΝ εμφανίζεις το path μόνο του και ΜΗΝ το βάζεις σε code block. Το Jarvis UI έχει ήδη file-link handler για αυτή τη μορφή.");
            if (emailMentioned)
                sb.AppendLine("Αν ο χρήστης είπε ότι θα σταλεί αργότερα με email αλλά δεν ζήτησε ρητά άμεση αποστολή, ετοίμασε μόνο το αρχείο και δώσε το clickable link. Η αποστολή θα γίνει σε επόμενο επιβεβαιωμένο turn.");
            return sb.ToString().Trim();
        }

        private static string BuildEchoGeneralPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Echo", "email/calendar/contacts agent", contextLine, durableContext);
            sb.AppendLine("Χρησιμοποίησε το μικρότερο αναγκαίο σύνολο tools. Εξωτερική αποστολή ή δημιουργία event/task μόνο με σαφή πρόθεση/επιβεβαίωση. Μην επαναλαμβάνεις επιτυχημένα lookups ή exports.");
            return sb.ToString().Trim();
        }

        private static string BuildForgePrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Forge", "agent ειδών", contextLine, durableContext);
            sb.AppendLine("Για αναζήτηση query_data. Για δημιουργία get_item_template όταν χρειάζεται και μετά create_item. Μην δημιουργείς χωρίς σαφή οδηγία/επιβεβαίωση όπου απαιτείται.");
            return sb.ToString().Trim();
        }

        private static string BuildCompassPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Compass", "agent συναλλασσομένων/ΑΦΜ", contextLine, durableContext);
            sb.AppendLine("Χρησιμοποίησε query_data/find_trader_by_afm για υπάρχοντα δεδομένα, get_aade_data για ΑΑΔΕ και create_trader_from_aade μόνο όταν ζητείται πραγματική δημιουργία.");
            return sb.ToString().Trim();
        }

        private static string BuildSprintPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Sprint", "courier agent", contextLine, durableContext);
            sb.AppendLine("Χρησιμοποίησε courier tools μόνο για το σχετικό παραστατικό. create/cancel voucher απαιτεί ρητή επιβεβαίωση. Μετά από επιτυχία μην επαναλάβεις το action.");
            return sb.ToString().Trim();
        }

        private static string BuildScoutPrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Scout", "browser/research agent", contextLine, durableContext);
            sb.AppendLine("Χρησιμοποίησε open_url/read_page_content/extract_page_tables για web περιεχόμενο. Μην ισχυρίζεσαι ότι διάβασες σελίδα πριν χρησιμοποιήσεις read_page_content/extract_page_tables.");
            return sb.ToString().Trim();
        }

        private static string BuildSagePrompt(string contextLine, string durableContext)
        {
            StringBuilder sb = PromptBase("Sage", "help/support agent", contextLine, durableContext);
            sb.AppendLine("Διάγνωσε το πρόβλημα και δώσε πρακτικά βήματα. query_data/open_document μόνο όταν χρειάζεται πραγματικό Soft1 context. Καμία write/external ενέργεια.");
            return sb.ToString().Trim();
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
            internal readonly List<string> DurableFilePaths = new List<string>();
            internal readonly List<string> SuccessfulTools = new List<string>();
        }
    }
}

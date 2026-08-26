using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Provider-neutral context optimization protocol applied immediately before
    /// a provider request leaves the desktop client.
    ///
    /// Safety rules:
    /// - Provider/model/routing authority is never changed.
    /// - Ambiguous or multi-domain work fails open to the original request.
    /// - Write/action capability is never added by the optimizer.
    /// - Conversational fast-path is used only for obviously non-business chatter.
    /// - Structured history (tool results, images, documents) is never pruned.
    /// - Dedicated agents receive only their role tools and a compact role prompt.
    /// - Any optimizer exception is non-fatal and returns the original request.
    /// </summary>
    internal static class VerilicProviderRequestOptimizer
    {
        private const int ConversationalMaxOutputTokens = 512;
        private const int ConversationalHistoryMessages = 6;

        private static readonly HashSet<string> AtlasReadOnlyTools = Set(
            "query_data", "export_query_to_file", "open_document",
            "get_conversion_targets", "export_shown_table");

        private static readonly HashSet<string> ForgeTools = Set(
            "query_data", "open_document", "get_item_template", "create_item",
            "export_query_to_file", "export_shown_table");

        private static readonly HashSet<string> CompassTools = Set(
            "query_data", "open_document", "find_trader_by_afm", "get_aade_data",
            "create_trader_from_aade", "export_query_to_file", "export_shown_table");

        private static readonly HashSet<string> EchoTools = Set(
            "query_data", "open_document", "create_crm_task",
            "read_email", "download_email_attachment", "read_calendar",
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

        // Strong business vocabulary prevents a short operational request from
        // being mistaken for harmless chat merely because it contains few words.
        private static readonly string[] BusinessSignals =
        {
            "soft1", "πελατ", "προμηθευτ", "συναλλασσομεν", "αφμ", "ειδος", "ειδη",
            "τιμολογ", "παραστατικ", "σειρα", "findoc", "trdr", "κωδικ", "ποσο",
            "υπολοιπ", "τζιρ", "πωλησ", "αγορ", "crm", "calendar", "ημερολογ",
            "courier", "voucher", "browser", "σελιδα", "url", "excel", "pdf",
            "export", "sql", "query", "ααδε", "mydata", "παραγγελι", "αποθηκ"
        };

        private static readonly string[] ConversationalExact =
        {
            "γεια", "γεια σου", "καλημερα", "καλησπερα", "καληνυχτα",
            "εισαι εδω", "εισαι εδω;", "με ακους", "με ακους;",
            "τι κανεις", "τι κανεις;", "πως εισαι", "πως εισαι;",
            "ευχαριστω", "ευχαριστω πολυ", "οκ", "ok", "okay",
            "ναι", "οχι", "τελεια", "ωραια", "μπραβο",
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
                string userText = FindLatestHumanText(messages);
                string systemText = ReadSystemText(request["system"]);
                string contextLine = ExtractContextLine(systemText);
                string role = (agentName ?? string.Empty).Trim().ToLowerInvariant();

                int originalChars = providerRequestJson.Length;
                int originalSystemChars = systemText.Length;
                int originalToolCount = tools == null ? 0 : tools.Count;
                int originalMessageCount = messages == null ? 0 : messages.Count;

                // Level 1: obvious non-business conversation. This is intentionally
                // provider-neutral and may apply to any logical agent. No tool schema
                // is useful for "είσαι εδώ;" and similar turns.
                if (IsClearlyConversational(userText) &&
                    !HasStructuredCurrentUserContent(messages))
                {
                    request["tools"] = new JArray();
                    request["system"] = CompactSystem(BuildConversationalPrompt(agentName, contextLine));
                    request["max_tokens"] = ConversationalMaxOutputTokens;
                    CompactPlainTextHistory(request, ConversationalHistoryMessages);
                    return Finish(request, agentName, "conversation", originalChars,
                        originalSystemChars, originalToolCount, originalMessageCount);
                }

                // A no-tools final iteration still benefits from a compact role prompt
                // for dedicated agents. Main Jarvis remains untouched unless the turn
                // was classified above, because it may be finalizing arbitrary work.
                if (tools == null || tools.Count == 0)
                {
                    string finalPrompt = BuildDedicatedPrompt(role, contextLine);
                    if (string.IsNullOrWhiteSpace(finalPrompt))
                        return providerRequestJson;

                    request["system"] = CompactSystem(finalPrompt);
                    return Finish(request, agentName, "final-role", originalChars,
                        originalSystemChars, originalToolCount, originalMessageCount);
                }

                HashSet<string> allowed = null;
                string compactPrompt = null;
                string mode = null;

                switch (role)
                {
                    case "jarvis":
                        ResolveMainJarvisOptimization(userText, tools, contextLine,
                            out allowed, out compactPrompt, out mode);
                        break;

                    case "atlas":
                        if (!IsClearlyReadOnly(userText))
                            return providerRequestJson;
                        allowed = AtlasReadOnlyTools;
                        compactPrompt = BuildCompactAtlasPrompt(contextLine);
                        mode = "atlas-read";
                        break;

                    case "forge":
                        if (HasForeignDedicatedTools(tools, "Forge"))
                            return providerRequestJson;
                        allowed = ForgeTools;
                        compactPrompt = BuildCompactForgePrompt(contextLine);
                        mode = "forge";
                        break;

                    case "compass":
                        if (HasForeignDedicatedTools(tools, "Compass"))
                            return providerRequestJson;
                        allowed = CompassTools;
                        compactPrompt = BuildCompactCompassPrompt(contextLine);
                        mode = "compass";
                        break;

                    case "echo":
                        if (HasForeignDedicatedTools(tools, "Echo"))
                            return providerRequestJson;
                        allowed = EchoTools;
                        compactPrompt = BuildCompactEchoPrompt(contextLine);
                        mode = "echo";
                        break;

                    case "sprint":
                        if (HasForeignDedicatedTools(tools, "Sprint"))
                            return providerRequestJson;
                        allowed = SprintTools;
                        compactPrompt = BuildCompactSprintPrompt(contextLine);
                        mode = "sprint";
                        break;

                    case "scout":
                        if (HasForeignDedicatedTools(tools, "Scout"))
                            return providerRequestJson;
                        allowed = ScoutTools;
                        compactPrompt = BuildCompactScoutPrompt(contextLine);
                        mode = "scout";
                        break;

                    case "sage":
                        if (HasForeignDedicatedTools(tools, "Sage"))
                            return providerRequestJson;
                        allowed = SageTools;
                        compactPrompt = BuildCompactSagePrompt(contextLine);
                        mode = "sage";
                        break;

                    default:
                        return providerRequestJson;
                }

                if (allowed == null || string.IsNullOrWhiteSpace(compactPrompt))
                    return providerRequestJson;

                JArray filtered = FilterTools(tools, allowed);
                if (filtered.Count == 0)
                    return providerRequestJson;

                if (filtered[filtered.Count - 1] is JObject lastTool)
                    lastTool["cache_control"] = new JObject { ["type"] = "ephemeral" };

                request["tools"] = filtered;
                request["system"] = CompactSystem(compactPrompt);

                return Finish(request, agentName, mode ?? "role", originalChars,
                    originalSystemChars, originalToolCount, originalMessageCount);
            }
            catch (Exception ex)
            {
                try { DebugLog.Log("[AI-CONTEXT] optimizer skipped: " + ex.Message); }
                catch { }
                return providerRequestJson;
            }
        }

        private static void ResolveMainJarvisOptimization(
            string userText,
            JArray tools,
            string contextLine,
            out HashSet<string> allowed,
            out string compactPrompt,
            out string mode)
        {
            allowed = null;
            compactPrompt = null;
            mode = null;

            if (IsClearlyReadOnly(userText) && !ContainsAny(userText, ActionSignals))
            {
                allowed = AtlasReadOnlyTools;
                compactPrompt = BuildCompactAtlasPrompt(contextLine);
                mode = "jarvis-read";
                return;
            }

            string normalized = NormalizeGreek(userText);
            int domains = 0;
            HashSet<string> candidate = null;
            string prompt = null;
            string candidateMode = null;

            if (ContainsAnyNormalized(normalized,
                "ειδος", "ειδη", "item", "κωδικο ειδ", "create item"))
            {
                domains++;
                candidate = ForgeTools;
                prompt = BuildCompactForgePrompt(contextLine);
                candidateMode = "jarvis-forge";
            }
            if (ContainsAnyNormalized(normalized,
                "αφμ", "συναλλασσομεν", "πελατ", "προμηθευτ", "ααδε"))
            {
                domains++;
                candidate = CompassTools;
                prompt = BuildCompactCompassPrompt(contextLine);
                candidateMode = "jarvis-compass";
            }
            if (ContainsAnyNormalized(normalized,
                "email", "mail", "ημερολογ", "calendar", "ραντεβου", "outlook", "contact"))
            {
                domains++;
                candidate = EchoTools;
                prompt = BuildCompactEchoPrompt(contextLine);
                candidateMode = "jarvis-echo";
            }
            if (ContainsAnyNormalized(normalized,
                "courier", "voucher", "tracking", "αποστολ", "δεμα"))
            {
                domains++;
                candidate = SprintTools;
                prompt = BuildCompactSprintPrompt(contextLine);
                candidateMode = "jarvis-sprint";
            }
            if (ContainsAnyNormalized(normalized,
                "http", "www", "url", "browser", "ιστοσελ", "σελιδα", "web "))
            {
                domains++;
                candidate = ScoutTools;
                prompt = BuildCompactScoutPrompt(contextLine);
                candidateMode = "jarvis-scout";
            }

            // Exactly one clear domain may be compacted. Composite or uncertain
            // requests keep the mature full Jarvis contract.
            if (domains == 1 && candidate != null && HasAtLeastOneTool(tools, candidate))
            {
                allowed = candidate;
                compactPrompt = prompt;
                mode = candidateMode;
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

        private static string Finish(
            JObject request,
            string agentName,
            string mode,
            int originalChars,
            int originalSystemChars,
            int originalToolCount,
            int originalMessageCount)
        {
            string optimized = request.ToString(Newtonsoft.Json.Formatting.None);
            int newSystemChars = ReadSystemText(request["system"]).Length;
            JArray newTools = request["tools"] as JArray;
            JArray newMessages = request["messages"] as JArray;

            try
            {
                DebugLog.Log(
                    "[AI-CONTEXT] protocol agent=" + agentName +
                    " mode=" + mode +
                    " requestChars=" + originalChars + "->" + optimized.Length +
                    " systemChars=" + originalSystemChars + "->" + newSystemChars +
                    " tools=" + originalToolCount + "->" + (newTools == null ? 0 : newTools.Count) +
                    " messages=" + originalMessageCount + "->" + (newMessages == null ? 0 : newMessages.Count));
            }
            catch { }

            return optimized;
        }

        private static JArray FilterTools(JArray tools, HashSet<string> allowed)
        {
            var filtered = new JArray();
            if (tools == null || allowed == null)
                return filtered;

            foreach (JToken tool in tools)
            {
                string name = tool?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && allowed.Contains(name))
                    filtered.Add(tool.DeepClone());
            }
            return filtered;
        }

        private static bool HasAtLeastOneTool(JArray tools, HashSet<string> allowed)
        {
            if (tools == null || allowed == null) return false;
            foreach (JToken tool in tools)
            {
                string name = tool?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && allowed.Contains(name))
                    return true;
            }
            return false;
        }

        private static bool HasForeignDedicatedTools(JArray tools, string agent)
        {
            bool hasForge = ContainsTool(tools, "get_item_template") || ContainsTool(tools, "create_item");
            bool hasCompass = ContainsTool(tools, "find_trader_by_afm") || ContainsTool(tools, "get_aade_data") || ContainsTool(tools, "create_trader_from_aade");
            bool hasEcho = ContainsTool(tools, "filter_email_inbox") || ContainsTool(tools, "filter_calendar") || ContainsTool(tools, "read_calendar");
            bool hasSprint = ContainsTool(tools, "show_courier_documents") || ContainsTool(tools, "create_courier_voucher") || ContainsTool(tools, "cancel_courier_voucher");
            bool hasScout = ContainsTool(tools, "open_url") || ContainsTool(tools, "read_page_content");

            int domains = (hasForge ? 1 : 0) + (hasCompass ? 1 : 0) + (hasEcho ? 1 : 0) +
                          (hasSprint ? 1 : 0) + (hasScout ? 1 : 0);
            return domains > 1;
        }

        private static bool IsClearlyReadOnly(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = NormalizeGreek(text);
            foreach (string action in ActionSignals)
                if (normalized.Contains(NormalizeGreek(action)))
                    return false;

            foreach (string read in ReadSignals)
                if (normalized.Contains(NormalizeGreek(read)))
                    return true;

            return false;
        }

        private static bool IsClearlyConversational(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = NormalizeGreek(text).Trim();
            while (normalized.EndsWith("?", StringComparison.Ordinal) ||
                   normalized.EndsWith("!", StringComparison.Ordinal) ||
                   normalized.EndsWith(".", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();

            if (normalized.Length == 0 || normalized.Length > 80)
                return false;
            if (ContainsAnyNormalized(normalized, BusinessSignals) ||
                ContainsAnyNormalized(normalized, ReadSignals) ||
                ContainsAnyNormalized(normalized, ActionSignals))
                return false;

            foreach (string phrase in ConversationalExact)
            {
                string p = NormalizeGreek(phrase).TrimEnd('?', '!', '.').Trim();
                if (string.Equals(normalized, p, StringComparison.Ordinal))
                    return true;
            }

            return normalized.StartsWith("γεια ", StringComparison.Ordinal) ||
                   normalized.StartsWith("καλημερα ", StringComparison.Ordinal) ||
                   normalized.StartsWith("καλησπερα ", StringComparison.Ordinal) ||
                   normalized.StartsWith("ευχαριστω ", StringComparison.Ordinal);
        }

        private static bool HasStructuredCurrentUserContent(JArray messages)
        {
            if (messages == null || messages.Count == 0)
                return false;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                JArray blocks = message["content"] as JArray;
                if (blocks == null) return false;
                foreach (JToken block in blocks)
                {
                    string type = block?["type"]?.ToString();
                    if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            return false;
        }

        private static void CompactPlainTextHistory(JObject request, int keepLast)
        {
            JArray messages = request["messages"] as JArray;
            if (messages == null || messages.Count <= keepLast || keepLast < 1)
                return;

            // Never prune a conversation containing provider/tool/attachment state.
            foreach (JToken token in messages)
            {
                JObject message = token as JObject;
                if (message == null) return;
                JToken content = message["content"];
                if (content == null) continue;
                if (content.Type == JTokenType.String) continue;

                JArray blocks = content as JArray;
                if (blocks == null) return;
                foreach (JToken block in blocks)
                {
                    string type = block?["type"]?.ToString();
                    if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            var compacted = new JArray();
            int start = Math.Max(0, messages.Count - keepLast);
            for (int i = start; i < messages.Count; i++)
                compacted.Add(messages[i].DeepClone());
            request["messages"] = compacted;
        }

        private static string FindLatestHumanText(JArray messages)
        {
            if (messages == null) return null;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                JToken content = message["content"];
                if (content == null) continue;
                if (content.Type == JTokenType.String) return content.ToString();

                JArray blocks = content as JArray;
                if (blocks == null) continue;

                var text = new StringBuilder();
                foreach (JToken block in blocks)
                {
                    if (!string.Equals(block?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string value = block?["text"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        if (text.Length > 0) text.Append(' ');
                        text.Append(value);
                    }
                }
                if (text.Length > 0) return text.ToString();
            }
            return null;
        }

        private static bool ContainsTool(JArray tools, string name)
        {
            if (tools == null) return false;
            foreach (JToken tool in tools)
                if (string.Equals(tool?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool ContainsAny(string text, string[] signals)
        {
            return ContainsAnyNormalized(NormalizeGreek(text), signals);
        }

        private static bool ContainsAnyNormalized(string normalized, params string[] signals)
        {
            if (string.IsNullOrWhiteSpace(normalized) || signals == null)
                return false;
            foreach (string signal in signals)
            {
                if (string.IsNullOrWhiteSpace(signal)) continue;
                if (normalized.Contains(NormalizeGreek(signal)))
                    return true;
            }
            return false;
        }

        private static string ReadSystemText(JToken system)
        {
            if (system == null) return string.Empty;
            if (system.Type == JTokenType.String) return system.ToString();

            JArray blocks = system as JArray;
            if (blocks == null) return system.ToString();

            var text = new StringBuilder();
            foreach (JToken block in blocks)
            {
                string value = block?["text"]?.ToString();
                if (string.IsNullOrEmpty(value)) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(value);
            }
            return text.ToString();
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

        private static string BuildConversationalPrompt(string agentName, string contextLine)
        {
            string name = string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim();
            var sb = new StringBuilder();
            sb.Append("Είσαι ο ").Append(name)
              .AppendLine(" του Jarvis μέσα στο Soft1. Απάντησε φυσικά στα ελληνικά, σύντομα και φιλικά.");
            sb.AppendLine("Αυτό το turn είναι απλή συνομιλία: δεν έχεις tools και δεν πρέπει να ισχυριστείς ότι διάβασες ή άλλαξες δεδομένα Soft1.");
            if (!string.IsNullOrWhiteSpace(contextLine))
                sb.AppendLine(contextLine);
            return sb.ToString().Trim();
        }

        private static StringBuilder PromptBase(string agent, string role, string contextLine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Είσαι ο " + agent + ", " + role + " του Jarvis μέσα στο Soft1. Απαντάς στα ελληνικά, σύντομα και συγκεκριμένα.");
            sb.AppendLine("Χρησιμοποίησε μόνο τα tools που σου δίνονται. Μην ισχυρίζεσαι ότι εκτέλεσες ενέργεια αν δεν επέστρεψε επιτυχώς το αντίστοιχο tool. Μόλις έχεις αρκετά δεδομένα, σταμάτα τα περιττά tool calls και απάντησε.");
            if (!string.IsNullOrWhiteSpace(contextLine))
            {
                sb.AppendLine();
                sb.AppendLine(contextLine);
            }
            sb.AppendLine();
            return sb;
        }

        private static string BuildDedicatedPrompt(string role, string contextLine)
        {
            switch (role)
            {
                case "atlas": return BuildCompactAtlasPrompt(contextLine);
                case "forge": return BuildCompactForgePrompt(contextLine);
                case "compass": return BuildCompactCompassPrompt(contextLine);
                case "echo": return BuildCompactEchoPrompt(contextLine);
                case "sprint": return BuildCompactSprintPrompt(contextLine);
                case "scout": return BuildCompactScoutPrompt(contextLine);
                case "sage": return BuildCompactSagePrompt(contextLine);
                default: return null;
            }
        }

        private static string BuildCompactAtlasPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Atlas", "read/reporting agent", contextLine);
            sb.AppendLine("Για δεδομένα Soft1 χρησιμοποίησε query_data (μόνο SELECT). Μην μαντεύεις άγνωστα tables/columns: χρησιμοποίησε INFORMATION_SCHEMA μόνο όταν λείπει πραγματικά το schema.");
            sb.AppendLine("Γνωστό schema: TRDR(TRDR,CODE,NAME,AFM,SODTYPE), FINDOC(FINDOC,TRDR,TRNDATE,FINCODE,SUMAMNT,SERIES,SOSOURCE,COMPANY), SERIES join σε COMPANY+SERIES+SOSOURCE, TRDBALSHEET(TRDR,FISCPRD,LDEBIT,LCREDIT), USERS(USERS,NAME). Δεν υπάρχει FINTRD.");
            sb.AppendLine("SOSOURCE: 1351 πωλήσεις, 1353 υπηρεσίες πωλήσεων, 1251 αγορές/παραλαβές, 1253 υπηρεσίες αγορών, 5151 ενδοδιακίνηση/παραγωγή, 1412 έμβασμα προμηθευτή, 1413 έμβασμα πελάτη, 2021 CRM εργασία.");
            sb.AppendLine("Πίνακες σε Markdown. Γνωστό παραστατικό: [FINCODE](doc:SOSOURCE:FINDOC). Αν totalRowCount>100, preview και πρότεινε export. Δεν έχεις write/action tools σε αυτό το turn.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactForgePrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Forge", "agent δημιουργίας/διαχείρισης ειδών", contextLine);
            sb.AppendLine("Για αναζήτηση χρησιμοποίησε query_data. Για δημιουργία είδους χρησιμοποίησε πρώτα get_item_template όταν χρειάζεται πρότυπο και μετά create_item. Μην δημιουργείς τίποτα χωρίς σαφή οδηγία/επιβεβαίωση του χειριστή όταν η ενέργεια είναι μη αναστρέψιμη ή μαζική.");
            sb.AppendLine("Σε bulk import κράτα μία επιβεβαίωση για όλο το batch, συνέχισε στα επόμενα αν αποτύχει μία γραμμή και στο τέλος δώσε σύντομη αναφορά επιτυχιών/αποτυχιών. Μην φορτώνεις άσχετους κανόνες email/CRM/browser.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactCompassPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Compass", "agent συναλλασσομένων/ΑΦΜ", contextLine);
            sb.AppendLine("Για υπάρχοντα δεδομένα χρησιμοποίησε query_data/find_trader_by_afm. Για στοιχεία ΑΑΔΕ χρησιμοποίησε get_aade_data και για δημιουργία create_trader_from_aade. Μην δημιουργήσεις νέο συναλλασσόμενο αν υπάρχει ήδη σαφής αντιστοίχιση ή χωρίς επιβεβαίωση όταν υπάρχουν πολλαπλές πιθανές εγγραφές.");
            sb.AppendLine("Δείξε καθαρά ποιον συναλλασσόμενο βρήκες/δημιούργησες και το ΑΦΜ. Μην φορτώνεις κανόνες ειδών, courier, browser ή email.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactEchoPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Echo", "agent email/calendar/contacts", contextLine);
            sb.AppendLine("Για ανάγνωση email/calendar χρησιμοποίησε τα αντίστοιχα read/filter tools. Για φιλτράρισμα που πρέπει να φανεί στην κύρια κουρτίνα χρησιμοποίησε filter_email_inbox/filter_calendar ή show_calendar_entries. Για σύνθετη ανάλυση Soft1 χρησιμοποίησε query_data.");
            sb.AppendLine("Αποστολή/reply email, δημιουργία CRM task ή Outlook event απαιτεί σαφή πρόθεση του χειριστή. Πριν από εξωτερική αποστολή βεβαιώσου ότι παραλήπτης/περιεχόμενο είναι ξεκάθαρα.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactSprintPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Sprint", "courier agent", contextLine);
            sb.AppendLine("Χρησιμοποίησε query_data/open_document για στοιχεία παραστατικού και τα courier tools για αποστολές. Πριν create_courier_voucher ή cancel_courier_voucher παρουσίασε τα κρίσιμα στοιχεία και ζήτησε ρητή επιβεβαίωση. Μην εκδίδεις/ακυρώνεις voucher χωρίς επιβεβαίωση.");
            sb.AppendLine("Μετά την επιτυχή έκδοση ή ακύρωση επέστρεψε σύντομη επιβεβαίωση με voucher/παραστατικό. Μην φορτώνεις email, item, trader ή browser κανόνες.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactScoutPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Scout", "browser/research agent", contextLine);
            sb.AppendLine("Χρησιμοποίησε open_url/read_page_content/extract_page_tables για web περιεχόμενο και query_data για Soft1 όταν το αίτημα συνδυάζει εξωτερικά και εσωτερικά δεδομένα. Μην ισχυρίζεσαι ότι διάβασες σελίδα πριν χρησιμοποιήσεις read_page_content/extract_page_tables.");
            sb.AppendLine("Για actions που είναι διαθέσιμα ως tools ακολούθησε ρητή πρόθεση/επιβεβαίωση πριν από εξωτερική αποστολή ή εγγραφή. Μην κάνεις άσχετη schema discovery αν έχεις ήδη τα δεδομένα.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactSagePrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Sage", "help/support agent", contextLine);
            sb.AppendLine("Στόχος σου είναι να διαγνώσεις το πρόβλημα του χειριστή και να δώσεις πρακτικά βήματα. Χρησιμοποίησε query_data/open_document μόνο όταν χρειάζεται πραγματικό Soft1 context. Μην κάνεις εγγραφές ή εξωτερικές ενέργειες.");
            sb.AppendLine("Όταν έχεις λύση, δώσε σύντομη περίληψη αιτήματος, βασικές λέξεις-κλειδιά και καθαρή λύση/βήματα. Αν λείπει κρίσιμη πληροφορία, ρώτα στοχευμένα αντί να μαντέψεις.");
            return sb.ToString().Trim();
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
    }
}

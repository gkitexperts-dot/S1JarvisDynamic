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
    /// - Ambiguous work fails open to the mature request whenever safe compaction
    ///   cannot be proven.
    /// - Write/action capability is never added by the optimizer.
    /// - Conversational fast-path is reserved for unambiguous greetings/chatter;
    ///   short confirmations such as "ναι" are NOT treated as chat because they
    ///   may confirm an email, voucher or another pending action.
    /// - Tool traces from the CURRENT human turn are preserved byte-for-byte.
    /// - Completed tool_use/tool_result/thinking traces from OLDER turns may be
    ///   removed while visible user/assistant text is retained.
    /// - Dedicated agents receive only their role tools and a compact role prompt.
    /// - Any optimizer exception is non-fatal and returns the original request.
    /// </summary>
    internal static class VerilicProviderRequestOptimizer
    {
        private const int ConversationalMaxOutputTokens = 512;
        private const int ConversationalHistoryMessages = 6;
        private const int ToolBudgetTopCount = 10;

        private static readonly object ToolBudgetLock = new object();
        private static readonly HashSet<string> LoggedToolBudgetSignatures =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<string> AtlasReadOnlyTools = Set(
            "query_data", "export_query_to_file", "open_document",
            "get_conversion_targets", "export_shown_table");

        private static readonly HashSet<string> ForgeTools = Set(
            "query_data", "open_document", "get_item_template", "create_item",
            "export_query_to_file", "export_shown_table");

        private static readonly HashSet<string> CompassTools = Set(
            "query_data", "open_document", "find_trader_by_afm", "get_aade_data",
            "create_trader_from_aade", "export_query_to_file", "export_shown_table");

        // Email flows often need to query Soft1 and export the result before send_email.
        // Keeping export tools here avoids falling back to the entire main Jarvis catalog
        // for report -> file -> email workflows.
        private static readonly HashSet<string> EchoTools = Set(
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
            "export", "sql", "query", "ααδε", "mydata", "παραγγελι", "αποθηκ",
            "εισερχομεν", "inbox", "outlook"
        };

        // Deliberately excludes "ναι", "οκ", "ωραία", "τέλεια" etc. Those
        // can be confirmations of pending write/external actions.
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
                string originalSystemText = ReadSystemText(request["system"]);
                int originalSystemChars = originalSystemText.Length;
                int originalToolCount = tools == null ? 0 : tools.Count;
                int originalMessageCount = messages == null ? 0 : messages.Count;

                LogToolSchemaBudget(agentName, tools);

                HistoryCompactionStats historyStats = CompactCompletedToolHistory(request);
                messages = request["messages"] as JArray;

                string userText = FindLatestHumanText(messages);
                int latestHumanIndex = FindLatestHumanMessageIndex(messages);
                string previousAssistantText = FindPreviousAssistantText(messages, latestHumanIndex);
                string systemText = ReadSystemText(request["system"]);
                string contextLine = ExtractContextLine(systemText);
                string role = (agentName ?? string.Empty).Trim().ToLowerInvariant();

                if (IsClearlyConversational(userText) &&
                    !HasStructuredCurrentUserContent(messages))
                {
                    request["tools"] = new JArray();
                    request["system"] = CompactSystem(BuildConversationalPrompt(agentName, contextLine));
                    request["max_tokens"] = ConversationalMaxOutputTokens;
                    CompactPlainTextHistory(request, ConversationalHistoryMessages);
                    return Finish(request, agentName, "conversation", originalChars,
                        originalSystemChars, originalToolCount, originalMessageCount, historyStats);
                }

                // Final no-tools iterations can still shed completed OLD tool traces.
                // Dedicated roles also receive their compact final prompt.
                if (tools == null || tools.Count == 0)
                {
                    string finalPrompt = BuildDedicatedPrompt(role, contextLine);
                    if (!string.IsNullOrWhiteSpace(finalPrompt))
                    {
                        request["system"] = CompactSystem(finalPrompt);
                        return Finish(request, agentName, "final-role", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, historyStats);
                    }

                    if (historyStats.Changed)
                        return Finish(request, agentName, "history-only", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, historyStats);

                    return providerRequestJson;
                }

                HashSet<string> allowed = null;
                string compactPrompt = null;
                string mode = null;

                switch (role)
                {
                    case "jarvis":
                        ResolveMainJarvisOptimization(userText, previousAssistantText, tools,
                            contextLine, out allowed, out compactPrompt, out mode);
                        break;

                    case "atlas":
                        if (IsClearlyReadOnly(userText))
                        {
                            allowed = AtlasReadOnlyTools;
                            compactPrompt = BuildCompactAtlasPrompt(contextLine);
                            mode = "atlas-read";
                        }
                        break;

                    case "forge":
                        allowed = ForgeTools;
                        compactPrompt = BuildCompactForgePrompt(contextLine);
                        mode = "forge";
                        break;

                    case "compass":
                        allowed = CompassTools;
                        compactPrompt = BuildCompactCompassPrompt(contextLine);
                        mode = "compass";
                        break;

                    case "echo":
                        allowed = EchoTools;
                        compactPrompt = BuildCompactEchoPrompt(contextLine);
                        mode = "echo";
                        break;

                    case "sprint":
                        allowed = SprintTools;
                        compactPrompt = BuildCompactSprintPrompt(contextLine);
                        mode = "sprint";
                        break;

                    case "scout":
                        allowed = ScoutTools;
                        compactPrompt = BuildCompactScoutPrompt(contextLine);
                        mode = "scout";
                        break;

                    case "sage":
                        allowed = SageTools;
                        compactPrompt = BuildCompactSagePrompt(contextLine);
                        mode = "sage";
                        break;
                }

                if (allowed == null || string.IsNullOrWhiteSpace(compactPrompt))
                {
                    if (historyStats.Changed)
                        return Finish(request, agentName, "history-only", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, historyStats);
                    return providerRequestJson;
                }

                JArray filtered = FilterTools(tools, allowed);
                if (filtered.Count == 0)
                {
                    if (historyStats.Changed)
                        return Finish(request, agentName, "history-only", originalChars,
                            originalSystemChars, originalToolCount, originalMessageCount, historyStats);
                    return providerRequestJson;
                }

                if (filtered[filtered.Count - 1] is JObject lastTool)
                    lastTool["cache_control"] = new JObject { ["type"] = "ephemeral" };

                request["tools"] = filtered;
                request["system"] = CompactSystem(compactPrompt);

                return Finish(request, agentName, mode ?? "role", originalChars,
                    originalSystemChars, originalToolCount, originalMessageCount, historyStats);
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
            string previousAssistantText,
            JArray tools,
            string contextLine,
            out HashSet<string> allowed,
            out string compactPrompt,
            out string mode)
        {
            allowed = null;
            compactPrompt = null;
            mode = null;

            if (IsShortConfirmation(userText))
            {
                string pendingDomain = InferPendingDomain(previousAssistantText);
                if (string.Equals(pendingDomain, "echo", StringComparison.Ordinal))
                {
                    allowed = EchoTools;
                    compactPrompt = BuildCompactEchoPrompt(contextLine);
                    mode = "jarvis-echo-followup";
                    return;
                }
                if (string.Equals(pendingDomain, "sprint", StringComparison.Ordinal))
                {
                    allowed = SprintTools;
                    compactPrompt = BuildCompactSprintPrompt(contextLine);
                    mode = "jarvis-sprint-followup";
                    return;
                }
            }

            if (IsClearlyReadOnly(userText) && !ContainsAny(userText, ActionSignals))
            {
                allowed = AtlasReadOnlyTools;
                compactPrompt = BuildCompactJarvisReadPrompt(contextLine);
                mode = "jarvis-read";
                return;
            }

            string normalized = NormalizeGreek(userText);
            var domains = new List<string>();
            var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (ContainsAnyNormalized(normalized,
                "ειδος", "ειδη", "item", "κωδικο ειδ", "create item"))
            {
                domains.Add("Forge");
                UnionInto(union, ForgeTools);
            }

            if (ContainsAnyNormalized(normalized,
                "αφμ", "συναλλασσομεν", "πελατ", "προμηθευτ", "ααδε"))
            {
                domains.Add("Compass");
                UnionInto(union, CompassTools);
            }

            if (ContainsAnyNormalized(normalized,
                "email", "mail", "εισερχομεν", "inbox", "outlook",
                "ημερολογ", "calendar", "ραντεβου", "contact"))
            {
                domains.Add("Echo");
                UnionInto(union, EchoTools);
            }

            if (ContainsAnyNormalized(normalized,
                "courier", "voucher", "tracking", "αποστολ", "δεμα"))
            {
                domains.Add("Sprint");
                UnionInto(union, SprintTools);
            }

            if (ContainsAnyNormalized(normalized,
                "http", "www", "url", "browser", "ιστοσελ", "σελιδα", "web "))
            {
                domains.Add("Scout");
                UnionInto(union, ScoutTools);
            }

            // Up to two explicit domains are safely unioned. This handles common
            // real workflows such as customer+email and report+email without loading
            // the entire Jarvis tool catalog. More complex/uncertain turns fail open.
            if (domains.Count >= 1 && domains.Count <= 2 && HasAtLeastOneTool(tools, union))
            {
                allowed = union;
                compactPrompt = BuildCompactMainJarvisPrompt(contextLine, domains);
                mode = "jarvis-" + string.Join("+", domains.ToArray()).ToLowerInvariant();
            }
        }

        private static HistoryCompactionStats CompactCompletedToolHistory(JObject request)
        {
            var stats = new HistoryCompactionStats();
            JArray messages = request["messages"] as JArray;
            if (messages == null || messages.Count < 3)
                return stats;

            int anchor = FindLatestHumanMessageIndex(messages);
            if (anchor <= 0)
                return stats;

            var compacted = new JArray();
            for (int i = 0; i < messages.Count; i++)
            {
                JToken original = messages[i];
                if (i >= anchor)
                {
                    compacted.Add(original.DeepClone());
                    continue;
                }

                JObject message = original as JObject;
                if (message == null)
                {
                    compacted.Add(original.DeepClone());
                    continue;
                }

                JObject cleaned = StripCompletedInternalBlocks(message, stats);
                if (cleaned != null)
                    compacted.Add(cleaned);
                else
                    stats.RemovedMessages++;
            }

            if (stats.RemovedBlocks > 0 || stats.RemovedMessages > 0)
            {
                request["messages"] = compacted;
                stats.Changed = true;
            }

            return stats;
        }

        private static JObject StripCompletedInternalBlocks(
            JObject message,
            HistoryCompactionStats stats)
        {
            JToken content = message["content"];
            if (content == null || content.Type == JTokenType.String)
                return (JObject)message.DeepClone();

            JArray blocks = content as JArray;
            if (blocks == null)
                return (JObject)message.DeepClone();

            var kept = new JArray();
            foreach (JToken block in blocks)
            {
                string type = block?["type"]?.ToString();
                if (IsCompletedInternalBlock(type))
                {
                    stats.RemovedBlocks++;
                    stats.RemovedChars += block.ToString(Newtonsoft.Json.Formatting.None).Length;
                    continue;
                }
                kept.Add(block.DeepClone());
            }

            if (kept.Count == 0)
                return null;

            JObject clone = (JObject)message.DeepClone();
            clone["content"] = kept;
            return clone;
        }

        private static bool IsCompletedInternalBlock(string type)
        {
            return string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "redacted_thinking", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindLatestHumanMessageIndex(JArray messages)
        {
            if (messages == null) return -1;
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (HasHumanText(message["content"]))
                    return i;
            }
            return -1;
        }

        private static bool HasHumanText(JToken content)
        {
            if (content == null) return false;
            if (content.Type == JTokenType.String)
                return !string.IsNullOrWhiteSpace(content.ToString());

            JArray blocks = content as JArray;
            if (blocks == null) return false;
            foreach (JToken block in blocks)
            {
                if (!string.Equals(block?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(block?["text"]?.ToString()))
                    return true;
            }
            return false;
        }

        private static string FindPreviousAssistantText(JArray messages, int beforeIndex)
        {
            if (messages == null || beforeIndex <= 0) return null;
            for (int i = beforeIndex - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = ExtractText(message["content"]);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return null;
        }

        private static bool IsShortConfirmation(string text)
        {
            string n = NormalizeGreek(text).Trim().TrimEnd('?', '!', '.');
            return n == "ναι" || n == "yes" || n == "ok" || n == "οκ" ||
                   n == "προχωρα" || n == "καντο" || n == "κανε το" ||
                   n == "στειλτο" || n == "ναι στειλτο" || n == "ναι προχωρα";
        }

        private static string InferPendingDomain(string previousAssistantText)
        {
            string n = NormalizeGreek(previousAssistantText);
            if (string.IsNullOrWhiteSpace(n)) return null;

            if (ContainsAnyNormalized(n,
                "draft", "email", "mail", "προς:", "θεμα:", "να το στειλω",
                "να στειλω", "προχειρο email", "παραληπτ"))
                return "echo";

            if (ContainsAnyNormalized(n,
                "voucher", "courier", "tracking", "να εκδωσω", "να ακυρωσω"))
                return "sprint";

            return null;
        }

        private static void LogToolSchemaBudget(string agentName, JArray tools)
        {
            if (tools == null || tools.Count == 0) return;

            var sizes = new List<ToolSize>();
            int totalChars = 0;
            var signatureBuilder = new StringBuilder(agentName ?? string.Empty);

            foreach (JToken tool in tools)
            {
                string name = tool?["name"]?.ToString() ?? "?";
                int chars = tool.ToString(Newtonsoft.Json.Formatting.None).Length;
                totalChars += chars;
                sizes.Add(new ToolSize { Name = name, Chars = chars });
                signatureBuilder.Append('|').Append(name).Append(':').Append(chars);
            }

            string signature = signatureBuilder.ToString();
            lock (ToolBudgetLock)
            {
                if (LoggedToolBudgetSignatures.Contains(signature)) return;
                LoggedToolBudgetSignatures.Add(signature);
            }

            sizes.Sort((a, b) => b.Chars.CompareTo(a.Chars));
            var top = new List<string>();
            int take = Math.Min(ToolBudgetTopCount, sizes.Count);
            for (int i = 0; i < take; i++)
                top.Add(sizes[i].Name + ":" + sizes[i].Chars);

            try
            {
                DebugLog.Log(
                    "[AI-TOOL-BUDGET] agent=" + agentName +
                    " tools=" + tools.Count +
                    " schemaChars=" + totalChars +
                    " estTokens~" + Math.Max(1, totalChars / 4) +
                    " top=" + string.Join(",", top.ToArray()));
            }
            catch { }
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
            int originalMessageCount,
            HistoryCompactionStats historyStats)
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
                    " messages=" + originalMessageCount + "->" + (newMessages == null ? 0 : newMessages.Count) +
                    " oldTraceBlocksRemoved=" + historyStats.RemovedBlocks +
                    " oldTraceCharsRemoved=" + historyStats.RemovedChars);
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

        private static void UnionInto(HashSet<string> target, HashSet<string> source)
        {
            if (target == null || source == null) return;
            foreach (string value in source)
                target.Add(value);
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

            int index = FindLatestHumanMessageIndex(messages);
            if (index < 0) return false;
            JObject message = messages[index] as JObject;
            JArray blocks = message?["content"] as JArray;
            if (blocks == null) return false;

            foreach (JToken block in blocks)
            {
                string type = block?["type"]?.ToString();
                if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void CompactPlainTextHistory(JObject request, int keepLast)
        {
            JArray messages = request["messages"] as JArray;
            if (messages == null || messages.Count <= keepLast || keepLast < 1)
                return;

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
            int index = FindLatestHumanMessageIndex(messages);
            if (index < 0) return null;
            JObject message = messages[index] as JObject;
            return ExtractText(message?["content"]);
        }

        private static string ExtractText(JToken content)
        {
            if (content == null) return null;
            if (content.Type == JTokenType.String) return content.ToString();

            JArray blocks = content as JArray;
            if (blocks == null) return null;
            var text = new StringBuilder();
            foreach (JToken block in blocks)
            {
                if (!string.Equals(block?["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = block?["text"]?.ToString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (text.Length > 0) text.Append(' ');
                text.Append(value);
            }
            return text.Length == 0 ? null : text.ToString();
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

        private static string BuildCompactJarvisReadPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Jarvis", "read/reporting orchestrator", contextLine);
            AppendSoft1ReadHints(sb);
            return sb.ToString().Trim();
        }

        private static string BuildCompactMainJarvisPrompt(string contextLine, List<string> domains)
        {
            StringBuilder sb = PromptBase("Jarvis", "multi-domain Soft1 assistant", contextLine);
            sb.AppendLine("Ενεργά domains αυτού του turn: " + string.Join(", ", domains.ToArray()) + ". Μην χρησιμοποιείς capability εκτός των tools που σου δόθηκαν.");
            sb.AppendLine("Για δεδομένα Soft1 χρησιμοποίησε query_data. Για εξωτερική αποστολή/εγγραφή χρησιμοποίησε το αντίστοιχο tool μόνο όταν η πρόθεση ή η επιβεβαίωση του χειριστή είναι σαφής.");
            AppendSoft1ReadHints(sb);
            return sb.ToString().Trim();
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
            AppendSoft1ReadHints(sb);
            sb.AppendLine("Δεν έχεις write/action tools σε αυτό το turn.");
            return sb.ToString().Trim();
        }

        private static void AppendSoft1ReadHints(StringBuilder sb)
        {
            sb.AppendLine("Για δεδομένα Soft1 χρησιμοποίησε query_data (μόνο SELECT). Μην μαντεύεις άγνωστα tables/columns: INFORMATION_SCHEMA μόνο όταν λείπει πραγματικά το schema.");
            sb.AppendLine("Γνωστό schema: TRDR(TRDR,CODE,NAME,AFM,SODTYPE), FINDOC(FINDOC,TRDR,TRNDATE,FINCODE,SUMAMNT,SERIES,SOSOURCE,COMPANY), SERIES join σε COMPANY+SERIES+SOSOURCE, TRDBALSHEET(TRDR,FISCPRD,LDEBIT,LCREDIT), USERS(USERS,NAME). Δεν υπάρχει FINTRD.");
            sb.AppendLine("SOSOURCE: 1351 πωλήσεις, 1353 υπηρεσίες πωλήσεων, 1251 αγορές/παραλαβές, 1253 υπηρεσίες αγορών, 5151 ενδοδιακίνηση/παραγωγή, 1412 έμβασμα προμηθευτή, 1413 έμβασμα πελάτη, 2021 CRM εργασία.");
            sb.AppendLine("Πίνακες σε Markdown. Γνωστό παραστατικό: [FINCODE](doc:SOSOURCE:FINDOC). Αν totalRowCount>100, preview και πρότεινε export.");
        }

        private static string BuildCompactForgePrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Forge", "agent δημιουργίας/διαχείρισης ειδών", contextLine);
            sb.AppendLine("Για αναζήτηση χρησιμοποίησε query_data. Για δημιουργία είδους χρησιμοποίησε πρώτα get_item_template όταν χρειάζεται πρότυπο και μετά create_item. Μην δημιουργείς τίποτα χωρίς σαφή οδηγία/επιβεβαίωση όταν η ενέργεια είναι μη αναστρέψιμη ή μαζική.");
            sb.AppendLine("Σε bulk import κράτα μία επιβεβαίωση για όλο το batch και στο τέλος δώσε σύντομη αναφορά επιτυχιών/αποτυχιών.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactCompassPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Compass", "agent συναλλασσομένων/ΑΦΜ", contextLine);
            sb.AppendLine("Για υπάρχοντα δεδομένα χρησιμοποίησε query_data/find_trader_by_afm. Για στοιχεία ΑΑΔΕ χρησιμοποίησε get_aade_data και για δημιουργία create_trader_from_aade. Μην δημιουργήσεις νέο συναλλασσόμενο αν υπάρχει ήδη σαφής αντιστοίχιση.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactEchoPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Echo", "agent email/calendar/contacts", contextLine);
            sb.AppendLine("Για ανάγνωση email/calendar χρησιμοποίησε τα αντίστοιχα read/filter tools. Για Soft1 δεδομένα χρησιμοποίησε query_data και για attachment από πίνακα χρησιμοποίησε export_query_to_file/export_shown_table πριν από send_email.");
            sb.AppendLine("Αποστολή/reply email, CRM task ή Outlook event απαιτεί σαφή πρόθεση/επιβεβαίωση. Σε follow-up επιβεβαίωση χρησιμοποίησε το ήδη ορατό draft/context αντί να επαναλάβεις άσχετες αναζητήσεις.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactSprintPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Sprint", "courier agent", contextLine);
            sb.AppendLine("Χρησιμοποίησε query_data/open_document για στοιχεία παραστατικού και τα courier tools για αποστολές. Πριν create_courier_voucher ή cancel_courier_voucher ζήτησε ρητή επιβεβαίωση.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactScoutPrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Scout", "browser/research agent", contextLine);
            sb.AppendLine("Χρησιμοποίησε open_url/read_page_content/extract_page_tables για web περιεχόμενο και query_data για Soft1. Μην ισχυρίζεσαι ότι διάβασες σελίδα πριν χρησιμοποιήσεις read_page_content/extract_page_tables.");
            return sb.ToString().Trim();
        }

        private static string BuildCompactSagePrompt(string contextLine)
        {
            StringBuilder sb = PromptBase("Sage", "help/support agent", contextLine);
            sb.AppendLine("Διάγνωσε το πρόβλημα και δώσε πρακτικά βήματα. Χρησιμοποίησε query_data/open_document μόνο όταν χρειάζεται πραγματικό Soft1 context. Μην κάνεις εγγραφές ή εξωτερικές ενέργειες.");
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

        private sealed class HistoryCompactionStats
        {
            public bool Changed { get; set; }
            public int RemovedBlocks { get; set; }
            public int RemovedMessages { get; set; }
            public int RemovedChars { get; set; }
        }

        private sealed class ToolSize
        {
            public string Name { get; set; }
            public int Chars { get; set; }
        }
    }
}

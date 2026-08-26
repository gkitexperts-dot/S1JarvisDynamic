using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Conservative request compaction for clearly read-only Atlas reporting turns.
    /// It never runs for action-oriented requests or dedicated agents. The goal is
    /// to reduce repeated system/tool-schema tokens without changing tool results,
    /// conversation history, routing authority or provider/model selection.
    /// </summary>
    internal static class VerilicProviderRequestOptimizer
    {
        private static readonly HashSet<string> AtlasReadOnlyTools =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "query_data",
                "export_query_to_file",
                "open_document",
                "get_conversion_targets",
                "export_shown_table"
            };

        private static readonly string[] ReadSignals =
        {
            "φερε", "δωσε", "δειξε", "βρες", "βγαλε", "ποια", "ποιο", "ποσο",
            "λιστα", "παραστατικ", "κινησ", "υπολοιπ", "τζιρ", "πωλησ", "αγορ",
            "αναφορ", "report", "στοιχεια", "καρτελα", "φορτωσ", "γραφημα", "chart",
            "top ", "στατιστικ", "συνολο", "μεσο", "μεση", "ημερομην"
        };

        private static readonly string[] ActionSignals =
        {
            "στειλ", "στελν", "email", "mail", "απαντησ", "reply",
            "δημιουργ", "καταχωρ", "περασε", "φτιαξε", "ακυρω", "voucher", "courier",
            "υπενθυμ", "ραντεβου", "εργασ", "task", "μετατρεψ", "μετατροπ",
            "νεο ειδος", "νεο πελατ", "νεο προμηθευτ", "εισαγωγ", "import"
        };

        internal static string TryOptimize(string agentName, string providerRequestJson)
        {
            if (!string.Equals(agentName, "Atlas", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(providerRequestJson))
                return providerRequestJson;

            try
            {
                JObject request = JObject.Parse(providerRequestJson);
                string userText = FindLatestHumanText(request["messages"] as JArray);
                if (!IsClearlyReadOnly(userText))
                    return providerRequestJson;

                JArray tools = request["tools"] as JArray;
                if (tools == null || tools.Count == 0)
                    return providerRequestJson;

                int originalToolCount = tools.Count;
                int originalSystemChars = ReadSystemText(request["system"]).Length;

                var filtered = new JArray();
                foreach (JToken tool in tools)
                {
                    string name = tool?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && AtlasReadOnlyTools.Contains(name))
                        filtered.Add(tool.DeepClone());
                }

                // query_data is the minimum capability for this fast path.
                if (!ContainsTool(filtered, "query_data"))
                    return providerRequestJson;

                if (filtered.Count > 0 && filtered[filtered.Count - 1] is JObject lastTool)
                    lastTool["cache_control"] = new JObject { ["type"] = "ephemeral" };

                request["tools"] = filtered;

                string contextLine = ExtractContextLine(ReadSystemText(request["system"]));
                string compactPrompt = BuildCompactAtlasPrompt(contextLine);
                request["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = compactPrompt,
                        ["cache_control"] = new JObject { ["type"] = "ephemeral" }
                    }
                };

                DebugLog.Log(
                    "[AI-CONTEXT] Atlas read-only compacted. systemChars=" +
                    originalSystemChars + "->" + compactPrompt.Length +
                    " tools=" + originalToolCount + "->" + filtered.Count);

                return request.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                // Optimization must never become a runtime dependency.
                try { DebugLog.Log("[AI-CONTEXT] optimizer skipped: " + ex.Message); }
                catch { }
                return providerRequestJson;
            }
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

        private static string FindLatestHumanText(JArray messages)
        {
            if (messages == null)
                return null;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                JObject message = messages[i] as JObject;
                if (message == null ||
                    !string.Equals(message["role"]?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                JToken content = message["content"];
                if (content == null)
                    continue;

                if (content.Type == JTokenType.String)
                    return content.ToString();

                JArray blocks = content as JArray;
                if (blocks == null)
                    continue;

                // Ignore synthetic tool_result-only user messages. Walk backwards
                // until we find the real human text that started this tool loop.
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
                if (text.Length > 0)
                    return text.ToString();
            }

            return null;
        }

        private static bool ContainsTool(JArray tools, string name)
        {
            foreach (JToken tool in tools)
                if (string.Equals(tool?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string ReadSystemText(JToken system)
        {
            if (system == null)
                return string.Empty;
            if (system.Type == JTokenType.String)
                return system.ToString();

            JArray blocks = system as JArray;
            if (blocks == null)
                return system.ToString();

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
            if (string.IsNullOrWhiteSpace(systemText))
                return string.Empty;

            string[] lines = systemText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.StartsWith("Τρέχον context:", StringComparison.OrdinalIgnoreCase))
                    return line;
            }
            return string.Empty;
        }

        private static string BuildCompactAtlasPrompt(string contextLine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Είσαι ο Atlas, ο read/reporting agent του Jarvis μέσα στο Soft1. Απαντάς στα ελληνικά, σύντομα και συγκεκριμένα.");
            sb.AppendLine();
            sb.AppendLine("Για δεδομένα Soft1 χρησιμοποίησε query_data (μόνο SELECT). Μην μαντεύεις άγνωστα tables/columns: χρησιμοποίησε INFORMATION_SCHEMA μόνο όταν λείπει πραγματικά το schema. Μόλις έχεις αρκετά δεδομένα, σταμάτα τα queries και απάντησε.");
            sb.AppendLine();
            sb.AppendLine("Γνωστό schema:");
            sb.AppendLine("- TRDR: TRDR, CODE, NAME, AFM, SODTYPE (12 προμηθευτής, 13 πελάτης).");
            sb.AppendLine("- FINDOC: FINDOC, TRDR, TRNDATE, FINCODE, SUMAMNT, SERIES, SOSOURCE, COMPANY. Όνομα τύπου: JOIN SERIES σε COMPANY+SERIES+SOSOURCE. Δεν υπάρχει FINTRD.");
            sb.AppendLine("- SOSOURCE: 1351 πωλήσεις, 1353 υπηρεσίες πωλήσεων, 1251 αγορές/παραλαβές, 1253 υπηρεσίες αγορών, 5151 ενδοδιακίνηση/παραγωγή, 1412 έμβασμα προμηθευτή, 1413 έμβασμα πελάτη, 2021 CRM εργασία.");
            sb.AppendLine("- TRDBALSHEET: TRDR, FISCPRD, LDEBIT, LCREDIT. CCCLOADING + CCCLOADCOMPS για φορτώσεις. USERS: USERS, NAME.");
            sb.AppendLine();
            sb.AppendLine("Κανόνες αποτελέσματος:");
            sb.AppendLine("- Αν υπάρχουν πολλαπλές πιθανές οντότητες και δεν υπάρχει σαφές κριτήριο, ρώτα με μορφή ❓ ερώτηση και επιλογές '> ...' αντί να μαντέψεις.");
            sb.AppendLine("- Αριθμητικές τιμές reports: 2 δεκαδικά. Tabular δεδομένα: Markdown table, όλες οι στήλες δεξιά με |---:|.");
            sb.AppendLine("- Γνωστό παραστατικό: [FINCODE](doc:SOSOURCE:FINDOC). Άγνωστο SOSOURCE: απλό κείμενο.");
            sb.AppendLine("- Αν totalRowCount>100, δείξε τις πρώτες 100 πραγματικές γραμμές και ρώτα αν θέλει όλα σε αρχείο. Αν ζητήσει export, χρησιμοποίησε export_query_to_file/export_shown_table.");
            sb.AppendLine("- Για συγκεκριμένο λογαριασμό: πρώτα βασικά στοιχεία, μετά κινήσεις. Κινήσεις: Ημ/νία | Παραστατικό | Χρέωση | Πίστωση | Υπόλοιπο με προοδευτικό υπόλοιπο.");
            sb.AppendLine("- Δεν έχεις write/action tools σε αυτό το read-only turn. Αν το αίτημα μετατραπεί σε αποστολή/δημιουργία/καταχώρηση/ακύρωση, ζήτησε από τον χειριστή να το διατυπώσει ως νέο action turn αντί να προσποιηθείς ότι το εκτέλεσες.");
            if (!string.IsNullOrWhiteSpace(contextLine))
            {
                sb.AppendLine();
                sb.AppendLine(contextLine);
            }
            return sb.ToString().Trim();
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisPresentationResult
    {
        internal string Intro { get; set; }
        internal string EmailSubject { get; set; }
        internal string EmailBody { get; set; }
    }

    internal static class JarvisPresentationComposer
    {
        private const string RuntimeAiAgent = "Jarvis";
        private const int MaxTokens = 2400;
        private const int MaxPresentationRows = 50;

        internal static async Task<JarvisPresentationResult> ComposeReportAsync(XSupport xSupport, string businessQuestion, string datasetJson, CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject context = BuildCompactDatasetContext(businessQuestion, datasetJson);
            JObject request = BuildRequest("report", context, null,
                "Επέστρεψε ΑΠΟΚΛΕΙΣΤΙΚΑ JSON: {\"intro\":\"...\"}.");
            JObject parsed = await CallJarvisAsync(xSupport, request, cancellationToken).ConfigureAwait(false);
            return new JarvisPresentationResult { Intro = parsed == null ? null : (string)parsed["intro"] };
        }

        internal static async Task<JarvisPresentationResult> ComposeEmailAsync(XSupport xSupport, string businessQuestion, string datasetJson, string recipient, CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject context = BuildCompactDatasetContext(businessQuestion, datasetJson);
            context["recipient"] = recipient ?? string.Empty;
            JObject request = BuildRequest("email-draft", context, recipient,
                "Επέστρεψε ΑΠΟΚΛΕΙΣΤΙΚΑ JSON: {\"intro\":\"...\",\"emailSubject\":\"...\",\"emailBody\":\"...\"}.");
            JObject parsed = await CallJarvisAsync(xSupport, request, cancellationToken).ConfigureAwait(false);
            string subject = parsed == null ? null : (string)parsed["emailSubject"];
            string body = parsed == null ? null : (string)parsed["emailBody"];
            string intro = parsed == null ? null : (string)parsed["intro"];
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            {
                DebugLog.Log("[JARVIS-PRESENTATION] email composition unavailable; using deterministic human fallback");
                JarvisPresentationResult fallback = BuildDeterministicEmailFallback(businessQuestion, datasetJson);
                if (string.IsNullOrWhiteSpace(subject)) subject = fallback.EmailSubject;
                if (string.IsNullOrWhiteSpace(body)) body = fallback.EmailBody;
                if (string.IsNullOrWhiteSpace(intro)) intro = fallback.Intro;
            }
            else
            {
                DebugLog.Log("[JARVIS-PRESENTATION] email composition success subjectChars=" + subject.Length + " bodyChars=" + body.Length);
            }
            return new JarvisPresentationResult { Intro = intro, EmailSubject = subject, EmailBody = body };
        }

        internal static string BuildMarkdownTable(string datasetJson, int maxRows)
        {
            try
            {
                JObject root = JObject.Parse(datasetJson ?? string.Empty);
                JArray rows = root["rows"] as JArray;
                if (rows == null || rows.Count == 0) return "_Δεν βρέθηκαν εγγραφές._";
                List<JObject> objects = rows.OfType<JObject>().ToList();
                if (objects.Count == 0) return "_Δεν βρέθηκαν εγγραφές._";
                var columns = new List<string>();
                foreach (JObject row in objects)
                    foreach (JProperty property in row.Properties())
                        if (!columns.Any(x => string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase))) columns.Add(property.Name);
                if (columns.Count == 0) return "_Δεν βρέθηκαν στήλες._";
                int take = maxRows <= 0 ? objects.Count : Math.Min(objects.Count, maxRows);
                var lines = new List<string>();
                lines.Add("| " + string.Join(" | ", columns.Select(EscapeMarkdownCell)) + " |");
                lines.Add("| " + string.Join(" | ", columns.Select(x => "---")) + " |");
                for (int i = 0; i < take; i++)
                {
                    JObject row = objects[i];
                    lines.Add("| " + string.Join(" | ", columns.Select(c => EscapeMarkdownCell(GetValue(row, c)))) + " |");
                }
                if (take < objects.Count) lines.Add("\n_Εμφανίζονται " + take + " από " + objects.Count + " εγγραφές._");
                return string.Join("\n", lines);
            }
            catch { return string.Empty; }
        }

        private static JObject BuildCompactDatasetContext(string businessQuestion, string datasetJson)
        {
            var context = new JObject
            {
                ["businessQuestion"] = businessQuestion ?? string.Empty,
                ["rowCount"] = 0,
                ["columns"] = new JArray(),
                ["validatedRows"] = new JArray(),
                ["rowsComplete"] = true
            };
            try
            {
                JObject root = JObject.Parse(datasetJson ?? string.Empty);
                JArray rows = root["rows"] as JArray ?? new JArray();
                int total = (int?)root["totalRowCount"] ?? rows.Count;
                context["rowCount"] = total;
                var columns = new List<string>();
                foreach (JObject row in rows.OfType<JObject>().Take(MaxPresentationRows))
                    foreach (JProperty p in row.Properties())
                        if (!columns.Any(x => string.Equals(x, p.Name, StringComparison.OrdinalIgnoreCase))) columns.Add(p.Name);
                context["columns"] = new JArray(columns);
                var validatedRows = new JArray();
                foreach (JObject row in rows.OfType<JObject>().Take(MaxPresentationRows)) validatedRows.Add(row.DeepClone());
                context["validatedRows"] = validatedRows;
                context["rowsComplete"] = total <= MaxPresentationRows && validatedRows.Count == total;
            }
            catch { context["datasetParseError"] = true; }
            return context;
        }

        private static JarvisPresentationResult BuildDeterministicEmailFallback(string businessQuestion, string datasetJson)
        {
            var result = new JarvisPresentationResult
            {
                Intro = "Έχω ετοιμάσει το email με τα επαληθευμένα στοιχεία.",
                EmailSubject = "Στοιχεία από Jarvis"
            };
            try
            {
                JObject root = JObject.Parse(datasetJson ?? string.Empty);
                JArray rows = root["rows"] as JArray ?? new JArray();
                int total = (int?)root["totalRowCount"] ?? rows.Count;
                var sb = new StringBuilder();
                sb.AppendLine("Καλησπέρα,");
                sb.AppendLine();
                sb.Append("Παρακάτω είναι τα στοιχεία που προέκυψαν για το αίτημα: ").AppendLine(businessQuestion ?? string.Empty);
                sb.AppendLine();
                sb.Append("Σύνολο εγγραφών: ").AppendLine(total.ToString());
                sb.AppendLine();
                int take = Math.Min(rows.Count, MaxPresentationRows);
                for (int i = 0; i < take; i++)
                {
                    JObject row = rows[i] as JObject;
                    if (row == null) continue;
                    sb.Append(i + 1).Append(". ");
                    bool first = true;
                    foreach (JProperty p in row.Properties())
                    {
                        if (!first) sb.Append(" | ");
                        first = false;
                        sb.Append(HumanizeColumn(p.Name)).Append(": ").Append(p.Value == null || p.Value.Type == JTokenType.Null ? "-" : p.Value.ToString(Formatting.None).Trim('"'));
                    }
                    sb.AppendLine();
                }
                if (total > take) sb.AppendLine("Υπάρχουν επιπλέον εγγραφές που δεν χωρούν στο σώμα του email.");
                sb.AppendLine();
                sb.AppendLine("Με εκτίμηση,");
                sb.Append("Jarvis");
                result.EmailBody = sb.ToString().Trim();
            }
            catch
            {
                result.EmailBody = "Παρακαλώ δείτε τα επαληθευμένα στοιχεία που προέκυψαν από το αίτημα: " + (businessQuestion ?? string.Empty);
            }
            return result;
        }

        private static string HumanizeColumn(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            switch (name.Trim().ToUpperInvariant())
            {
                case "FINDOC": return "ID";
                case "FINCODE": return "Παραστατικό";
                case "TRNDATE": return "Ημερομηνία";
                case "SUMAMNT": return "Ποσό";
                case "CUSTOMER_CODE": return "Κωδικός πελάτη";
                case "CUSTOMER_NAME": return "Πελάτης";
                case "SERIES": return "Σειρά";
                default: return name.Replace("_", " ");
            }
        }

        private static JObject BuildRequest(string mode, JObject context, string recipient, string outputContract)
        {
            string policyContext = JarvisPolicyRegistry.BuildTrainingContext(
                "Jarvis", "__presentation", new string[0], new string[0]);

            return new JObject
            {
                ["max_tokens"] = MaxTokens,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Είσαι το registered Jarvis presentation layer. Εφάρμοσε υποχρεωτικά το JARVIS_POLICY_CONTEXT. " +
                            outputContract + "\n\n" + policyContext
                    }
                },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject
                        {
                            ["mode"] = mode ?? string.Empty,
                            ["recipient"] = recipient ?? string.Empty,
                            ["validatedContext"] = context ?? new JObject()
                        }.ToString(Formatting.None)
                    }
                }
            };
        }

        private static async Task<JObject> CallJarvisAsync(XSupport xSupport, JObject request, CancellationToken cancellationToken)
        {
            if (xSupport == null || request == null) return null;
            AgentProxyResponse response = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient().SendAsync(xSupport, RuntimeAiAgent, request.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
            if (response == null || !response.Success || string.IsNullOrWhiteSpace(response.RawResponseJson))
            {
                DebugLog.Log("[JARVIS-PRESENTATION] provider call failed: " + (response == null ? "null-response" : (response.ErrorMessage ?? "unknown")));
                return null;
            }
            try
            {
                JObject root = JObject.Parse(response.RawResponseJson);
                JArray content = root["content"] as JArray ?? new JArray();
                JObject textBlock = content.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
                string text = textBlock == null ? null : (string)textBlock["text"];
                JObject parsed = ParseJsonObject(text);
                if (parsed == null) DebugLog.Log("[JARVIS-PRESENTATION] response parse failed preview=" + Truncate(text, 400));
                return parsed;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-PRESENTATION] response processing failed: " + ex.Message);
                return null;
            }
        }

        private static JObject ParseJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string value = text.Trim();
            int first = value.IndexOf('{');
            int last = value.LastIndexOf('}');
            if (first >= 0 && last > first) value = value.Substring(first, last - first + 1);
            try { return JObject.Parse(value); } catch { return null; }
        }

        private static string GetValue(JObject row, string column)
        {
            if (row == null || string.IsNullOrWhiteSpace(column)) return string.Empty;
            JProperty property = row.Properties().FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
            if (property == null || property.Value == null || property.Value.Type == JTokenType.Null) return string.Empty;
            return property.Value.Type == JTokenType.String ? property.Value.ToString() : property.Value.ToString(Formatting.None);
        }

        private static string EscapeMarkdownCell(string value) { return (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " "); }
        private static string Truncate(string value, int max) { string text = value ?? string.Empty; return text.Length <= max ? text : text.Substring(0, max) + "..."; }
    }
}

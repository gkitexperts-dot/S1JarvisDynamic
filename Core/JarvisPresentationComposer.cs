using System;
using System.Collections.Generic;
using System.Linq;
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
        // The desktop selects only the logical agent. Provider and model are
        // authoritative Verilic routing decisions and must never be hardcoded here.
        private const string RuntimeAiAgent = "Jarvis";
        private const int MaxTokens = 1600;

        internal static async Task<JarvisPresentationResult> ComposeReportAsync(
            XSupport xSupport, string businessQuestion, string datasetJson,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject context = BuildCompactDatasetContext(businessQuestion, datasetJson);
            JObject request = BuildRequest("report", context, null,
                "Γράψε μόνο ένα σύντομο, φυσικό εισαγωγικό κείμενο στα Ελληνικά για τα validated αποτελέσματα. " +
                "Μην αντιγράψεις όλες τις γραμμές και μην προσθέσεις κανένα γεγονός που δεν υπάρχει στο context. " +
                "Επέστρεψε ΑΠΟΚΛΕΙΣΤΙΚΑ JSON: {\"intro\":\"...\"}.");
            JObject parsed = await CallJarvisAsync(xSupport, request, cancellationToken).ConfigureAwait(false);
            return new JarvisPresentationResult { Intro = parsed == null ? null : (string)parsed["intro"] };
        }

        internal static async Task<JarvisPresentationResult> ComposeEmailAsync(
            XSupport xSupport, string businessQuestion, string datasetJson, string recipient,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            JObject context = BuildCompactDatasetContext(businessQuestion, datasetJson);
            context["recipient"] = recipient ?? string.Empty;
            JObject request = BuildRequest("email-draft", context, recipient,
                "Σύνθεσε φυσικό, επαγγελματικό email στα Ελληνικά χρησιμοποιώντας ΜΟΝΟ τα validated δεδομένα του context. " +
                "Μην αλλάξεις αριθμούς, ημερομηνίες, ονόματα ή ids και μην εφεύρεις πληροφορίες. " +
                "Το subject να είναι σύντομο και σχετικό. Το body να είναι έτοιμο για πραγματική αποστολή και να έχει ανθρώπινη μορφή, όχι raw key=value dump. " +
                "Επέστρεψε ΑΠΟΚΛΕΙΣΤΙΚΑ JSON: {\"intro\":\"σύντομη φράση πριν το draft\",\"emailSubject\":\"...\",\"emailBody\":\"...\"}.");
            JObject parsed = await CallJarvisAsync(xSupport, request, cancellationToken).ConfigureAwait(false);
            return new JarvisPresentationResult
            {
                Intro = parsed == null ? null : (string)parsed["intro"],
                EmailSubject = parsed == null ? null : (string)parsed["emailSubject"],
                EmailBody = parsed == null ? null : (string)parsed["emailBody"]
            };
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
                ["sampleRows"] = new JArray()
            };
            try
            {
                JObject root = JObject.Parse(datasetJson ?? string.Empty);
                JArray rows = root["rows"] as JArray ?? new JArray();
                context["rowCount"] = rows.Count;
                var columns = new List<string>();
                foreach (JObject row in rows.OfType<JObject>().Take(12))
                    foreach (JProperty p in row.Properties())
                        if (!columns.Any(x => string.Equals(x, p.Name, StringComparison.OrdinalIgnoreCase))) columns.Add(p.Name);
                context["columns"] = new JArray(columns);
                var samples = new JArray();
                foreach (JObject row in rows.OfType<JObject>().Take(5)) samples.Add(row.DeepClone());
                context["sampleRows"] = samples;
            }
            catch { context["datasetParseError"] = true; }
            return context;
        }

        private static JObject BuildRequest(string mode, JObject context, string recipient, string instruction)
        {
            return new JObject
            {
                ["max_tokens"] = MaxTokens,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Είσαι ο Jarvis presentation layer. Τα δεδομένα που λαμβάνεις έχουν ήδη επικυρωθεί από τον Jarvis control plane. " +
                                   "Επιτρέπεται να αλλάξεις μόνο wording/μορφοποίηση. Απαγορεύεται να αλλάξεις business facts, να κάνεις query, να συμπληρώσεις ελλείποντα στοιχεία ή να εκτελέσεις action. " + instruction
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
            AgentProxyResponse response = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                .SendAsync(xSupport, RuntimeAiAgent, request.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
            if (response == null || !response.Success || string.IsNullOrWhiteSpace(response.RawResponseJson)) return null;
            try
            {
                JObject root = JObject.Parse(response.RawResponseJson);
                JArray content = root["content"] as JArray ?? new JArray();
                JObject textBlock = content.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
                string text = textBlock == null ? null : (string)textBlock["text"];
                return ParseJsonObject(text);
            }
            catch { return null; }
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

        private static string EscapeMarkdownCell(string value)
        {
            return (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}

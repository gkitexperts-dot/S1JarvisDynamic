using System;
using System.Collections.Generic;
using System.Globalization;
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

    internal enum JarvisPresentationValueKind
    {
        Text,
        Number,
        Currency,
        Date,
        DateTime,
        Boolean
    }

    internal static class JarvisPresentationComposer
    {
        private const string RuntimeAiAgent = "Jarvis";
        private const int MaxTokens = 2400;

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

        /// <summary>
        /// Canonical deterministic table renderer. All user-visible tabular data
        /// uses the central Policies Inventory presentation profile for labels,
        /// dates, numbers, currency, nulls, alignment and authoritative links.
        /// </summary>
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
                        if (!columns.Any(x => string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase)))
                            columns.Add(property.Name);
                if (columns.Count == 0) return "_Δεν βρέθηκαν στήλες._";

                Dictionary<string, JarvisPresentationValueKind> kinds = columns.ToDictionary(
                    x => x,
                    x => DetectColumnKind(objects, x),
                    StringComparer.OrdinalIgnoreCase);

                int policyMax = JarvisPolicySettings.Presentation.MaxChatTableRows;
                int requestedMax = maxRows <= 0 ? policyMax : Math.Min(maxRows, policyMax);
                int take = Math.Min(objects.Count, requestedMax);
                var lines = new List<string>();

                lines.Add("| " + string.Join(" | ", columns.Select(c => EscapeMarkdownCell(JarvisPolicySettings.Presentation.GetColumnLabel(c)))) + " |");
                lines.Add("| " + string.Join(" | ", columns.Select(c => AlignmentMarker(kinds[c]))) + " |");

                for (int i = 0; i < take; i++)
                {
                    JObject row = objects[i];
                    lines.Add("| " + string.Join(" | ", columns.Select(c => RenderCell(row, c, kinds[c]))) + " |");
                }

                if (take < objects.Count)
                    lines.Add("\n_Εμφανίζονται " + take.ToString(CultureInfo.InvariantCulture) + " από " + objects.Count.ToString(CultureInfo.InvariantCulture) + " εγγραφές._");
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-PRESENTATION] canonical table render failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static JObject BuildCompactDatasetContext(string businessQuestion, string datasetJson)
        {
            int maxPresentationRows = JarvisPolicySettings.Presentation.DefaultPreviewRows;
            var context = new JObject
            {
                ["businessQuestion"] = businessQuestion ?? string.Empty,
                ["rowCount"] = 0,
                ["columns"] = new JArray(),
                ["validatedRows"] = new JArray(),
                ["rowsComplete"] = true,
                ["presentationPolicy"] = JarvisPolicySettings.Presentation.BuildPolicyEnvelope()
            };
            try
            {
                JObject root = JObject.Parse(datasetJson ?? string.Empty);
                JArray rows = root["rows"] as JArray ?? new JArray();
                int total = (int?)root["totalRowCount"] ?? rows.Count;
                context["rowCount"] = total;
                var columns = new List<string>();
                foreach (JObject row in rows.OfType<JObject>().Take(maxPresentationRows))
                    foreach (JProperty p in row.Properties())
                        if (!columns.Any(x => string.Equals(x, p.Name, StringComparison.OrdinalIgnoreCase)))
                            columns.Add(p.Name);
                context["columns"] = new JArray(columns.Select(JarvisPolicySettings.Presentation.GetColumnLabel));
                var validatedRows = new JArray();
                foreach (JObject row in rows.OfType<JObject>().Take(maxPresentationRows))
                    validatedRows.Add(row.DeepClone());
                context["validatedRows"] = validatedRows;
                context["rowsComplete"] = total <= maxPresentationRows && validatedRows.Count == total;
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
                sb.Append("Σύνολο εγγραφών: ").AppendLine(total.ToString(CultureInfo.GetCultureInfo(JarvisPolicySettings.Presentation.CultureName)));
                sb.AppendLine();
                int take = Math.Min(rows.Count, JarvisPolicySettings.Presentation.DefaultPreviewRows);
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
                        JarvisPresentationValueKind kind = DetectColumnKind(rows.OfType<JObject>().ToList(), p.Name);
                        sb.Append(JarvisPolicySettings.Presentation.GetColumnLabel(p.Name)).Append(": ")
                          .Append(FormatValue(p.Value, p.Name, kind));
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

        private static JObject BuildRequest(string mode, JObject context, string recipient, string outputContract)
        {
            string policyContext = JarvisAgentContextBuilder.BuildPresentationPolicyContext();

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
                            "Είσαι το registered Jarvis presentation layer. Εφάρμοσε υποχρεωτικά το JARVIS_POLICY_CONTEXT και το JARVIS_PRESENTATION_POLICY_PROFILE. " +
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

        private static string RenderCell(JObject row, string column, JarvisPresentationValueKind kind)
        {
            JToken value = FindValue(row, column);
            string formatted = FormatValue(value, column, kind);
            string linked = JarvisResultLinkMaterializer.MaterializeDatasetCell(row, column, formatted);
            return string.IsNullOrWhiteSpace(linked) ? EscapeMarkdownCell(formatted) : linked;
        }

        private static string FormatValue(JToken value, string column, JarvisPresentationValueKind kind)
        {
            if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined)
                return JarvisPolicySettings.Presentation.NullDisplay;

            CultureInfo culture = CultureInfo.GetCultureInfo(JarvisPolicySettings.Presentation.CultureName);
            if (kind == JarvisPresentationValueKind.Date || kind == JarvisPresentationValueKind.DateTime)
            {
                DateTime date;
                if (TryReadDate(value, out date))
                    return date.ToString(kind == JarvisPresentationValueKind.Date
                        ? JarvisPolicySettings.Presentation.DateFormat
                        : JarvisPolicySettings.Presentation.DateTimeFormat, culture);
            }

            if (kind == JarvisPresentationValueKind.Currency || kind == JarvisPresentationValueKind.Number)
            {
                decimal number;
                if (TryReadDecimal(value, out number))
                {
                    if (kind == JarvisPresentationValueKind.Currency)
                        return number.ToString(JarvisPolicySettings.Presentation.CurrencyNumberFormat, culture) + JarvisPolicySettings.Presentation.CurrencySuffix;
                    return number.ToString(JarvisPolicySettings.Presentation.NumberFormat, culture);
                }
            }

            if (kind == JarvisPresentationValueKind.Boolean)
            {
                bool flag;
                if (bool.TryParse(value.ToString(), out flag)) return flag ? "Ναι" : "Όχι";
            }

            return value.Type == JTokenType.String
                ? value.ToString()
                : value.ToString(Formatting.None).Trim('"');
        }

        private static JarvisPresentationValueKind DetectColumnKind(IEnumerable<JObject> rows, string column)
        {
            List<JToken> values = (rows ?? Enumerable.Empty<JObject>())
                .Select(x => FindValue(x, column))
                .Where(x => x != null && x.Type != JTokenType.Null && x.Type != JTokenType.Undefined)
                .Take(25)
                .ToList();

            bool dateHint = JarvisPolicySettings.Presentation.ColumnNameMatches(column, JarvisPolicySettings.Presentation.DateColumnHints);
            bool currencyHint = JarvisPolicySettings.Presentation.ColumnNameMatches(column, JarvisPolicySettings.Presentation.CurrencyColumnHints);

            if (dateHint && values.Any())
            {
                DateTime d;
                if (values.All(v => TryReadDate(v, out d)))
                {
                    bool hasTime = values.Any(v =>
                    {
                        DateTime parsed;
                        return TryReadDate(v, out parsed) && parsed.TimeOfDay != TimeSpan.Zero;
                    });
                    return hasTime ? JarvisPresentationValueKind.DateTime : JarvisPresentationValueKind.Date;
                }
            }

            if (values.Count > 0 && values.All(v => v.Type == JTokenType.Boolean))
                return JarvisPresentationValueKind.Boolean;

            decimal n;
            if (values.Count > 0 && values.All(v => TryReadDecimal(v, out n)))
                return currencyHint ? JarvisPresentationValueKind.Currency : JarvisPresentationValueKind.Number;

            return JarvisPresentationValueKind.Text;
        }

        private static string AlignmentMarker(JarvisPresentationValueKind kind)
        {
            switch (kind)
            {
                case JarvisPresentationValueKind.Number:
                case JarvisPresentationValueKind.Currency:
                    return JarvisPolicySettings.Presentation.NumericAlignmentMarker;
                case JarvisPresentationValueKind.Date:
                case JarvisPresentationValueKind.DateTime:
                case JarvisPresentationValueKind.Boolean:
                    return JarvisPolicySettings.Presentation.DateAlignmentMarker;
                default:
                    return JarvisPolicySettings.Presentation.TextAlignmentMarker;
            }
        }

        private static JToken FindValue(JObject row, string column)
        {
            if (row == null || string.IsNullOrWhiteSpace(column)) return null;
            JProperty property = row.Properties().FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
            return property == null ? null : property.Value;
        }

        private static bool TryReadDecimal(JToken token, out decimal value)
        {
            value = 0m;
            if (token == null) return false;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                try { value = token.Value<decimal>(); return true; }
                catch { return false; }
            }
            string text = token.ToString().Trim();
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return true;
            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo(JarvisPolicySettings.Presentation.CultureName), out value);
        }

        private static bool TryReadDate(JToken token, out DateTime value)
        {
            value = default(DateTime);
            if (token == null) return false;
            if (token.Type == JTokenType.Date)
            {
                try { value = token.Value<DateTime>(); return true; }
                catch { return false; }
            }

            string text = token.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text)) return false;
            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                value = parsed;
                return true;
            }
            if (DateTime.TryParse(text, CultureInfo.GetCultureInfo(JarvisPolicySettings.Presentation.CultureName), DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        private static string EscapeMarkdownCell(string value)
        {
            return (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        private static string Truncate(string value, int max)
        {
            string text = value ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}

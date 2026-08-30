using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// The single user-facing presentation channel for Jarvis business results.
    /// Agents/executors own facts and structured outputs; this gateway owns how
    /// those results are rendered to the operator. Applicable central policies
    /// are mandatory. Model initiative is allowed only where no clear policy exists.
    /// </summary>
    internal static class JarvisPresentationGateway
    {
        internal static Task<JarvisPresentationResult> ComposeReportAsync(
            XSupport xSupport, string businessQuestion, string datasetJson)
        {
            return JarvisPresentationComposer.ComposeReportAsync(xSupport, businessQuestion, datasetJson);
        }

        internal static Task<JarvisPresentationResult> ComposeEmailAsync(
            XSupport xSupport, string businessQuestion, string datasetJson, string recipient)
        {
            return JarvisPresentationComposer.ComposeEmailAsync(xSupport, businessQuestion, datasetJson, recipient);
        }

        internal static string BuildDatasetTable(string datasetJson, int maxRows)
        {
            return JarvisPresentationComposer.BuildMarkdownTable(datasetJson, maxRows);
        }

        internal static string BuildTaskResultStatus(JarvisTaskExecutionResult result)
        {
            if (result == null) return string.Empty;

            string status;
            if (string.Equals(result.TaskType, "CreateCrmTask", StringComparison.OrdinalIgnoreCase))
            {
                status = JarvisPolicySettings.Presentation.CrmTaskCreatedLabel;
                JArray ids = result.Outputs == null ? null : result.Outputs["soaction_ids"] as JArray;
                if (ids != null && ids.Count > 0)
                    status = status.TrimEnd('.') + " (ID: " + string.Join(", ", ids.Select(x => x.ToString()).ToArray()) + ").";
            }
            else if (string.Equals(result.TaskType, "CreateCalendarEvent", StringComparison.OrdinalIgnoreCase))
            {
                status = JarvisPolicySettings.Presentation.CalendarCreatedLabel;
            }
            else if (string.Equals(result.TaskType, "ExportData", StringComparison.OrdinalIgnoreCase))
            {
                status = JarvisPolicySettings.Presentation.ExportCreatedLabel;
            }
            else if (string.Equals(result.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase))
            {
                status = JarvisPolicySettings.Presentation.EmailSentLabel;
            }
            else
            {
                // No explicit presentation policy exists for this task type.
                // Preserve the structured result without inventing a private style.
                status = result.Success ? JarvisPolicySettings.Presentation.DefaultSuccessIntro : string.Empty;
            }

            string[] links = JarvisResultLinkMaterializer.BuildMarkdownLinks(result);
            return links.Length == 0 ? status : status + " " + string.Join(" ", links);
        }

        internal static string BuildCombinedMessage(
            string intro,
            string datasetJson,
            IEnumerable<JarvisTaskExecutionResult> taskResults,
            IEnumerable<string> policyMessages,
            string confirmation,
            bool completed)
        {
            var parts = new List<string>();
            string resolvedIntro = intro;
            if (string.IsNullOrWhiteSpace(resolvedIntro))
                resolvedIntro = completed
                    ? JarvisPolicySettings.Presentation.DefaultSuccessIntro
                    : JarvisPolicySettings.Presentation.PartialSuccessIntro;

            if (!string.IsNullOrWhiteSpace(resolvedIntro)) parts.Add(resolvedIntro.Trim());

            if (!string.IsNullOrWhiteSpace(datasetJson))
            {
                string table = BuildDatasetTable(datasetJson, JarvisPolicySettings.Presentation.MaxChatTableRows);
                if (!string.IsNullOrWhiteSpace(table)) parts.Add(table.Trim());
            }

            if (taskResults != null)
            {
                string[] statuses = taskResults
                    .Where(x => x != null)
                    .Select(BuildTaskResultStatus)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                if (statuses.Length > 0) parts.Add(string.Join("\n", statuses));
            }

            if (policyMessages != null)
            {
                string[] messages = policyMessages.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
                if (messages.Length > 0) parts.Add(string.Join("\n", messages));
            }

            if (!string.IsNullOrWhiteSpace(confirmation)) parts.Add(confirmation.Trim());
            return FinalizeFreeform(string.Join("\n\n", parts.ToArray()));
        }

        internal static string BuildConfirmationMessage(JObject payload, string intro)
        {
            if (payload == null) return "Δεν υπάρχει payload για επιβεβαίωση.";
            string to = payload["to"] == null ? string.Empty : payload["to"].ToString();
            string subject = payload["subject"] == null ? string.Empty : payload["subject"].ToString();
            string body = payload["body"] == null ? string.Empty : payload["body"].ToString();
            string prefix = string.IsNullOrWhiteSpace(intro) ? "Έχω ετοιμάσει το email που θα σταλεί:" : intro.Trim();
            return FinalizeFreeform(prefix + "\n\n**Προς:** " + to + "\n**Θέμα:** " + subject + "\n\n" + body + "\n\nΝα το στείλω;");
        }

        internal static string BuildFailureMessage(string prefix, IEnumerable<string> issues)
        {
            string[] details = (issues ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            return FinalizeFreeform((prefix ?? string.Empty) + (details.Length == 0 ? string.Empty : " " + string.Join(" | ", details)));
        }

        /// <summary>
        /// Mandatory final policy boundary for every user-facing business output,
        /// including continuation/fallback text. Model-authored prose may survive
        /// only for aspects for which no explicit presentation policy exists.
        /// Known presentation invariants are always normalized here.
        /// </summary>
        internal static string FinalizeFreeform(string text)
        {
            string value = text ?? string.Empty;
            if (value.Length == 0) return value;

            value = NormalizeCrmTaskLinks(value);
            value = NormalizeLocalFileLinks(value);
            value = NormalizePolicyDates(value);
            value = NormalizeMarkdownTableAlignment(value);
            return value;
        }

        private static string NormalizeCrmTaskLinks(string text)
        {
            string value = text ?? string.Empty;

            // Replace legacy/non-actionable CRM placeholders when the visible link
            // itself contains the authoritative SOACTION id.
            value = Regex.Replace(
                value,
                @"\[(?<label>[^\]]*?(?<id>\d{5,})[^\]]*)\]\(\s*javascript\\?:void\\?\(0\\?\)\s*\)",
                m => "[" + m.Groups["label"].Value + "](" + BuildCrmTaskUri(m.Groups["id"].Value) + ")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Continuation/legacy processing may return a verified creation status
            // as prose rather than JarvisTaskExecutionResult. The final gateway is
            // still responsible for the presentation invariant: a created CRM task
            // with an explicit returned id must be addressable.
            if (ContainsSuccessfulCrmCreation(value))
            {
                MatchCollection ids = Regex.Matches(
                    value,
                    @"(?:ID|Κωδικός\s+Εργασίας)\s*[:#]?\s*\[?(?<id>\d{5,})\]?",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (Match match in ids)
                {
                    string id = match.Groups["id"].Value;
                    string uri = BuildCrmTaskUri(id);
                    if (value.IndexOf(uri, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    value = value.TrimEnd() + "\n\n[Άνοιγμα εργασίας " + id + "](" + uri + ")";
                }
            }

            return value;
        }

        private static bool ContainsSuccessfulCrmCreation(string text)
        {
            string value = (text ?? string.Empty).ToLowerInvariant();
            if (!(value.Contains("crm") || value.Contains("εργασία") || value.Contains("εργασια"))) return false;
            return value.Contains("δημιουργήθηκε") || value.Contains("δημιουργηθηκε") ||
                   value.Contains("καταχωρήθηκε") || value.Contains("καταχωρηθηκε") ||
                   value.Contains("created successfully");
        }

        private static string NormalizeLocalFileLinks(string text)
        {
            return Regex.Replace(
                text ?? string.Empty,
                @"\[(?<label>[^\]]+)\]\((?<url>file:///[^\)]+)\)",
                m =>
                {
                    try
                    {
                        string url = m.Groups["url"].Value.Replace("\\_", "_");
                        string path = new Uri(url).LocalPath;
                        return "[" + m.Groups["label"].Value + "](" + path + ")";
                    }
                    catch { return m.Value; }
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string NormalizePolicyDates(string text)
        {
            string value = text ?? string.Empty;
            value = Regex.Replace(
                value,
                @"\b(?<y>20\d{2})-(?<m>0[1-9]|1[0-2])-(?<d>0[1-9]|[12]\d|3[01])(?:[T ](?<hh>[01]\d|2[0-3]):(?<mm>[0-5]\d)(?::[0-5]\d)?)?\b",
                m =>
                {
                    DateTime parsed;
                    string raw = m.Value.Replace('T', ' ');
                    string[] formats = raw.Length > 10
                        ? new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }
                        : new[] { "yyyy-MM-dd" };
                    if (!DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                        return m.Value;
                    return parsed.ToString(
                        raw.Length > 10 ? JarvisPolicySettings.Presentation.DateTimeFormat : JarvisPolicySettings.Presentation.DateFormat,
                        CultureInfo.GetCultureInfo(JarvisPolicySettings.Presentation.CultureName));
                },
                RegexOptions.CultureInvariant);
            return value;
        }

        private static string NormalizeMarkdownTableAlignment(string text)
        {
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (!LooksLikeMarkdownRow(lines[i]) || !LooksLikeMarkdownSeparator(lines[i + 1])) continue;
                string[] headers = SplitMarkdownRow(lines[i]);
                if (headers.Length == 0) continue;

                string[] markers = new string[headers.Length];
                for (int c = 0; c < headers.Length; c++)
                {
                    string header = StripMarkdown(headers[c]);
                    if (JarvisPolicySettings.Presentation.ColumnNameMatches(header, JarvisPolicySettings.Presentation.DateColumnHints))
                        markers[c] = JarvisPolicySettings.Presentation.DateAlignmentMarker;
                    else if (JarvisPolicySettings.Presentation.ColumnNameMatches(header, JarvisPolicySettings.Presentation.CurrencyColumnHints) || ColumnLooksNumeric(lines, i + 2, c))
                        markers[c] = JarvisPolicySettings.Presentation.NumericAlignmentMarker;
                    else
                        markers[c] = JarvisPolicySettings.Presentation.TextAlignmentMarker;
                }
                lines[i + 1] = "| " + string.Join(" | ", markers) + " |";
            }
            return string.Join("\n", lines);
        }

        private static bool LooksLikeMarkdownRow(string line)
        {
            string value = (line ?? string.Empty).Trim();
            return value.StartsWith("|", StringComparison.Ordinal) && value.EndsWith("|", StringComparison.Ordinal) && value.Count(x => x == '|') >= 2;
        }

        private static bool LooksLikeMarkdownSeparator(string line)
        {
            if (!LooksLikeMarkdownRow(line)) return false;
            string[] cells = SplitMarkdownRow(line);
            return cells.Length > 0 && cells.All(x => Regex.IsMatch(x.Trim(), @"^:?-{3,}:?$", RegexOptions.CultureInvariant));
        }

        private static string[] SplitMarkdownRow(string line)
        {
            string value = (line ?? string.Empty).Trim();
            if (value.StartsWith("|", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.EndsWith("|", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);
            return value.Split('|').Select(x => x.Trim()).ToArray();
        }

        private static string StripMarkdown(string value)
        {
            string text = value ?? string.Empty;
            text = Regex.Replace(text, @"\[[^\]]+\]\([^\)]+\)", m => m.Value.Substring(1, m.Value.IndexOf(']') - 1));
            return text.Replace("**", string.Empty).Replace("__", string.Empty).Trim();
        }

        private static bool ColumnLooksNumeric(string[] lines, int firstDataLine, int columnIndex)
        {
            int inspected = 0;
            for (int i = firstDataLine; i < lines.Length && inspected < 5; i++)
            {
                if (!LooksLikeMarkdownRow(lines[i])) break;
                string[] cells = SplitMarkdownRow(lines[i]);
                if (columnIndex >= cells.Length) continue;
                string value = StripMarkdown(cells[columnIndex]).Replace("€", string.Empty).Replace(" ", string.Empty);
                if (string.IsNullOrWhiteSpace(value)) continue;
                inspected++;
                decimal parsed;
                if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.GetCultureInfo(JarvisPolicySettings.Presentation.CultureName), out parsed) ||
                    decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                    return true;
            }
            return false;
        }

        private static string BuildCrmTaskUri(string soactionId)
        {
            return JarvisPolicySettings.Presentation.CrmTaskUriTemplate.Replace("{soactionId}", soactionId ?? string.Empty);
        }
    }
}

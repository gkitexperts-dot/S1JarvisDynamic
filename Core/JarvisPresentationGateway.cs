using System;
using System.Collections.Generic;
using System.Linq;
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
            return string.Join("\n\n", parts.ToArray());
        }

        internal static string BuildConfirmationMessage(JObject payload, string intro)
        {
            if (payload == null) return "Δεν υπάρχει payload για επιβεβαίωση.";
            string to = payload["to"] == null ? string.Empty : payload["to"].ToString();
            string subject = payload["subject"] == null ? string.Empty : payload["subject"].ToString();
            string body = payload["body"] == null ? string.Empty : payload["body"].ToString();
            string prefix = string.IsNullOrWhiteSpace(intro) ? "Έχω ετοιμάσει το email που θα σταλεί:" : intro.Trim();
            return prefix + "\n\n**Προς:** " + to + "\n**Θέμα:** " + subject + "\n\n" + body + "\n\nΝα το στείλω;";
        }

        internal static string BuildFailureMessage(string prefix, IEnumerable<string> issues)
        {
            string[] details = (issues ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            return (prefix ?? string.Empty) + (details.Length == 0 ? string.Empty : " " + string.Join(" | ", details));
        }

        /// <summary>
        /// Final boundary for model-authored text. Existing deterministic policies
        /// have already been applied upstream. This method intentionally does not
        /// rewrite content for which the policy plane has no structured evidence;
        /// that is the only area where model presentation initiative may survive.
        /// </summary>
        internal static string FinalizeFreeform(string text)
        {
            return text ?? string.Empty;
        }
    }
}

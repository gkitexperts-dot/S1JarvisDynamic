using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Restricted Echo executor for one already-confirmed SendEmail task.
    /// It receives only the frozen payload approved by the operator. It never
    /// performs recipient lookup, data lookup, body recomposition or replanning.
    /// </summary>
    internal static class JarvisControlledEchoExecutor
    {
        internal static async Task<JarvisTaskExecutionResult> ExecuteSendEmailAsync(
            XSupport xSupport,
            string objectId,
            JObject frozenPayload)
        {
            var result = new JarvisTaskExecutionResult
            {
                ObjectId = objectId,
                TaskType = "SendEmail",
                OwnerAgent = "Echo",
                Success = false
            };

            try
            {
                if (xSupport == null)
                    throw new ArgumentNullException("xSupport");
                if (frozenPayload == null)
                    throw new InvalidOperationException("Echo SendEmail requires a frozen confirmation payload.");

                string to = frozenPayload["to"] == null ? null : frozenPayload["to"].ToString();
                string subject = frozenPayload["subject"] == null ? null : frozenPayload["subject"].ToString();
                string body = frozenPayload["body"] == null ? null : frozenPayload["body"].ToString();
                if (string.IsNullOrWhiteSpace(to))
                    throw new InvalidOperationException("Frozen SendEmail payload is missing 'to'.");
                if (string.IsNullOrWhiteSpace(subject))
                    throw new InvalidOperationException("Frozen SendEmail payload is missing 'subject'.");
                if (string.IsNullOrWhiteSpace(body))
                    throw new InvalidOperationException("Frozen SendEmail payload is missing 'body'.");

                // Execute the mature, already deployed email transport directly.
                // No AI/model call is allowed between operator confirmation and
                // the irreversible send operation.
                string raw = await JarvisEmailAccess.ExecuteSendEmail(
                    xSupport,
                    (JObject)frozenPayload.DeepClone());

                JObject transportResult;
                try { transportResult = JObject.Parse(raw ?? string.Empty); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Echo send_email returned a non-JSON result: " + ex.Message);
                }

                bool success = (bool?)transportResult["success"] == true;
                bool hasAttachment = (bool?)transportResult["hasAttachment"] == true;

                result.Outputs["email_send_result"] = transportResult.DeepClone();
                result.Outputs["success"] = success;
                result.Outputs["hasAttachment"] = hasAttachment;
                result.Success = success;

                if (!success)
                {
                    string error = transportResult["error"] == null
                        ? "send_email returned success=false."
                        : transportResult["error"].ToString();
                    result.Issues.Add(error);
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add(ex.Message);
                return result;
            }
        }
    }
}

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Restricted executor for the first live/read-only orchestration slice.
    /// Supports ReportData only. Jarvis owns dispatch and validation; Atlas may
    /// request exactly one query_data SELECT. The returned query dataset is then
    /// normalized deterministically into the registered ReportData outputs.
    /// No write/external tool is exposed here.
    /// </summary>
    internal static class JarvisControlledTaskExecutor
    {
        private const string Model = "claude-opus-5";
        private const int MaxTokens = 6000;

        internal static async Task<JarvisTaskExecutionResult> ExecuteReportDataAsync(
            XSupport xSupport,
            string objectId,
            JObject dispatchInputs,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new JarvisTaskExecutionResult
            {
                ObjectId = objectId,
                TaskType = "ReportData",
                OwnerAgent = "Atlas",
                Success = false
            };

            try
            {
                if (xSupport == null)
                    throw new ArgumentNullException("xSupport");
                if (dispatchInputs == null || dispatchInputs["business_question"] == null)
                    throw new InvalidOperationException("ReportData dispatch is missing business_question.");

                string question = dispatchInputs["business_question"].ToString();
                JObject firstRequest = BuildQueryRequest(question);
                S1Jarvis.Core.AgentProxyResponse firstResponse = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                    .SendAsync(xSupport, "Atlas", firstRequest.ToString(Formatting.None), cancellationToken)
                    .ConfigureAwait(false);

                EnsureSuccess(firstResponse, "Atlas ReportData query planning failed.");
                JObject queryUse = FindToolUse(firstResponse.RawResponseJson, "query_data");
                if (queryUse == null)
                    throw new InvalidOperationException("Atlas ReportData did not return the required query_data tool call.");

                JObject queryInput = queryUse["input"] as JObject;
                string sql = queryInput == null ? null : (string)queryInput["sql"];
                if (string.IsNullOrWhiteSpace(sql))
                    throw new InvalidOperationException("Atlas ReportData returned query_data without SQL.");

                string queryResult = JarvisTools.ExecuteQueryData(xSupport, sql);
                if (string.IsNullOrWhiteSpace(queryResult))
                    throw new InvalidOperationException("Atlas ReportData query returned an empty dataset.");
                if (LooksLikeQueryError(queryResult))
                    throw new InvalidOperationException("Atlas ReportData query failed: " + queryResult);

                string summary = BuildDeterministicSummary(question, queryResult);
                if (string.IsNullOrWhiteSpace(summary))
                    throw new InvalidOperationException("Atlas ReportData could not normalize the query result into a summary.");

                result.Outputs["dataset"] = new JValue(queryResult);
                result.Outputs["summary"] = new JValue(summary);
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add(ex.Message);
                return result;
            }
        }

        private static JObject BuildQueryRequest(string question)
        {
            return new JObject
            {
                ["model"] = Model,
                ["max_tokens"] = MaxTokens,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Είσαι ο Atlas executor υπό τον έλεγχο του Jarvis. Εκτελείς μόνο το συγκεκριμένο ReportData task. " +
                                   "Επιτρέπεται αποκλειστικά query_data και αποκλειστικά SELECT. Κάνε ΕΝΑ στοχευμένο query που απαντά το business_question. " +
                                   "Για παραστατικά χρησιμοποίησε FINDOC: FINDOC, FINCODE, TRNDATE, SUMAMNT, SERIES, SOSOURCE, COMPANY, TRDR. " +
                                   "Για όνομα συναλλασσόμενου JOIN TRDR ON TRDR.TRDR=FINDOC.TRDR. " +
                                   "Για όνομα σειράς JOIN SERIES ON SERIES.COMPANY=FINDOC.COMPANY AND SERIES.SERIES=FINDOC.SERIES AND SERIES.SOSOURCE=FINDOC.SOSOURCE. " +
                                   "Μην χρησιμοποιείς άγνωστες στήλες. Ο Jarvis θα κάνει validation και downstream σύνθεση του αποτελέσματος."
                    }
                },
                ["tools"] = new JArray(BuildQueryDataTool()),
                ["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = "query_data" },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = "business_question: " + (question ?? string.Empty)
                    }
                }
            };
        }

        private static JObject BuildQueryDataTool()
        {
            return new JObject
            {
                ["name"] = "query_data",
                ["description"] = "Execute a read-only SQL Server SELECT against Soft1 data.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["sql"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("sql")
                }
            };
        }

        private static JObject FindToolUse(string rawResponseJson, string toolName)
        {
            if (string.IsNullOrWhiteSpace(rawResponseJson))
                return null;
            JObject root = JObject.Parse(rawResponseJson);
            JArray content = root["content"] as JArray ?? new JArray();
            return content.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string)x["type"], "tool_use", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)x["name"], toolName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeQueryError(string queryResult)
        {
            string value = (queryResult ?? string.Empty).TrimStart();
            return value.StartsWith("Σφάλμα:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDeterministicSummary(string question, string queryResult)
        {
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null)
                    return queryResult.Trim();

                int totalRowCount = (int?)root["totalRowCount"] ?? rows.Count;
                if (rows.Count == 0)
                    return "Δεν βρέθηκαν δεδομένα για: " + (question ?? string.Empty);

                var sb = new StringBuilder();
                sb.Append("Αποτέλεσμα για: ").Append(question ?? string.Empty).AppendLine();
                sb.Append("Εγγραφές: ").Append(totalRowCount).AppendLine();

                int take = Math.Min(rows.Count, 10);
                for (int i = 0; i < take; i++)
                {
                    JObject row = rows[i] as JObject;
                    if (row == null)
                        continue;

                    if (take > 1)
                        sb.Append("#").Append(i + 1).Append(": ");

                    bool first = true;
                    foreach (JProperty property in row.Properties())
                    {
                        if (!first)
                            sb.Append(" | ");
                        first = false;
                        sb.Append(property.Name).Append(": ");
                        if (property.Value == null || property.Value.Type == JTokenType.Null)
                            sb.Append("-");
                        else
                            sb.Append(property.Value.ToString(Formatting.None).Trim('"'));
                    }
                    sb.AppendLine();
                }

                if (totalRowCount > take)
                    sb.Append("... και άλλες ").Append(totalRowCount - take).Append(" εγγραφές.");

                return sb.ToString().Trim();
            }
            catch
            {
                return queryResult.Trim();
            }
        }

        private static void EnsureSuccess(S1Jarvis.Core.AgentProxyResponse response, string fallback)
        {
            if (response == null || !response.Success)
                throw new InvalidOperationException(
                    response != null && !string.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? response.ErrorMessage
                        : fallback);
        }
    }
}

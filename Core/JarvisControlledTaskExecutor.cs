using System;
using System.Linq;
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
    /// request exactly one query_data SELECT and must return a structured summary.
    /// No write/external tool is exposed here.
    /// </summary>
    internal static class JarvisControlledTaskExecutor
    {
        private const string Model = "claude-opus-5";
        private const int MaxTokens = 6000;
        private const string ResultToolName = "emit_task_result";

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

                JObject firstRequest = BuildQueryRequest(dispatchInputs["business_question"].ToString());
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
                string toolUseId = (string)queryUse["id"] ?? string.Empty;

                JObject secondRequest = BuildResultRequest(
                    dispatchInputs["business_question"].ToString(),
                    queryUse,
                    toolUseId,
                    queryResult);

                S1Jarvis.Core.AgentProxyResponse secondResponse = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                    .SendAsync(xSupport, "Atlas", secondRequest.ToString(Formatting.None), cancellationToken)
                    .ConfigureAwait(false);

                EnsureSuccess(secondResponse, "Atlas ReportData result synthesis failed.");
                JObject resultUse = FindToolUse(secondResponse.RawResponseJson, ResultToolName);
                if (resultUse == null)
                    throw new InvalidOperationException("Atlas ReportData did not return the structured task result.");

                JObject output = resultUse["input"] as JObject;
                string summary = output == null ? null : (string)output["summary"];
                if (string.IsNullOrWhiteSpace(summary))
                    throw new InvalidOperationException("Atlas ReportData structured result is missing summary.");

                result.Outputs["dataset"] = new JValue(queryResult ?? string.Empty);
                result.Outputs["summary"] = new JValue(summary.Trim());
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
                                   "Επιτρέπεται αποκλειστικά query_data και αποκλειστικά SELECT. Κάνε ένα στοχευμένο query που απαντά το business_question."
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

        private static JObject BuildResultRequest(string question, JObject queryUse, string toolUseId, string queryResult)
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
                        ["text"] = "Είσαι ο Atlas executor υπό τον έλεγχο του Jarvis. Από το validated query result δημιούργησε σύντομη αλλά πλήρη περίληψη για downstream χρήση. " +
                                   "Μην εκτελέσεις άλλο business action. Επέστρεψε αποκλειστικά emit_task_result."
                    }
                },
                ["tools"] = new JArray(BuildResultTool()),
                ["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = ResultToolName },
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = "business_question: " + (question ?? string.Empty) },
                    new JObject { ["role"] = "assistant", ["content"] = new JArray(queryUse.DeepClone()) },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = toolUseId ?? string.Empty,
                                ["content"] = queryResult ?? string.Empty
                            }
                        }
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

        private static JObject BuildResultTool()
        {
            return new JObject
            {
                ["name"] = ResultToolName,
                ["description"] = "Return the validated structured output of the ReportData task. No action is performed.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["summary"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("summary")
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

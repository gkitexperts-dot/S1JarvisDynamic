using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Controlled execution for the currently promoted Echo write tasks.
    /// Jarvis owns graph state/confirmation. Echo only materializes the native
    /// tool arguments from the atomic task fragment and the scoped tool contract.
    /// No agent-to-agent calls are possible here.
    /// </summary>
    internal static class JarvisControlledEchoTaskExecutor
    {
        internal static async Task<JarvisTaskExecutionResult> ExecuteCreateCrmTaskAsync(
            XSupport xSupport, string objectId, JObject dispatchInputs)
        {
            var result = NewResult(objectId, "CreateCrmTask", "Echo");
            try
            {
                string fragment = ReadFragment(dispatchInputs);
                int currentUserId = xSupport != null && xSupport.ConnectionInfo != null
                    ? xSupport.ConnectionInfo.UserId
                    : 0;

                var history = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["content"] = fragment
                });

                for (int iteration = 0; iteration < 4; iteration++)
                {
                    var request = new JObject
                    {
                        ["model"] = "runtime-session-model",
                        ["max_tokens"] = 4000,
                        ["system"] = new JArray(new JObject
                        {
                            ["type"] = "text",
                            ["text"] =
                                "Εκτελείς ΕΝΑ atomic Jarvis task: CreateCrmTask. " +
                                "Δεν αποφασίζεις capabilities ή άλλα tasks. Χρησιμοποίησε μόνο τα attached tools. " +
                                "Ο τρέχων Soft1 userId είναι " + currentUserId.ToString() + ". " +
                                "Αν η οδηγία λέει 'μου/σε μένα', actorUserId=" + currentUserId.ToString() + ". " +
                                "Μετέτρεψε φυσική ημερομηνία/ώρα σε ISO. Αν χρειάζεται άλλος Soft1 χρήστης, " +
                                "βρες τον με query_data στον USERS πριν το create_crm_task. " +
                                "Μόλις έχεις title, description, fromDate και actorUserId, κάλεσε create_crm_task."
                        }),
                        ["tools"] = JArray.FromObject(new object[]
                        {
                            JarvisTools.QueryDataToolDefinition,
                            JarvisTools.CreateCrmTaskToolDefinition
                        }),
                        ["messages"] = history
                    };

                    AgentProxyResponse proxy = await new VerilicAiMessagesClient()
                        .SendAsync(xSupport, "Echo", request.ToString(Formatting.None), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (proxy == null || !proxy.Success)
                    {
                        result.Issues.Add(proxy == null ? "Echo returned no response." : proxy.ErrorMessage ?? "Echo execution failed.");
                        return result;
                    }

                    JObject response = JObject.Parse(proxy.RawResponseJson ?? "{}");
                    JArray content = response["content"] as JArray ?? new JArray();
                    string stop = (string)response["stop_reason"] ?? string.Empty;
                    history.Add(new JObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                    if (!string.Equals(stop, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add("Echo did not materialize the registered CreateCrmTask tool call.");
                        return result;
                    }

                    var toolResults = new JArray();
                    foreach (JObject block in content.OfType<JObject>().Where(x => string.Equals((string)x["type"], "tool_use", StringComparison.OrdinalIgnoreCase)))
                    {
                        string id = (string)block["id"] ?? string.Empty;
                        string name = (string)block["name"] ?? string.Empty;
                        JObject input = block["input"] as JObject ?? new JObject();
                        if (string.Equals(name, "query_data", StringComparison.OrdinalIgnoreCase))
                        {
                            string toolResult = JarvisTools.ExecuteQueryData(xSupport, input);
                            toolResults.Add(ToolResult(id, toolResult, false));
                            continue;
                        }
                        if (!string.Equals(name, "create_crm_task", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Issues.Add("Echo attempted an unscoped tool: " + name);
                            return result;
                        }

                        string raw = JarvisTools.ExecuteCreateCrmTask(xSupport, input);
                        JObject parsed = TryObject(raw);
                        if (parsed == null || (bool?)parsed["success"] != true)
                        {
                            result.Issues.Add("create_crm_task failed: " + raw);
                            return result;
                        }

                        JArray ids = new JArray();
                        JArray rows = parsed["results"] as JArray;
                        if (rows != null)
                            foreach (JObject row in rows.OfType<JObject>())
                                if (row["soactionId"] != null) ids.Add(row["soactionId"].DeepClone());
                        if (ids.Count == 0 && parsed["soactionId"] != null) ids.Add(parsed["soactionId"].DeepClone());

                        result.Outputs["crm_task_reference"] = parsed.DeepClone();
                        result.Outputs["soaction_ids"] = ids;
                        result.Success = true;
                        return result;
                    }

                    if (toolResults.Count == 0)
                    {
                        result.Issues.Add("Echo returned tool_use without an executable scoped tool.");
                        return result;
                    }
                    history.Add(new JObject { ["role"] = "user", ["content"] = toolResults });
                }

                result.Issues.Add("CreateCrmTask exceeded controlled tool iterations.");
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add("CreateCrmTask controlled executor failed: " + ex.Message);
                return result;
            }
        }

        internal static async Task<JarvisTaskExecutionResult> ExecuteCreateCalendarEventAsync(
            XSupport xSupport, string objectId, JObject dispatchInputs)
        {
            var result = NewResult(objectId, "CreateCalendarEvent", "Echo");
            try
            {
                string fragment = ReadFragment(dispatchInputs);
                var request = new JObject
                {
                    ["model"] = "runtime-session-model",
                    ["max_tokens"] = 3000,
                    ["system"] = new JArray(new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Εκτελείς ΕΝΑ atomic Jarvis task: CreateCalendarEvent. " +
                            "Δεν αποφασίζεις capabilities ή άλλα tasks. Χρησιμοποίησε μόνο το attached create_outlook_event. " +
                            "Μετέτρεψε φυσική ημερομηνία/ώρα σε ISO. Αν δεν δίνεται διάρκεια, χρησιμοποίησε 30 λεπτά. " +
                            "Η αναφορά προσώπου μέσα στην περιγραφή ΔΕΝ σημαίνει attendee εκτός αν ο χρήστης ζήτησε ρητά πρόσκληση. " +
                            "Κάλεσε create_outlook_event ακριβώς μία φορά."
                    }),
                    ["tools"] = JArray.FromObject(new object[] { JarvisEmailAccess.CreateOutlookEventToolDefinition }),
                    ["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = "create_outlook_event" },
                    ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = fragment })
                };

                AgentProxyResponse proxy = await new VerilicAiMessagesClient()
                    .SendAsync(xSupport, "Echo", request.ToString(Formatting.None), CancellationToken.None)
                    .ConfigureAwait(false);
                if (proxy == null || !proxy.Success)
                {
                    result.Issues.Add(proxy == null ? "Echo returned no response." : proxy.ErrorMessage ?? "Echo execution failed.");
                    return result;
                }

                JObject response = JObject.Parse(proxy.RawResponseJson ?? "{}");
                JObject call = (response["content"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(x => string.Equals((string)x["type"], "tool_use", StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals((string)x["name"], "create_outlook_event", StringComparison.OrdinalIgnoreCase));
                if (call == null)
                {
                    result.Issues.Add("Echo did not materialize create_outlook_event.");
                    return result;
                }

                JObject input = call["input"] as JObject ?? new JObject();
                string raw = await JarvisEmailAccess.ExecuteCreateOutlookEvent(xSupport, input).ConfigureAwait(false);
                JObject parsed = TryObject(raw);
                if (parsed == null || (bool?)parsed["success"] != true)
                {
                    result.Issues.Add("create_outlook_event failed: " + raw);
                    return result;
                }

                result.Outputs["calendar_event"] = parsed.DeepClone();
                result.Outputs["eventId"] = parsed["id"] == null ? JValue.CreateNull() : parsed["id"].DeepClone();
                result.Outputs["webLink"] = parsed["webLink"] == null ? JValue.CreateNull() : parsed["webLink"].DeepClone();
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add("CreateCalendarEvent controlled executor failed: " + ex.Message);
                return result;
            }
        }

        private static string ReadFragment(JObject inputs)
        {
            string fragment = inputs == null || inputs["__intent_fragment"] == null
                ? string.Empty
                : inputs["__intent_fragment"].ToString();
            if (string.IsNullOrWhiteSpace(fragment))
                throw new InvalidOperationException("Atomic intent fragment is missing.");
            return fragment;
        }

        private static JarvisTaskExecutionResult NewResult(string objectId, string taskType, string owner)
        {
            return new JarvisTaskExecutionResult
            {
                ObjectId = objectId,
                TaskType = taskType,
                OwnerAgent = owner,
                Success = false
            };
        }

        private static JObject ToolResult(string id, string content, bool isError)
        {
            return new JObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id ?? string.Empty,
                ["content"] = content ?? string.Empty,
                ["is_error"] = isError
            };
        }

        private static JObject TryObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); }
            catch { return null; }
        }
    }
}

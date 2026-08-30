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
    /// Controlled execution for promoted Echo write tasks.
    /// Jarvis owns graph state, prerequisites, confirmation and resolved policy
    /// context. Echo only materializes the registered terminal tool call.
    /// </summary>
    internal static class JarvisControlledEchoTaskExecutor
    {
        internal static async Task<JarvisTaskExecutionResult> ExecuteCreateCrmTaskAsync(
            XSupport xSupport, string objectId, JObject dispatchInputs)
        {
            var result = NewResult(objectId, "CreateCrmTask", "Echo");
            try
            {
                string fragment = ReadRequiredInternalContext(dispatchInputs, "__intent_fragment", "Atomic intent fragment is missing.");
                string policies = ReadRequiredInternalContext(dispatchInputs, "__policy_context", "Jarvis dispatch policy context is missing.");
                string runtimeNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                int currentUserId = xSupport != null && xSupport.ConnectionInfo != null
                    ? xSupport.ConnectionInfo.UserId
                    : 0;

                var request = new JObject
                {
                    ["max_tokens"] = 3000,
                    ["metadata"] = new JObject { ["jarvis_task"] = "CreateCrmTask" },
                    ["system"] = new JArray(new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Εκτελείς το registered atomic task CreateCrmTask με terminal tool create_crm_task. " +
                            "Runtime context: localNow=" + runtimeNow + "; currentSoft1UserId=" + currentUserId.ToString() + ". " +
                            "Εφάρμοσε υποχρεωτικά το JARVIS_POLICY_CONTEXT.\n\n" + policies
                    }),
                    ["tools"] = JArray.FromObject(new object[] { JarvisTools.CreateCrmTaskToolDefinition }),
                    ["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = "create_crm_task" },
                    ["messages"] = new JArray(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = fragment
                    })
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
                                         string.Equals((string)x["name"], "create_crm_task", StringComparison.OrdinalIgnoreCase));
                if (call == null)
                {
                    result.Issues.Add("Echo did not materialize create_crm_task.");
                    return result;
                }

                JObject input = call["input"] as JObject ?? new JObject();
                JObject resolutionContext = BuildResolutionContext(dispatchInputs);
                JarvisToolContractValidator.ApplyResolutionEvidence("create_crm_task", input, resolutionContext);

                string[] resolutionIssues = JarvisToolContractValidator.ValidateResolutionEvidence("create_crm_task", resolutionContext);
                if (resolutionIssues.Length > 0)
                {
                    result.Issues.Add("NEEDS_USER_INPUT: " + string.Join(" | ", resolutionIssues));
                    return result;
                }

                string[] contractIssues = JarvisToolContractValidator.ValidateProposedInput("create_crm_task", input);
                if (contractIssues.Length > 0)
                {
                    result.Issues.Add("NEEDS_USER_INPUT: " + string.Join(" | ", contractIssues));
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
                string fragment = ReadRequiredInternalContext(dispatchInputs, "__intent_fragment", "Atomic intent fragment is missing.");
                string policies = ReadRequiredInternalContext(dispatchInputs, "__policy_context", "Jarvis dispatch policy context is missing.");
                string runtimeNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                var request = new JObject
                {
                    ["max_tokens"] = 3000,
                    ["metadata"] = new JObject { ["jarvis_task"] = "CreateCalendarEvent" },
                    ["system"] = new JArray(new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Εκτελείς το registered atomic task CreateCalendarEvent με terminal tool create_outlook_event. " +
                            "Runtime context: localNow=" + runtimeNow + ". " +
                            "Εφάρμοσε υποχρεωτικά το JARVIS_POLICY_CONTEXT.\n\n" + policies
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
                JObject resolutionContext = BuildResolutionContext(dispatchInputs);

                // External parties are authorization-bearing inputs. The executor
                // may format an already-resolved attendee list, but it may never
                // introduce one after Jarvis made the authorization decision.
                JToken resolvedAttendees = resolutionContext["attendees"];
                JToken proposedAttendees = input["attendees"];
                bool resolvedHasAttendees = HasValue(resolvedAttendees);
                bool proposedHasAttendees = HasValue(proposedAttendees);
                if (!resolvedHasAttendees && proposedHasAttendees)
                {
                    result.Issues.Add("NEEDS_USER_INPUT: create_outlook_event proposed attendees that were not resolved and authorized by Jarvis.");
                    return result;
                }
                if (resolvedHasAttendees)
                    input["attendees"] = resolvedAttendees.DeepClone();

                JarvisToolContractValidator.ApplyResolutionEvidence("create_outlook_event", input, resolutionContext);

                string[] resolutionIssues = JarvisToolContractValidator.ValidateResolutionEvidence("create_outlook_event", resolutionContext);
                if (resolutionIssues.Length > 0)
                {
                    result.Issues.Add("NEEDS_USER_INPUT: " + string.Join(" | ", resolutionIssues));
                    return result;
                }

                string[] contractIssues = JarvisToolContractValidator.ValidateProposedInput("create_outlook_event", input);
                if (contractIssues.Length > 0)
                {
                    result.Issues.Add("NEEDS_USER_INPUT: " + string.Join(" | ", contractIssues));
                    return result;
                }

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

        private static bool HasValue(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined) return false;
            if (value.Type == JTokenType.Array) return ((JArray)value).Count > 0;
            return !string.IsNullOrWhiteSpace(value.ToString());
        }

        private static JObject BuildResolutionContext(JObject dispatchInputs)
        {
            var resolutionContext = new JObject();
            if (dispatchInputs == null) return resolutionContext;
            foreach (JProperty property in dispatchInputs.Properties())
            {
                if (!property.Name.StartsWith("__", StringComparison.Ordinal))
                    resolutionContext[property.Name] = property.Value.DeepClone();
            }
            return resolutionContext;
        }

        private static string ReadRequiredInternalContext(JObject inputs, string name, string error)
        {
            string value = inputs == null || inputs[name] == null ? string.Empty : inputs[name].ToString();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(error);
            return value;
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

        private static JObject TryObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); }
            catch { return null; }
        }
    }
}

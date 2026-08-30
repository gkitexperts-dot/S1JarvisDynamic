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
    /// Jarvis owns graph state, prerequisites and confirmation. Echo may only
    /// materialize one native call for the already-authorized atomic task.
    /// The proposed call is validated against JarvisToolRegistry BEFORE the
    /// real tool is executed. No agent-to-agent calls and no retry loops live here.
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
                string runtimeNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                int currentUserId = xSupport != null && xSupport.ConnectionInfo != null
                    ? xSupport.ConnectionInfo.UserId
                    : 0;

                var request = new JObject
                {
                    ["max_tokens"] = 3000,
                    ["system"] = new JArray(new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Εκτελείς ΕΝΑ atomic Jarvis task: CreateCrmTask. " +
                            "Δεν αποφασίζεις capabilities, prerequisites ή άλλα tasks. " +
                            "Πρότεινε ακριβώς ΜΙΑ κλήση create_crm_task από το atomic intent και το runtime context. " +
                            "Δεν επιτρέπεται lookup/retry loop σε αυτό το execution layer. " +
                            "Τρέχουσα τοπική ημερομηνία/ώρα Jarvis=" + runtimeNow + ". " +
                            "Ο τρέχων Soft1 userId είναι " + currentUserId.ToString() + ". " +
                            "Αν η οδηγία αναθέτει την εργασία στον ίδιο τον χειριστή (π.χ. 'βάλε μου'), actorUserId=" + currentUserId.ToString() + ". " +
                            "Μετέτρεψε φυσική ημερομηνία/ώρα σε ISO. " +
                            "Ο Jarvis θα ελέγξει deterministic την προτεινόμενη κλήση με το authoritative tool prerequisite contract πριν εκτελεστεί."
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
                string[] nativeIssues = JarvisToolContractValidator.ValidateProposedInput("create_crm_task", input);
                string[] resolutionIssues = JarvisToolContractValidator.ValidateResolutionEvidence("create_crm_task", input);
                string[] contractIssues = nativeIssues.Concat(resolutionIssues)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
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
                string fragment = ReadFragment(dispatchInputs);
                string runtimeNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                var request = new JObject
                {
                    ["max_tokens"] = 3000,
                    ["system"] = new JArray(new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Εκτελείς ΕΝΑ atomic Jarvis task: CreateCalendarEvent. " +
                            "Δεν αποφασίζεις capabilities, prerequisites ή άλλα tasks. " +
                            "Πρότεινε ακριβώς ΜΙΑ κλήση create_outlook_event. " +
                            "Τρέχουσα τοπική ημερομηνία/ώρα Jarvis=" + runtimeNow + ". " +
                            "Μετέτρεψε φυσική ημερομηνία/ώρα σε ISO. Αν δεν δίνεται διάρκεια, χρησιμοποίησε 30 λεπτά. " +
                            "Η αναφορά προσώπου μέσα στην περιγραφή ΔΕΝ σημαίνει attendee εκτός αν ο χρήστης ζήτησε ρητά πρόσκληση. " +
                            "Ο Jarvis θα ελέγξει deterministic την προτεινόμενη κλήση με το authoritative tool prerequisite contract πριν εκτελεστεί."
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

        private static JObject TryObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return JObject.Parse(raw); }
            catch { return null; }
        }
    }
}

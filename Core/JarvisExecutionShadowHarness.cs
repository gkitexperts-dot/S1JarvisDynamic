using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisControlledPilotOutcome
    {
        internal bool Handled { get; set; }
        internal bool Completed { get; set; }
        internal string UserMessage { get; set; }
    }

    internal static class JarvisExecutionShadowHarness
    {
        private static readonly HashSet<string> PromotedControlledTasks = new HashSet<string>(
            new[] { "ReportData", "SendEmail", "CreateCrmTask", "CreateCalendarEvent" },
            StringComparer.OrdinalIgnoreCase);

        internal static async Task RunAndLogSafeAsync(XSupport xSupport, string userPrompt,
            JarvisPendingConfirmationSession pendingSession = null, JarvisDatasetSession datasetSession = null)
        {
            try { await TryRunControlledPilotAsync(xSupport, userPrompt, pendingSession, datasetSession); }
            catch (Exception ex) { DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex); }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(
            XSupport xSupport, string userPrompt, JarvisPendingConfirmationSession pendingSession,
            JarvisDatasetSession datasetSession = null)
        {
            var outcome = new JarvisControlledPilotOutcome { Handled = false, Completed = false };
            try
            {
                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, userPrompt);
                if (!IsSupportedControlledPlan(planning))
                    return outcome;

                outcome.Handled = true;
                bool hasEmail = HasTask(planning, "SendEmail");
                if (hasEmail && pendingSession == null)
                {
                    outcome.UserMessage = "Το controlled orchestration δεν έχει διαθέσιμο confirmation session.";
                    return outcome;
                }
                if (pendingSession != null) pendingSession.Clear();

                ResolveDeterministicSendRecipient(planning);
                ResolveDeterministicRuntimeContext(planning, xSupport);

                var coordinator = new JarvisExecutionCoordinator(planning.Graph, planning.Preview);
                JarvisExecutionControlSnapshot before = coordinator.Inspect();
                LogSnapshot("initial", before, coordinator.GetDispatchableObjectIds(), null, null, null);
                if (!before.IsValid)
                {
                    outcome.UserMessage = BuildFailureMessage("Ο Jarvis απέρριψε το execution plan.", before.ValidationIssues.ToArray());
                    return outcome;
                }

                JarvisExecutionStepSnapshot reportStep = FindStep(before, "ReportData", "Atlas");
                JarvisPresentationResult presentation = null;
                string datasetJson = string.Empty;
                string businessQuestion = string.Empty;

                if (reportStep != null)
                {
                    JObject reportInputs;
                    string[] reportInputIssues;
                    if (!coordinator.TryGetDispatchInputs(reportStep.ObjectId, out reportInputs, out reportInputIssues))
                    {
                        LogSnapshot("dispatch_input_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, reportInputIssues, null);
                        outcome.UserMessage = BuildFailureMessage("Ο Jarvis απέρριψε τα inputs του Atlas.", reportInputIssues);
                        return outcome;
                    }

                    string[] beginIssues;
                    if (!coordinator.TryBeginDispatch(reportStep.ObjectId, out beginIssues))
                    {
                        LogSnapshot("dispatch_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, beginIssues, null);
                        outcome.UserMessage = BuildFailureMessage("Ο Jarvis δεν επέτρεψε την εκτέλεση του Atlas.", beginIssues);
                        return outcome;
                    }

                    LogSnapshot("running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);
                    JarvisTaskExecutionResult reportResult = await JarvisControlledTaskExecutor.ExecuteReportDataAsync(xSupport, reportStep.ObjectId, reportInputs);
                    if (!reportResult.Success)
                    {
                        string[] failedAcceptIssues;
                        coordinator.TryAcceptResult(reportResult, out failedAcceptIssues);
                        LogSnapshot("result_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, reportResult.Issues.ToArray(), null);
                        outcome.UserMessage = BuildFailureMessage("Η ανάκτηση δεδομένων απέτυχε.", reportResult.Issues.ToArray());
                        return outcome;
                    }

                    businessQuestion = reportInputs["business_question"] == null ? userPrompt : reportInputs["business_question"].ToString();
                    datasetJson = reportResult.Outputs["dataset"] == null ? string.Empty : reportResult.Outputs["dataset"].ToString();
                    if (datasetSession != null) datasetSession.TryCapture(businessQuestion, datasetJson);

                    if (hasEmail)
                    {
                        string recipient = FindResolvedSendInput(planning, "to");
                        presentation = await JarvisPresentationComposer.ComposeEmailAsync(xSupport, businessQuestion, datasetJson, recipient);
                        if (presentation != null && !string.IsNullOrWhiteSpace(presentation.EmailBody))
                            reportResult.Outputs["summary"] = presentation.EmailBody;
                        if (presentation != null && !string.IsNullOrWhiteSpace(presentation.EmailSubject))
                            ApplyResolvedSendInput(planning, "subject", presentation.EmailSubject);
                    }
                    else
                    {
                        presentation = await JarvisPresentationComposer.ComposeReportAsync(xSupport, businessQuestion, datasetJson);
                    }

                    string[] acceptIssues;
                    if (!coordinator.TryAcceptResult(reportResult, out acceptIssues))
                    {
                        LogSnapshot("result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, acceptIssues, null);
                        outcome.UserMessage = BuildFailureMessage("Ο Jarvis απέρριψε το αποτέλεσμα του Atlas.", acceptIssues);
                        return outcome;
                    }
                    LogSnapshot("result_accepted", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);
                }

                var completedSideEffects = new List<string>();
                var deferredIssues = new List<string>();

                JarvisExecutionStepSnapshot crmStep = FindStep(coordinator.Inspect(), "CreateCrmTask", "Echo");
                if (crmStep != null)
                {
                    string[] crmAuthIssues;
                    if (!GrantInitialInstructionAuthorization(coordinator, crmStep, out crmAuthIssues))
                    {
                        deferredIssues.Add(BuildFailureMessage("Η εργασία CRM χρειάζεται επίλυση πριν εκτελεστεί.", crmAuthIssues));
                    }
                    else
                    {
                        JObject crmInputs;
                        string[] crmInputIssues;
                        if (!coordinator.TryGetDispatchInputs(crmStep.ObjectId, out crmInputs, out crmInputIssues))
                        {
                            deferredIssues.Add(BuildFailureMessage("Η εργασία CRM χρειάζεται επιπλέον πληροφορίες.", crmInputIssues));
                        }
                        else
                        {
                            string[] crmBeginIssues;
                            if (!coordinator.TryBeginDispatch(crmStep.ObjectId, out crmBeginIssues))
                            {
                                deferredIssues.Add(BuildFailureMessage("Η εργασία CRM δεν είναι ακόμη dispatchable.", crmBeginIssues));
                            }
                            else
                            {
                                LogSnapshot("echo_crm_running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), crmStep.ObjectId, null, null);
                                JarvisTaskExecutionResult crmResult = await JarvisControlledEchoTaskExecutor.ExecuteCreateCrmTaskAsync(xSupport, crmStep.ObjectId, crmInputs);
                                string[] crmAcceptIssues;
                                if (!coordinator.TryAcceptResult(crmResult, out crmAcceptIssues))
                                {
                                    deferredIssues.Add(BuildFailureMessage("Ο Jarvis απέρριψε το αποτέλεσμα της εργασίας CRM.", crmAcceptIssues));
                                }
                                else
                                {
                                    LogSnapshot(crmResult.Success ? "echo_crm_accepted" : "echo_crm_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), crmStep.ObjectId, crmResult.Success ? null : crmResult.Issues.ToArray(), null);
                                    if (crmResult.Success)
                                        completedSideEffects.Add(BuildCrmStatus(crmResult));
                                    else
                                        deferredIssues.Add(BuildFailureMessage("Η εργασία CRM δεν ολοκληρώθηκε.", crmResult.Issues.ToArray()));
                                }
                            }
                        }
                    }
                }

                JarvisExecutionStepSnapshot calendarStep = FindStep(coordinator.Inspect(), "CreateCalendarEvent", "Echo");
                if (calendarStep != null)
                {
                    string calendarFragment = ReadIntentFragment(planning, calendarStep.ObjectId);
                    if (LooksLikeExternalInvitation(calendarFragment))
                    {
                        deferredIssues.Add("Το Outlook event περιλαμβάνει πιθανή πρόσκληση τρίτου και χρειάζεται ξεχωριστή επιβεβαίωση για attendees.");
                    }
                    else
                    {
                        string[] calendarAuthIssues;
                        if (!GrantInitialInstructionAuthorization(coordinator, calendarStep, out calendarAuthIssues))
                        {
                            deferredIssues.Add(BuildFailureMessage("Το calendar event χρειάζεται επίλυση πριν εκτελεστεί.", calendarAuthIssues));
                        }
                        else
                        {
                            JObject calendarInputs;
                            string[] calendarInputIssues;
                            if (!coordinator.TryGetDispatchInputs(calendarStep.ObjectId, out calendarInputs, out calendarInputIssues))
                            {
                                deferredIssues.Add(BuildFailureMessage("Το calendar event χρειάζεται επιπλέον πληροφορίες.", calendarInputIssues));
                            }
                            else
                            {
                                string[] calendarBeginIssues;
                                if (!coordinator.TryBeginDispatch(calendarStep.ObjectId, out calendarBeginIssues))
                                {
                                    deferredIssues.Add(BuildFailureMessage("Το calendar event δεν είναι ακόμη dispatchable.", calendarBeginIssues));
                                }
                                else
                                {
                                    LogSnapshot("echo_calendar_running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), calendarStep.ObjectId, null, null);
                                    JarvisTaskExecutionResult calendarResult = await JarvisControlledEchoTaskExecutor.ExecuteCreateCalendarEventAsync(xSupport, calendarStep.ObjectId, calendarInputs);
                                    string[] calendarAcceptIssues;
                                    if (!coordinator.TryAcceptResult(calendarResult, out calendarAcceptIssues))
                                    {
                                        deferredIssues.Add(BuildFailureMessage("Ο Jarvis απέρριψε το αποτέλεσμα του calendar event.", calendarAcceptIssues));
                                    }
                                    else
                                    {
                                        LogSnapshot(calendarResult.Success ? "echo_calendar_accepted" : "echo_calendar_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), calendarStep.ObjectId, calendarResult.Success ? null : calendarResult.Issues.ToArray(), null);
                                        if (calendarResult.Success)
                                            completedSideEffects.Add(BuildCalendarStatus(calendarResult));
                                        else
                                            deferredIssues.Add(BuildFailureMessage("Το Outlook calendar event δεν ολοκληρώθηκε.", calendarResult.Issues.ToArray()));
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (string issue in deferredIssues)
                    completedSideEffects.Add("⚠ " + issue);

                if (!hasEmail)
                {
                    string table = string.IsNullOrWhiteSpace(datasetJson) ? string.Empty : JarvisPresentationComposer.BuildMarkdownTable(datasetJson, 250);
                    string intro = presentation == null ? null : presentation.Intro;
                    if (string.IsNullOrWhiteSpace(intro)) intro = deferredIssues.Count == 0 ? "Ολοκλήρωσα την εντολή." : "Εκτέλεσα όσα βήματα ήταν διαθέσιμα και χρειάζομαι διευκρίνιση για τα υπόλοιπα.";
                    outcome.Completed = deferredIssues.Count == 0;
                    outcome.UserMessage = BuildCombinedMessage(intro, table, completedSideEffects, null);
                    return outcome;
                }

                JarvisExecutionStepSnapshot emailStep = FindStep(coordinator.Inspect(), "SendEmail", "Echo");
                if (emailStep == null)
                {
                    outcome.UserMessage = BuildCombinedMessage(null, null, completedSideEffects, "Ο Jarvis απέρριψε το plan: λείπει το SendEmail/Echo βήμα.");
                    return outcome;
                }

                string[] captureIssues;
                if (!pendingSession.TryCapture(coordinator, out captureIssues))
                {
                    LogSnapshot("confirmation_payload_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), emailStep.ObjectId, captureIssues, null);
                    completedSideEffects.Add("⚠ " + BuildFailureMessage("Το email δεν μπόρεσε να προετοιμαστεί για επιβεβαίωση.", captureIssues));
                    string failedTable = string.IsNullOrWhiteSpace(datasetJson) ? string.Empty : JarvisPresentationComposer.BuildMarkdownTable(datasetJson, 250);
                    outcome.UserMessage = BuildCombinedMessage("Εκτέλεσα όσα ανεξάρτητα βήματα ήταν διαθέσιμα.", failedTable, completedSideEffects, null);
                    return outcome;
                }

                LogSnapshot("confirmation_payload_frozen", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), pendingSession.PendingObjectId, null, pendingSession.PayloadHash);
                string reportTable = string.IsNullOrWhiteSpace(datasetJson) ? string.Empty : JarvisPresentationComposer.BuildMarkdownTable(datasetJson, 250);
                string confirmation = BuildConfirmationMessage(pendingSession.FrozenPayload, presentation == null ? null : presentation.Intro);
                outcome.UserMessage = BuildCombinedMessage(null, reportTable, completedSideEffects, confirmation);
                return outcome;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] controlled pilot exception: " + ex);
                if (outcome.Handled) outcome.UserMessage = "✖ Σφάλμα controlled orchestration: " + ex.Message;
                return outcome;
            }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryResumeConfirmationAndExecuteAsync(
            XSupport xSupport, JarvisPendingConfirmationSession pendingSession, string userText)
        {
            var outcome = new JarvisControlledPilotOutcome { Handled = false, Completed = false };
            if (pendingSession == null || !pendingSession.HasPending || !JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText)) return outcome;
            outcome.Handled = true;

            JarvisExecutionCoordinator coordinator;
            string objectId;
            string payloadHash;
            string[] issues;
            if (!pendingSession.TryConfirm(userText, out coordinator, out objectId, out payloadHash, out issues))
            {
                LogSnapshot("confirmation_rejected", null, new string[0], pendingSession.PendingObjectId, issues, pendingSession.PayloadHash);
                outcome.UserMessage = BuildFailureMessage("Η επιβεβαίωση απορρίφθηκε από τον Jarvis.", issues);
                return outcome;
            }

            JObject frozenPayload = pendingSession.FrozenPayload;
            LogSnapshot("confirmation_granted", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, null, payloadHash);
            JarvisExecutionStepSnapshot sendStep = coordinator.Inspect().Steps.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
            if (sendStep == null || !string.Equals(sendStep.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) || !string.Equals(sendStep.OwnerAgent, "Echo", StringComparison.OrdinalIgnoreCase))
            {
                pendingSession.Clear();
                outcome.UserMessage = "Ο Jarvis απέρριψε την επιβεβαίωση: το pending task δεν είναι SendEmail/Echo.";
                return outcome;
            }

            string[] beginIssues;
            if (!coordinator.TryBeginDispatch(objectId, out beginIssues))
            {
                LogSnapshot("echo_dispatch_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, beginIssues, payloadHash);
                pendingSession.Clear();
                outcome.UserMessage = BuildFailureMessage("Ο Jarvis δεν επέτρεψε την αποστολή.", beginIssues);
                return outcome;
            }

            LogSnapshot("echo_running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, null, payloadHash);
            JarvisTaskExecutionResult echoResult = await JarvisControlledEchoExecutor.ExecuteSendEmailAsync(xSupport, objectId, frozenPayload);
            pendingSession.Clear();

            string[] acceptIssues;
            if (!coordinator.TryAcceptResult(echoResult, out acceptIssues))
            {
                LogSnapshot("echo_result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, acceptIssues, payloadHash);
                outcome.UserMessage = "Η ενέργεια αποστολής εκτελέστηκε, αλλά ο Jarvis δεν μπόρεσε να επικυρώσει το αποτέλεσμα. Δεν θα γίνει αυτόματο retry για αποφυγή διπλής αποστολής.";
                return outcome;
            }

            LogSnapshot(echoResult.Success ? "echo_result_accepted" : "echo_result_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId,
                echoResult.Success ? null : echoResult.Issues.ToArray(), payloadHash);
            if (!echoResult.Success)
            {
                outcome.UserMessage = BuildFailureMessage("Η αποστολή email απέτυχε.", echoResult.Issues.ToArray());
                return outcome;
            }

            outcome.Completed = true;
            string to = frozenPayload == null || frozenPayload["to"] == null ? string.Empty : frozenPayload["to"].ToString();
            outcome.UserMessage = "Το email στάλθηκε με επιτυχία" + (string.IsNullOrWhiteSpace(to) ? "." : " στο " + to + ".");
            return outcome;
        }

        internal static bool TryResumeConfirmation(JarvisPendingConfirmationSession pendingSession, string userText)
        {
            return pendingSession != null && pendingSession.HasPending && JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText);
        }

        private static bool IsSupportedControlledPlan(JarvisShadowOrchestrationResult planning)
        {
            if (!IsBaseValid(planning) || planning.Preview.Entries.Count == 0) return false;
            foreach (JarvisExecutionPlanEntry entry in planning.Preview.Entries)
            {
                if (entry == null || !PromotedControlledTasks.Contains(entry.TaskType)) return false;
                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(entry.TaskType);
                if (descriptor == null || !string.Equals(descriptor.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static bool IsBaseValid(JarvisShadowOrchestrationResult planning)
        {
            return planning != null && planning.GateEnabled && planning.Graph != null && planning.Preview != null && planning.Graph.IsValid && planning.Preview.IsValid;
        }

        private static bool HasTask(JarvisShadowOrchestrationResult planning, string taskType)
        {
            return planning != null && planning.Preview != null && planning.Preview.Entries.Any(x => x != null && string.Equals(x.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        }

        private static JarvisExecutionStepSnapshot FindStep(JarvisExecutionControlSnapshot snapshot, string taskType, string owner)
        {
            return snapshot == null ? null : snapshot.Steps.FirstOrDefault(x => x != null &&
                string.Equals(x.TaskType, taskType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.OwnerAgent, owner, StringComparison.OrdinalIgnoreCase));
        }

        private static bool GrantInitialInstructionAuthorization(JarvisExecutionCoordinator coordinator, JarvisExecutionStepSnapshot step, out string[] issues)
        {
            if (step == null || !step.RequiresConfirmation)
            {
                issues = new string[0];
                return true;
            }
            bool ok = coordinator.GrantConfirmation(step.ObjectId, out issues);
            if (ok)
                DebugLog.Log("[ORCH-CONTROL] initial explicit instruction authorizes local task object=" + step.ObjectId + " task=" + step.TaskType);
            return ok;
        }

        private static void ResolveDeterministicSendRecipient(JarvisShadowOrchestrationResult planning)
        {
            if (planning == null || planning.Graph == null) return;
            JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase));
            if (node == null) return;
            JarvisPrerequisiteResolutionItem item = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "to", StringComparison.OrdinalIgnoreCase));
            if (item == null || item.Value != null) return;

            Match match = Regex.Match(node.IntentFragment ?? string.Empty, @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return;
            item.Value = new JValue(match.Value);
            item.Kind = JarvisPrerequisiteResolutionKind.ResolvedFromRouting;
            item.Reason = "Literal email recipient extracted deterministically from the atomic SendEmail intent fragment.";
        }

        private static void ResolveDeterministicRuntimeContext(JarvisShadowOrchestrationResult planning, XSupport xSupport)
        {
            if (planning == null || planning.Graph == null || xSupport == null || xSupport.ConnectionInfo == null) return;
            int currentUserId = xSupport.ConnectionInfo.UserId;
            if (currentUserId <= 0) return;

            foreach (JarvisValidatedTaskNode node in planning.Graph.Nodes.Where(x => x != null && x.Descriptor != null))
            {
                if (!RefersToCurrentOperator(node.IntentFragment)) continue;

                bool needsActor = false;
                foreach (string toolName in node.Descriptor.Tools ?? new string[0])
                {
                    JarvisToolPrerequisiteDescriptor contract = JarvisToolRegistry.FindPrerequisites(toolName);
                    if (contract == null) continue;
                    foreach (string resolution in contract.ResolutionInputs ?? new string[0])
                    {
                        string token = resolution ?? string.Empty;
                        if (token.IndexOf("actorUserId", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            needsActor = true;
                            break;
                        }
                    }
                    if (needsActor) break;
                }

                if (!needsActor) continue;

                SetResolvedRuntimeValue(node, "actorUserId", new JValue(currentUserId), "Current operator resolved deterministically from the active Soft1 session.");
                JarvisPrerequisiteResolutionItem assignee = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "assignee", StringComparison.OrdinalIgnoreCase));
                if (assignee != null && assignee.Value == null)
                {
                    assignee.Value = new JValue(currentUserId);
                    assignee.Kind = JarvisPrerequisiteResolutionKind.ResolvedFromRouting;
                    assignee.Reason = "Self-assignment resolved deterministically to the active Soft1 operator.";
                }
            }
        }

        private static bool RefersToCurrentOperator(string fragment)
        {
            string value = (fragment ?? string.Empty).ToLowerInvariant();
            return value.Contains("βάλε μου") || value.Contains("βαλε μου") || value.Contains("για μένα") || value.Contains("για μενα") ||
                   value.Contains("σε εμένα") || value.Contains("σε εμενα") || value.Contains("my task") || value.Contains("assign to me");
        }

        private static void SetResolvedRuntimeValue(JarvisValidatedTaskNode node, string inputName, JToken value, string reason)
        {
            JarvisPrerequisiteResolutionItem item = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, inputName, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true };
                node.Prerequisites.Add(item);
            }
            item.Value = value == null ? null : value.DeepClone();
            item.Kind = JarvisPrerequisiteResolutionKind.ResolvedFromRouting;
            item.Reason = reason ?? string.Empty;
        }

        private static string ReadIntentFragment(JarvisShadowOrchestrationResult planning, string objectId)
        {
            JarvisValidatedTaskNode node = planning == null || planning.Graph == null ? null : planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
            return node == null ? string.Empty : node.IntentFragment ?? string.Empty;
        }

        private static bool LooksLikeExternalInvitation(string fragment)
        {
            string value = (fragment ?? string.Empty).ToLowerInvariant();
            return value.Contains("κάλεσε") || value.Contains("καλεσε") || value.Contains("πρόσκλη") || value.Contains("προσκλη") ||
                   value.Contains("invite") || value.Contains("attendee") || value.Contains("συμμετέχ") || value.Contains("συμμετεχ");
        }

        private static string FindResolvedSendInput(JarvisShadowOrchestrationResult planning, string inputName)
        {
            if (planning == null || planning.Graph == null) return string.Empty;
            JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase));
            if (node == null) return string.Empty;
            JarvisPrerequisiteResolutionItem item = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, inputName, StringComparison.OrdinalIgnoreCase) && x.Value != null);
            return item == null ? string.Empty : item.Value.ToString();
        }

        private static void ApplyResolvedSendInput(JarvisShadowOrchestrationResult planning, string inputName, string value)
        {
            if (planning == null || planning.Graph == null || string.IsNullOrWhiteSpace(inputName) || string.IsNullOrWhiteSpace(value)) return;
            JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase));
            if (node == null) return;
            JarvisPrerequisiteResolutionItem item = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, inputName, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            item.Value = new JValue(value.Trim());
            item.Kind = JarvisPrerequisiteResolutionKind.ResolvedFromRouting;
            item.Reason = "Jarvis presentation layer composed/resolved this value before confirmation from validated task context.";
        }

        private static string BuildCrmStatus(JarvisTaskExecutionResult result)
        {
            JToken ids = result == null || result.Outputs == null ? null : result.Outputs["soaction_ids"];
            string status = ids == null ? "✓ Η εργασία CRM στο Soft1 δημιουργήθηκε." : "✓ Η εργασία CRM στο Soft1 δημιουργήθηκε (ID: " + ids.ToString(Formatting.None) + ").";
            string[] links = JarvisResultLinkPolicy.BuildMarkdownLinks(result);
            return links.Length == 0 ? status : status + " " + string.Join(" ", links);
        }

        private static string BuildCalendarStatus(JarvisTaskExecutionResult result)
        {
            string status = "✓ Το προσωπικό Outlook calendar event δημιουργήθηκε.";
            string[] links = JarvisResultLinkPolicy.BuildMarkdownLinks(result);
            return links.Length == 0 ? status : status + " " + string.Join(" ", links);
        }

        private static string BuildCombinedMessage(string intro, string table, IList<string> statuses, string confirmation)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(intro)) parts.Add(intro.Trim());
            if (!string.IsNullOrWhiteSpace(table)) parts.Add(table.Trim());
            if (statuses != null && statuses.Count > 0) parts.Add(string.Join("\n", statuses));
            if (!string.IsNullOrWhiteSpace(confirmation)) parts.Add(confirmation.Trim());
            return string.Join("\n\n", parts.ToArray());
        }

        private static string BuildConfirmationMessage(JObject payload, string intro)
        {
            if (payload == null) return "Δεν υπάρχει payload για επιβεβαίωση.";
            string to = payload["to"] == null ? string.Empty : payload["to"].ToString();
            string subject = payload["subject"] == null ? string.Empty : payload["subject"].ToString();
            string body = payload["body"] == null ? string.Empty : payload["body"].ToString();
            string prefix = string.IsNullOrWhiteSpace(intro) ? "Έχω ετοιμάσει το email που θα σταλεί:" : intro.Trim();
            return prefix + "\n\n**Προς:** " + to + "\n**Θέμα:** " + subject + "\n\n" + body + "\n\nΝα το στείλω;";
        }

        private static string BuildFailureMessage(string prefix, string[] issues)
        {
            string detail = issues == null || issues.Length == 0 ? string.Empty : " " + string.Join(" | ", issues);
            return prefix + detail;
        }

        private static void LogSnapshot(string phase, JarvisExecutionControlSnapshot snapshot, string[] dispatchableObjectIds,
            string activeObjectId, string[] eventIssues, string payloadHash)
        {
            var root = new JObject
            {
                ["phase"] = phase ?? string.Empty,
                ["activeObjectId"] = activeObjectId ?? string.Empty,
                ["valid"] = snapshot == null || snapshot.IsValid,
                ["dispatchable"] = new JArray(dispatchableObjectIds ?? new string[0]),
                ["payloadHash"] = payloadHash ?? string.Empty,
                ["steps"] = snapshot == null ? new JArray() : new JArray(snapshot.Steps.Select(x => new JObject
                {
                    ["wave"] = x.Wave,
                    ["ordinal"] = x.Ordinal,
                    ["objectId"] = x.ObjectId ?? string.Empty,
                    ["taskType"] = x.TaskType ?? string.Empty,
                    ["owner"] = x.OwnerAgent ?? string.Empty,
                    ["state"] = x.State.ToString(),
                    ["requiresConfirmation"] = x.RequiresConfirmation,
                    ["confirmationGranted"] = x.ConfirmationGranted,
                    ["dependsOn"] = new JArray(x.DependsOn ?? new List<string>()),
                    ["boundInputs"] = new JArray(x.BoundInputs ?? new List<string>()),
                    ["ownerAgentInputs"] = new JArray(x.OwnerAgentInputs ?? new List<string>()),
                    ["materializedInputs"] = x.MaterializedInputs == null ? new JObject() : x.MaterializedInputs.DeepClone(),
                    ["issues"] = new JArray(x.ValidationIssues ?? new List<string>())
                })),
                ["issues"] = snapshot == null ? new JArray() : new JArray(snapshot.ValidationIssues ?? new List<string>()),
                ["eventIssues"] = new JArray(eventIssues ?? new string[0])
            };
            DebugLog.Log("[ORCH-CONTROL] " + root.ToString(Formatting.None));
        }
    }
}

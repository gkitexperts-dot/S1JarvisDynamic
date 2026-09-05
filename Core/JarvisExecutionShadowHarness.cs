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
            new[] { "ReportData", "ExportData", "SendEmail", "CreateCrmTask", "CreateCalendarEvent" },
            StringComparer.OrdinalIgnoreCase);

        internal static bool ShouldAttemptControlledPilot(
            string userPrompt,
            JarvisActiveOrchestrationContext activeContext = null)
        {
            // An open Jarvis-owned run is always eligible for continuation.
            if (activeContext != null && activeContext.HasOpenRun)
                return true;

            if (string.IsNullOrWhiteSpace(userPrompt))
                return false;

            // The rollout boundary is the promoted task set itself. Use the
            // authoritative task registry's IntentHints to decide whether the
            // controlled planner should run at all. This avoids a second provider
            // call for ordinary conversation while keeping task capability truth
            // in JarvisTaskRegistry rather than in UI/private keyword tables.
            string prompt = userPrompt.Trim();
            foreach (string taskType in PromotedControlledTasks)
            {
                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(taskType);
                if (descriptor == null)
                    continue;

                foreach (string hint in descriptor.IntentHints ?? new string[0])
                {
                    if (!string.IsNullOrWhiteSpace(hint) &&
                        prompt.IndexOf(hint.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        internal static async Task RunAndLogSafeAsync(XSupport xSupport, string userPrompt,
            JarvisPendingConfirmationSession pendingSession = null, JarvisDatasetSession datasetSession = null,
            JarvisActiveOrchestrationContext activeContext = null)
        {
            try { await TryRunControlledPilotAsync(xSupport, userPrompt, pendingSession, datasetSession, activeContext); }
            catch (Exception ex) { DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex); }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(
            XSupport xSupport, string userPrompt, JarvisPendingConfirmationSession pendingSession,
            JarvisDatasetSession datasetSession = null, JarvisActiveOrchestrationContext activeContext = null,
            JarvisRuntimeContext runtimeContext = null)
        {
            var outcome = new JarvisControlledPilotOutcome { Handled = false, Completed = false };
            try
            {
                runtimeContext = runtimeContext ?? JarvisRuntimeContext.Capture(xSupport);
                string planningPrompt = activeContext == null ? userPrompt : activeContext.PreparePrompt(userPrompt);
                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, planningPrompt, runtimeContext);
                bool replaceActiveRun = planning != null && planning.IntentObjects != null &&
                    planning.IntentObjects.ActiveContextDisposition == JarvisActiveContextDisposition.Replace;
                if (!IsSupportedControlledPlan(planning))
                {
                    if (activeContext != null && activeContext.HasOpenRun && replaceActiveRun) activeContext.Clear();
                    if (datasetSession != null && replaceActiveRun) datasetSession.Clear();
                    return outcome;
                }

                outcome.Handled = true;
                if (activeContext != null && (!activeContext.HasOpenRun || replaceActiveRun))
                {
                    if (replaceActiveRun && datasetSession != null) datasetSession.Clear();
                    activeContext.Begin(userPrompt);
                }
                bool hasEmail = HasTask(planning, "SendEmail");
                if (hasEmail && pendingSession == null)
                {
                    outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                        "Το controlled orchestration δεν έχει διαθέσιμο confirmation session.", null);
                    return outcome;
                }
                ResolveDeterministicSendRecipient(planning);
                ResolveDeterministicRuntimeContext(planning, runtimeContext);
                if (activeContext != null) activeContext.CapturePlanning(planning);

                if (!hasEmail && pendingSession != null && pendingSession.HasPending)
                {
                    pendingSession.Clear();
                    if (activeContext != null) activeContext.ClearPendingConfirmation();
                }

                var coordinator = new JarvisExecutionCoordinator(planning.Graph, planning.Preview);
                JarvisExecutionControlSnapshot before = coordinator.Inspect();
                LogSnapshot("initial", before, coordinator.GetDispatchableObjectIds(), null, null, null);
                if (!before.IsValid)
                {
                    outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                        "Ο Jarvis απέρριψε το execution plan.", before.ValidationIssues);
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
                        outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                            "Ο Jarvis απέρριψε τα inputs του Atlas.", reportInputIssues);
                        return outcome;
                    }

                    string ambiguityMessage = JarvisReportIdentityGuard.GetAmbiguityMessage(xSupport, reportInputs);
                    if (!string.IsNullOrWhiteSpace(ambiguityMessage))
                    {
                        outcome.UserMessage = JarvisPresentationGateway.FinalizeFreeform(ambiguityMessage);
                        return outcome;
                    }

                    JarvisRuntimeContext reportRuntimeContext = runtimeContext;
                    string existingPolicyContext = reportInputs["__policy_context"] == null ? string.Empty : reportInputs["__policy_context"].ToString();
                    reportInputs["__policy_context"] = existingPolicyContext + "\n" + reportRuntimeContext.BuildEnvelope();
                    reportInputs["__current_user_id"] = reportRuntimeContext.CurrentUserId;

                    string[] beginIssues;
                    if (!coordinator.TryBeginDispatch(reportStep.ObjectId, out beginIssues))
                    {
                        LogSnapshot("dispatch_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, beginIssues, null);
                        outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                            "Ο Jarvis δεν επέτρεψε την εκτέλεση του Atlas.", beginIssues);
                        return outcome;
                    }

                    LogSnapshot("running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);
                    JarvisTaskExecutionResult reportResult = await JarvisControlledTaskExecutor.ExecuteReportDataAsync(xSupport, reportStep.ObjectId, reportInputs);
                    if (reportResult.Success)
                    {
                        string entityRole = reportInputs["entity_role"] == null ? string.Empty : reportInputs["entity_role"].ToString();
                        string documentScope = reportInputs["document_scope"] == null ? string.Empty : reportInputs["document_scope"].ToString();
                        string operatorScope = reportInputs["operator_scope"] == null ? string.Empty : reportInputs["operator_scope"].ToString();
                        string verifiedSql = reportResult.Outputs["query_sql"] == null ? string.Empty : reportResult.Outputs["query_sql"].ToString();
                        int verifiedUserId = reportInputs["__current_user_id"] == null ? 0 : (int)reportInputs["__current_user_id"];
                        string[] queryScopeIssues = JarvisStructuredQueryScopeValidator.Validate(verifiedSql, entityRole, operatorScope, verifiedUserId);
                        string reportDatasetForValidation = reportResult.Outputs["dataset"] == null ? string.Empty : reportResult.Outputs["dataset"].ToString();
                        string[] documentScopeIssues = JarvisDocumentScopeValidator.Validate(documentScope, reportDatasetForValidation);
                        foreach (string scopeIssue in queryScopeIssues.Concat(documentScopeIssues))
                        {
                            reportResult.Success = false;
                            reportResult.Issues.Add(scopeIssue);
                        }
                    }
                    if (!reportResult.Success)
                    {
                        string[] failedAcceptIssues;
                        coordinator.TryAcceptResult(reportResult, out failedAcceptIssues);
                        LogSnapshot("result_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, reportResult.Issues.ToArray(), null);
                        outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                            "Η ανάκτηση δεδομένων απέτυχε.", reportResult.Issues);
                        return outcome;
                    }

                    businessQuestion = reportInputs["business_question"] == null ? userPrompt : reportInputs["business_question"].ToString();
                    datasetJson = reportResult.Outputs["dataset"] == null ? string.Empty : reportResult.Outputs["dataset"].ToString();
                    if (datasetSession != null) datasetSession.TryCapture(activeContext == null ? null : activeContext.RunId, businessQuestion, datasetJson);

                    if (hasEmail)
                    {
                        string recipient = FindResolvedSendInput(planning, "to");
                        presentation = await JarvisPresentationGateway.ComposeEmailAsync(xSupport, businessQuestion, datasetJson, recipient);
                        if (presentation != null && !string.IsNullOrWhiteSpace(presentation.EmailBody))
                            reportResult.Outputs["summary"] = presentation.EmailBody;
                        if (presentation != null && !string.IsNullOrWhiteSpace(presentation.EmailSubject))
                            ApplyResolvedSendInput(planning, "subject", presentation.EmailSubject);
                    }
                    else
                    {
                        presentation = await JarvisPresentationGateway.ComposeReportAsync(xSupport, businessQuestion, datasetJson);
                    }

                    string[] acceptIssues;
                    if (!coordinator.TryAcceptResult(reportResult, out acceptIssues))
                    {
                        LogSnapshot("result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, acceptIssues, null);
                        outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                            "Ο Jarvis απέρριψε το αποτέλεσμα του Atlas.", acceptIssues);
                        return outcome;
                    }
                    LogSnapshot("result_accepted", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);
                    if (activeContext != null) activeContext.CaptureVerifiedResult(reportResult);
                }

                var completedResults = new List<JarvisTaskExecutionResult>();
                var deferredIssues = new List<string>();

                JarvisExecutionStepSnapshot exportStep = FindStep(coordinator.Inspect(), "ExportData", "Atlas");
                if (exportStep != null)
                {
                    JObject exportInputs;
                    string[] exportInputIssues;
                    if (!coordinator.TryGetDispatchInputs(exportStep.ObjectId, out exportInputs, out exportInputIssues))
                    {
                        deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                            "Η εξαγωγή χρειάζεται επιπλέον πληροφορίες.", exportInputIssues));
                    }
                    else
                    {
                        JarvisRuntimeContext exportRuntime = runtimeContext;
                        string exportPolicyContext = exportInputs["__policy_context"] == null ? string.Empty : exportInputs["__policy_context"].ToString();
                        exportInputs["__policy_context"] = exportPolicyContext + "\n" + exportRuntime.BuildEnvelope();
                        exportInputs["__current_user_id"] = exportRuntime.CurrentUserId;
                        string[] exportBeginIssues;
                        if (!coordinator.TryBeginDispatch(exportStep.ObjectId, out exportBeginIssues))
                        {
                            deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                "Η εξαγωγή δεν είναι ακόμη dispatchable.", exportBeginIssues));
                        }
                        else
                        {
                            JarvisTaskExecutionResult exportResult = await JarvisControlledExportTaskExecutor.ExecuteAsync(xSupport, exportStep.ObjectId, exportInputs);
                            string[] exportAcceptIssues;
                            if (!coordinator.TryAcceptResult(exportResult, out exportAcceptIssues))
                                deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                    "Ο Jarvis απέρριψε το αποτέλεσμα της εξαγωγής.", exportAcceptIssues));
                            else if (exportResult.Success)
                            {
                                completedResults.Add(exportResult);
                                if (activeContext != null) activeContext.CaptureVerifiedResult(exportResult);
                            }
                            else
                                deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                    "Η εξαγωγή αρχείου απέτυχε.", exportResult.Issues));
                        }
                    }
                }

                JarvisExecutionStepSnapshot crmStep = FindStep(coordinator.Inspect(), "CreateCrmTask", "Echo");
                if (crmStep != null)
                {
                    string[] crmAuthIssues;
                    if (!coordinator.GrantConfirmation(crmStep.ObjectId, out crmAuthIssues))
                    {
                        deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                            "Η εργασία CRM χρειάζεται επίλυση πριν εκτελεστεί.", crmAuthIssues));
                    }
                    else
                    {
                        JObject crmInputs;
                        string[] crmInputIssues;
                        if (!coordinator.TryGetDispatchInputs(crmStep.ObjectId, out crmInputs, out crmInputIssues))
                        {
                            deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                "Η εργασία CRM χρειάζεται επιπλέον πληροφορίες.", crmInputIssues));
                        }
                        else
                        {
                            string[] crmBeginIssues;
                            if (!coordinator.TryBeginDispatch(crmStep.ObjectId, out crmBeginIssues))
                            {
                                deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                    "Η εργασία CRM δεν είναι ακόμη dispatchable.", crmBeginIssues));
                            }
                            else
                            {
                                LogSnapshot("echo_crm_running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), crmStep.ObjectId, null, null);
                                JarvisTaskExecutionResult crmResult = await JarvisControlledEchoTaskExecutor.ExecuteCreateCrmTaskAsync(xSupport, crmStep.ObjectId, crmInputs);
                                string[] crmAcceptIssues;
                                if (!coordinator.TryAcceptResult(crmResult, out crmAcceptIssues))
                                {
                                    deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                        "Ο Jarvis απέρριψε το αποτέλεσμα της εργασίας CRM.", crmAcceptIssues));
                                }
                                else
                                {
                                    LogSnapshot(crmResult.Success ? "echo_crm_accepted" : "echo_crm_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), crmStep.ObjectId, crmResult.Success ? null : crmResult.Issues.ToArray(), null);
                                    if (crmResult.Success)
                                    {
                                        completedResults.Add(crmResult);
                                        if (activeContext != null) activeContext.CaptureVerifiedResult(crmResult);
                                    }
                                    else
                                        deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                            "Η εργασία CRM δεν ολοκληρώθηκε.", crmResult.Issues));
                                }
                            }
                        }
                    }
                }

                JarvisExecutionStepSnapshot calendarStep = FindStep(coordinator.Inspect(), "CreateCalendarEvent", "Echo");
                if (calendarStep != null)
                {
                    JObject calendarInputs;
                    string[] calendarInputIssues;
                    if (!coordinator.TryGetDispatchInputs(calendarStep.ObjectId, out calendarInputs, out calendarInputIssues))
                    {
                        deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                            "Το calendar event χρειάζεται επιπλέον πληροφορίες.", calendarInputIssues));
                    }
                    else
                    {
                        string[] calendarAuthIssues;
                        if (!coordinator.GrantConfirmation(calendarStep.ObjectId, out calendarAuthIssues))
                        {
                            deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                "Το calendar event χρειάζεται ρητή επιβεβαίωση πριν εκτελεστεί.", calendarAuthIssues));
                        }
                        else
                        {
                            string[] calendarBeginIssues;
                            if (!coordinator.TryBeginDispatch(calendarStep.ObjectId, out calendarBeginIssues))
                            {
                                deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                    "Το calendar event δεν είναι ακόμη dispatchable.", calendarBeginIssues));
                            }
                            else
                            {
                                LogSnapshot("echo_calendar_running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), calendarStep.ObjectId, null, null);
                                JarvisTaskExecutionResult calendarResult = await JarvisControlledEchoTaskExecutor.ExecuteCreateCalendarEventAsync(xSupport, calendarStep.ObjectId, calendarInputs);
                                string[] calendarAcceptIssues;
                                if (!coordinator.TryAcceptResult(calendarResult, out calendarAcceptIssues))
                                {
                                    deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                        "Ο Jarvis απέρριψε το αποτέλεσμα του calendar event.", calendarAcceptIssues));
                                }
                                else
                                {
                                    LogSnapshot(calendarResult.Success ? "echo_calendar_accepted" : "echo_calendar_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), calendarStep.ObjectId, calendarResult.Success ? null : calendarResult.Issues.ToArray(), null);
                                    if (calendarResult.Success)
                                    {
                                        completedResults.Add(calendarResult);
                                        if (activeContext != null) activeContext.CaptureVerifiedResult(calendarResult);
                                    }
                                    else
                                        deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                                            "Το Outlook calendar event δεν ολοκληρώθηκε.", calendarResult.Issues));
                                }
                            }
                        }
                    }
                }

                if (!hasEmail)
                {
                    string intro = presentation == null ? null : presentation.Intro;
                    outcome.Completed = deferredIssues.Count == 0;
                    if (outcome.Completed && activeContext != null) activeContext.Complete();
                    outcome.UserMessage = JarvisPresentationGateway.BuildCombinedMessage(
                        intro, datasetJson, completedResults, deferredIssues, null, outcome.Completed);
                    return outcome;
                }

                JarvisExecutionStepSnapshot emailStep = FindStep(coordinator.Inspect(), "SendEmail", "Echo");
                if (emailStep == null)
                {
                    outcome.UserMessage = JarvisPresentationGateway.BuildCombinedMessage(
                        null, datasetJson, completedResults, deferredIssues,
                        "Ο Jarvis απέρριψε το plan: λείπει το SendEmail/Echo βήμα.", false);
                    return outcome;
                }

                string[] captureIssues;
                if (!pendingSession.TryCapture(coordinator, out captureIssues))
                {
                    LogSnapshot("confirmation_payload_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), emailStep.ObjectId, captureIssues, null);
                    deferredIssues.Add(JarvisPresentationGateway.BuildFailureMessage(
                        "Το email δεν μπόρεσε να προετοιμαστεί για επιβεβαίωση.", captureIssues));
                    outcome.UserMessage = JarvisPresentationGateway.BuildCombinedMessage(
                        "Εκτέλεσα όσα ανεξάρτητα βήματα ήταν διαθέσιμα.", datasetJson,
                        completedResults, deferredIssues, null, false);
                    return outcome;
                }

                LogSnapshot("confirmation_payload_frozen", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), pendingSession.PendingObjectId, null, pendingSession.PayloadHash);
                if (activeContext != null) activeContext.CapturePendingConfirmation(pendingSession);
                string confirmation = JarvisPresentationGateway.BuildConfirmationMessage(
                    pendingSession.FrozenPayload, presentation == null ? null : presentation.Intro);
                outcome.UserMessage = JarvisPresentationGateway.BuildCombinedMessage(
                    null, datasetJson, completedResults, deferredIssues, confirmation, false);
                return outcome;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] controlled pilot exception: " + ex);
                if (outcome.Handled)
                    outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                        "✖ Σφάλμα controlled orchestration:", new[] { ex.Message });
                return outcome;
            }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryResumeConfirmationAndExecuteAsync(
            XSupport xSupport, JarvisPendingConfirmationSession pendingSession, string userText,
            JarvisActiveOrchestrationContext activeContext = null)
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
                outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                    "Η επιβεβαίωση απορρίφθηκε από τον Jarvis.", issues);
                return outcome;
            }

            JObject frozenPayload = pendingSession.FrozenPayload;
            LogSnapshot("confirmation_granted", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, null, payloadHash);
            JarvisExecutionStepSnapshot sendStep = coordinator.Inspect().Steps.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
            if (sendStep == null || !string.Equals(sendStep.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) || !string.Equals(sendStep.OwnerAgent, "Echo", StringComparison.OrdinalIgnoreCase))
            {
                pendingSession.Clear();
                if (activeContext != null) activeContext.ClearPendingConfirmation();
                outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                    "Ο Jarvis απέρριψε την επιβεβαίωση: το pending task δεν είναι SendEmail/Echo.", null);
                return outcome;
            }

            string[] beginIssues;
            if (!coordinator.TryBeginDispatch(objectId, out beginIssues))
            {
                LogSnapshot("echo_dispatch_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, beginIssues, payloadHash);
                pendingSession.Clear();
                if (activeContext != null) activeContext.ClearPendingConfirmation();
                outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                    "Ο Jarvis δεν επέτρεψε την αποστολή.", beginIssues);
                return outcome;
            }

            LogSnapshot("echo_running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, null, payloadHash);
            JarvisTaskExecutionResult echoResult = await JarvisControlledEchoExecutor.ExecuteSendEmailAsync(xSupport, objectId, frozenPayload);
            pendingSession.Clear();
            if (activeContext != null) activeContext.ClearPendingConfirmation();

            string[] acceptIssues;
            if (!coordinator.TryAcceptResult(echoResult, out acceptIssues))
            {
                LogSnapshot("echo_result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, acceptIssues, payloadHash);
                outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                    "Η ενέργεια αποστολής εκτελέστηκε, αλλά ο Jarvis δεν μπόρεσε να επικυρώσει το αποτέλεσμα. Δεν θα γίνει αυτόματο retry για αποφυγή διπλής αποστολής.",
                    acceptIssues);
                return outcome;
            }

            LogSnapshot(echoResult.Success ? "echo_result_accepted" : "echo_result_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId,
                echoResult.Success ? null : echoResult.Issues.ToArray(), payloadHash);
            if (!echoResult.Success)
            {
                outcome.UserMessage = JarvisPresentationGateway.BuildFailureMessage(
                    "Η αποστολή email απέτυχε.", echoResult.Issues);
                return outcome;
            }

            outcome.Completed = true;
            if (activeContext != null)
            {
                activeContext.CaptureVerifiedResult(echoResult);
                activeContext.Complete();
            }
            outcome.UserMessage = JarvisPresentationGateway.BuildTaskResultStatus(echoResult);
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
                if (string.Equals(entry.TaskType, "ExportData", StringComparison.OrdinalIgnoreCase))
                {
                    JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, entry.ObjectId, StringComparison.OrdinalIgnoreCase));
                    JarvisPrerequisiteResolutionItem format = node == null ? null : node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "format", StringComparison.OrdinalIgnoreCase));
                    if (format != null && format.Value != null && string.Equals(format.Value.ToString(), "pdf", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
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

        private static void ResolveDeterministicRuntimeContext(JarvisShadowOrchestrationResult planning, JarvisRuntimeContext runtimeContext)
        {
            if (planning == null || planning.Graph == null || runtimeContext == null) return;
            int currentUserId = runtimeContext.CurrentUserId;
            if (currentUserId <= 0) return;

            foreach (JarvisValidatedTaskNode node in planning.Graph.Nodes.Where(x => x != null && x.Descriptor != null))
            {
                JarvisPrerequisiteResolutionItem assignee = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "assignee", StringComparison.OrdinalIgnoreCase));
                if (assignee == null || assignee.Value == null) continue;
                string assigneeText = assignee.Value.ToString().Trim();
                int assigneeUserId;
                bool isCurrentOperator = string.Equals(assigneeText, "__CURRENT_OPERATOR__", StringComparison.Ordinal) ||
                    (int.TryParse(assigneeText, out assigneeUserId) && assigneeUserId == currentUserId) ||
                    (!string.IsNullOrWhiteSpace(runtimeContext.CurrentUserDisplayName) &&
                     string.Equals(assigneeText, runtimeContext.CurrentUserDisplayName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!isCurrentOperator) continue;

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

                SetResolvedRuntimeValue(node, "actorUserId", new JValue(currentUserId), "Current operator semantic marker resolved deterministically from the active Soft1 session.");
                assignee.Value = new JValue(currentUserId);
                assignee.Kind = JarvisPrerequisiteResolutionKind.ResolvedFromRouting;
                assignee.Reason = "__CURRENT_OPERATOR__ resolved deterministically to the active Soft1 operator.";
            }
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
            item.Reason = "Canonical presentation channel composed/resolved this value before confirmation from validated task context.";
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

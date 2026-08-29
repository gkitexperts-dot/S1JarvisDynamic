using System;
using System.Linq;
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

    /// <summary>
    /// Controlled ReportData and ReportData -> SendEmail execution path.
    /// Execution/data remain strict; presentation is a separate Jarvis-only layer.
    /// </summary>
    internal static class JarvisExecutionShadowHarness
    {
        internal static async Task RunAndLogSafeAsync(
            XSupport xSupport,
            string userPrompt,
            JarvisPendingConfirmationSession pendingSession = null,
            JarvisDatasetSession datasetSession = null)
        {
            try
            {
                await TryRunControlledPilotAsync(xSupport, userPrompt, pendingSession, datasetSession);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex);
            }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(
            XSupport xSupport,
            string userPrompt,
            JarvisPendingConfirmationSession pendingSession,
            JarvisDatasetSession datasetSession = null)
        {
            var outcome = new JarvisControlledPilotOutcome { Handled = false, Completed = false };

            try
            {
                JarvisShadowOrchestrationResult planning =
                    await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, userPrompt);

                bool reportOnly = IsSupportedReportOnlyPlan(planning);
                bool reportEmail = IsSupportedReportEmailPlan(planning);
                if (!reportOnly && !reportEmail)
                    return outcome;

                outcome.Handled = true;
                if (reportEmail && pendingSession == null)
                {
                    outcome.UserMessage = "Το controlled orchestration δεν έχει διαθέσιμο confirmation session.";
                    return outcome;
                }

                if (pendingSession != null) pendingSession.Clear();

                var coordinator = new JarvisExecutionCoordinator(planning.Graph, planning.Preview);
                JarvisExecutionControlSnapshot before = coordinator.Inspect();
                LogSnapshot("initial", before, coordinator.GetDispatchableObjectIds(), null, null, null);

                JarvisExecutionStepSnapshot reportStep = before.Steps.FirstOrDefault(x =>
                    x != null && string.Equals(x.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.OwnerAgent, "Atlas", StringComparison.OrdinalIgnoreCase));
                if (reportStep == null)
                {
                    outcome.UserMessage = "Ο Jarvis απέρριψε το plan: δεν βρέθηκε έγκυρο ReportData/Atlas βήμα.";
                    return outcome;
                }

                JObject dispatchInputs;
                string[] inputIssues;
                if (!coordinator.TryGetDispatchInputs(reportStep.ObjectId, out dispatchInputs, out inputIssues))
                {
                    LogSnapshot("dispatch_input_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, inputIssues, null);
                    outcome.UserMessage = BuildFailureMessage("Ο Jarvis απέρριψε τα inputs του Atlas.", inputIssues);
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

                JarvisTaskExecutionResult executionResult = await JarvisControlledTaskExecutor.ExecuteReportDataAsync(
                    xSupport, reportStep.ObjectId, dispatchInputs);

                if (!executionResult.Success)
                {
                    string[] failedAcceptIssues;
                    coordinator.TryAcceptResult(executionResult, out failedAcceptIssues);
                    LogSnapshot("result_failed", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, executionResult.Issues.ToArray(), null);
                    outcome.UserMessage = BuildFailureMessage("Η ανάκτηση δεδομένων απέτυχε.", executionResult.Issues.ToArray());
                    return outcome;
                }

                string businessQuestion = dispatchInputs["business_question"] == null
                    ? userPrompt
                    : dispatchInputs["business_question"].ToString();
                string datasetJson = executionResult.Outputs["dataset"] == null
                    ? string.Empty
                    : executionResult.Outputs["dataset"].ToString();

                if (datasetSession != null)
                    datasetSession.TryCapture(businessQuestion, datasetJson);

                JarvisPresentationResult presentation;
                if (reportEmail)
                {
                    string recipient = FindResolvedSendInput(planning, "to");
                    presentation = await JarvisPresentationComposer.ComposeEmailAsync(
                        xSupport, businessQuestion, datasetJson, recipient);
                    if (presentation != null && !string.IsNullOrWhiteSpace(presentation.EmailBody))
                    {
                        // The downstream body still comes through the registered
                        // ReportData.summary -> SendEmail.body binding. Jarvis only
                        // replaces raw formatting with a presentation derived from
                        // the same validated dataset before result acceptance.
                        executionResult.Outputs["summary"] = presentation.EmailBody;
                    }
                }
                else
                {
                    presentation = await JarvisPresentationComposer.ComposeReportAsync(
                        xSupport, businessQuestion, datasetJson);
                }

                string[] acceptIssues;
                if (!coordinator.TryAcceptResult(executionResult, out acceptIssues))
                {
                    LogSnapshot("result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, acceptIssues, null);
                    outcome.UserMessage = BuildFailureMessage("Ο Jarvis απέρριψε το αποτέλεσμα του Atlas.", acceptIssues);
                    return outcome;
                }

                JarvisExecutionControlSnapshot after = coordinator.Inspect();
                LogSnapshot("result_accepted", after, coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);

                if (reportOnly)
                {
                    string intro = presentation == null ? null : presentation.Intro;
                    if (string.IsNullOrWhiteSpace(intro))
                        intro = "Βρήκα τα αποτελέσματα που ζήτησες:";
                    string table = JarvisPresentationComposer.BuildMarkdownTable(datasetJson, 250);
                    outcome.Completed = true;
                    outcome.UserMessage = intro + "\n\n" + table;
                    return outcome;
                }

                string[] captureIssues;
                if (!pendingSession.TryCapture(coordinator, out captureIssues))
                {
                    LogSnapshot("confirmation_payload_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, captureIssues, null);
                    outcome.UserMessage = BuildFailureMessage("Ο Jarvis δεν μπόρεσε να παγώσει το payload για επιβεβαίωση.", captureIssues);
                    return outcome;
                }

                LogSnapshot("confirmation_payload_frozen", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), pendingSession.PendingObjectId, null, pendingSession.PayloadHash);
                outcome.UserMessage = BuildConfirmationMessage(pendingSession.FrozenPayload, presentation == null ? null : presentation.Intro);
                return outcome;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] controlled pilot exception: " + ex);
                if (outcome.Handled)
                    outcome.UserMessage = "✖ Σφάλμα controlled orchestration: " + ex.Message;
                return outcome;
            }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryResumeConfirmationAndExecuteAsync(
            XSupport xSupport,
            JarvisPendingConfirmationSession pendingSession,
            string userText)
        {
            var outcome = new JarvisControlledPilotOutcome { Handled = false, Completed = false };
            if (pendingSession == null || !pendingSession.HasPending ||
                !JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText))
                return outcome;

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

            JarvisExecutionStepSnapshot sendStep = coordinator.Inspect().Steps.FirstOrDefault(x =>
                x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
            if (sendStep == null || !string.Equals(sendStep.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sendStep.OwnerAgent, "Echo", StringComparison.OrdinalIgnoreCase))
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

            JarvisExecutionControlSnapshot finalSnapshot = coordinator.Inspect();
            LogSnapshot(echoResult.Success ? "echo_result_accepted" : "echo_result_failed", finalSnapshot,
                coordinator.GetDispatchableObjectIds(), objectId,
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
            return pendingSession != null && pendingSession.HasPending &&
                   JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText);
        }

        private static bool IsBaseValid(JarvisShadowOrchestrationResult planning)
        {
            return planning != null && planning.GateEnabled && planning.Graph != null && planning.Preview != null &&
                   planning.Graph.IsValid && planning.Preview.IsValid;
        }

        private static bool IsSupportedReportOnlyPlan(JarvisShadowOrchestrationResult planning)
        {
            if (!IsBaseValid(planning) || planning.Preview.Entries.Count != 1) return false;
            JarvisExecutionPlanEntry entry = planning.Preview.Entries[0];
            return entry != null && string.Equals(entry.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(entry.OwnerAgent, "Atlas", StringComparison.OrdinalIgnoreCase) && !entry.RequiresConfirmation;
        }

        private static bool IsSupportedReportEmailPlan(JarvisShadowOrchestrationResult planning)
        {
            if (!IsBaseValid(planning) || planning.Preview.Entries.Count != 2) return false;
            JarvisExecutionPlanEntry report = planning.Preview.Entries.FirstOrDefault(x => x != null &&
                string.Equals(x.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) && string.Equals(x.OwnerAgent, "Atlas", StringComparison.OrdinalIgnoreCase));
            JarvisExecutionPlanEntry send = planning.Preview.Entries.FirstOrDefault(x => x != null &&
                string.Equals(x.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) && string.Equals(x.OwnerAgent, "Echo", StringComparison.OrdinalIgnoreCase));
            return report != null && send != null && send.RequiresConfirmation &&
                   send.DependsOnObjectIds.Any(x => string.Equals(x, report.ObjectId, StringComparison.OrdinalIgnoreCase));
        }

        private static string FindResolvedSendInput(JarvisShadowOrchestrationResult planning, string inputName)
        {
            if (planning == null || planning.Graph == null) return string.Empty;
            JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase));
            if (node == null) return string.Empty;
            JarvisPrerequisiteResolutionItem item = node.Prerequisites.FirstOrDefault(x => x != null &&
                string.Equals(x.InputName, inputName, StringComparison.OrdinalIgnoreCase) && x.Value != null);
            return item == null ? string.Empty : item.Value.ToString();
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

        private static void LogSnapshot(string phase, JarvisExecutionControlSnapshot snapshot,
            string[] dispatchableObjectIds, string activeObjectId, string[] eventIssues, string payloadHash)
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
                    ["wave"] = x.Wave, ["ordinal"] = x.Ordinal, ["objectId"] = x.ObjectId ?? string.Empty,
                    ["taskType"] = x.TaskType ?? string.Empty, ["owner"] = x.OwnerAgent ?? string.Empty,
                    ["state"] = x.State.ToString(), ["requiresConfirmation"] = x.RequiresConfirmation,
                    ["confirmationGranted"] = x.ConfirmationGranted, ["dependsOn"] = new JArray(x.DependsOn),
                    ["boundInputs"] = new JArray(x.BoundInputs),
                    ["materializedInputs"] = x.MaterializedInputs == null ? new JObject() : (JObject)x.MaterializedInputs.DeepClone(),
                    ["issues"] = new JArray(x.ValidationIssues)
                })),
                ["issues"] = snapshot == null ? new JArray() : new JArray(snapshot.ValidationIssues),
                ["eventIssues"] = new JArray(eventIssues ?? new string[0])
            };
            DebugLog.Log("[ORCH-CONTROL] " + root.ToString(Formatting.None));
        }
    }
}

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
    /// Controlled pilot for ReportData -> SendEmail.
    /// Jarvis owns planning, dispatch authorization, result validation, the
    /// frozen confirmation payload and final Echo result validation.
    /// </summary>
    internal static class JarvisExecutionShadowHarness
    {
        internal static async Task RunAndLogSafeAsync(
            XSupport xSupport,
            string userPrompt,
            JarvisPendingConfirmationSession pendingSession = null)
        {
            try
            {
                await TryRunControlledPilotAsync(xSupport, userPrompt, pendingSession);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex);
            }
        }

        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(
            XSupport xSupport,
            string userPrompt,
            JarvisPendingConfirmationSession pendingSession)
        {
            var outcome = new JarvisControlledPilotOutcome { Handled = false, Completed = false };

            try
            {
                JarvisShadowOrchestrationResult planning =
                    await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, userPrompt);

                if (!IsSupportedPilotPlan(planning))
                    return outcome;

                outcome.Handled = true;

                if (pendingSession == null)
                {
                    outcome.UserMessage = "Το controlled orchestration δεν έχει διαθέσιμο confirmation session.";
                    return outcome;
                }

                pendingSession.Clear();

                var coordinator = new JarvisExecutionCoordinator(
                    planning.Graph,
                    planning.Preview);

                JarvisExecutionControlSnapshot before = coordinator.Inspect();
                LogSnapshot("initial", before, coordinator.GetDispatchableObjectIds(), null, null, null);

                JarvisExecutionStepSnapshot reportStep = before.Steps.FirstOrDefault(x =>
                    x != null &&
                    string.Equals(x.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&
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

                JarvisTaskExecutionResult executionResult =
                    await JarvisControlledTaskExecutor.ExecuteReportDataAsync(
                        xSupport,
                        reportStep.ObjectId,
                        dispatchInputs);

                string[] acceptIssues;
                bool accepted = coordinator.TryAcceptResult(executionResult, out acceptIssues);
                if (!accepted)
                {
                    LogSnapshot("result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, acceptIssues, null);
                    outcome.UserMessage = BuildFailureMessage("Ο Jarvis απέρριψε το αποτέλεσμα του Atlas.", acceptIssues);
                    return outcome;
                }

                JarvisExecutionControlSnapshot after = coordinator.Inspect();
                LogSnapshot("result_accepted", after, coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);

                if (!executionResult.Success)
                {
                    outcome.UserMessage = BuildFailureMessage("Η ανάκτηση δεδομένων απέτυχε.", executionResult.Issues.ToArray());
                    return outcome;
                }

                string[] captureIssues;
                if (!pendingSession.TryCapture(coordinator, out captureIssues))
                {
                    LogSnapshot("confirmation_payload_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, captureIssues, null);
                    outcome.UserMessage = BuildFailureMessage("Ο Jarvis δεν μπόρεσε να παγώσει το payload για επιβεβαίωση.", captureIssues);
                    return outcome;
                }

                LogSnapshot(
                    "confirmation_payload_frozen",
                    coordinator.Inspect(),
                    coordinator.GetDispatchableObjectIds(),
                    pendingSession.PendingObjectId,
                    null,
                    pendingSession.PayloadHash);

                outcome.UserMessage = BuildConfirmationMessage(pendingSession.FrozenPayload);
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

            LogSnapshot(
                "confirmation_granted",
                coordinator.Inspect(),
                coordinator.GetDispatchableObjectIds(),
                objectId,
                null,
                payloadHash);

            JarvisExecutionStepSnapshot sendStep = coordinator.Inspect().Steps.FirstOrDefault(x =>
                x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
            if (sendStep == null ||
                !string.Equals(sendStep.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) ||
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

            JarvisTaskExecutionResult echoResult = await JarvisControlledEchoExecutor.ExecuteSendEmailAsync(
                xSupport,
                objectId,
                frozenPayload);

            // Once the irreversible transport has been invoked, never allow the
            // same confirmation session to be replayed, even if post-validation
            // later rejects the executor result.
            pendingSession.Clear();

            string[] acceptIssues;
            bool accepted = coordinator.TryAcceptResult(echoResult, out acceptIssues);
            if (!accepted)
            {
                LogSnapshot("echo_result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, acceptIssues, payloadHash);
                outcome.UserMessage = "Η ενέργεια αποστολής εκτελέστηκε, αλλά ο Jarvis δεν μπόρεσε να επικυρώσει το αποτέλεσμα. Δεν θα γίνει αυτόματο retry για αποφυγή διπλής αποστολής.";
                return outcome;
            }

            JarvisExecutionControlSnapshot finalSnapshot = coordinator.Inspect();
            LogSnapshot(
                echoResult.Success ? "echo_result_accepted" : "echo_result_failed",
                finalSnapshot,
                coordinator.GetDispatchableObjectIds(),
                objectId,
                echoResult.Success ? null : echoResult.Issues.ToArray(),
                payloadHash);

            if (!echoResult.Success)
            {
                outcome.UserMessage = BuildFailureMessage("Η αποστολή email απέτυχε.", echoResult.Issues.ToArray());
                return outcome;
            }

            outcome.Completed = true;
            string to = frozenPayload == null || frozenPayload["to"] == null
                ? string.Empty
                : frozenPayload["to"].ToString();
            outcome.UserMessage = "Το email στάλθηκε με επιτυχία" +
                                  (string.IsNullOrWhiteSpace(to) ? "." : " στο " + to + ".");
            return outcome;
        }

        internal static bool TryResumeConfirmation(
            JarvisPendingConfirmationSession pendingSession,
            string userText)
        {
            // Kept only for binary/source compatibility with older callers.
            // Live controlled execution uses TryResumeConfirmationAndExecuteAsync.
            return pendingSession != null && pendingSession.HasPending &&
                   JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText);
        }

        private static bool IsSupportedPilotPlan(JarvisShadowOrchestrationResult planning)
        {
            if (planning == null || !planning.GateEnabled ||
                planning.Graph == null || planning.Preview == null ||
                !planning.Graph.IsValid || !planning.Preview.IsValid)
                return false;

            if (planning.Preview.Entries.Count != 2)
                return false;

            JarvisExecutionPlanEntry report = planning.Preview.Entries.FirstOrDefault(x =>
                x != null && string.Equals(x.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.OwnerAgent, "Atlas", StringComparison.OrdinalIgnoreCase));
            JarvisExecutionPlanEntry send = planning.Preview.Entries.FirstOrDefault(x =>
                x != null && string.Equals(x.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.OwnerAgent, "Echo", StringComparison.OrdinalIgnoreCase));

            return report != null && send != null && send.RequiresConfirmation &&
                   send.DependsOnObjectIds.Any(x => string.Equals(x, report.ObjectId, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildConfirmationMessage(JObject payload)
        {
            if (payload == null)
                return "Δεν υπάρχει payload για επιβεβαίωση.";

            string to = payload["to"] == null ? string.Empty : payload["to"].ToString();
            string subject = payload["subject"] == null ? string.Empty : payload["subject"].ToString();
            string body = payload["body"] == null ? string.Empty : payload["body"].ToString();

            return "Έχω έτοιμο το email με το ακριβές payload που θα σταλεί:\n\n" +
                   "Προς: " + to + "\n" +
                   "Θέμα: " + subject + "\n\n" +
                   body + "\n\n" +
                   "Να το στείλω;";
        }

        private static string BuildFailureMessage(string prefix, string[] issues)
        {
            string detail = issues == null || issues.Length == 0
                ? string.Empty
                : " " + string.Join(" | ", issues);
            return prefix + detail;
        }

        private static void LogSnapshot(
            string phase,
            JarvisExecutionControlSnapshot snapshot,
            string[] dispatchableObjectIds,
            string activeObjectId,
            string[] eventIssues,
            string payloadHash)
        {
            var root = new JObject
            {
                ["phase"] = phase ?? string.Empty,
                ["activeObjectId"] = activeObjectId ?? string.Empty,
                ["valid"] = snapshot == null || snapshot.IsValid,
                ["dispatchable"] = new JArray(dispatchableObjectIds ?? new string[0]),
                ["payloadHash"] = payloadHash ?? string.Empty,
                ["steps"] = snapshot == null
                    ? new JArray()
                    : new JArray(snapshot.Steps.Select(x => new JObject
                    {
                        ["wave"] = x.Wave,
                        ["ordinal"] = x.Ordinal,
                        ["objectId"] = x.ObjectId ?? string.Empty,
                        ["taskType"] = x.TaskType ?? string.Empty,
                        ["owner"] = x.OwnerAgent ?? string.Empty,
                        ["state"] = x.State.ToString(),
                        ["requiresConfirmation"] = x.RequiresConfirmation,
                        ["confirmationGranted"] = x.ConfirmationGranted,
                        ["dependsOn"] = new JArray(x.DependsOn),
                        ["boundInputs"] = new JArray(x.BoundInputs),
                        ["materializedInputs"] = x.MaterializedInputs == null
                            ? new JObject()
                            : (JObject)x.MaterializedInputs.DeepClone(),
                        ["issues"] = new JArray(x.ValidationIssues)
                    })),
                ["issues"] = snapshot == null ? new JArray() : new JArray(snapshot.ValidationIssues),
                ["eventIssues"] = new JArray(eventIssues ?? new string[0])
            };

            DebugLog.Log("[ORCH-CONTROL] " + root.ToString(Formatting.None));
        }
    }
}

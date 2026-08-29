using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Shadow bridge between planning and the Jarvis execution control plane.
    /// Executes only the safe/read-only ReportData slice. When a downstream task
    /// reaches WaitingForConfirmation, its exact materialized payload is frozen in
    /// the shell-scoped confirmation session. No write/external executor runs here.
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
                JarvisShadowOrchestrationResult planning =
                    await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, userPrompt);

                if (planning == null || !planning.GateEnabled ||
                    planning.Graph == null || planning.Preview == null ||
                    !planning.Graph.IsValid || !planning.Preview.IsValid)
                    return;

                var coordinator = new JarvisExecutionCoordinator(
                    planning.Graph,
                    planning.Preview);

                JarvisExecutionControlSnapshot before = coordinator.Inspect();
                LogSnapshot("initial", before, coordinator.GetDispatchableObjectIds(), null, null, null);

                string[] dispatchable = coordinator.GetDispatchableObjectIds();
                foreach (string objectId in dispatchable)
                {
                    JarvisExecutionStepSnapshot step = before.Steps.FirstOrDefault(x =>
                        x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
                    if (step == null)
                        continue;

                    if (!string.Equals(step.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(step.OwnerAgent, "Atlas", StringComparison.OrdinalIgnoreCase))
                        continue;

                    JObject dispatchInputs;
                    string[] inputIssues;
                    if (!coordinator.TryGetDispatchInputs(objectId, out dispatchInputs, out inputIssues))
                    {
                        LogSnapshot("dispatch_input_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, inputIssues, null);
                        continue;
                    }

                    string[] beginIssues;
                    if (!coordinator.TryBeginDispatch(objectId, out beginIssues))
                    {
                        LogSnapshot("dispatch_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, beginIssues, null);
                        continue;
                    }

                    LogSnapshot("running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, null, null);

                    JarvisTaskExecutionResult executionResult =
                        await JarvisControlledTaskExecutor.ExecuteReportDataAsync(
                            xSupport,
                            objectId,
                            dispatchInputs);

                    string[] acceptIssues;
                    bool accepted = coordinator.TryAcceptResult(executionResult, out acceptIssues);
                    if (!accepted)
                    {
                        LogSnapshot("result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, acceptIssues, null);
                        continue;
                    }

                    JarvisExecutionControlSnapshot after = coordinator.Inspect();
                    LogSnapshot("result_accepted", after, coordinator.GetDispatchableObjectIds(), objectId, null, null);

                    if (pendingSession != null && after.Steps.Any(x => x != null && x.State == JarvisExecutionStepState.WaitingForConfirmation))
                    {
                        string[] captureIssues;
                        if (pendingSession.TryCapture(coordinator, out captureIssues))
                        {
                            LogSnapshot(
                                "confirmation_payload_frozen",
                                coordinator.Inspect(),
                                coordinator.GetDispatchableObjectIds(),
                                pendingSession.PendingObjectId,
                                null,
                                pendingSession.PayloadHash);
                        }
                        else
                        {
                            LogSnapshot(
                                "confirmation_payload_rejected",
                                coordinator.Inspect(),
                                coordinator.GetDispatchableObjectIds(),
                                objectId,
                                captureIssues,
                                null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex);
            }
        }

        internal static bool TryResumeConfirmation(
            JarvisPendingConfirmationSession pendingSession,
            string userText)
        {
            if (pendingSession == null || !pendingSession.HasPending ||
                !JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText))
                return false;

            JarvisExecutionCoordinator coordinator;
            string objectId;
            string payloadHash;
            string[] issues;
            if (!pendingSession.TryConfirm(userText, out coordinator, out objectId, out payloadHash, out issues))
            {
                LogSnapshot("confirmation_rejected", null, new string[0], pendingSession.PendingObjectId, issues, pendingSession.PayloadHash);
                return true;
            }

            LogSnapshot(
                "confirmation_granted",
                coordinator.Inspect(),
                coordinator.GetDispatchableObjectIds(),
                objectId,
                null,
                payloadHash);

            // Deliberately stop here. This milestone proves that the exact frozen
            // payload survives the user turn and unlocks the same pending task.
            // Echo dispatch is the next controlled slice and is not enabled yet.
            return true;
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

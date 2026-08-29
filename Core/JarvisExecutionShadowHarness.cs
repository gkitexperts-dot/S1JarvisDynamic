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
    ///
    /// The harness now executes only the first safe/read-only orchestration slice:
    /// ReportData -> Atlas -> query_data SELECT -> structured result -> Jarvis
    /// post-result validation. It never executes SendEmail or any write/external
    /// action. Downstream tasks remain controlled state only.
    /// </summary>
    internal static class JarvisExecutionShadowHarness
    {
        internal static async Task RunAndLogSafeAsync(XSupport xSupport, string userPrompt)
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
                LogSnapshot("initial", before, coordinator.GetDispatchableObjectIds(), null, null);

                string[] dispatchable = coordinator.GetDispatchableObjectIds();
                foreach (string objectId in dispatchable)
                {
                    JarvisExecutionStepSnapshot step = before.Steps.FirstOrDefault(x =>
                        x != null && string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
                    if (step == null)
                        continue;

                    // First safe runtime slice: read-only ReportData only.
                    if (!string.Equals(step.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(step.OwnerAgent, "Atlas", StringComparison.OrdinalIgnoreCase))
                        continue;

                    JObject dispatchInputs;
                    string[] inputIssues;
                    if (!coordinator.TryGetDispatchInputs(objectId, out dispatchInputs, out inputIssues))
                    {
                        LogSnapshot("dispatch_input_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, inputIssues);
                        continue;
                    }

                    string[] beginIssues;
                    if (!coordinator.TryBeginDispatch(objectId, out beginIssues))
                    {
                        LogSnapshot("dispatch_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, beginIssues);
                        continue;
                    }

                    LogSnapshot("running", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, null);

                    JarvisTaskExecutionResult executionResult =
                        await JarvisControlledTaskExecutor.ExecuteReportDataAsync(
                            xSupport,
                            objectId,
                            dispatchInputs);

                    string[] acceptIssues;
                    bool accepted = coordinator.TryAcceptResult(executionResult, out acceptIssues);
                    if (!accepted)
                    {
                        LogSnapshot("result_rejected", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), objectId, acceptIssues);
                        continue;
                    }

                    JarvisExecutionControlSnapshot after = coordinator.Inspect();
                    LogSnapshot("result_accepted", after, coordinator.GetDispatchableObjectIds(), objectId, null);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex);
            }
        }

        private static void LogSnapshot(
            string phase,
            JarvisExecutionControlSnapshot snapshot,
            string[] dispatchableObjectIds,
            string activeObjectId,
            string[] eventIssues)
        {
            if (snapshot == null)
                return;

            var root = new JObject
            {
                ["phase"] = phase ?? string.Empty,
                ["activeObjectId"] = activeObjectId ?? string.Empty,
                ["valid"] = snapshot.IsValid,
                ["dispatchable"] = new JArray(dispatchableObjectIds ?? new string[0]),
                ["steps"] = new JArray(snapshot.Steps.Select(x => new JObject
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
                ["issues"] = new JArray(snapshot.ValidationIssues),
                ["eventIssues"] = new JArray(eventIssues ?? new string[0])
            };

            DebugLog.Log("[ORCH-CONTROL] " + root.ToString(Formatting.None));
        }
    }
}

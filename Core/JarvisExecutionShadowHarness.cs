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
    /// It never dispatches an executor. It only proves which task Jarvis would
    /// allow to run next after validating the complete current state.
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

                JarvisExecutionControlSnapshot snapshot = coordinator.Inspect();
                LogSnapshot(snapshot, coordinator.GetDispatchableObjectIds());
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-CONTROL] shadow harness suppressed exception: " + ex);
            }
        }

        private static void LogSnapshot(
            JarvisExecutionControlSnapshot snapshot,
            string[] dispatchableObjectIds)
        {
            if (snapshot == null)
                return;

            var root = new JObject
            {
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
                    ["issues"] = new JArray(x.ValidationIssues)
                })),
                ["issues"] = new JArray(snapshot.ValidationIssues)
            };

            DebugLog.Log("[ORCH-CONTROL] " + root.ToString(Formatting.None));
        }
    }
}

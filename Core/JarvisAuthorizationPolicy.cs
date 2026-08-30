using System;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisAuthorizationAction
    {
        Allow,
        RequireExplicitConfirmation,
        Block
    }

    internal sealed class JarvisAuthorizationDecision
    {
        internal JarvisAuthorizationAction Action { get; set; }
        internal string PolicyId { get; set; }
        internal string Reason { get; set; }
    }

    internal static class JarvisAuthorizationPolicy
    {
        internal static JarvisAuthorizationDecision EvaluateInitialInstruction(
            JarvisExecutionStepSnapshot step,
            JObject materializedInputs)
        {
            if (step == null)
                return New(JarvisAuthorizationAction.Block, "GLOBAL.CONFIRM_IRREVERSIBLE_ACTION", "Missing execution step.");

            JarvisTaskDescriptor task = JarvisTaskRegistry.Find(step.TaskType);
            if (task == null || !string.Equals(task.OwnerAgent, step.OwnerAgent, StringComparison.OrdinalIgnoreCase))
                return New(JarvisAuthorizationAction.Block, "GLOBAL.REGISTRY_IS_AUTHORITY", "Task/owner is not registry-authoritative.");

            if (!task.RequiresConfirmation)
                return New(JarvisAuthorizationAction.Allow, "GLOBAL.REGISTRY_IS_AUTHORITY", "Registered task does not require confirmation.");

            if (task.Operation == JarvisTaskOperation.ExternalAction)
                return New(JarvisAuthorizationAction.RequireExplicitConfirmation, "GLOBAL.CONFIRM_IRREVERSIBLE_ACTION", "External action requires explicit confirmation of the materialized payload.");

            if (string.Equals(task.TaskType, "CreateCrmTask", StringComparison.OrdinalIgnoreCase))
                return New(JarvisAuthorizationAction.Allow, "GLOBAL.CONFIRM_IRREVERSIBLE_ACTION", "The explicit initial instruction authorizes this local controlled write after required inputs are materialized.");

            return New(JarvisAuthorizationAction.RequireExplicitConfirmation, "GLOBAL.CONFIRM_IRREVERSIBLE_ACTION", "Registered write requires explicit confirmation unless centralized enforcement explicitly permits it.");
        }

        private static JarvisAuthorizationDecision New(JarvisAuthorizationAction action, string policyId, string reason)
        {
            return new JarvisAuthorizationDecision { Action = action, PolicyId = policyId, Reason = reason };
        }
    }
}

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")
    print("updated", path)


def must_replace(text, old, new, label, count=1):
    found = text.count(old)
    if found < count:
        raise RuntimeError(f"{label}: expected at least {count}, found {found}")
    return text.replace(old, new, count)


def replace_method(text, signature, replacement):
    start = text.find(signature)
    if start < 0:
        raise RuntimeError("method signature not found: " + signature)
    brace = text.find("{", start)
    if brace < 0:
        raise RuntimeError("opening brace not found: " + signature)
    depth = 0
    in_string = False
    verbatim = False
    escape = False
    i = brace
    while i < len(text):
        ch = text[i]
        if in_string:
            if verbatim:
                if ch == '"':
                    if i + 1 < len(text) and text[i + 1] == '"':
                        i += 2
                        continue
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif ch == '\\':
                    escape = True
                elif ch == '"':
                    in_string = False
        else:
            if ch == '"':
                in_string = True
                verbatim = i > 0 and text[i - 1] == '@'
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return text[:start] + replacement.rstrip() + text[i + 1:]
        i += 1
    raise RuntimeError("closing brace not found: " + signature)


def update_agent_client():
    path = "Core/JarvisAgentClient.cs"
    text = read(path)
    text = text.replace(
        ',\n                ["businessEntityKnowledge"] = JarvisBusinessEntityCatalog.BuildAgentContext()',
        '')
    write(path, text)


def update_controlled_report():
    path = "Core/JarvisControlledTaskExecutor.cs"
    text = read(path)
    text = must_replace(text,
        '            string knowledgeContext = JarvisBusinessEntityCatalog.BuildAgentContext().ToString(Formatting.None);\n            string userContent = "business_question: " + (question ?? string.Empty) +\n                                 "\\n\\nauthoritative_knowledge_context: " + knowledgeContext;',
        '            string userContent = "business_question: " + (question ?? string.Empty);',
        "report local knowledge copy")
    text = must_replace(text,
        '                ["output_config"] = new JObject { ["effort"] = "low" },\n                ["system"] = new JArray',
        '                ["output_config"] = new JObject { ["effort"] = "low" },\n                ["metadata"] = new JObject { ["jarvis_task"] = "ReportData" },\n                ["system"] = new JArray',
        "report metadata")
    text = text.replace(
        '                            "Το authoritative_knowledge_context περιέχει business/schema knowledge και το JARVIS_POLICY_CONTEXT περιέχει τους behavioral κανόνες. " +\n                            "Εφάρμοσέ τα υποχρεωτικά και επέστρεψε το required tool call.\\n\\n" + (policyContext ?? string.Empty)',
        '                            "Το JARVIS_KNOWLEDGE_CONTEXT περιέχει authoritative business/schema facts και το JARVIS_POLICY_CONTEXT τους behavioral κανόνες. " +\n                            "Το envelope ορίζει μόνο το atomic protocol και το required tool call.\\n\\n" + (policyContext ?? string.Empty)')
    write(path, text)


def update_echo():
    path = "Core/JarvisControlledEchoTaskExecutor.cs"
    text = read(path)
    marker = '                    ["max_tokens"] = 3000,\n                    ["system"] = new JArray'
    text = must_replace(text, marker,
        '                    ["max_tokens"] = 3000,\n                    ["metadata"] = new JObject { ["jarvis_task"] = "CreateCrmTask" },\n                    ["system"] = new JArray',
        "crm metadata")
    text = must_replace(text, marker,
        '                    ["max_tokens"] = 3000,\n                    ["metadata"] = new JObject { ["jarvis_task"] = "CreateCalendarEvent" },\n                    ["system"] = new JArray',
        "calendar metadata")
    write(path, text)


def update_dataset_session():
    path = "Core/JarvisDatasetSession.cs"
    text = read(path)
    signature = '        internal static bool LooksLikeRefinement(string userText)'
    if signature in text:
        text = replace_method(text, signature, '')
    text = text.replace(
        '            if (source == null || !(source["rows"] is JArray) || !LooksLikeRefinement(userText))\n                return outcome;',
        '            if (source == null || !(source["rows"] is JArray))\n                return outcome;')
    old = '''            JObject catalog = BuildCompactCatalog(dataset);
            JObject request = new JObject
            {
                ["max_tokens"] = 1200,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Είσαι ο Jarvis local dataset refinement planner. Αποφασίζεις αν το follow-up μπορεί να απαντηθεί ΜΟΝΟ από τις υπάρχουσες στήλες του validated dataset. " +
                                   "Δεν βλέπεις και δεν επεξεργάζεσαι όλες τις γραμμές. Αν χρειάζεται νέα πληροφορία ή στήλη, βάλε canRefine=false. " +
                                   "Αν γίνεται τοπικά, επέστρεψε JSON μόνο: {\\"canRefine\\":true,\\"filters\\":[{\\"column\\":\\"...\\",\\"op\\":\\"eq|neq|contains|not_contains|gt|gte|lt|lte\\",\\"value\\":\\"...\\"}],\\"sort\\":[{\\"column\\":\\"...\\",\\"direction\\":\\"asc|desc\\"}],\\"limit\\":null}. " +
                                   "Χρησιμοποίησε αποκλειστικά column names που υπάρχουν στο catalog. Μην εφεύρεις mapping αν το catalog δεν το υποστηρίζει."
                    }
                },'''
    new = '''            JObject catalog = BuildCompactCatalog(dataset);
            string policyContext = JarvisPolicyRegistry.BuildTrainingContext(
                "Jarvis", "__dataset_refinement", new string[] { "Reporting" }, new string[0]);
            JObject request = new JObject
            {
                ["max_tokens"] = 1200,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Jarvis local dataset refinement protocol. Return JSON only: {\\"canRefine\\":true|false,\\"filters\\":[{\\"column\\":\\"...\\",\\"op\\":\\"eq|neq|contains|not_contains|gt|gte|lt|lte\\",\\"value\\":\\"...\\"}],\\"sort\\":[{\\"column\\":\\"...\\",\\"direction\\":\\"asc|desc\\"}],\\"limit\\":null}.\\n\\n" + policyContext
                    }
                },'''
    text = must_replace(text, old, new, "dataset policy extraction")
    write(path, text)


def update_policy_registry():
    path = "Core/JarvisPolicyRegistry.cs"
    text = read(path)
    if 'ORCHESTRATION.ACTIVE_CONTEXT_IS_DURABLE' not in text:
        anchor = '''            P("GLOBAL.VERIFIED_SUCCESS_ONLY", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Success σημαίνει validated terminal result με τα registered outputs. Model prose, draft, lookup result ή tool intention δεν θεωρούνται ολοκληρωμένο business outcome.", priority: 935),
'''
        insert = anchor + '''
            P("ORCHESTRATION.ACTIVE_CONTEXT_IS_DURABLE", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Σε multi-turn active run διατήρησε original intent, explicit user facts, validated graph/results, completed/invalidated nodes και pending confirmations. Follow-up interpretation χρησιμοποιεί αυτό το structured context και όχι phrase/keyword heuristics. Νέο user fact μπορεί να αλλάξει μόνο τα σχετικά downstream nodes/payloads.", agents: A("Jarvis"), priority: 934),

            P("ORCHESTRATION.DATASET_REFINEMENT_EXISTING_FACTS_ONLY", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Local dataset refinement επιτρέπεται μόνο όταν το follow-up απαντιέται αποκλειστικά από τις υπάρχουσες validated στήλες/τιμές. Αν απαιτείται νέα πληροφορία ή νέα στήλη, canRefine=false και το request επιστρέφει στο κανονικό orchestration.", agents: A("Jarvis"), tasks: A("__dataset_refinement"), domains: A("Reporting"), priority: 933),
'''
        text = must_replace(text, anchor, insert, "policy registry active context")
    write(path, text)


def update_harness():
    path = "Core/JarvisExecutionShadowHarness.cs"
    text = read(path)
    text = must_replace(text,
        '        internal static async Task RunAndLogSafeAsync(XSupport xSupport, string userPrompt,\n            JarvisPendingConfirmationSession pendingSession = null, JarvisDatasetSession datasetSession = null)\n        {\n            try { await TryRunControlledPilotAsync(xSupport, userPrompt, pendingSession, datasetSession); }',
        '        internal static async Task RunAndLogSafeAsync(XSupport xSupport, string userPrompt,\n            JarvisPendingConfirmationSession pendingSession = null, JarvisDatasetSession datasetSession = null,\n            JarvisActiveOrchestrationContext activeContext = null)\n        {\n            try { await TryRunControlledPilotAsync(xSupport, userPrompt, pendingSession, datasetSession, activeContext); }',
        "harness safe signature")
    text = must_replace(text,
        '        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(\n            XSupport xSupport, string userPrompt, JarvisPendingConfirmationSession pendingSession,\n            JarvisDatasetSession datasetSession = null)\n        {',
        '        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(\n            XSupport xSupport, string userPrompt, JarvisPendingConfirmationSession pendingSession,\n            JarvisDatasetSession datasetSession = null, JarvisActiveOrchestrationContext activeContext = null)\n        {',
        "harness pilot signature")
    text = must_replace(text,
        '                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, userPrompt);',
        '                string planningPrompt = activeContext == null ? userPrompt : activeContext.PreparePrompt(userPrompt);\n                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, planningPrompt);',
        "harness active prompt")
    text = must_replace(text,
        '                outcome.Handled = true;\n                bool hasEmail = HasTask(planning, "SendEmail");',
        '                outcome.Handled = true;\n                if (activeContext != null) activeContext.CapturePlanning(planning);\n                bool hasEmail = HasTask(planning, "SendEmail");',
        "harness capture planning")
    text = text.replace('                if (pendingSession != null) pendingSession.Clear();\n\n', '')
    text = must_replace(text,
        '                    LogSnapshot("result_accepted", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);',
        '                    LogSnapshot("result_accepted", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), reportStep.ObjectId, null, null);\n                    if (activeContext != null) activeContext.CaptureVerifiedResult(reportResult);',
        "capture report")
    text = must_replace(text,
        '                                    if (crmResult.Success)\n                                        completedSideEffects.Add(BuildCrmStatus(crmResult));',
        '                                    if (crmResult.Success)\n                                    {\n                                        completedSideEffects.Add(BuildCrmStatus(crmResult));\n                                        if (activeContext != null) activeContext.CaptureVerifiedResult(crmResult);\n                                    }',
        "capture crm")
    text = must_replace(text,
        '                                    if (calendarResult.Success)\n                                        completedSideEffects.Add(BuildCalendarStatus(calendarResult));',
        '                                    if (calendarResult.Success)\n                                    {\n                                        completedSideEffects.Add(BuildCalendarStatus(calendarResult));\n                                        if (activeContext != null) activeContext.CaptureVerifiedResult(calendarResult);\n                                    }',
        "capture calendar")
    text = must_replace(text,
        '                    outcome.Completed = deferredIssues.Count == 0;\n                    outcome.UserMessage = BuildCombinedMessage(intro, table, completedSideEffects, null);',
        '                    outcome.Completed = deferredIssues.Count == 0;\n                    if (outcome.Completed && activeContext != null) activeContext.Complete();\n                    outcome.UserMessage = BuildCombinedMessage(intro, table, completedSideEffects, null);',
        "complete no email")
    text = must_replace(text,
        '                LogSnapshot("confirmation_payload_frozen", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), pendingSession.PendingObjectId, null, pendingSession.PayloadHash);',
        '                LogSnapshot("confirmation_payload_frozen", coordinator.Inspect(), coordinator.GetDispatchableObjectIds(), pendingSession.PendingObjectId, null, pendingSession.PayloadHash);\n                if (activeContext != null) activeContext.CapturePendingConfirmation(pendingSession);',
        "capture confirmation")
    text = must_replace(text,
        '        internal static async Task<JarvisControlledPilotOutcome> TryResumeConfirmationAndExecuteAsync(\n            XSupport xSupport, JarvisPendingConfirmationSession pendingSession, string userText)',
        '        internal static async Task<JarvisControlledPilotOutcome> TryResumeConfirmationAndExecuteAsync(\n            XSupport xSupport, JarvisPendingConfirmationSession pendingSession, string userText,\n            JarvisActiveOrchestrationContext activeContext = null)',
        "resume signature")
    text = text.replace(
        '                pendingSession.Clear();\n                outcome.UserMessage = "Ο Jarvis απέρριψε την επιβεβαίωση: το pending task δεν είναι SendEmail/Echo.";',
        '                pendingSession.Clear();\n                if (activeContext != null) activeContext.ClearPendingConfirmation();\n                outcome.UserMessage = "Ο Jarvis απέρριψε την επιβεβαίωση: το pending task δεν είναι SendEmail/Echo.";')
    text = text.replace(
        '                pendingSession.Clear();\n                outcome.UserMessage = BuildFailureMessage("Ο Jarvis δεν επέτρεψε την αποστολή.", beginIssues);',
        '                pendingSession.Clear();\n                if (activeContext != null) activeContext.ClearPendingConfirmation();\n                outcome.UserMessage = BuildFailureMessage("Ο Jarvis δεν επέτρεψε την αποστολή.", beginIssues);')
    text = must_replace(text,
        '            pendingSession.Clear();\n\n            string[] acceptIssues;',
        '            pendingSession.Clear();\n            if (activeContext != null) activeContext.ClearPendingConfirmation();\n\n            string[] acceptIssues;',
        "resume clear pending")
    text = must_replace(text,
        '            outcome.Completed = true;\n            string to = frozenPayload == null',
        '            outcome.Completed = true;\n            if (activeContext != null)\n            {\n                activeContext.CaptureVerifiedResult(echoResult);\n                activeContext.Complete();\n            }\n            string to = frozenPayload == null',
        "resume complete context")
    write(path, text)


def update_shell():
    path = "UI/JarvisShell.OrchestrationShadow.cs"
    text = read(path)
    text = must_replace(text,
        '        private readonly JarvisDatasetSession _orchestrationDatasetSession = new JarvisDatasetSession();',
        '        private readonly JarvisDatasetSession _orchestrationDatasetSession = new JarvisDatasetSession();\n        private readonly JarvisActiveOrchestrationContext _orchestrationActiveContext = new JarvisActiveOrchestrationContext();',
        "shell active field")
    text = must_replace(text,
        '                        _xSupport, _orchestrationPendingConfirmation, userText);',
        '                        _xSupport, _orchestrationPendingConfirmation, userText, _orchestrationActiveContext);',
        "shell resume active")
    old_gate = '''                // Cheap lexical gate avoids an AI call on unrelated new prompts.
                // Only likely follow-ups are offered to the local dataset planner.
                if (!_orchestrationPendingConfirmation.HasPending &&
                    _orchestrationDatasetSession.HasDataset &&
                    JarvisDatasetSession.LooksLikeRefinement(userText))'''
    new_gate = '''                // Dataset continuity is decided semantically by the local refinement
                // planner. Phrase/keyword lists are not orchestration authority.
                if (!_orchestrationPendingConfirmation.HasPending &&
                    _orchestrationDatasetSession.HasDataset)'''
    text = must_replace(text, old_gate, new_gate, "shell dataset lexical gate")
    text = must_replace(text,
        '                    _xSupport, userText, _orchestrationPendingConfirmation, _orchestrationDatasetSession);',
        '                    _xSupport, userText, _orchestrationPendingConfirmation, _orchestrationDatasetSession, _orchestrationActiveContext);',
        "shell pilot active")
    write(path, text)


def main():
    update_agent_client()
    update_controlled_report()
    update_echo()
    update_dataset_session()
    update_policy_registry()
    update_harness()
    update_shell()


if __name__ == "__main__":
    main()

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8')

def replace_once(text, old, new, label):
    if old not in text:
        raise SystemExit('missing anchor: ' + label)
    return text.replace(old, new, 1)

# 1) Runtime context enters semantic decomposition.
p = 'Core/JarvisIntentOrchestration.cs'
s = read(p)
s = replace_once(s,
'''        internal static string BuildDecomposerUserPayload(string userPrompt)\n        {\n            return new JObject\n            {\n                ["userPrompt"] = userPrompt ?? string.Empty,\n                ["catalog"] = JObject.Parse(JarvisSemanticPlanning.BuildTaskCatalogJson())["TASK_CATALOG"]\n            }.ToString(Formatting.None);\n        }''',
'''        internal static string BuildDecomposerUserPayload(string userPrompt, JarvisRuntimeContext runtimeContext = null)\n        {\n            return new JObject\n            {\n                ["userPrompt"] = userPrompt ?? string.Empty,\n                ["runtimeContext"] = runtimeContext == null ? new JObject() : runtimeContext.ToJson(),\n                ["catalog"] = JObject.Parse(JarvisSemanticPlanning.BuildTaskCatalogJson())["TASK_CATALOG"]\n            }.ToString(Formatting.None);\n        }''', 'decomposer payload')
write(p, s)

p = 'Core/JarvisOrchestrationShadowCoordinator.cs'
s = read(p)
s = replace_once(s,
'                        ["content"] = JarvisIntentOrchestration.BuildDecomposerUserPayload(userPrompt)',
'                        ["content"] = JarvisIntentOrchestration.BuildDecomposerUserPayload(userPrompt, JarvisRuntimeContext.Capture(xSupport))',
'decomposer runtime call')
write(p, s)

# 2) Task contracts carry semantic scope and query provenance.
p = 'Core/JarvisTaskRegistry.cs'
s = read(p)
s = replace_once(s,
'                A("filters", "date_range", "entity_reference"),\n                A("dataset", "summary"),',
'                A("filters", "date_range", "entity_reference", "entity_role", "document_scope", "operator_scope"),\n                A("dataset", "summary", "query_sql"),',
'report contract scope')
s = replace_once(s,
'                A("sql", "format", "filename", "columns", "visible_table"),',
'                A("sql", "format", "filename", "columns", "visible_table", "document_scope"),',
'export contract scope')
write(p, s)

# Optional semantic inputs must survive prerequisite planning.
p = 'Core/JarvisPrerequisiteResolution.cs'
s = read(p)
anchor = '''            foreach (string requiredInput in descriptor.RequiredInputs)\n            {\n                JarvisPrerequisiteResolutionItem item = ResolveRequiredInput(intentObject, descriptor, requiredInput);\n                node.Prerequisites.Add(item);\n                if (item == null || item.Kind == JarvisPrerequisiteResolutionKind.Invalid)\n                    node.ValidationIssues.Add("Invalid prerequisite contract for " + descriptor.TaskType + "." + requiredInput);\n            }\n            return node;'''
replacement = '''            foreach (string requiredInput in descriptor.RequiredInputs)\n            {\n                JarvisPrerequisiteResolutionItem item = ResolveRequiredInput(intentObject, descriptor, requiredInput);\n                node.Prerequisites.Add(item);\n                if (item == null || item.Kind == JarvisPrerequisiteResolutionKind.Invalid)\n                    node.ValidationIssues.Add("Invalid prerequisite contract for " + descriptor.TaskType + "." + requiredInput);\n            }\n            foreach (string optionalInput in descriptor.OptionalInputs ?? new string[0])\n            {\n                JToken supplied;\n                if (!intentObject.InputHints.TryGetValue(optionalInput, out supplied) || !HasValue(supplied)) continue;\n                node.Prerequisites.Add(new JarvisPrerequisiteResolutionItem\n                {\n                    InputName = optionalInput,\n                    Required = false,\n                    Kind = JarvisPrerequisiteResolutionKind.ResolvedFromIntent,\n                    Value = supplied.DeepClone(),\n                    Reason = "Optional structured semantic input is explicitly present in this intent object."\n                });\n            }\n            return node;'''
s = replace_once(s, anchor, replacement, 'optional prerequisite propagation')
write(p, s)

# Deterministic ReportData -> ExportData dependency.
p = 'Core/JarvisDependencyBinding.cs'
s = read(p)
s = replace_once(s,
'''            R("ReportData", "summary", "SendEmail", "body",\n                "Report summary is the authoritative content source for the requested email body."),''',
'''            R("ReportData", "summary", "SendEmail", "body",\n                "Report summary is the authoritative content source for the requested email body."),\n\n            R("ReportData", "dataset", "ExportData", "source_result",\n                "Validated report dataset and its query provenance feed the requested export."),''',
'report export dependency')
write(p, s)

# 3) Central policies define runtime facts and structured scope semantics.
p = 'Core/JarvisPolicyRegistry.cs'
s = read(p)
insert = '''            P("GLOBAL.AUTHENTICATED_RUNTIME_FACTS", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,\n                "JARVIS_RUNTIME_CONTEXT contains authenticated Soft1 session facts such as currentUserId/currentCompanyId/localDateTime. When a required fact is present there, do not ask the operator to identify himself or restate it. Self/current-operator references resolve against that context.", priority: 923),\n\n            P("DECOMPOSER.STRUCTURED_SEMANTIC_SCOPE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,\n                "For reporting/export intents emit canonical structured inputs when semantically present: entity_role=Customer|Supplier|Debtor|Creditor only when the role is explicit/resolved; document_scope=invoice|order|quotation|credit|delivery|documents|movements; operator_scope=current_operator when the request concerns the authenticated operator. Do not invent entity_role when the same identity may exist in multiple roles.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 922),\n\n            P("DECOMPOSER.CURRENT_OPERATOR_MARKER", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,\n                "For CRM assignment semantically referring to the authenticated operator himself, emit assignee=__CURRENT_OPERATOR__. Runtime resolves this marker deterministically to currentUserId; never ask for the operator name when currentUserId is available.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 921),\n\n'''
s = replace_once(s, '            // ── Decomposition / planning ─────────────────────────────────────', insert + '            // ── Decomposition / planning ─────────────────────────────────────', 'policy insertion')
write(p, s)

# 4) Report executor preserves query provenance and validates current-operator SQL structurally.
p = 'Core/JarvisControlledTaskExecutor.cs'
s = read(p)
s = replace_once(s,
'''                string question = dispatchInputs["business_question"].ToString();\n                string policyContext = ReadRequiredInternalContext(dispatchInputs, "__policy_context");''',
'''                string question = dispatchInputs["business_question"].ToString();\n                string policyContext = ReadRequiredInternalContext(dispatchInputs, "__policy_context");\n                string operatorScope = dispatchInputs["operator_scope"] == null ? string.Empty : dispatchInputs["operator_scope"].ToString();\n                int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];''',
'report runtime inputs')
s = replace_once(s,
'                string sql = await PlanAndValidateSqlAsync(xSupport, question, policyContext, cancellationToken).ConfigureAwait(false);',
'                string sql = await PlanAndValidateSqlAsync(xSupport, question, policyContext, operatorScope, currentUserId, cancellationToken).ConfigureAwait(false);',
'report planner signature call')
s = replace_once(s,
'''                string normalizedQueryResult = NormalizeQueryResultForQuestion(question, queryResult);\n                string summary = BuildDeterministicSummary(question, normalizedQueryResult);''',
'''                string normalizedQueryResult = NormalizeQueryResultForQuestion(question, queryResult);\n                JObject normalizedDataset = JObject.Parse(normalizedQueryResult);\n                normalizedDataset["querySql"] = sql;\n                normalizedQueryResult = normalizedDataset.ToString(Formatting.None);\n                string summary = BuildDeterministicSummary(question, normalizedQueryResult);''',
'query provenance dataset')
s = replace_once(s,
'''                result.Outputs["dataset"] = new JValue(normalizedQueryResult);\n                result.Outputs["summary"] = new JValue(summary);''',
'''                result.Outputs["dataset"] = new JValue(normalizedQueryResult);\n                result.Outputs["summary"] = new JValue(summary);\n                result.Outputs["query_sql"] = new JValue(sql);''',
'query provenance output')
s = replace_once(s,
'''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, CancellationToken cancellationToken)''',
'''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, string operatorScope, int currentUserId, CancellationToken cancellationToken)''',
'planner method signature')
s = replace_once(s,
'                JObject request = BuildQueryRequest(question, policyContext, previousSql, previousDiagnostic, attempt);',
'                JObject request = BuildQueryRequest(question, policyContext, operatorScope, currentUserId, previousSql, previousDiagnostic, attempt);',
'build query request call')
s = replace_once(s,
'                string[] issues = ValidateSqlForQuestion(question, sql);',
'                string[] issues = ValidateSqlForQuestion(question, sql, operatorScope, currentUserId);',
'validate sql call')
s = replace_once(s,
'''        private static JObject BuildQueryRequest(string question, string policyContext, string previousSql, string previousDiagnostic, int attempt)\n        {\n            string userContent = "business_question: " + (question ?? string.Empty);''',
'''        private static JObject BuildQueryRequest(string question, string policyContext, string operatorScope, int currentUserId, string previousSql, string previousDiagnostic, int attempt)\n        {\n            string userContent = "business_question: " + (question ?? string.Empty);\n            if (!string.IsNullOrWhiteSpace(operatorScope)) userContent += "\\noperator_scope: " + operatorScope;\n            if (currentUserId > 0) userContent += "\\ncurrentUserId: " + currentUserId;''',
'build query request signature')
s = replace_once(s,
'        private static string[] ValidateSqlForQuestion(string question, string sql)\n        {',
'        private static string[] ValidateSqlForQuestion(string question, string sql, string operatorScope, int currentUserId)\n        {',
'validate sql signature')
s = replace_once(s,
'''            if (IsLatestDocumentQuestion(question))\n            {\n                if (!normalized.Contains("TOP 1")) issues.Add("Latest-document intent requires TOP 1.");\n                if (!normalized.Contains(" ORDER BY ")) issues.Add("Latest-document intent requires ORDER BY.");\n                if (!ContainsOrderedColumn(normalized, "TRNDATE", "DESC")) issues.Add("Latest-document intent requires TRNDATE DESC.");\n                if (!ContainsOrderedColumn(normalized, "FINDOC", "DESC")) issues.Add("Latest-document intent requires FINDOC DESC as deterministic tie-breaker.");\n            }''',
'''            bool currentOperatorScope = string.Equals(operatorScope, "current_operator", StringComparison.OrdinalIgnoreCase);\n            if (currentOperatorScope)\n            {\n                if (currentUserId <= 0) issues.Add("current_operator scope requires authenticated currentUserId.");\n                else if (!Regex.IsMatch(normalized, @"\\bINSUSER\\s*=\\s*" + currentUserId + @"\\b", RegexOptions.CultureInvariant))\n                    issues.Add("current_operator report must filter FINDOC.INSUSER to authenticated currentUserId.");\n            }\n            if (IsLatestDocumentQuestion(question))\n            {\n                if (!normalized.Contains("TOP 1")) issues.Add("Latest-document intent requires TOP 1.");\n                if (!normalized.Contains(" ORDER BY ")) issues.Add("Latest-document intent requires ORDER BY.");\n                if (currentOperatorScope)\n                {\n                    if (!ContainsOrderedColumn(normalized, "INSDATE", "DESC")) issues.Add("Latest current-operator document requires INSDATE DESC.");\n                }\n                else if (!ContainsOrderedColumn(normalized, "TRNDATE", "DESC")) issues.Add("Latest business document requires TRNDATE DESC.");\n                if (!ContainsOrderedColumn(normalized, "FINDOC", "DESC")) issues.Add("Latest-document intent requires FINDOC DESC as deterministic tie-breaker.");\n            }''',
'latest operator validation')
write(p, s)

# 5) Dataset cache becomes run-scoped and requires explicit semantic refinement relation.
p = 'Core/JarvisDatasetSession.cs'
s = read(p)
s = replace_once(s,
'''        private string _businessQuestion;\n        private JObject _dataset;''',
'''        private string _businessQuestion;\n        private string _runId;\n        private JObject _dataset;''',
'dataset run id field')
s = replace_once(s,
'''        internal bool TryCapture(string businessQuestion, string datasetJson)\n        {''',
'''        internal bool TryCapture(string businessQuestion, string datasetJson)\n        {\n            return TryCapture(null, businessQuestion, datasetJson);\n        }\n\n        internal bool TryCapture(string runId, string businessQuestion, string datasetJson)\n        {''',
'dataset capture overload')
s = replace_once(s,
'''                    _businessQuestion = businessQuestion ?? string.Empty;\n                    _dataset = (JObject)parsed.DeepClone();''',
'''                    _runId = runId ?? string.Empty;\n                    _businessQuestion = businessQuestion ?? string.Empty;\n                    _dataset = (JObject)parsed.DeepClone();''',
'dataset capture run')
s = replace_once(s,
'''                _businessQuestion = null;\n                _dataset = null;''',
'''                _businessQuestion = null;\n                _runId = null;\n                _dataset = null;''',
'dataset clear run')
s = replace_once(s,
'''        internal async Task<JarvisDatasetRefinementOutcome> TryRefineAsync(\n            XSupport xSupport,\n            string userText,''',
'''        internal async Task<JarvisDatasetRefinementOutcome> TryRefineAsync(\n            XSupport xSupport,\n            string activeRunId,\n            string userText,''',
'dataset refine signature')
s = replace_once(s,
'''            JObject source;\n            string originalQuestion;\n            lock (_sync)\n            {\n                source = _dataset == null ? null : (JObject)_dataset.DeepClone();\n                originalQuestion = _businessQuestion ?? string.Empty;\n            }''',
'''            JObject source;\n            string originalQuestion;\n            string datasetRunId;\n            lock (_sync)\n            {\n                source = _dataset == null ? null : (JObject)_dataset.DeepClone();\n                originalQuestion = _businessQuestion ?? string.Empty;\n                datasetRunId = _runId ?? string.Empty;\n            }\n            if (!string.IsNullOrWhiteSpace(datasetRunId) && !string.Equals(datasetRunId, activeRunId ?? string.Empty, StringComparison.OrdinalIgnoreCase))\n                return outcome;''',
'dataset run guard')
s = replace_once(s,
'''            if (plan == null || (bool?)plan["canRefine"] != true)\n                return outcome;''',
'''            if (plan == null || (bool?)plan["canRefine"] != true ||\n                !string.Equals((string)plan["relation"], "refine", StringComparison.OrdinalIgnoreCase))\n                return outcome;''',
'dataset relation guard')
s = replace_once(s,
'''["text"] = "Jarvis local dataset refinement protocol. Return JSON only: {\\"canRefine\\":true|false,\\"filters\\":[{\\"column\\":\\"...\\",\\"op\\":\\"eq|neq|contains|not_contains|gt|gte|lt|lte\\",\\"value\\":\\"...\\"}],\\"sort\\":[{\\"column\\":\\"...\\",\\"direction\\":\\"asc|desc\\"}],\\"limit\\":null}.\\n\\n" + policyContext''',
'''["text"] = "Jarvis local dataset refinement protocol. Return JSON only: {\\"relation\\":\\"refine|new_intent\\",\\"canRefine\\":true|false,\\"filters\\":[{\\"column\\":\\"...\\",\\"op\\":\\"eq|neq|contains|not_contains|gt|gte|lt|lte\\",\\"value\\":\\"...\\"}],\\"sort\\":[{\\"column\\":\\"...\\",\\"direction\\":\\"asc|desc\\"}],\\"limit\\":null}. relation=refine only when the new message modifies the existing dataset question; independent business questions are new_intent. canRefine=true only if every required fact/column already exists in catalog.\\n\\n" + policyContext''',
'dataset relation prompt')
write(p, s)

# 6) Shell passes the lineage id into local refinement.
p = 'UI/JarvisShell.OrchestrationShadow.cs'
s = read(p)
s = replace_once(s,
'                    JarvisDatasetRefinementOutcome refined = await _orchestrationDatasetSession.TryRefineAsync(_xSupport, userText);',
'                    JarvisDatasetRefinementOutcome refined = await _orchestrationDatasetSession.TryRefineAsync(_xSupport, _orchestrationActiveContext.RunId, userText);',
'shell dataset lineage')
write(p, s)

# 7) Harness wires runtime, ambiguity, document validation and controlled export.
p = 'Core/JarvisExecutionShadowHarness.cs'
s = read(p)
s = replace_once(s,
'            new[] { "ReportData", "SendEmail", "CreateCrmTask", "CreateCalendarEvent" },',
'            new[] { "ReportData", "ExportData", "SendEmail", "CreateCrmTask", "CreateCalendarEvent" },',
'promote export')
s = replace_once(s,
'''                    if (activeContext != null && activeContext.HasOpenRun && replaceActiveRun)\n                        activeContext.Clear();\n                    return outcome;''',
'''                    if (activeContext != null && activeContext.HasOpenRun && replaceActiveRun) activeContext.Clear();\n                    if (datasetSession != null && replaceActiveRun) datasetSession.Clear();\n                    return outcome;''',
'unsupported replace clears dataset')
s = replace_once(s,
'''                if (activeContext != null && (!activeContext.HasOpenRun || replaceActiveRun))\n                    activeContext.Begin(userPrompt);''',
'''                if (activeContext != null && (!activeContext.HasOpenRun || replaceActiveRun))\n                {\n                    if (replaceActiveRun && datasetSession != null) datasetSession.Clear();\n                    activeContext.Begin(userPrompt);\n                }''',
'supported replace clears dataset')
s = replace_once(s,
'''                    string[] beginIssues;\n                    if (!coordinator.TryBeginDispatch(reportStep.ObjectId, out beginIssues))''',
'''                    string ambiguityMessage = JarvisReportIdentityGuard.GetAmbiguityMessage(xSupport, reportInputs);\n                    if (!string.IsNullOrWhiteSpace(ambiguityMessage))\n                    {\n                        outcome.UserMessage = ambiguityMessage;\n                        return outcome;\n                    }\n\n                    JarvisRuntimeContext runtimeContext = JarvisRuntimeContext.Capture(xSupport);\n                    string existingPolicyContext = reportInputs["__policy_context"] == null ? string.Empty : reportInputs["__policy_context"].ToString();\n                    reportInputs["__policy_context"] = existingPolicyContext + "\\n" + runtimeContext.BuildEnvelope();\n                    reportInputs["__current_user_id"] = runtimeContext.CurrentUserId;\n\n                    string[] beginIssues;\n                    if (!coordinator.TryBeginDispatch(reportStep.ObjectId, out beginIssues))''',
'report guards runtime')
s = replace_once(s,
'''                    if (!reportResult.Success)\n                    {''',
'''                    if (reportResult.Success)\n                    {\n                        string documentScope = reportInputs["document_scope"] == null ? string.Empty : reportInputs["document_scope"].ToString();\n                        string reportDatasetForValidation = reportResult.Outputs["dataset"] == null ? string.Empty : reportResult.Outputs["dataset"].ToString();\n                        string[] scopeIssues = JarvisDocumentScopeValidator.Validate(documentScope, reportDatasetForValidation);\n                        if (scopeIssues.Length > 0)\n                        {\n                            reportResult.Success = false;\n                            foreach (string scopeIssue in scopeIssues) reportResult.Issues.Add(scopeIssue);\n                        }\n                    }\n                    if (!reportResult.Success)\n                    {''',
'document scope result validation')
s = replace_once(s,
'                    if (datasetSession != null) datasetSession.TryCapture(businessQuestion, datasetJson);',
'                    if (datasetSession != null) datasetSession.TryCapture(activeContext == null ? null : activeContext.RunId, businessQuestion, datasetJson);',
'dataset capture lineage')
anchor = '''                var completedSideEffects = new List<string>();\n                var deferredIssues = new List<string>();'''
export_block = '''                var completedSideEffects = new List<string>();\n                var deferredIssues = new List<string>();\n\n                JarvisExecutionStepSnapshot exportStep = FindStep(coordinator.Inspect(), "ExportData", "Atlas");\n                if (exportStep != null)\n                {\n                    JObject exportInputs;\n                    string[] exportInputIssues;\n                    if (!coordinator.TryGetDispatchInputs(exportStep.ObjectId, out exportInputs, out exportInputIssues))\n                    {\n                        deferredIssues.Add(BuildFailureMessage("Η εξαγωγή χρειάζεται επιπλέον πληροφορίες.", exportInputIssues));\n                    }\n                    else\n                    {\n                        string[] exportBeginIssues;\n                        if (!coordinator.TryBeginDispatch(exportStep.ObjectId, out exportBeginIssues))\n                        {\n                            deferredIssues.Add(BuildFailureMessage("Η εξαγωγή δεν είναι ακόμη dispatchable.", exportBeginIssues));\n                        }\n                        else\n                        {\n                            JarvisTaskExecutionResult exportResult = JarvisControlledExportTaskExecutor.Execute(xSupport, exportStep.ObjectId, exportInputs);\n                            string[] exportAcceptIssues;\n                            if (!coordinator.TryAcceptResult(exportResult, out exportAcceptIssues))\n                                deferredIssues.Add(BuildFailureMessage("Ο Jarvis απέρριψε το αποτέλεσμα της εξαγωγής.", exportAcceptIssues));\n                            else if (exportResult.Success)\n                            {\n                                completedSideEffects.Add(BuildExportStatus(exportResult));\n                                if (activeContext != null) activeContext.CaptureVerifiedResult(exportResult);\n                            }\n                            else\n                                deferredIssues.Add(BuildFailureMessage("Η εξαγωγή αρχείου απέτυχε.", exportResult.Issues.ToArray()));\n                        }\n                    }\n                }'''
s = replace_once(s, anchor, export_block, 'controlled export execution')
s = replace_once(s,
'''        private static string BuildCalendarStatus(JarvisTaskExecutionResult result)\n        {\n            string status = "✓ Το προσωπικό Outlook calendar event δημιουργήθηκε.";\n            string[] links = JarvisResultLinkPolicy.BuildMarkdownLinks(result);\n            return links.Length == 0 ? status : status + " " + string.Join(" ", links);\n        }''',
'''        private static string BuildCalendarStatus(JarvisTaskExecutionResult result)\n        {\n            string status = "✓ Το προσωπικό Outlook calendar event δημιουργήθηκε.";\n            string[] links = JarvisResultLinkPolicy.BuildMarkdownLinks(result);\n            return links.Length == 0 ? status : status + " " + string.Join(" ", links);\n        }\n\n        private static string BuildExportStatus(JarvisTaskExecutionResult result)\n        {\n            string status = "✓ Το αρχείο εξαγωγής δημιουργήθηκε.";\n            string[] links = JarvisResultLinkPolicy.BuildMarkdownLinks(result);\n            return links.Length == 0 ? status : status + " " + string.Join(" ", links);\n        }''',
'export status helper')
write(p, s)

# 8) Regression audit covers the new generalized contracts without natural-language fixtures.
p = 'Core/JarvisArchitectureRegressionAudit.cs'
s = read(p)
s = replace_once(s,
'''            ValidateLastMileContextInjection(issues);\n            ValidateActiveContextLifecycle(issues);''',
'''            ValidateLastMileContextInjection(issues);\n            ValidateActiveContextLifecycle(issues);\n            ValidateExportContract(issues);''',
'audit export call')
s = replace_once(s,
'''        private static int Count(string text, string token)''',
'''        private static void ValidateExportContract(List<string> issues)\n        {\n            JarvisTaskDescriptor report = JarvisTaskRegistry.Find("ReportData");\n            JarvisTaskDescriptor export = JarvisTaskRegistry.Find("ExportData");\n            if (report == null || !(report.Produces ?? new string[0]).Contains("query_sql", StringComparer.OrdinalIgnoreCase))\n                issues.Add("Export regression: ReportData must expose query_sql provenance.");\n            if (export == null) issues.Add("Export regression: ExportData task is missing.");\n            bool binding = JarvisDependencyBinder.AllRules.Any(x =>\n                string.Equals(x.SourceTaskType, "ReportData", StringComparison.OrdinalIgnoreCase) &&\n                string.Equals(x.TargetTaskType, "ExportData", StringComparison.OrdinalIgnoreCase) &&\n                string.Equals(x.TargetInput, "source_result", StringComparison.OrdinalIgnoreCase));\n            if (!binding) issues.Add("Export regression: ReportData dataset is not bound to ExportData source_result.");\n        }\n\n        private static int Count(string text, string token)''',
'audit export method')
write(p, s)

print('runtime regression hardening applied')

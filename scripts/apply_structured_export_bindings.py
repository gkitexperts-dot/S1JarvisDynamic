from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path): return (ROOT / path).read_text(encoding='utf-8')
def write(path, text): (ROOT / path).write_text(text, encoding='utf-8')
def rep(text, old, new, label):
    if old not in text: raise SystemExit('missing anchor: ' + label)
    return text.replace(old, new, 1)

# Task contracts: standalone export is autonomous; structured report semantics are explicit.
p='Core/JarvisTaskRegistry.cs'; s=read(p)
s=rep(s,
'                A("filters", "date_range", "entity_reference", "entity_role", "document_scope", "operator_scope"),',
'                A("filters", "date_range", "entity_reference", "entity_role", "document_scope", "operator_scope", "result_mode"),',
'report result mode')
s=rep(s,
'''                "Export query results or an already visible result table to a file. The source may be a prior report result or the currently visible table.",\n                A("export_query_to_file", "export_shown_table"),\n                A("source_result"),\n                A("sql", "format", "filename", "columns", "visible_table", "document_scope"),''',
'''                "Export business data to a file. It can autonomously plan a validated SELECT from export_request or reuse an explicitly bound upstream report result/query provenance.",\n                A("export_query_to_file", "export_shown_table"),\n                A("export_request"),\n                A("source_result", "sql", "format", "filename", "columns", "visible_table", "document_scope", "operator_scope", "result_mode"),''',
'export autonomous contract')
write(p,s)

# Prerequisites: semantic upstream markers become real dependency-pending contracts, never literal values.
p='Core/JarvisPrerequisiteResolution.cs'; s=read(p)
s=rep(s,
'''                node.Prerequisites.Add(new JarvisPrerequisiteResolutionItem\n                {\n                    InputName = optionalInput,\n                    Required = false,\n                    Kind = JarvisPrerequisiteResolutionKind.ResolvedFromIntent,\n                    Value = supplied.DeepClone(),\n                    Reason = "Optional structured semantic input is explicitly present in this intent object."\n                });''',
'''                if (IsSemanticDependencyMarker(descriptor.TaskType, optionalInput, supplied))\n                {\n                    node.Prerequisites.Add(new JarvisPrerequisiteResolutionItem\n                    {\n                        InputName = optionalInput,\n                        Required = false,\n                        Kind = JarvisPrerequisiteResolutionKind.DependencyPending,\n                        Reason = "Structured semantic marker requires an explicit registered upstream binding."\n                    });\n                }\n                else\n                {\n                    node.Prerequisites.Add(new JarvisPrerequisiteResolutionItem\n                    {\n                        InputName = optionalInput,\n                        Required = false,\n                        Kind = JarvisPrerequisiteResolutionKind.ResolvedFromIntent,\n                        Value = supplied.DeepClone(),\n                        Reason = "Optional structured semantic input is explicitly present in this intent object."\n                    });\n                }''',
'optional semantic dependency')
s=rep(s,
'''        private static bool IsCompositionDependency(string taskType, string inputName)''',
'''        private static bool IsSemanticDependencyMarker(string taskType, string inputName, JToken value)\n        {\n            if (value == null || value.Type != JTokenType.String) return false;\n            string marker = value.ToString();\n            if (string.Equals(taskType, "ExportData", StringComparison.OrdinalIgnoreCase) &&\n                string.Equals(inputName, "source_result", StringComparison.OrdinalIgnoreCase))\n                return string.Equals(marker, "__UPSTREAM_REPORT__", StringComparison.Ordinal);\n            if (string.Equals(taskType, "SendEmail", StringComparison.OrdinalIgnoreCase) &&\n                (string.Equals(inputName, "artifact_reference", StringComparison.OrdinalIgnoreCase) ||\n                 string.Equals(inputName, "attachmentFilePath", StringComparison.OrdinalIgnoreCase)))\n                return string.Equals(marker, "__UPSTREAM_EXPORT__", StringComparison.Ordinal);\n            return false;\n        }\n\n        private static bool IsCompositionDependency(string taskType, string inputName)''',
'dependency marker helper')
s=rep(s,
'case "reportdata:business_question": case "findtrader:trader_identity":',
'case "reportdata:business_question": case "exportdata:export_request": case "findtrader:trader_identity":',
'export intent contract')
write(p,s)

# Dependency policy: marker-fed optional attachment/source inputs bind through existing whitelist rules.
# Existing ReportData.dataset->ExportData.source_result and ExportData path/artifact -> SendEmail rules already own mapping.

# Central decomposition policy: structured bindings and result mode, never task coexistence heuristics.
p='Core/JarvisPolicyRegistry.cs'; s=read(p)
s=rep(s,
'''            P("DECOMPOSER.STRUCTURED_SEMANTIC_SCOPE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,\n                "For reporting/export intents emit canonical structured inputs when semantically present: entity_role=Customer|Supplier|Debtor|Creditor only when the role is explicit/resolved; document_scope=invoice|order|quotation|credit|delivery|documents|movements; operator_scope=current_operator when the request concerns the authenticated operator. Do not invent entity_role when the same identity may exist in multiple roles.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 922),''',
'''            P("DECOMPOSER.STRUCTURED_SEMANTIC_SCOPE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,\n                "For reporting/export intents emit canonical structured inputs when semantically present: entity_role=Customer|Supplier|Debtor|Creditor only when the role is explicit/resolved; document_scope=invoice|order|quotation|credit|delivery|documents|movements; operator_scope=current_operator when the request concerns the authenticated operator; result_mode=latest when one most-recent business row is requested. Do not invent entity_role when the same identity may exist in multiple roles.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 922),\n\n            P("DECOMPOSER.EXPLICIT_UPSTREAM_BINDINGS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,\n                "Cross-task composition must be explicit in structured inputs. If an ExportData object semantically exports the result requested by a ReportData object in the same instruction, emit source_result=__UPSTREAM_REPORT__. If a SendEmail object explicitly attaches the file produced by an ExportData object, emit artifact_reference=__UPSTREAM_EXPORT__. Never emit these markers merely because those task types coexist; they represent a real semantic dependency.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 921),''',
'central semantic binding policy')
# shift current operator marker priority to avoid duplicate priority is harmless but make distinct
s=s.replace('tasks: A("__decomposition"), priority: 921),\n\n            // ── Decomposition', 'tasks: A("__decomposition"), priority: 920),\n\n            // ── Decomposition', 1)
write(p,s)

# Report executor uses structured result_mode, not lexical latest detection on controlled paths.
p='Core/JarvisControlledTaskExecutor.cs'; s=read(p)
s=rep(s,
'''                string operatorScope = dispatchInputs["operator_scope"] == null ? string.Empty : dispatchInputs["operator_scope"].ToString();\n                int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];''',
'''                string operatorScope = dispatchInputs["operator_scope"] == null ? string.Empty : dispatchInputs["operator_scope"].ToString();\n                string resultMode = dispatchInputs["result_mode"] == null ? string.Empty : dispatchInputs["result_mode"].ToString();\n                int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];''',
'report result mode read')
s=rep(s,
'PlanAndValidateSqlAsync(xSupport, question, policyContext, operatorScope, currentUserId, cancellationToken)',
'PlanAndValidateSqlAsync(xSupport, question, policyContext, operatorScope, resultMode, currentUserId, cancellationToken)',
'report planner result mode call')
s=rep(s,
'''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, string operatorScope, int currentUserId, CancellationToken cancellationToken)''',
'''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, string operatorScope, string resultMode, int currentUserId, CancellationToken cancellationToken)''',
'planner result mode signature')
s=rep(s,
'BuildQueryRequest(question, policyContext, operatorScope, currentUserId, previousSql, previousDiagnostic, attempt)',
'BuildQueryRequest(question, policyContext, operatorScope, resultMode, currentUserId, previousSql, previousDiagnostic, attempt)',
'query request result mode call')
s=rep(s,
'ValidateSqlForQuestion(question, sql, operatorScope, currentUserId)',
'ValidateSqlForQuestion(question, sql, operatorScope, resultMode, currentUserId)',
'validate result mode call')
s=rep(s,
'''        private static JObject BuildQueryRequest(string question, string policyContext, string operatorScope, int currentUserId, string previousSql, string previousDiagnostic, int attempt)''',
'''        private static JObject BuildQueryRequest(string question, string policyContext, string operatorScope, string resultMode, int currentUserId, string previousSql, string previousDiagnostic, int attempt)''',
'query request result mode signature')
s=rep(s,
'''            if (!string.IsNullOrWhiteSpace(operatorScope)) userContent += "\\noperator_scope: " + operatorScope;\n            if (currentUserId > 0) userContent += "\\ncurrentUserId: " + currentUserId;''',
'''            if (!string.IsNullOrWhiteSpace(operatorScope)) userContent += "\\noperator_scope: " + operatorScope;\n            if (!string.IsNullOrWhiteSpace(resultMode)) userContent += "\\nresult_mode: " + resultMode;\n            if (currentUserId > 0) userContent += "\\ncurrentUserId: " + currentUserId;''',
'user content result mode')
s=rep(s,
'''        private static string[] ValidateSqlForQuestion(string question, string sql, string operatorScope, int currentUserId)''',
'''        private static string[] ValidateSqlForQuestion(string question, string sql, string operatorScope, string resultMode, int currentUserId)''',
'validate result mode signature')
s=rep(s,
'            if (IsLatestDocumentQuestion(question))\n            {',
'            if (string.Equals(resultMode, "latest", StringComparison.OrdinalIgnoreCase))\n            {',
'structured latest validation')
# expose planner to autonomous ExportData without executing query_data
insert='''\n        internal static Task<string> PlanValidatedSqlForExportAsync(\n            XSupport xSupport, string exportRequest, string policyContext, string operatorScope, string resultMode,\n            int currentUserId, CancellationToken cancellationToken = default(CancellationToken))\n        {\n            return PlanAndValidateSqlAsync(xSupport, exportRequest, policyContext, operatorScope, resultMode, currentUserId, cancellationToken);\n        }\n'''
anchor='''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, string operatorScope, string resultMode, int currentUserId, CancellationToken cancellationToken)'''
s=rep(s, anchor, insert+'\n'+anchor, 'export planner wrapper')
write(p,s)

# Export executor becomes async and autonomous when no upstream query provenance exists.
p='Core/JarvisControlledExportTaskExecutor.cs'; s=read(p)
s=s.replace('using Newtonsoft.Json.Linq;\nusing Softone;', 'using Newtonsoft.Json.Linq;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing Softone;')
s=s.replace('internal static JarvisTaskExecutionResult Execute(\n            XSupport xSupport,\n            string objectId,\n            JObject dispatchInputs)', 'internal static async Task<JarvisTaskExecutionResult> ExecuteAsync(\n            XSupport xSupport,\n            string objectId,\n            JObject dispatchInputs,\n            CancellationToken cancellationToken = default(CancellationToken))')
s=rep(s,
'''                if (string.IsNullOrWhiteSpace(sql))\n                    throw new InvalidOperationException("ExportData has no authoritative SELECT provenance from its upstream result.");''',
'''                if (string.IsNullOrWhiteSpace(sql))\n                {\n                    string exportRequest = ReadString(dispatchInputs, "export_request");\n                    if (string.IsNullOrWhiteSpace(exportRequest))\n                        throw new InvalidOperationException("ExportData has neither upstream query provenance nor export_request.");\n                    string policyContext = ReadString(dispatchInputs, "__policy_context");\n                    string operatorScope = ReadString(dispatchInputs, "operator_scope");\n                    string resultMode = ReadString(dispatchInputs, "result_mode");\n                    int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];\n                    sql = await JarvisControlledTaskExecutor.PlanValidatedSqlForExportAsync(\n                        xSupport, exportRequest, policyContext, operatorScope, resultMode, currentUserId, cancellationToken).ConfigureAwait(false);\n                }''',
'autonomous export planning')
write(p,s)

# Harness awaits export and injects authenticated runtime context for standalone export too.
p='Core/JarvisExecutionShadowHarness.cs'; s=read(p)
s=rep(s,
'''                    else\n                    {\n                        string[] exportBeginIssues;''',
'''                    else\n                    {\n                        JarvisRuntimeContext exportRuntime = JarvisRuntimeContext.Capture(xSupport);\n                        string exportPolicyContext = exportInputs["__policy_context"] == null ? string.Empty : exportInputs["__policy_context"].ToString();\n                        exportInputs["__policy_context"] = exportPolicyContext + "\\n" + exportRuntime.BuildEnvelope();\n                        exportInputs["__current_user_id"] = exportRuntime.CurrentUserId;\n                        string[] exportBeginIssues;''',
'export runtime injection')
s=rep(s,
'JarvisTaskExecutionResult exportResult = JarvisControlledExportTaskExecutor.Execute(xSupport, exportStep.ObjectId, exportInputs);',
'JarvisTaskExecutionResult exportResult = await JarvisControlledExportTaskExecutor.ExecuteAsync(xSupport, exportStep.ObjectId, exportInputs);',
'await controlled export')
write(p,s)

# Document scope validator scans all type-like columns and uses any classifiable authoritative metadata.
p='Core/JarvisDocumentScopeValidator.cs'; s=read(p)
s=rep(s,
'''                string typeText = ReadDocumentTypeText(row);\n                if (string.IsNullOrWhiteSpace(typeText)) continue;\n                string category = Classify(typeText);\n                if (string.IsNullOrWhiteSpace(category)) continue;\n                if (!string.Equals(category, scope, StringComparison.OrdinalIgnoreCase))\n                    violations.Add(typeText.Trim());''',
'''                foreach (string typeText in ReadDocumentTypeTexts(row))\n                {\n                    string category = Classify(typeText);\n                    if (string.IsNullOrWhiteSpace(category)) continue;\n                    if (!string.Equals(category, scope, StringComparison.OrdinalIgnoreCase))\n                    {\n                        violations.Add(typeText.Trim());\n                        break;\n                    }\n                    break;\n                }''',
'document type scan loop')
s=rep(s,
'''        private static string ReadDocumentTypeText(JObject row)\n        {\n            if (row == null) return string.Empty;\n            foreach (JProperty property in row.Properties())\n            {\n                string name = (property.Name ?? string.Empty).ToLowerInvariant();\n                if (!(name.Contains("series") || name.Contains("type") || name.Contains("σειρ") || name.Contains("τυπ")))\n                    continue;\n                if (property.Value == null || property.Value.Type == JTokenType.Null) continue;\n                string value = property.Value.ToString();\n                if (!string.IsNullOrWhiteSpace(value)) return value;\n            }\n            return string.Empty;\n        }''',
'''        private static IEnumerable<string> ReadDocumentTypeTexts(JObject row)\n        {\n            if (row == null) yield break;\n            foreach (JProperty property in row.Properties())\n            {\n                string name = (property.Name ?? string.Empty).ToLowerInvariant();\n                if (!(name.Contains("series") || name.Contains("type") || name.Contains("σειρ") || name.Contains("τυπ")))\n                    continue;\n                if (property.Value == null || property.Value.Type == JTokenType.Null) continue;\n                string value = property.Value.ToString();\n                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(Classify(value)))\n                    yield return value;\n            }\n        }''',
'document type scan helper')
write(p,s)

# Regression audit now verifies autonomous export contract and marker-binding semantics.
p='Core/JarvisArchitectureRegressionAudit.cs'; s=read(p)
s=rep(s,
'''            if (export == null) issues.Add("Export regression: ExportData task is missing.");''',
'''            if (export == null) issues.Add("Export regression: ExportData task is missing.");\n            else if (!(export.RequiredInputs ?? new string[0]).Contains("export_request", StringComparer.OrdinalIgnoreCase))\n                issues.Add("Export regression: ExportData must be autonomous through export_request.");\n            else if ((export.RequiredInputs ?? new string[0]).Contains("source_result", StringComparer.OrdinalIgnoreCase))\n                issues.Add("Export regression: source_result must be optional/upstream-bound, not mandatory.");''',
'audit autonomous export')
write(p,s)

print('structured export bindings applied')

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path): return (ROOT / path).read_text(encoding='utf-8')
def write(path, text): (ROOT / path).write_text(text, encoding='utf-8')
def rep(text, old, new, label):
    if old not in text: raise SystemExit('missing anchor: ' + label)
    return text.replace(old, new, 1)

# Export gets the same canonical entity-role scope as ReportData.
p='Core/JarvisTaskRegistry.cs'; s=read(p)
s=rep(s,
'                A("source_result", "sql", "format", "filename", "columns", "visible_table", "document_scope", "operator_scope", "result_mode"),',
'                A("source_result", "sql", "format", "filename", "columns", "visible_table", "entity_role", "document_scope", "operator_scope", "result_mode"),',
'export entity role contract')
write(p,s)

# Decomposition publishes export format explicitly and Atlas treats structured scope as binding.
p='Core/JarvisPolicyRegistry.cs'; s=read(p)
s=rep(s,
'entity_role=Customer|Supplier|Debtor|Creditor only when the role is explicit/resolved; document_scope=invoice|order|quotation|credit|delivery|documents|movements; operator_scope=current_operator',
'entity_role=Customer|Supplier|Debtor|Creditor only when the role is explicit/resolved; document_scope=invoice|order|quotation|credit|delivery|documents|movements; format=xlsx|csv|pdf for ExportData when explicitly requested; operator_scope=current_operator',
'decomposer export format')
anchor='''            P("ATLAS.SELECT_ONLY", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,\n                "Το query_data εκτελεί μόνο SELECT. Μη χρησιμοποιείς write/DDL/EXEC operations. Ο deterministic SELECT-only validator παραμένει authoritative.", agents: A("Atlas"), tools: A("query_data"), priority: 885),'''
insert=anchor+'''\n\n            P("ATLAS.STRUCTURED_SCOPE_IS_BINDING", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,\n                "Canonical entity_role, document_scope, operator_scope and result_mode are binding task constraints, not hints. The final verified SQL/result must make those constraints deterministically checkable; specific document scopes must expose authoritative human-readable document type metadata such as SERIES.NAME.", agents: A("Atlas", "Jarvis"), tasks: A("ReportData", "ExportData"), domains: A("Reporting"), priority: 884),'''
s=rep(s, anchor, insert, 'atlas structured policy')
write(p,s)

# Report validation checks actual SQL trace against structured role/operator scope.
p='Core/JarvisExecutionShadowHarness.cs'; s=read(p)
old='''                    if (reportResult.Success)\n                    {\n                        string documentScope = reportInputs["document_scope"] == null ? string.Empty : reportInputs["document_scope"].ToString();\n                        string reportDatasetForValidation = reportResult.Outputs["dataset"] == null ? string.Empty : reportResult.Outputs["dataset"].ToString();\n                        string[] scopeIssues = JarvisDocumentScopeValidator.Validate(documentScope, reportDatasetForValidation);\n                        if (scopeIssues.Length > 0)\n                        {\n                            reportResult.Success = false;\n                            foreach (string scopeIssue in scopeIssues) reportResult.Issues.Add(scopeIssue);\n                        }\n                    }'''
new='''                    if (reportResult.Success)\n                    {\n                        string entityRole = reportInputs["entity_role"] == null ? string.Empty : reportInputs["entity_role"].ToString();\n                        string documentScope = reportInputs["document_scope"] == null ? string.Empty : reportInputs["document_scope"].ToString();\n                        string operatorScope = reportInputs["operator_scope"] == null ? string.Empty : reportInputs["operator_scope"].ToString();\n                        string verifiedSql = reportResult.Outputs["query_sql"] == null ? string.Empty : reportResult.Outputs["query_sql"].ToString();\n                        int verifiedUserId = reportInputs["__current_user_id"] == null ? 0 : (int)reportInputs["__current_user_id"];\n                        string[] queryScopeIssues = JarvisStructuredQueryScopeValidator.Validate(verifiedSql, entityRole, operatorScope, verifiedUserId);\n                        string reportDatasetForValidation = reportResult.Outputs["dataset"] == null ? string.Empty : reportResult.Outputs["dataset"].ToString();\n                        string[] documentScopeIssues = JarvisDocumentScopeValidator.Validate(documentScope, reportDatasetForValidation);\n                        foreach (string scopeIssue in queryScopeIssues.Concat(documentScopeIssues))\n                        {\n                            reportResult.Success = false;\n                            reportResult.Issues.Add(scopeIssue);\n                        }\n                    }'''
s=rep(s, old, new, 'report structured validation')

# Explicit PDF export keeps using the mature callback-backed path until controlled direct PDF exists.
old='''            foreach (JarvisExecutionPlanEntry entry in planning.Preview.Entries)\n            {\n                if (entry == null || !PromotedControlledTasks.Contains(entry.TaskType)) return false;\n                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(entry.TaskType);\n                if (descriptor == null || !string.Equals(descriptor.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) return false;\n            }'''
new='''            foreach (JarvisExecutionPlanEntry entry in planning.Preview.Entries)\n            {\n                if (entry == null || !PromotedControlledTasks.Contains(entry.TaskType)) return false;\n                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(entry.TaskType);\n                if (descriptor == null || !string.Equals(descriptor.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) return false;\n                if (string.Equals(entry.TaskType, "ExportData", StringComparison.OrdinalIgnoreCase))\n                {\n                    JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, entry.ObjectId, StringComparison.OrdinalIgnoreCase));\n                    JarvisPrerequisiteResolutionItem format = node == null ? null : node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "format", StringComparison.OrdinalIgnoreCase));\n                    if (format != null && format.Value != null && string.Equals(format.Value.ToString(), "pdf", StringComparison.OrdinalIgnoreCase))\n                        return false;\n                }\n            }'''
s=rep(s, old, new, 'controlled pdf fallback')
write(p,s)

# Standalone export validates the verified SQL against structured entity/operator scope before file creation.
p='Core/JarvisControlledExportTaskExecutor.cs'; s=read(p)
old='''                ValidateStructuredDocumentScope(xSupport, sql, ReadString(dispatchInputs, "document_scope"));'''
new='''                string entityRole = ReadString(dispatchInputs, "entity_role");\n                string operatorScope = ReadString(dispatchInputs, "operator_scope");\n                int verifiedCurrentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];\n                string[] queryScopeIssues = JarvisStructuredQueryScopeValidator.Validate(sql, entityRole, operatorScope, verifiedCurrentUserId);\n                if (queryScopeIssues.Length > 0)\n                    throw new InvalidOperationException("Jarvis rejected ExportData structured query scope: " + string.Join(" | ", queryScopeIssues));\n                ValidateStructuredDocumentScope(xSupport, sql, ReadString(dispatchInputs, "document_scope"));'''
s=rep(s, old, new, 'export structured query validation')
write(p,s)

# Architecture smoke tests cover the added canonical contract.
p='Core/JarvisArchitectureRegressionAudit.cs'; s=read(p)
s=rep(s,
'''            if (export != null && !(export.OptionalInputs ?? new string[0]).Contains("document_scope", StringComparer.OrdinalIgnoreCase))\n                issues.Add("Export regression: ExportData must accept structured document_scope.");''',
'''            if (export != null && !(export.OptionalInputs ?? new string[0]).Contains("document_scope", StringComparer.OrdinalIgnoreCase))\n                issues.Add("Export regression: ExportData must accept structured document_scope.");\n            if (export != null && !(export.OptionalInputs ?? new string[0]).Contains("entity_role", StringComparer.OrdinalIgnoreCase))\n                issues.Add("Export regression: ExportData must accept structured entity_role.");''',
'audit export entity role')
write(p,s)

print('final orchestration hardening applied')

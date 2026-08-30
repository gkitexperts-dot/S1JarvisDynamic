from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
def read(p): return (ROOT/p).read_text(encoding='utf-8')
def write(p,s): (ROOT/p).write_text(s,encoding='utf-8')
def rep(s,a,b,label):
    if a not in s: raise SystemExit('missing anchor: '+label)
    return s.replace(a,b,1)

# Registry: ExportData gets the same structured entity role semantics as ReportData.
p='Core/JarvisTaskRegistry.cs'; s=read(p)
s=rep(s,
'                A("source_result", "sql", "format", "filename", "columns", "visible_table", "document_scope", "operator_scope", "result_mode"),',
'                A("source_result", "sql", "format", "filename", "columns", "visible_table", "entity_role", "document_scope", "operator_scope", "result_mode"),',
'export entity role')
write(p,s)

# Knowledge companion explicitly publishes SERIES.NAME as authoritative document type metadata.
p='Core/JarvisKnowledgeCompanion.cs'; s=read(p)
s=rep(s,
'''            return new JObject\n            {\n                ["joinKeys"] = new JArray("COMPANY", "SERIES", "SOSOURCE"),\n                ["purpose"] = "document type/series metadata for FINDOC"\n            };''',
'''            return new JObject\n            {\n                ["joinKeys"] = new JArray("COMPANY", "SERIES", "SOSOURCE"),\n                ["fields"] = new JArray("COMPANY", "SERIES", "SOSOURCE", "NAME"),\n                ["purpose"] = "document type/series metadata for FINDOC; NAME is authoritative human-readable type metadata"\n            };''',
'series name knowledge')
write(p,s)

# Policy: scoped document reports must expose verifiable metadata.
p='Core/JarvisPolicyRegistry.cs'; s=read(p)
anchor='''            P("ATLAS.SELECT_ONLY", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,\n                "Το query_data εκτελεί μόνο SELECT. Μη χρησιμοποιείς write/DDL/EXEC operations. Ο deterministic SELECT-only validator παραμένει authoritative.", agents: A("Atlas"), tools: A("query_data"), priority: 885),'''
insert=anchor+'''\n\n            P("ATLAS.STRUCTURED_SCOPE_IS_ENFORCED", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,\n                "When ReportData/ExportData receives structured entity_role/document_scope/operator_scope/result_mode, treat them as binding constraints, not suggestions. For a specific document_scope project human-readable authoritative document type metadata (for example SERIES.NAME) so Jarvis can validate returned rows deterministically. For entity_role use the authoritative SODTYPE mapping from JARVIS_KNOWLEDGE_CONTEXT.", agents: A("Atlas"), tasks: A("ReportData", "ExportData"), domains: A("Reporting"), priority: 884),'''
s=rep(s,anchor,insert,'atlas structured scope policy')
write(p,s)

# Controlled SQL planning carries and deterministically validates entity/document scope.
p='Core/JarvisControlledTaskExecutor.cs'; s=read(p)
s=rep(s,
'''                string operatorScope = dispatchInputs["operator_scope"] == null ? string.Empty : dispatchInputs["operator_scope"].ToString();\n                string resultMode = dispatchInputs["result_mode"] == null ? string.Empty : dispatchInputs["result_mode"].ToString();''',
'''                string entityRole = dispatchInputs["entity_role"] == null ? string.Empty : dispatchInputs["entity_role"].ToString();\n                string documentScope = dispatchInputs["document_scope"] == null ? string.Empty : dispatchInputs["document_scope"].ToString();\n                string operatorScope = dispatchInputs["operator_scope"] == null ? string.Empty : dispatchInputs["operator_scope"].ToString();\n                string resultMode = dispatchInputs["result_mode"] == null ? string.Empty : dispatchInputs["result_mode"].ToString();''',
'report structured read')
s=rep(s,
'PlanAndValidateSqlAsync(xSupport, question, policyContext, operatorScope, resultMode, currentUserId, cancellationToken)',
'PlanAndValidateSqlAsync(xSupport, question, policyContext, entityRole, documentScope, operatorScope, resultMode, currentUserId, cancellationToken)',
'report structured planner call')
s=rep(s,
'''            XSupport xSupport, string exportRequest, string policyContext, string operatorScope, string resultMode,\n            int currentUserId, CancellationToken cancellationToken = default(CancellationToken))\n        {\n            return PlanAndValidateSqlAsync(xSupport, exportRequest, policyContext, operatorScope, resultMode, currentUserId, cancellationToken);''',
'''            XSupport xSupport, string exportRequest, string policyContext, string entityRole, string documentScope,\n            string operatorScope, string resultMode, int currentUserId, CancellationToken cancellationToken = default(CancellationToken))\n        {\n            return PlanAndValidateSqlAsync(xSupport, exportRequest, policyContext, entityRole, documentScope, operatorScope, resultMode, currentUserId, cancellationToken);''',
'export planner structured signature')
s=rep(s,
'''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, string operatorScope, string resultMode, int currentUserId, CancellationToken cancellationToken)''',
'''        private static async Task<string> PlanAndValidateSqlAsync(XSupport xSupport, string question, string policyContext, string entityRole, string documentScope, string operatorScope, string resultMode, int currentUserId, CancellationToken cancellationToken)''',
'planner structured signature')
s=rep(s,
'BuildQueryRequest(question, policyContext, operatorScope, resultMode, currentUserId, previousSql, previousDiagnostic, attempt)',
'BuildQueryRequest(question, policyContext, entityRole, documentScope, operatorScope, resultMode, currentUserId, previousSql, previousDiagnostic, attempt)',
'build structured query request')
s=rep(s,
'ValidateSqlForQuestion(question, sql, operatorScope, resultMode, currentUserId)',
'ValidateSqlForQuestion(question, sql, entityRole, documentScope, operatorScope, resultMode, currentUserId)',
'validate structured query')
s=rep(s,
'''        private static JObject BuildQueryRequest(string question, string policyContext, string operatorScope, string resultMode, int currentUserId, string previousSql, string previousDiagnostic, int attempt)''',
'''        private static JObject BuildQueryRequest(string question, string policyContext, string entityRole, string documentScope, string operatorScope, string resultMode, int currentUserId, string previousSql, string previousDiagnostic, int attempt)''',
'query structured signature')
s=rep(s,
'''            string userContent = "business_question: " + (question ?? string.Empty);\n            if (!string.IsNullOrWhiteSpace(operatorScope)) userContent += "\\noperator_scope: " + operatorScope;''',
'''            string userContent = "business_question: " + (question ?? string.Empty);\n            if (!string.IsNullOrWhiteSpace(entityRole)) userContent += "\\nentity_role: " + entityRole;\n            if (!string.IsNullOrWhiteSpace(documentScope)) userContent += "\\ndocument_scope: " + documentScope;\n            if (!string.IsNullOrWhiteSpace(operatorScope)) userContent += "\\noperator_scope: " + operatorScope;''',
'user structured content')
s=rep(s,
'''        private static string[] ValidateSqlForQuestion(string question, string sql, string operatorScope, string resultMode, int currentUserId)''',
'''        private static string[] ValidateSqlForQuestion(string question, string sql, string entityRole, string documentScope, string operatorScope, string resultMode, int currentUserId)''',
'validate structured signature')
s=rep(s,
'''            ValidateRegisteredTraderRoleDiscriminators(normalized, issues);\n\n            if (IsDocumentQuestion(question))''',
'''            ValidateRegisteredTraderRoleDiscriminators(normalized, issues);\n            if (!string.IsNullOrWhiteSpace(entityRole))\n            {\n                JarvisTraderRoleDescriptor role = JarvisBusinessEntityCatalog.FindTraderRole(entityRole);\n                if (role == null) issues.Add("Unknown structured entity_role: " + entityRole);\n                else if (!Regex.IsMatch(normalized, @"(?:\\b[A-Z0-9_]+\\.)?SODTYPE\\s*=\\s*" + role.SodType + @"\\b", RegexOptions.CultureInvariant))\n                    issues.Add("SQL does not enforce structured entity_role=" + entityRole + " using registered SODTYPE=" + role.SodType + ".");\n            }\n\n            if (IsDocumentQuestion(question) || IsSpecificDocumentScope(documentScope))''',
'entity and document scope validation')
# Add helper near ValidateRegisteredTraderRoleDiscriminators
s=rep(s,
'''        private static void ValidateRegisteredTraderRoleDiscriminators(string normalizedSql, List<string> issues)''',
'''        private static bool IsSpecificDocumentScope(string value)\n        {\n            string v = (value ?? string.Empty).Trim().ToLowerInvariant();\n            return v == "invoice" || v == "order" || v == "quotation" || v == "credit" || v == "delivery";\n        }\n\n        private static void ValidateRegisteredTraderRoleDiscriminators(string normalizedSql, List<string> issues)''',
'document scope helper')
write(p,s)

# Autonomous export forwards all structured constraints to shared planner.
p='Core/JarvisControlledExportTaskExecutor.cs'; s=read(p)
s=rep(s,
'''                    string policyContext = ReadString(dispatchInputs, "__policy_context");\n                    string operatorScope = ReadString(dispatchInputs, "operator_scope");\n                    string resultMode = ReadString(dispatchInputs, "result_mode");''',
'''                    string policyContext = ReadString(dispatchInputs, "__policy_context");\n                    string entityRole = ReadString(dispatchInputs, "entity_role");\n                    string documentScope = ReadString(dispatchInputs, "document_scope");\n                    string operatorScope = ReadString(dispatchInputs, "operator_scope");\n                    string resultMode = ReadString(dispatchInputs, "result_mode");''',
'export structured read')
s=rep(s,
'''                        xSupport, exportRequest, policyContext, operatorScope, resultMode, currentUserId, cancellationToken).ConfigureAwait(false);''',
'''                        xSupport, exportRequest, policyContext, entityRole, documentScope, operatorScope, resultMode, currentUserId, cancellationToken).ConfigureAwait(false);''',
'export structured planner call')
write(p,s)

# Result validation must fail closed if a specific document scope cannot be verified from returned metadata.
p='Core/JarvisDocumentScopeValidator.cs'; s=read(p)
s=rep(s,
'''            var violations = new List<string>();\n            foreach (JObject row in rows.OfType<JObject>())''',
'''            var violations = new List<string>();\n            bool sawClassifiableMetadata = false;\n            foreach (JObject row in rows.OfType<JObject>())''',
'doc metadata flag')
s=rep(s,
'''                    string category = Classify(typeText);\n                    if (string.IsNullOrWhiteSpace(category)) continue;''',
'''                    string category = Classify(typeText);\n                    if (string.IsNullOrWhiteSpace(category)) continue;\n                    sawClassifiableMetadata = true;''',
'doc metadata observed')
s=rep(s,
'''            if (violations.Count == 0) return new string[0];\n            return new[]''',
'''            if (!sawClassifiableMetadata)\n                return new[] { "Specific document_scope='" + scope + "' cannot be verified because returned rows contain no classifiable authoritative document-type metadata." };\n            if (violations.Count == 0) return new string[0];\n            return new[]''',
'doc metadata fail closed')
write(p,s)

# Decomposer policy includes canonical export format so unsupported PDF can deliberately stay on mature fallback.
p='Core/JarvisPolicyRegistry.cs'; s=read(p)
s=rep(s,
'document_scope=invoice|order|quotation|credit|delivery|documents|movements; operator_scope=current_operator',
'document_scope=invoice|order|quotation|credit|delivery|documents|movements; format=xlsx|csv|pdf for ExportData when explicitly requested; operator_scope=current_operator',
'export format policy')
write(p,s)

# Harness rejects controlled PDF export until a callback-backed PDF executor is registered; no capability regression.
p='Core/JarvisExecutionShadowHarness.cs'; s=read(p)
anchor='''            foreach (JarvisExecutionPlanEntry entry in planning.Preview.Entries)\n            {\n                if (entry == null || !PromotedControlledTasks.Contains(entry.TaskType)) return false;\n                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(entry.TaskType);\n                if (descriptor == null || !string.Equals(descriptor.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) return false;\n            }'''
replacement='''            foreach (JarvisExecutionPlanEntry entry in planning.Preview.Entries)\n            {\n                if (entry == null || !PromotedControlledTasks.Contains(entry.TaskType)) return false;\n                JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(entry.TaskType);\n                if (descriptor == null || !string.Equals(descriptor.OwnerAgent, entry.OwnerAgent, StringComparison.OrdinalIgnoreCase)) return false;\n                if (string.Equals(entry.TaskType, "ExportData", StringComparison.OrdinalIgnoreCase))\n                {\n                    JarvisValidatedTaskNode node = planning.Graph.Nodes.FirstOrDefault(x => x != null && string.Equals(x.ObjectId, entry.ObjectId, StringComparison.OrdinalIgnoreCase));\n                    JarvisPrerequisiteResolutionItem format = node == null ? null : node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "format", StringComparison.OrdinalIgnoreCase));\n                    if (format != null && format.Value != null && string.Equals(format.Value.ToString(), "pdf", StringComparison.OrdinalIgnoreCase))\n                        return false;\n                }\n            }'''
s=rep(s,anchor,replacement,'pdf mature fallback')
write(p,s)

print('structured report validation applied')

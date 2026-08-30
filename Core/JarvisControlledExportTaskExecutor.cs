using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Controlled executor for the registered ExportData task. It consumes the
    /// validated upstream dataset contract and reuses its authoritative query when
    /// available, so export rows bypass LLM context and the artifact is produced once.
    /// Standalone exports plan one validated SELECT and perform any structured-scope
    /// verification locally before the full dataset is written to disk.
    /// </summary>
    internal static class JarvisControlledExportTaskExecutor
    {
        internal static async Task<JarvisTaskExecutionResult> ExecuteAsync(
            XSupport xSupport,
            string objectId,
            JObject dispatchInputs,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new JarvisTaskExecutionResult
            {
                ObjectId = objectId,
                TaskType = "ExportData",
                OwnerAgent = "Atlas",
                Success = false
            };

            try
            {
                if (xSupport == null) throw new ArgumentNullException("xSupport");
                if (dispatchInputs == null) throw new InvalidOperationException("ExportData inputs are missing.");

                JObject source = ParseSource(dispatchInputs["source_result"]);
                string sql = ReadString(dispatchInputs, "sql");
                if (string.IsNullOrWhiteSpace(sql) && source != null)
                    sql = ReadString(source, "querySql");

                string exportRequest = ReadString(dispatchInputs, "export_request");
                string policyContext = ReadString(dispatchInputs, "__policy_context");
                string planningOperatorScope = ReadString(dispatchInputs, "operator_scope");
                string resultMode = ReadString(dispatchInputs, "result_mode");
                string documentScope = ReadString(dispatchInputs, "document_scope");
                int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];

                if (string.IsNullOrWhiteSpace(sql))
                {
                    if (string.IsNullOrWhiteSpace(exportRequest))
                        throw new InvalidOperationException("ExportData has neither upstream query provenance nor export_request.");
                    sql = await PlanScopeBoundSqlAsync(
                        xSupport,
                        exportRequest,
                        documentScope,
                        policyContext,
                        planningOperatorScope,
                        resultMode,
                        currentUserId,
                        cancellationToken).ConfigureAwait(false);
                }

                // Tenant isolation is re-applied even when ExportData reuses an
                // upstream/legacy query. FINDOC SQL can never inherit a stale or
                // cross-company scope merely because provenance was already present.
                sql = JarvisDocumentCompanyScopePolicy.EnforceIfFindocQuery(xSupport, sql);

                string entityRole = ReadString(dispatchInputs, "entity_role");
                string operatorScope = ReadString(dispatchInputs, "operator_scope");
                int verifiedCurrentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];
                string[] queryScopeIssues = JarvisStructuredQueryScopeValidator.Validate(sql, entityRole, operatorScope, verifiedCurrentUserId);
                if (queryScopeIssues.Length > 0)
                    throw new InvalidOperationException("Jarvis rejected ExportData structured query scope: " + string.Join(" | ", queryScopeIssues));

                string[] documentScopeIssues = ValidateStructuredDocumentScope(xSupport, sql, documentScope);
                if (documentScopeIssues.Length > 0)
                {
                    if (string.IsNullOrWhiteSpace(exportRequest))
                        throw new InvalidOperationException(
                            "Jarvis rejected ExportData document scope before file creation: " + string.Join(" | ", documentScopeIssues));

                    DebugLog.Log("[ORCH-EXPORT] document_scope_repair object=" + (objectId ?? string.Empty) +
                                 " scope=" + documentScope + " issues=" + OneLine(string.Join(" | ", documentScopeIssues)));

                    string repairedRequest = BuildScopeRepairRequest(exportRequest, documentScope, documentScopeIssues);
                    sql = await PlanScopeBoundSqlAsync(
                        xSupport,
                        repairedRequest,
                        documentScope,
                        policyContext,
                        planningOperatorScope,
                        resultMode,
                        currentUserId,
                        cancellationToken).ConfigureAwait(false);

                    sql = JarvisDocumentCompanyScopePolicy.EnforceIfFindocQuery(xSupport, sql);

                    queryScopeIssues = JarvisStructuredQueryScopeValidator.Validate(sql, entityRole, operatorScope, verifiedCurrentUserId);
                    if (queryScopeIssues.Length > 0)
                        throw new InvalidOperationException("Jarvis rejected repaired ExportData structured query scope: " + string.Join(" | ", queryScopeIssues));

                    documentScopeIssues = ValidateStructuredDocumentScope(xSupport, sql, documentScope);
                    if (documentScopeIssues.Length > 0)
                        throw new InvalidOperationException(
                            "Jarvis rejected repaired ExportData document scope before file creation: " + string.Join(" | ", documentScopeIssues));

                    DebugLog.Log("[ORCH-EXPORT] document_scope_repair_accepted object=" + (objectId ?? string.Empty) +
                                 " scope=" + documentScope);
                }

                string format = ReadString(dispatchInputs, "format");
                if (string.IsNullOrWhiteSpace(format)) format = "xlsx";
                format = format.Trim().ToLowerInvariant();
                if (format != "xlsx" && format != "csv")
                    throw new InvalidOperationException("ExportData supports only xlsx or csv in the controlled direct-export path.");

                string filename = ReadString(dispatchInputs, "filename");
                if (string.IsNullOrWhiteSpace(filename)) filename = "Jarvis_export";

                JObject toolInput = new JObject
                {
                    ["sql"] = sql,
                    ["format"] = format,
                    ["filename"] = filename
                };
                string raw = JarvisTools.ExecuteExportQueryToFile(xSupport, toolInput);
                JObject payload = JObject.Parse(raw ?? "{}");
                if ((bool?)payload["success"] != true)
                    throw new InvalidOperationException("Export tool did not report success.");

                string path = ReadString(payload, "path");
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Export tool returned no artifact path.");

                result.Outputs["path"] = path;
                result.Outputs["filename"] = System.IO.Path.GetFileName(path);
                result.Outputs["file_artifact"] = new JObject
                {
                    ["path"] = path,
                    ["filename"] = System.IO.Path.GetFileName(path),
                    ["format"] = format,
                    ["rowsWritten"] = payload["rowsWritten"] == null ? 0 : payload["rowsWritten"].DeepClone(),
                    ["totalFound"] = payload["totalFound"] == null ? 0 : payload["totalFound"].DeepClone()
                };
                result.Outputs["query_sql"] = sql;
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add(ex.Message);
                return result;
            }
        }

        private static async Task<string> PlanScopeBoundSqlAsync(
            XSupport xSupport,
            string exportRequest,
            string documentScope,
            string policyContext,
            string operatorScope,
            string resultMode,
            int currentUserId,
            CancellationToken cancellationToken)
        {
            string request = BuildScopeBoundRequest(exportRequest, documentScope);
            return await JarvisControlledTaskExecutor.PlanValidatedSqlForExportAsync(
                xSupport,
                request,
                policyContext,
                operatorScope,
                resultMode,
                documentScope,
                currentUserId,
                cancellationToken).ConfigureAwait(false);
        }

        private static string BuildScopeBoundRequest(string exportRequest, string documentScope)
        {
            string request = exportRequest ?? string.Empty;
            string scope = NormalizeDocumentScope(documentScope);
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements")
                return request;

            return request +
                   "\n\n[JARVIS_STRUCTURED_CONSTRAINTS]" +
                   "\ndocument_scope: " + scope +
                   "\nThe final SQL rows must satisfy this canonical document_scope. Do not broaden the document category.";
        }

        private static string BuildScopeRepairRequest(string exportRequest, string documentScope, string[] issues)
        {
            return BuildScopeBoundRequest(exportRequest, documentScope) +
                   "\n\n[JARVIS_SCOPE_REPAIR]" +
                   "\nThe previous candidate was rejected by deterministic dataset validation." +
                   "\nvalidation_issues: " + string.Join(" | ", issues ?? new string[0]) +
                   "\nReturn a corrected SELECT whose result contains only the requested canonical document category.";
        }

        private static string[] ValidateStructuredDocumentScope(XSupport xSupport, string sql, string documentScope)
        {
            string scope = NormalizeDocumentScope(documentScope);
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements")
                return new string[0];

            string validationSql = BuildValidationSql(sql);
            string preview = JarvisTools.ExecuteQueryData(xSupport, validationSql);
            return JarvisDocumentScopeValidator.Validate(scope, preview);
        }

        private static string NormalizeDocumentScope(string documentScope)
        {
            string scope = (documentScope ?? string.Empty).Trim().ToLowerInvariant();
            if (scope == "invoices") return "invoice";
            if (scope == "orders") return "order";
            if (scope == "quotes" || scope == "quotation") return "quotation";
            if (scope == "credits") return "credit";
            if (scope == "delivery_note" || scope == "delivery_notes") return "delivery";
            return scope;
        }

        private static string BuildValidationSql(string sql)
        {
            string value = (sql ?? string.Empty).Trim();
            if (value.Length == 0) throw new InvalidOperationException("ExportData SQL is empty.");
            if (value.StartsWith("SELECT DISTINCT ", StringComparison.OrdinalIgnoreCase))
                return "SELECT DISTINCT TOP 200 " + value.Substring("SELECT DISTINCT ".Length);
            if (value.StartsWith("SELECT TOP ", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
                return "SELECT TOP 200 " + value.Substring("SELECT ".Length);
            return value;
        }

        private static JObject ParseSource(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            JObject obj = token as JObject;
            if (obj != null) return (JObject)obj.DeepClone();
            if (token.Type != JTokenType.String) return null;
            try { return JObject.Parse(token.ToString()); }
            catch { return null; }
        }

        private static string ReadString(JObject obj, string name)
        {
            if (obj == null || obj[name] == null || obj[name].Type == JTokenType.Null) return string.Empty;
            return obj[name].ToString();
        }

        private static string OneLine(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}

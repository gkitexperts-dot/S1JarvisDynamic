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
                if (string.IsNullOrWhiteSpace(sql))
                {
                    string exportRequest = ReadString(dispatchInputs, "export_request");
                    if (string.IsNullOrWhiteSpace(exportRequest))
                        throw new InvalidOperationException("ExportData has neither upstream query provenance nor export_request.");
                    string policyContext = ReadString(dispatchInputs, "__policy_context");
                    string planningOperatorScope = ReadString(dispatchInputs, "operator_scope");
                    string resultMode = ReadString(dispatchInputs, "result_mode");
                    int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];
                    sql = await JarvisControlledTaskExecutor.PlanValidatedSqlForExportAsync(
                        xSupport, exportRequest, policyContext, planningOperatorScope, resultMode, currentUserId, cancellationToken).ConfigureAwait(false);
                }

                string entityRole = ReadString(dispatchInputs, "entity_role");
                string operatorScope = ReadString(dispatchInputs, "operator_scope");
                int verifiedCurrentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];
                string[] queryScopeIssues = JarvisStructuredQueryScopeValidator.Validate(sql, entityRole, operatorScope, verifiedCurrentUserId);
                if (queryScopeIssues.Length > 0)
                    throw new InvalidOperationException("Jarvis rejected ExportData structured query scope: " + string.Join(" | ", queryScopeIssues));
                ValidateStructuredDocumentScope(xSupport, sql, ReadString(dispatchInputs, "document_scope"));

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
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add(ex.Message);
                return result;
            }
        }

        private static void ValidateStructuredDocumentScope(XSupport xSupport, string sql, string documentScope)
        {
            if (string.IsNullOrWhiteSpace(documentScope)) return;

            string scope = documentScope.Trim().ToLowerInvariant();
            if (scope == "documents" || scope == "movements") return;

            string validationSql = BuildValidationSql(sql);
            string preview = JarvisTools.ExecuteQueryData(xSupport, validationSql);
            string[] issues = JarvisDocumentScopeValidator.Validate(documentScope, preview);
            if (issues.Length > 0)
                throw new InvalidOperationException(
                    "Jarvis rejected ExportData document scope before file creation: " + string.Join(" | ", issues));
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
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Restricted executor for the first live/read-only orchestration slice.
    /// Supports ReportData only. Jarvis owns dispatch and validation; Atlas may
    /// request exactly one query_data SELECT. Jarvis validates the proposed SQL
    /// against the business intent before execution and may request one corrected
    /// proposal. The accepted query dataset is normalized deterministically into
    /// the registered ReportData outputs. No write/external tool is exposed here.
    /// </summary>
    internal static class JarvisControlledTaskExecutor
    {
        private const int MaxTokens = 6000;
        private const int MaxPlanningAttempts = 2;

        internal static async Task<JarvisTaskExecutionResult> ExecuteReportDataAsync(
            XSupport xSupport,
            string objectId,
            JObject dispatchInputs,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new JarvisTaskExecutionResult
            {
                ObjectId = objectId,
                TaskType = "ReportData",
                OwnerAgent = "Atlas",
                Success = false
            };

            try
            {
                if (xSupport == null)
                    throw new ArgumentNullException("xSupport");
                if (dispatchInputs == null || dispatchInputs["business_question"] == null)
                    throw new InvalidOperationException("ReportData dispatch is missing business_question.");

                string question = dispatchInputs["business_question"].ToString();
                string sql = await PlanAndValidateSqlAsync(xSupport, question, cancellationToken).ConfigureAwait(false);

                string queryResult = JarvisTools.ExecuteQueryData(xSupport, sql);
                if (string.IsNullOrWhiteSpace(queryResult))
                    throw new InvalidOperationException("Atlas ReportData query returned an empty dataset.");
                if (LooksLikeQueryError(queryResult))
                    throw new InvalidOperationException("Atlas ReportData query failed: " + queryResult);

                string[] resultIssues = ValidateQueryResultForQuestion(question, queryResult);
                if (resultIssues.Length > 0)
                    throw new InvalidOperationException("Jarvis rejected Atlas result: " + string.Join(" | ", resultIssues));

                string normalizedQueryResult = NormalizeQueryResultForQuestion(question, queryResult);
                string summary = BuildDeterministicSummary(question, normalizedQueryResult);
                if (string.IsNullOrWhiteSpace(summary))
                    throw new InvalidOperationException("Atlas ReportData could not normalize the query result into a summary.");

                result.Outputs["dataset"] = new JValue(normalizedQueryResult);
                result.Outputs["summary"] = new JValue(summary);
                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Issues.Add(ex.Message);
                return result;
            }
        }

        private static async Task<string> PlanAndValidateSqlAsync(
            XSupport xSupport,
            string question,
            CancellationToken cancellationToken)
        {
            string previousSql = null;
            string previousDiagnostic = null;

            for (int attempt = 1; attempt <= MaxPlanningAttempts; attempt++)
            {
                JObject request = BuildQueryRequest(question, previousSql, previousDiagnostic, attempt);
                S1Jarvis.Core.AgentProxyResponse response = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                    .SendAsync(xSupport, "Atlas", request.ToString(Formatting.None), cancellationToken)
                    .ConfigureAwait(false);

                EnsureSuccess(response, "Atlas ReportData query planning failed.");
                JObject queryUse = FindToolUse(response.RawResponseJson, "query_data");
                if (queryUse == null)
                    throw new InvalidOperationException("Atlas ReportData did not return the required query_data tool call.");

                JObject queryInput = queryUse["input"] as JObject;
                string sql = queryInput == null ? null : (string)queryInput["sql"];
                if (string.IsNullOrWhiteSpace(sql))
                    throw new InvalidOperationException("Atlas ReportData returned query_data without SQL.");

                string[] issues = ValidateSqlForQuestion(question, sql);
                if (issues.Length == 0)
                    return sql;

                previousSql = sql;
                previousDiagnostic = string.Join(" | ", issues);
            }

            throw new InvalidOperationException(
                "Jarvis semantic SQL validation failed after retry. Last SQL=" +
                (previousSql ?? "<none>") + " Diagnostic=" + (previousDiagnostic ?? "<none>"));
        }

        private static JObject BuildQueryRequest(string question, string previousSql, string previousDiagnostic, int attempt)
        {
            string userContent = "business_question: " + (question ?? string.Empty);
            if (attempt > 1)
            {
                userContent += "\n\n[JARVIS_VALIDATION_RETRY]" +
                               "\nΤο προηγούμενο SQL απορρίφθηκε από τον Jarvis και ΔΕΝ εκτελέστηκε." +
                               "\nprevious_sql: " + (previousSql ?? string.Empty) +
                               "\nvalidation_issues: " + (previousDiagnostic ?? string.Empty) +
                               "\nΕπέστρεψε νέο query_data call που διορθώνει ΟΛΑ τα παραπάνω. Μην εξηγήσεις με κείμενο.";
            }

            return new JObject
            {
                ["max_tokens"] = MaxTokens,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Είσαι ο Atlas executor υπό τον έλεγχο του Jarvis. Εκτελείς μόνο το συγκεκριμένο ReportData task. " +
                                   "Επιτρέπεται αποκλειστικά query_data και αποκλειστικά SELECT. Κάνε ΕΝΑ στοχευμένο query που απαντά το business_question. " +
                                   "Αν το business_question ζητά ΕΝΑ τελευταίο/πιο πρόσφατο αποτέλεσμα, χρησιμοποίησε TOP 1 και deterministic ORDER BY. " +
                                   "Για παραστατικά χρησιμοποίησε FINDOC: FINDOC, FINCODE, TRNDATE, SUMAMNT, SERIES, SOSOURCE, COMPANY, TRDR. " +
                                   "Για όνομα συναλλασσόμενου JOIN TRDR ON TRDR.TRDR=FINDOC.TRDR. " +
                                   "Για όνομα σειράς το SERIES είναι composite identity: JOIN SERIES με COMPANY + SERIES + SOSOURCE, όχι μόνο SERIES. " +
                                   "Μην χρησιμοποιείς άγνωστες στήλες. Ο Jarvis θα ελέγξει το SQL ΠΡΙΝ το εκτελέσει και θα απορρίψει query που δεν εκφράζει σωστά το intent."
                    }
                },
                ["tools"] = new JArray(BuildQueryDataTool()),
                ["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = "query_data" },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = userContent
                    }
                }
            };
        }

        private static JObject BuildQueryDataTool()
        {
            return new JObject
            {
                ["name"] = "query_data",
                ["description"] = "Execute a read-only SQL Server SELECT against Soft1 data.",
                ["input_schema"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["sql"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("sql")
                }
            };
        }

        private static JObject FindToolUse(string rawResponseJson, string toolName)
        {
            if (string.IsNullOrWhiteSpace(rawResponseJson))
                return null;
            JObject root = JObject.Parse(rawResponseJson);
            JArray content = root["content"] as JArray ?? new JArray();
            return content.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string)x["type"], "tool_use", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)x["name"], toolName, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] ValidateSqlForQuestion(string question, string sql)
        {
            var issues = new List<string>();
            string normalized = NormalizeSql(sql);

            if (!normalized.StartsWith(" SELECT ", StringComparison.Ordinal))
                issues.Add("Only SELECT is allowed.");
            if (normalized.Contains(" INSERT ") || normalized.Contains(" UPDATE ") || normalized.Contains(" DELETE ") ||
                normalized.Contains(" MERGE ") || normalized.Contains(" DROP ") || normalized.Contains(" ALTER ") ||
                normalized.Contains(" EXEC ") || normalized.Contains(" EXECUTE "))
                issues.Add("SQL contains a non-read-only operation.");

            if (IsLatestDocumentQuestion(question))
            {
                if (!normalized.Contains(" FROM FINDOC "))
                    issues.Add("Latest-document intent must read from FINDOC.");
                if (!normalized.Contains("TOP 1"))
                    issues.Add("Latest-document intent requires TOP 1.");
                if (!normalized.Contains(" ORDER BY "))
                    issues.Add("Latest-document intent requires ORDER BY.");
                if (!ContainsOrderedColumn(normalized, "TRNDATE", "DESC"))
                    issues.Add("Latest-document intent requires TRNDATE DESC.");
                if (!ContainsOrderedColumn(normalized, "FINDOC", "DESC"))
                    issues.Add("Latest-document intent requires FINDOC DESC as deterministic tie-breaker.");
            }

            string seriesJoin = ExtractJoinClause(normalized, "SERIES");
            if (seriesJoin != null)
            {
                if (!seriesJoin.Contains("COMPANY"))
                    issues.Add("SERIES join must include COMPANY.");
                if (!seriesJoin.Contains("SOSOURCE"))
                    issues.Add("SERIES join must include SOSOURCE.");
                if (!seriesJoin.Contains("SERIES"))
                    issues.Add("SERIES join must include SERIES key.");
            }

            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string[] ValidateQueryResultForQuestion(string question, string queryResult)
        {
            var issues = new List<string>();
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null)
                {
                    issues.Add("query_data result has no rows array.");
                    return issues.ToArray();
                }

                if (IsSingularLatestQuestion(question) && rows.Count > 1)
                    issues.Add("Singular latest intent returned more than one row despite validated TOP 1 SQL.");

                if (IsLatestDocumentQuestion(question) && rows.Count > 0)
                {
                    JObject row = rows[0] as JObject;
                    if (row == null)
                        issues.Add("Latest-document result row is not an object.");
                    else
                    {
                        if (FindPropertyValue(row, "FINDOC") == null)
                            issues.Add("Latest-document result is missing FINDOC.");
                        if (FindPropertyValue(row, "TRNDATE") == null)
                            issues.Add("Latest-document result is missing TRNDATE.");
                    }
                }
            }
            catch (Exception ex)
            {
                issues.Add("query_data result is not a valid JSON envelope: " + ex.Message);
            }
            return issues.ToArray();
        }

        private static string NormalizeSql(string sql)
        {
            string value = (sql ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim().ToUpperInvariant();
            while (value.Contains("  "))
                value = value.Replace("  ", " ");
            return " " + value.Trim() + " ";
        }

        private static bool ContainsOrderedColumn(string normalizedSql, string columnName, string direction)
        {
            int orderIndex = normalizedSql.IndexOf(" ORDER BY ", StringComparison.Ordinal);
            if (orderIndex < 0)
                return false;
            string orderClause = normalizedSql.Substring(orderIndex);
            string bare = columnName + " " + direction;
            if (orderClause.Contains(bare))
                return true;

            string dottedSuffix = "." + columnName + " " + direction;
            return orderClause.Contains(dottedSuffix);
        }

        private static string ExtractJoinClause(string normalizedSql, string tableName)
        {
            string marker = " JOIN " + tableName + " ";
            int start = normalizedSql.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return null;

            int end = normalizedSql.Length;
            int nextJoin = normalizedSql.IndexOf(" JOIN ", start + marker.Length, StringComparison.Ordinal);
            int where = normalizedSql.IndexOf(" WHERE ", start + marker.Length, StringComparison.Ordinal);
            int order = normalizedSql.IndexOf(" ORDER BY ", start + marker.Length, StringComparison.Ordinal);
            if (nextJoin >= 0 && nextJoin < end) end = nextJoin;
            if (where >= 0 && where < end) end = where;
            if (order >= 0 && order < end) end = order;
            return normalizedSql.Substring(start, end - start);
        }

        private static JToken FindPropertyValue(JObject row, string propertyName)
        {
            if (row == null || string.IsNullOrWhiteSpace(propertyName))
                return null;
            JProperty property = row.Properties().FirstOrDefault(x => string.Equals(x.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            return property == null ? null : property.Value;
        }

        private static bool LooksLikeQueryError(string queryResult)
        {
            string value = (queryResult ?? string.Empty).TrimStart();
            return value.StartsWith("Σφάλμα:", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeQueryResultForQuestion(string question, string queryResult)
        {
            if (!IsSingularLatestQuestion(question))
                return queryResult;

            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null || rows.Count <= 1)
                    return queryResult;

                JToken first = rows[0];
                var normalizedRows = new JArray();
                if (first != null)
                    normalizedRows.Add(first.DeepClone());

                root["rows"] = normalizedRows;
                root["rowCount"] = normalizedRows.Count;
                root["totalRowCount"] = normalizedRows.Count;
                root["truncated"] = false;
                return root.ToString(Formatting.None);
            }
            catch
            {
                return queryResult;
            }
        }

        private static bool IsSingularLatestQuestion(string question)
        {
            string value = (question ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0)
                return false;

            return value.Contains("πιο πρόσφατ") ||
                   value.Contains("πιο προσφατ") ||
                   value.Contains("τελευταίο") ||
                   value.Contains("τελευταιο") ||
                   value.Contains("τελευταία εγγραφή") ||
                   value.Contains("τελευταια εγγραφη") ||
                   value.Contains("latest") ||
                   value.Contains("most recent");
        }

        private static bool IsLatestDocumentQuestion(string question)
        {
            string value = (question ?? string.Empty).Trim().ToLowerInvariant();
            return IsSingularLatestQuestion(question) &&
                   (value.Contains("παραστατικ") || value.Contains("document") || value.Contains("voucher"));
        }

        private static string BuildDeterministicSummary(string question, string queryResult)
        {
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null)
                    return queryResult.Trim();

                int totalRowCount = (int?)root["totalRowCount"] ?? rows.Count;
                if (rows.Count == 0)
                    return "Δεν βρέθηκαν δεδομένα για: " + (question ?? string.Empty);

                var sb = new StringBuilder();
                sb.Append("Αποτέλεσμα για: ").Append(question ?? string.Empty).AppendLine();
                sb.Append("Εγγραφές: ").Append(totalRowCount).AppendLine();

                int take = Math.Min(rows.Count, 10);
                for (int i = 0; i < take; i++)
                {
                    JObject row = rows[i] as JObject;
                    if (row == null)
                        continue;

                    if (take > 1)
                        sb.Append("#").Append(i + 1).Append(": ");

                    bool first = true;
                    foreach (JProperty property in row.Properties())
                    {
                        if (!first)
                            sb.Append(" | ");
                        first = false;
                        sb.Append(property.Name).Append(": ");
                        if (property.Value == null || property.Value.Type == JTokenType.Null)
                            sb.Append("-");
                        else
                            sb.Append(property.Value.ToString(Formatting.None).Trim('"'));
                    }
                    sb.AppendLine();
                }

                if (totalRowCount > take)
                    sb.Append("... και άλλες ").Append(totalRowCount - take).Append(" εγγραφές.");

                return sb.ToString().Trim();
            }
            catch
            {
                return queryResult.Trim();
            }
        }

        private static void EnsureSuccess(S1Jarvis.Core.AgentProxyResponse response, string fallback)
        {
            if (response == null || !response.Success)
                throw new InvalidOperationException(
                    response != null && !string.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? response.ErrorMessage
                        : fallback);
        }
    }
}

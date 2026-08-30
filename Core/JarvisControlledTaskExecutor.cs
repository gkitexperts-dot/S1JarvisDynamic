using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal static class JarvisControlledTaskExecutor
    {
        private const int MaxTokens = 6000;
        private const int MaxPlanningAttempts = 3;

        internal static async Task<JarvisTaskExecutionResult> ExecuteReportDataAsync(XSupport xSupport, string objectId, JObject dispatchInputs, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new JarvisTaskExecutionResult { ObjectId = objectId, TaskType = "ReportData", OwnerAgent = "Atlas", Success = false };
            try
            {
                if (xSupport == null) throw new ArgumentNullException("xSupport");
                if (dispatchInputs == null || dispatchInputs["business_question"] == null) throw new InvalidOperationException("ReportData dispatch is missing business_question.");
                string question = dispatchInputs["business_question"].ToString();
                string policyContext = ReadRequiredInternalContext(dispatchInputs, "__policy_context");
                string operatorScope = dispatchInputs["operator_scope"] == null ? string.Empty : dispatchInputs["operator_scope"].ToString();
                string resultMode = dispatchInputs["result_mode"] == null ? string.Empty : dispatchInputs["result_mode"].ToString();
                string documentScope = dispatchInputs["document_scope"] == null ? string.Empty : dispatchInputs["document_scope"].ToString();
                int currentUserId = dispatchInputs["__current_user_id"] == null ? 0 : (int)dispatchInputs["__current_user_id"];
                DebugLog.Log("[ORCH-SQL] plan_begin object=" + (objectId ?? string.Empty) + " question=" + OneLine(question));
                string sql = await PlanAndValidateSqlAsync(xSupport, question, policyContext, operatorScope, resultMode, documentScope, currentUserId, cancellationToken).ConfigureAwait(false);
                DebugLog.Log("[ORCH-SQL] execute object=" + (objectId ?? string.Empty) + " sql=" + OneLine(sql));
                string queryResult = JarvisTools.ExecuteQueryData(xSupport, sql);
                DebugLog.Log("[ORCH-SQL] result object=" + (objectId ?? string.Empty) + " " + DescribeQueryResult(queryResult));
                if (string.IsNullOrWhiteSpace(queryResult)) throw new InvalidOperationException("Atlas ReportData query returned an empty dataset.");
                if (LooksLikeQueryError(queryResult)) throw new InvalidOperationException("Atlas ReportData query failed: " + queryResult);
                var resultIssues = new List<string>(ValidateQueryResultForQuestion(question, queryResult));
                resultIssues.AddRange(JarvisDocumentScopeValidator.Validate(documentScope, queryResult));
                if (resultIssues.Count > 0)
                {
                    DebugLog.Log("[ORCH-SQL] result_rejected object=" + (objectId ?? string.Empty) + " issues=" + OneLine(string.Join(" | ", resultIssues)));
                    throw new InvalidOperationException("Jarvis rejected Atlas result: " + string.Join(" | ", resultIssues.Distinct(StringComparer.OrdinalIgnoreCase)));
                }
                string normalizedQueryResult = NormalizeQueryResultForQuestion(question, queryResult);
                JObject normalizedDataset = JObject.Parse(normalizedQueryResult);
                normalizedDataset["querySql"] = sql;
                normalizedQueryResult = normalizedDataset.ToString(Formatting.None);
                string summary = BuildDeterministicSummary(question, normalizedQueryResult);
                if (string.IsNullOrWhiteSpace(summary)) throw new InvalidOperationException("Atlas ReportData could not normalize the query result into a summary.");
                result.Outputs["dataset"] = new JValue(normalizedQueryResult);
                result.Outputs["summary"] = new JValue(summary);
                result.Outputs["query_sql"] = new JValue(sql);
                result.Success = true;
                DebugLog.Log("[ORCH-SQL] result_accepted object=" + (objectId ?? string.Empty) + " " + DescribeQueryResult(normalizedQueryResult));
                return result;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SQL] failure object=" + (objectId ?? string.Empty) + " error=" + OneLine(ex.Message));
                result.Issues.Add(ex.Message);
                return result;
            }
        }

        internal static Task<string> PlanValidatedSqlForExportAsync(
            XSupport xSupport, string exportRequest, string policyContext, string operatorScope, string resultMode,
            string documentScope, int currentUserId, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PlanAndValidateSqlAsync(xSupport, exportRequest, policyContext, operatorScope, resultMode, documentScope, currentUserId, cancellationToken);
        }

        private static async Task<string> PlanAndValidateSqlAsync(
            XSupport xSupport, string question, string policyContext, string operatorScope, string resultMode,
            string documentScope, int currentUserId, CancellationToken cancellationToken)
        {
            string previousSql = null;
            string previousDiagnostic = null;
            for (int attempt = 1; attempt <= MaxPlanningAttempts; attempt++)
            {
                JObject request = BuildQueryRequest(question, policyContext, operatorScope, resultMode, documentScope, currentUserId, previousSql, previousDiagnostic, attempt);
                S1Jarvis.Core.AgentProxyResponse response = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient().SendAsync(xSupport, "Atlas", request.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response, "Atlas ReportData query planning failed.");
                JObject queryUse = FindToolUse(response.RawResponseJson, "query_data");
                if (queryUse == null) throw new InvalidOperationException("Atlas ReportData did not return the required query_data tool call.");
                JObject queryInput = queryUse["input"] as JObject;
                string sql = queryInput == null ? null : (string)queryInput["sql"];
                if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Atlas ReportData returned query_data without SQL.");

                string normalizedScope = NormalizeDocumentScope(documentScope);
                var issues = new List<string>();
                if (!string.IsNullOrWhiteSpace(normalizedScope) && normalizedScope != "documents" && normalizedScope != "movements")
                {
                    string constrainedSql;
                    string scopeIssue;
                    if (TryApplyDocumentScopePredicate(sql, normalizedScope, out constrainedSql, out scopeIssue))
                        sql = constrainedSql;
                    else if (!string.IsNullOrWhiteSpace(scopeIssue))
                        issues.Add(scopeIssue);
                }

                DebugLog.Log("[ORCH-SQL] candidate attempt=" + attempt + " sql=" + OneLine(sql));
                issues.AddRange(ValidateSqlForQuestion(question, sql, operatorScope, resultMode, currentUserId));

                if (issues.Count == 0 && !string.IsNullOrWhiteSpace(normalizedScope) && normalizedScope != "documents" && normalizedScope != "movements")
                {
                    string previewSql = BuildValidationSql(sql);
                    string preview = JarvisTools.ExecuteQueryData(xSupport, previewSql);
                    if (string.IsNullOrWhiteSpace(preview) || LooksLikeQueryError(preview))
                        issues.Add("Structured document_scope preview could not be validated.");
                    else
                        issues.AddRange(JarvisDocumentScopeValidator.Validate(normalizedScope, preview));
                }

                if (issues.Count == 0)
                {
                    DebugLog.Log("[ORCH-SQL] accepted attempt=" + attempt + " sql=" + OneLine(sql));
                    return sql;
                }
                previousSql = sql;
                previousDiagnostic = string.Join(" | ", issues.Distinct(StringComparer.OrdinalIgnoreCase));
                DebugLog.Log("[ORCH-SQL] rejected attempt=" + attempt + " issues=" + OneLine(previousDiagnostic) + " sql=" + OneLine(sql));
            }
            throw new InvalidOperationException("Jarvis semantic SQL validation failed after retry. Last SQL=" + (previousSql ?? "<none>") + " Diagnostic=" + (previousDiagnostic ?? "<none>"));
        }

        private static JObject BuildQueryRequest(
            string question, string policyContext, string operatorScope, string resultMode, string documentScope,
            int currentUserId, string previousSql, string previousDiagnostic, int attempt)
        {
            string userContent = "business_question: " + (question ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(operatorScope)) userContent += "\noperator_scope: " + operatorScope;
            if (!string.IsNullOrWhiteSpace(resultMode)) userContent += "\nresult_mode: " + resultMode;
            if (!string.IsNullOrWhiteSpace(documentScope)) userContent += "\ndocument_scope: " + NormalizeDocumentScope(documentScope);
            if (currentUserId > 0) userContent += "\ncurrentUserId: " + currentUserId;
            if (attempt > 1)
            {
                userContent += "\n\n[JARVIS_VALIDATION_RETRY]" +
                               "\nprevious_sql: " + (previousSql ?? string.Empty) +
                               "\nvalidation_issues: " + (previousDiagnostic ?? string.Empty) +
                               "\nCorrect every validation issue before returning the next SELECT. Do not broaden a structured document_scope.";
            }

            return new JObject
            {
                ["max_tokens"] = MaxTokens,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["metadata"] = new JObject { ["jarvis_task"] = "ReportData" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] =
                            "Εκτελείς το registered atomic task ReportData ως Atlas με scoped tool query_data. " +
                            "Το JARVIS_KNOWLEDGE_CONTEXT περιέχει authoritative business/schema facts και το JARVIS_POLICY_CONTEXT τους behavioral κανόνες. " +
                            "Το envelope ορίζει μόνο το atomic protocol και το required tool call. " +
                            "Για κάθε FINDOC document query χρησιμοποίησε INNER JOIN FPRMS ON FINDOC.FPRMS=FPRMS.FPRMS και INNER JOIN SERIES ON SERIES.FPRMS=FPRMS.FPRMS. Το FPRMS είναι ο authoritative document type discriminator· η SERIES είναι descriptive subtype/variant. Πρόβαλε FINDOC+SOSOURCE, FPRMS.NAME και SERIES.NAME.\n\n" +
                            (policyContext ?? string.Empty)
                    }
                },
                ["tools"] = new JArray(BuildQueryDataTool()),
                ["tool_choice"] = new JObject { ["type"] = "tool", ["name"] = "query_data" },
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = userContent } }
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
                    ["properties"] = new JObject { ["sql"] = new JObject { ["type"] = "string" } },
                    ["required"] = new JArray("sql")
                }
            };
        }

        private static JObject FindToolUse(string rawResponseJson, string toolName)
        {
            if (string.IsNullOrWhiteSpace(rawResponseJson)) return null;
            JObject root = JObject.Parse(rawResponseJson);
            JArray content = root["content"] as JArray ?? new JArray();
            return content.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["type"], "tool_use", StringComparison.OrdinalIgnoreCase) && string.Equals((string)x["name"], toolName, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] ValidateSqlForQuestion(string question, string sql, string operatorScope, string resultMode, int currentUserId)
        {
            var issues = new List<string>();
            string normalized = NormalizeSql(sql);
            if (!normalized.StartsWith(" SELECT ", StringComparison.Ordinal)) issues.Add("Only SELECT is allowed.");
            if (normalized.Contains(" INSERT ") || normalized.Contains(" UPDATE ") || normalized.Contains(" DELETE ") || normalized.Contains(" MERGE ") || normalized.Contains(" DROP ") || normalized.Contains(" ALTER ") || normalized.Contains(" EXEC ") || normalized.Contains(" EXECUTE ")) issues.Add("SQL contains a non-read-only operation.");

            ValidateRegisteredTraderRoleDiscriminators(normalized, issues);

            if (IsDocumentQuestion(question))
            {
                if (!normalized.Contains(" FROM FINDOC ")) issues.Add("Document intent must read its final business rows from FINDOC, not only from lookup/master tables.");
                if (!SelectClauseContainsColumn(normalized, "FINDOC")) issues.Add("Document intent must project document identity FINDOC.");
                if (!SelectClauseContainsColumn(normalized, "SOSOURCE")) issues.Add("Document intent must project document identity SOSOURCE for authoritative row links.");
                if (!SelectClauseContainsColumn(normalized, "FINCODE")) issues.Add("Document intent must project FINCODE.");
                if (!SelectClauseContainsColumn(normalized, "TRNDATE")) issues.Add("Document intent must project TRNDATE.");

                string findocAlias;
                string fprmsAlias;
                string seriesAlias;
                if (!TryReadFromAlias(sql, "FINDOC", out findocAlias)) issues.Add("Every FINDOC document SELECT must expose FINDOC as the source table.");
                if (!TryReadJoinAlias(sql, "FPRMS", out fprmsAlias)) issues.Add("Every FINDOC document SELECT must INNER JOIN FPRMS.");
                if (!TryReadJoinAlias(sql, "SERIES", out seriesAlias)) issues.Add("Every FINDOC document SELECT must INNER JOIN SERIES.");

                string fprmsJoin = ExtractJoinClause(normalized, "FPRMS");
                if (!string.IsNullOrWhiteSpace(findocAlias) && !string.IsNullOrWhiteSpace(fprmsAlias))
                    if (fprmsJoin == null || !JoinBinds(fprmsJoin, findocAlias, "FPRMS", fprmsAlias, "FPRMS"))
                        issues.Add("FPRMS join must bind FINDOC.FPRMS to FPRMS.FPRMS.");

                string seriesJoin = ExtractJoinClause(normalized, "SERIES");
                if (!string.IsNullOrWhiteSpace(seriesAlias) && !string.IsNullOrWhiteSpace(fprmsAlias))
                    if (seriesJoin == null || !JoinBinds(seriesJoin, seriesAlias, "FPRMS", fprmsAlias, "FPRMS"))
                        issues.Add("SERIES join must bind SERIES.FPRMS to FPRMS.FPRMS.");

                if (!string.IsNullOrWhiteSpace(seriesAlias) && !SelectClauseContainsExpression(sql, seriesAlias + ".NAME")) issues.Add("Document intent must project SERIES.NAME metadata.");
                if (!string.IsNullOrWhiteSpace(fprmsAlias) && !SelectClauseContainsExpression(sql, fprmsAlias + ".NAME")) issues.Add("Document intent must project FPRMS.NAME metadata.");
            }

            bool currentOperatorScope = string.Equals(operatorScope, "current_operator", StringComparison.OrdinalIgnoreCase);
            if (currentOperatorScope)
            {
                if (currentUserId <= 0) issues.Add("current_operator scope requires authenticated currentUserId.");
                else if (!Regex.IsMatch(normalized, @"\bINSUSER\s*=\s*" + currentUserId + @"\b", RegexOptions.CultureInvariant)) issues.Add("current_operator report must filter FINDOC.INSUSER to authenticated currentUserId.");
            }
            if (string.Equals(resultMode, "latest", StringComparison.OrdinalIgnoreCase))
            {
                if (!normalized.Contains("TOP 1")) issues.Add("Latest-document intent requires TOP 1.");
                if (!normalized.Contains(" ORDER BY ")) issues.Add("Latest-document intent requires ORDER BY.");
                if (currentOperatorScope)
                {
                    if (!ContainsOrderedColumn(normalized, "INSDATE", "DESC")) issues.Add("Latest current-operator document requires INSDATE DESC.");
                }
                else if (!ContainsOrderedColumn(normalized, "TRNDATE", "DESC")) issues.Add("Latest business document requires TRNDATE DESC.");
                if (!ContainsOrderedColumn(normalized, "FINDOC", "DESC")) issues.Add("Latest-document intent requires FINDOC DESC as deterministic tie-breaker.");
            }
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void ValidateRegisteredTraderRoleDiscriminators(string normalizedSql, List<string> issues)
        {
            if (string.IsNullOrWhiteSpace(normalizedSql) || issues == null) return;
            MatchCollection matches = Regex.Matches(normalizedSql, @"(?:\b[A-Z0-9_]+\.)?SODTYPE\s*=\s*(\d+)", RegexOptions.CultureInvariant);
            foreach (Match match in matches)
            {
                int sodType;
                if (!match.Success || match.Groups.Count < 2 || !int.TryParse(match.Groups[1].Value, out sodType)) continue;
                if (JarvisBusinessEntityCatalog.FindTraderRole(sodType) == null) issues.Add("SQL uses unregistered TRDR.SODTYPE=" + sodType + ". Entity discriminators must come from authoritative knowledge.");
            }
        }

        private static string[] ValidateQueryResultForQuestion(string question, string queryResult)
        {
            var issues = new List<string>();
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null) { issues.Add("query_data result has no rows array."); return issues.ToArray(); }
                if (IsSingularLatestQuestion(question) && rows.Count > 1) issues.Add("Singular latest intent returned more than one row despite validated TOP 1 SQL.");
                if (IsDocumentQuestion(question) && rows.Count > 0)
                {
                    JObject row = rows[0] as JObject;
                    if (row == null) issues.Add("Document result row is not an object.");
                    else
                    {
                        if (FindPropertyValue(row, "FINDOC") == null) issues.Add("Document result is missing FINDOC.");
                        if (FindPropertyValue(row, "SOSOURCE") == null) issues.Add("Document result is missing SOSOURCE required by the central addressable-link policy.");
                        if (FindPropertyValue(row, "FINCODE") == null) issues.Add("Document result is missing FINCODE.");
                        if (FindPropertyValue(row, "TRNDATE") == null) issues.Add("Document result is missing TRNDATE.");
                        if (!RowContainsMetadata(row, "FPRMS")) issues.Add("Document result is missing authoritative FPRMS metadata.");
                        if (!RowContainsMetadata(row, "SERIES")) issues.Add("Document result is missing SERIES subtype metadata.");
                    }
                }
            }
            catch (Exception ex) { issues.Add("query_data result is not a valid JSON envelope: " + ex.Message); }
            return issues.ToArray();
        }

        private static string NormalizeSql(string sql)
        {
            string value = (sql ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim().ToUpperInvariant();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return " " + value.Trim() + " ";
        }

        private static bool SelectClauseContainsColumn(string normalizedSql, string columnName)
        {
            if (string.IsNullOrWhiteSpace(normalizedSql) || string.IsNullOrWhiteSpace(columnName)) return false;
            int selectIndex = normalizedSql.IndexOf(" SELECT ", StringComparison.Ordinal);
            int fromIndex = normalizedSql.IndexOf(" FROM ", selectIndex < 0 ? 0 : selectIndex + 8, StringComparison.Ordinal);
            if (selectIndex < 0 || fromIndex <= selectIndex) return false;
            string projection = normalizedSql.Substring(selectIndex, fromIndex - selectIndex);
            return Regex.IsMatch(projection, @"(?:\b[A-Z0-9_]+\.)?" + Regex.Escape(columnName.ToUpperInvariant()) + @"\b", RegexOptions.CultureInvariant);
        }

        private static bool SelectClauseContainsExpression(string sql, string expression)
        {
            string value = sql ?? string.Empty;
            int selectIndex = value.IndexOf("SELECT ", StringComparison.OrdinalIgnoreCase);
            int fromIndex = value.IndexOf(" FROM ", selectIndex < 0 ? 0 : selectIndex + 7, StringComparison.OrdinalIgnoreCase);
            if (selectIndex < 0 || fromIndex <= selectIndex) return false;
            string projection = value.Substring(selectIndex, fromIndex - selectIndex);
            return projection.IndexOf(expression ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsOrderedColumn(string normalizedSql, string columnName, string direction)
        {
            int orderIndex = normalizedSql.IndexOf(" ORDER BY ", StringComparison.Ordinal);
            if (orderIndex < 0) return false;
            string orderClause = normalizedSql.Substring(orderIndex);
            return orderClause.Contains(columnName + " " + direction) || orderClause.Contains("." + columnName + " " + direction);
        }

        private static string ExtractJoinClause(string normalizedSql, string tableName)
        {
            Match table = Regex.Match(normalizedSql ?? string.Empty, @"\b(?:INNER\s+)?JOIN\s+" + Regex.Escape(tableName ?? string.Empty) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!table.Success) return null;
            int start = table.Index;
            int end = normalizedSql.Length;
            Match next = Regex.Match(normalizedSql.Substring(start + table.Length), @"\b(?:INNER\s+|LEFT\s+|RIGHT\s+|FULL\s+)?JOIN\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (next.Success) end = start + table.Length + next.Index;
            foreach (string marker in new[] { " WHERE ", " ORDER BY ", " GROUP BY " })
            {
                int i = normalizedSql.IndexOf(marker, start + table.Length, StringComparison.Ordinal);
                if (i >= 0 && i < end) end = i;
            }
            return normalizedSql.Substring(start, end - start);
        }

        private static JToken FindPropertyValue(JObject row, string propertyName)
        {
            if (row == null || string.IsNullOrWhiteSpace(propertyName)) return null;
            JProperty property = row.Properties().FirstOrDefault(x => string.Equals(x.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            return property == null ? null : property.Value;
        }

        private static bool RowContainsMetadata(JObject row, string token)
        {
            return row != null && row.Properties().Any(x => x.Name.IndexOf(token ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0 && x.Value != null && x.Value.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(x.Value.ToString()));
        }

        private static bool TryApplyDocumentScopePredicate(string sql, string documentScope, out string constrainedSql, out string issue)
        {
            constrainedSql = sql ?? string.Empty;
            issue = string.Empty;
            if (string.IsNullOrWhiteSpace(constrainedSql)) { issue = "Structured document_scope cannot be enforced on empty SQL."; return false; }

            string findocAlias;
            string fprmsAlias;
            string seriesAlias;
            if (!TryReadFromAlias(constrainedSql, "FINDOC", out findocAlias)) { issue = "Specific document_scope requires FINDOC as document source."; return false; }
            if (!TryReadJoinAlias(constrainedSql, "FPRMS", out fprmsAlias)) { issue = "Specific document_scope requires INNER JOIN FPRMS ON FINDOC.FPRMS=FPRMS.FPRMS."; return false; }
            if (!TryReadJoinAlias(constrainedSql, "SERIES", out seriesAlias)) { issue = "Specific document_scope requires INNER JOIN SERIES ON SERIES.FPRMS=FPRMS.FPRMS."; return false; }

            string normalized = NormalizeSql(constrainedSql);
            string fprmsJoin = ExtractJoinClause(normalized, "FPRMS");
            if (fprmsJoin == null || !JoinBinds(fprmsJoin, findocAlias, "FPRMS", fprmsAlias, "FPRMS")) { issue = "FPRMS must be joined with FINDOC.FPRMS=FPRMS.FPRMS."; return false; }
            string seriesJoin = ExtractJoinClause(normalized, "SERIES");
            if (seriesJoin == null || !JoinBinds(seriesJoin, seriesAlias, "FPRMS", fprmsAlias, "FPRMS")) { issue = "SERIES must be joined with SERIES.FPRMS=FPRMS.FPRMS."; return false; }

            string predicate;
            if (!JarvisDocumentScopeValidator.TryBuildDocumentSqlPredicate(documentScope, seriesAlias, fprmsAlias, out predicate) || string.IsNullOrWhiteSpace(predicate)) { issue = "No deterministic FPRMS predicate is registered for document_scope='" + documentScope + "'."; return false; }

            int insertion = FindClauseInsertionPoint(constrainedSql);
            string head = insertion < constrainedSql.Length ? constrainedSql.Substring(0, insertion).TrimEnd() : constrainedSql.TrimEnd();
            string tail = insertion < constrainedSql.Length ? constrainedSql.Substring(insertion) : string.Empty;
            bool hasWhere = Regex.IsMatch(head, @"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            constrainedSql = head + (hasWhere ? " AND (" : " WHERE (") + predicate + ")" + tail;
            return true;
        }

        private static bool TryReadFromAlias(string sql, string tableName, out string alias)
        {
            alias = string.Empty;
            Match match = Regex.Match(sql ?? string.Empty, @"\bFROM\s+" + Regex.Escape(tableName ?? string.Empty) + @"(?:\s+AS)?(?:\s+(?<alias>[A-Z_][A-Z0-9_]*))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : tableName;
            return !string.IsNullOrWhiteSpace(alias);
        }

        private static bool TryReadJoinAlias(string sql, string tableName, out string alias)
        {
            alias = string.Empty;
            Match match = Regex.Match(sql ?? string.Empty, @"\b(?:INNER\s+)?JOIN\s+" + Regex.Escape(tableName ?? string.Empty) + @"(?:\s+AS)?(?:\s+(?<alias>[A-Z_][A-Z0-9_]*))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : tableName;
            return !string.IsNullOrWhiteSpace(alias);
        }

        private static bool JoinBinds(string joinClause, string leftAlias, string leftField, string rightAlias, string rightField)
        {
            string clause = NormalizeSql(joinClause);
            string left = Regex.Escape((leftAlias ?? string.Empty).ToUpperInvariant()) + @"\." + Regex.Escape((leftField ?? string.Empty).ToUpperInvariant());
            string right = Regex.Escape((rightAlias ?? string.Empty).ToUpperInvariant()) + @"\." + Regex.Escape((rightField ?? string.Empty).ToUpperInvariant());
            return Regex.IsMatch(clause, @"\b" + left + @"\s*=\s*" + right + @"\b|\b" + right + @"\s*=\s*" + left + @"\b", RegexOptions.CultureInvariant);
        }

        private static int FindClauseInsertionPoint(string sql)
        {
            int result = (sql ?? string.Empty).Length;
            foreach (string marker in new[] { " GROUP BY ", " HAVING ", " ORDER BY ", ";" })
            {
                int index = (sql ?? string.Empty).IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && index < result) result = index;
            }
            return result;
        }

        private static string BuildValidationSql(string sql)
        {
            string value = (sql ?? string.Empty).Trim();
            if (value.Length == 0) return value;
            if (value.StartsWith("SELECT DISTINCT ", StringComparison.OrdinalIgnoreCase)) return "SELECT DISTINCT TOP 200 " + value.Substring("SELECT DISTINCT ".Length);
            if (value.StartsWith("SELECT TOP ", StringComparison.OrdinalIgnoreCase)) return value;
            if (value.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)) return "SELECT TOP 200 " + value.Substring("SELECT ".Length);
            return value;
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

        private static bool LooksLikeQueryError(string queryResult)
        {
            string value = (queryResult ?? string.Empty).TrimStart();
            return value.StartsWith("Σφάλμα:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeQueryResultForQuestion(string question, string queryResult)
        {
            if (!IsSingularLatestQuestion(question)) return queryResult;
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null || rows.Count <= 1) return queryResult;
                var normalizedRows = new JArray();
                if (rows[0] != null) normalizedRows.Add(rows[0].DeepClone());
                root["rows"] = normalizedRows;
                root["rowCount"] = normalizedRows.Count;
                root["totalRowCount"] = normalizedRows.Count;
                root["truncated"] = false;
                return root.ToString(Formatting.None);
            }
            catch { return queryResult; }
        }

        private static bool IsSingularLatestQuestion(string question)
        {
            string value = (question ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) return false;
            return value.Contains("πιο πρόσφατ") || value.Contains("πιο προσφατ") || value.Contains("τελευταίο") || value.Contains("τελευταιο") || value.Contains("τελευταία εγγραφή") || value.Contains("τελευταια εγγραφη") || value.Contains("latest") || value.Contains("most recent");
        }

        private static bool IsDocumentQuestion(string question)
        {
            string value = (question ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) return false;
            return value.Contains("παραστατικ") || value.Contains("τιμολόγ") || value.Contains("τιμολογ") || value.Contains("document") || value.Contains("voucher") || value.Contains("invoice");
        }

        private static string BuildDeterministicSummary(string question, string queryResult)
        {
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                if (rows == null) return queryResult.Trim();
                int totalRowCount = (int?)root["totalRowCount"] ?? rows.Count;
                if (rows.Count == 0) return "Δεν βρέθηκαν δεδομένα για: " + (question ?? string.Empty);
                var sb = new StringBuilder();
                sb.Append("Αποτέλεσμα για: ").Append(question ?? string.Empty).AppendLine();
                sb.Append("Εγγραφές: ").Append(totalRowCount).AppendLine();
                int take = Math.Min(rows.Count, 10);
                for (int i = 0; i < take; i++)
                {
                    JObject row = rows[i] as JObject;
                    if (row == null) continue;
                    if (take > 1) sb.Append("#").Append(i + 1).Append(": ");
                    bool first = true;
                    foreach (JProperty property in row.Properties())
                    {
                        if (!first) sb.Append(" | ");
                        first = false;
                        sb.Append(property.Name).Append(": ");
                        if (property.Value == null || property.Value.Type == JTokenType.Null) sb.Append("-");
                        else sb.Append(property.Value.ToString(Formatting.None).Trim('"'));
                    }
                    sb.AppendLine();
                }
                if (totalRowCount > take) sb.Append("... και άλλες ").Append(totalRowCount - take).Append(" εγγραφές.");
                return sb.ToString().Trim();
            }
            catch { return queryResult.Trim(); }
        }

        private static string DescribeQueryResult(string queryResult)
        {
            if (string.IsNullOrWhiteSpace(queryResult)) return "chars=0 rowCount=<unknown> columns=[] preview=<empty>";
            try
            {
                JObject root = JObject.Parse(queryResult);
                JArray rows = root["rows"] as JArray;
                int rowCount = (int?)root["totalRowCount"] ?? (int?)root["rowCount"] ?? (rows == null ? 0 : rows.Count);
                string[] columns = rows == null ? new string[0] : rows.OfType<JObject>().Take(1).SelectMany(x => x.Properties().Select(p => p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return "chars=" + queryResult.Length + " rowCount=" + rowCount + " columns=[" + string.Join(",", columns) + "] preview=" + OneLine(Truncate(queryResult, 1200));
            }
            catch { return "chars=" + queryResult.Length + " rowCount=<unparsed> columns=[] preview=" + OneLine(Truncate(queryResult, 1200)); }
        }

        private static string ReadRequiredInternalContext(JObject inputs, string name)
        {
            string value = inputs == null || inputs[name] == null ? string.Empty : inputs[name].ToString();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Jarvis dispatch is missing required internal context: " + name);
            return value;
        }

        private static string OneLine(string value) { return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim(); }
        private static string Truncate(string value, int maxChars) { string text = value ?? string.Empty; if (maxChars <= 0 || text.Length <= maxChars) return text; return text.Substring(0, maxChars) + "..."; }
        private static void EnsureSuccess(S1Jarvis.Core.AgentProxyResponse response, string fallback)
        {
            if (response == null || !response.Success) throw new InvalidOperationException(response != null && !string.IsNullOrWhiteSpace(response.ErrorMessage) ? response.ErrorMessage : fallback);
        }
    }
}

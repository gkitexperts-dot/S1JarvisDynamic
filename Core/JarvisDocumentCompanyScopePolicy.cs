using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Central deterministic company-scope policy for FINDOC document queries.
    /// The authenticated Soft1 runtime company is authoritative. Document SQL is
    /// rewritten before execution so FINDOC is always company-scoped; FPRMS and
    /// SERIES receive the same company predicate when their runtime schema exposes
    /// a COMPANY column. This avoids cross-company metadata/classification leaks
    /// without assuming that a master-table id is globally unique.
    /// </summary>
    internal static class JarvisDocumentCompanyScopePolicy
    {
        internal sealed class Scope
        {
            internal int CompanyId { get; set; }
            internal bool FprmsHasCompany { get; set; }
            internal bool SeriesHasCompany { get; set; }
        }

        internal static Scope Resolve(XSupport xSupport)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null || xSupport.ConnectionInfo.CompanyId <= 0)
                throw new InvalidOperationException("Document query requires an authenticated current Soft1 company.");

            return new Scope
            {
                CompanyId = xSupport.ConnectionInfo.CompanyId,
                FprmsHasCompany = HasColumn(xSupport, "FPRMS", "COMPANY"),
                SeriesHasCompany = HasColumn(xSupport, "SERIES", "COMPANY")
            };
        }

        internal static string BuildPlanningDirective(Scope scope)
        {
            if (scope == null || scope.CompanyId <= 0)
                throw new InvalidOperationException("Current company scope is missing.");

            var parts = new List<string>
            {
                "FINDOC.COMPANY=" + scope.CompanyId
            };
            if (scope.FprmsHasCompany) parts.Add("FPRMS.COMPANY=" + scope.CompanyId);
            if (scope.SeriesHasCompany) parts.Add("SERIES.COMPANY=" + scope.CompanyId);

            return "[JARVIS_CURRENT_COMPANY_SCOPE] currentCompanyId=" + scope.CompanyId +
                   "; requiredPredicates=" + string.Join(",", parts.ToArray()) +
                   "; these predicates are mandatory for every FINDOC document query.";
        }

        internal static string Apply(
            string sql,
            Scope scope,
            string findocAlias,
            string fprmsAlias,
            string seriesAlias)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new InvalidOperationException("Cannot apply current-company scope to empty SQL.");
            if (scope == null || scope.CompanyId <= 0)
                throw new InvalidOperationException("Current company scope is missing.");
            if (string.IsNullOrWhiteSpace(findocAlias))
                throw new InvalidOperationException("Current-company scope requires FINDOC source alias.");

            var predicates = new List<string>
            {
                findocAlias + ".COMPANY=" + scope.CompanyId
            };
            if (scope.FprmsHasCompany)
            {
                if (string.IsNullOrWhiteSpace(fprmsAlias))
                    throw new InvalidOperationException("FPRMS exposes COMPANY but the document query has no FPRMS join.");
                predicates.Add(fprmsAlias + ".COMPANY=" + scope.CompanyId);
            }
            if (scope.SeriesHasCompany)
            {
                if (string.IsNullOrWhiteSpace(seriesAlias))
                    throw new InvalidOperationException("SERIES exposes COMPANY but the document query has no SERIES join.");
                predicates.Add(seriesAlias + ".COMPANY=" + scope.CompanyId);
            }

            string result = sql;
            foreach (string predicate in predicates)
            {
                if (ContainsPredicate(result, predicate)) continue;
                result = AppendWherePredicate(result, predicate);
            }
            return result;
        }

        internal static string[] Validate(
            string sql,
            Scope scope,
            string findocAlias,
            string fprmsAlias,
            string seriesAlias)
        {
            var issues = new List<string>();
            if (scope == null || scope.CompanyId <= 0)
            {
                issues.Add("Document SQL has no authenticated current-company scope.");
                return issues.ToArray();
            }

            Require(sql, findocAlias, scope.CompanyId, "FINDOC", issues);
            if (scope.FprmsHasCompany) Require(sql, fprmsAlias, scope.CompanyId, "FPRMS", issues);
            if (scope.SeriesHasCompany) Require(sql, seriesAlias, scope.CompanyId, "SERIES", issues);
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void Require(string sql, string alias, int companyId, string table, List<string> issues)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                issues.Add(table + " company scope cannot be verified because its SQL alias is missing.");
                return;
            }
            string pattern = @"\b" + Regex.Escape(alias) + @"\.COMPANY\s*=\s*" + companyId + @"\b";
            string reverse = @"\b" + companyId + @"\s*=\s*" + Regex.Escape(alias) + @"\.COMPANY\b";
            if (!Regex.IsMatch(sql ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                !Regex.IsMatch(sql ?? string.Empty, reverse, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                issues.Add(table + ".COMPANY must be constrained to authenticated currentCompanyId=" + companyId + ".");
        }

        private static bool ContainsPredicate(string sql, string predicate)
        {
            string normalizedSql = Regex.Replace((sql ?? string.Empty).ToUpperInvariant(), @"\s+", string.Empty);
            string normalizedPredicate = Regex.Replace((predicate ?? string.Empty).ToUpperInvariant(), @"\s+", string.Empty);
            return normalizedSql.Contains(normalizedPredicate);
        }

        private static string AppendWherePredicate(string sql, string predicate)
        {
            int insertion = FindClauseInsertionPoint(sql);
            string head = insertion < sql.Length ? sql.Substring(0, insertion).TrimEnd() : sql.TrimEnd();
            string tail = insertion < sql.Length ? sql.Substring(insertion) : string.Empty;
            bool hasWhere = Regex.IsMatch(head, @"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return head + (hasWhere ? " AND " : " WHERE ") + predicate + tail;
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

        private static bool HasColumn(XSupport xSupport, string tableName, string columnName)
        {
            string safeTable = (tableName ?? string.Empty).Replace("'", "''");
            string safeColumn = (columnName ?? string.Empty).Replace("'", "''");
            XTable table = xSupport.GetSQLDataSet(
                "SELECT TOP 1 1 AS X FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='" +
                safeTable + "' AND COLUMN_NAME='" + safeColumn + "'");
            return table != null && table.Count > 0;
        }
    }
}

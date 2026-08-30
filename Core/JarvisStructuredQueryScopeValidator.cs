using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic enforcement for structured semantic constraints that must be
    /// visible in the actual SQL produced by Atlas. This validator never infers
    /// intent from natural-language wording; it consumes canonical orchestration
    /// fields and the verified query trace only.
    /// </summary>
    internal static class JarvisStructuredQueryScopeValidator
    {
        internal static string[] Validate(string sql, string entityRole, string operatorScope, int currentUserId)
        {
            var issues = new List<string>();
            string normalized = Normalize(sql);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                issues.Add("Structured query validation has no verified SQL trace.");
                return issues.ToArray();
            }

            if (!string.IsNullOrWhiteSpace(entityRole))
            {
                JarvisTraderRoleDescriptor role = JarvisBusinessEntityCatalog.FindTraderRole(entityRole);
                if (role == null)
                {
                    issues.Add("Unknown structured entity_role='" + entityRole + "'.");
                }
                else if (!Regex.IsMatch(
                    normalized,
                    @"(?:\b[A-Z0-9_]+\.)?SODTYPE\s*=\s*" + role.SodType + @"\b",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                {
                    issues.Add("Verified SQL does not enforce entity_role='" + role.Role +
                               "' through registered TRDR.SODTYPE=" + role.SodType + ".");
                }
            }

            if (string.Equals(operatorScope, "current_operator", StringComparison.OrdinalIgnoreCase))
            {
                if (currentUserId <= 0)
                {
                    issues.Add("current_operator scope has no authenticated currentUserId.");
                }
                else if (!Regex.IsMatch(
                    normalized,
                    @"(?:\b[A-Z0-9_]+\.)?INSUSER\s*=\s*" + currentUserId + @"\b",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                {
                    issues.Add("Verified SQL does not enforce current_operator through FINDOC.INSUSER=" + currentUserId + ".");
                }
            }

            return issues.ToArray();
        }

        private static string Normalize(string sql)
        {
            return Regex.Replace(sql ?? string.Empty, @"\s+", " ").Trim();
        }
    }
}

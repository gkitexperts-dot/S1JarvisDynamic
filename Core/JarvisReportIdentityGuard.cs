using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic ambiguity guard for report entity references. If the same
    /// explicit trader identity exists in more than one registered business role
    /// and semantic decomposition did not resolve entity_role, Jarvis must ask
    /// instead of silently selecting a role from conversational history.
    /// </summary>
    internal static class JarvisReportIdentityGuard
    {
        internal static string GetAmbiguityMessage(XSupport xSupport, JObject reportInputs)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null || reportInputs == null)
                return null;

            string reference = ReadString(reportInputs, "entity_reference");
            string explicitRole = ReadString(reportInputs, "entity_role");
            if (string.IsNullOrWhiteSpace(reference) || !string.IsNullOrWhiteSpace(explicitRole))
                return null;

            try
            {
                int company = xSupport.ConnectionInfo.CompanyId;
                XTable table = xSupport.GetSQLDataSet(
                    "SELECT TOP 20 TRDR,SODTYPE,CODE,NAME FROM TRDR WHERE COMPANY=:1 AND (CODE=:2 OR NAME=:2)",
                    company, reference.Trim());
                if (table == null || table.Count == 0) return null;

                var roles = new HashSet<int>();
                var roleNames = new List<string>();
                table.First();
                for (int i = 0; i < table.Count; i++)
                {
                    int sodType;
                    if (int.TryParse(Convert.ToString(table.Current["SODTYPE"]), out sodType))
                    {
                        JarvisTraderRoleDescriptor descriptor = JarvisBusinessEntityCatalog.FindTraderRole(sodType);
                        if (descriptor != null && roles.Add(sodType)) roleNames.Add(descriptor.Role);
                    }
                    table.Next();
                }

                if (roles.Count <= 1) return null;
                return "Ο συναλλασσόμενος '" + reference.Trim() +
                       "' υπάρχει σε περισσότερους από έναν business roles (" +
                       string.Join(", ", roleNames.Distinct(StringComparer.OrdinalIgnoreCase)) +
                       "). Διευκρίνισε τον ρόλο πριν συνεχίσω.";
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-IDENTITY] ambiguity guard skipped: " + ex.Message);
                return null;
            }
        }

        private static string ReadString(JObject obj, string name)
        {
            return obj[name] == null || obj[name].Type == JTokenType.Null ? string.Empty : obj[name].ToString();
        }
    }
}

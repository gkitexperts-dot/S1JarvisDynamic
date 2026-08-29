using System;
using System.Collections.Generic;
using System.Data;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Safe rollout gate for the new semantic/dynamic routing methodology.
    ///
    /// ParamCode 500060 (Jarvis New Routing Pilot Users)
    /// ParamValueString: comma/semicolon/whitespace separated Soft1 USER ids.
    ///
    /// Fail-closed-to-legacy policy:
    /// - parameter missing => legacy routing
    /// - parameter inactive => legacy routing
    /// - text empty => legacy routing
    /// - current USER id absent => legacy routing
    /// - any read/parse error => legacy routing
    /// </summary>
    internal static class JarvisRoutingFeatureGate
    {
        internal const int ParamCode = 500060;

        internal static bool UseNewRouting(XSupport xSupport)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                return false;

            int userId = xSupport.ConnectionInfo.UserId;
            if (userId <= 0)
                return false;

            try
            {
                const string sql = @"
SELECT TOP 1
    ParamValueString,
    paramsIsActive
FROM cccParams
WHERE ParamCode = :1
ORDER BY cccParams DESC;";

                XTable table = xSupport.GetSQLDataSet(sql, ParamCode);
                DataTable data = table == null ? null : table.CreateDataTable(true);
                if (data == null || data.Rows.Count == 0)
                    return false;

                DataRow row = data.Rows[0];
                bool isActive = row["paramsIsActive"] != DBNull.Value &&
                    Convert.ToInt32(row["paramsIsActive"]) == 1;
                if (!isActive)
                    return false;

                string raw = row["ParamValueString"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(row["ParamValueString"]);
                if (string.IsNullOrWhiteSpace(raw))
                    return false;

                HashSet<int> pilotUsers = ParseUserIds(raw);
                bool enabled = pilotUsers.Contains(userId);

                DebugLog.Log("[ROUTING-GATE] Param 500060 user=" + userId +
                    " mode=" + (enabled ? "NEW" : "LEGACY"));
                return enabled;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ROUTING-GATE] Param 500060 unavailable; LEGACY routing remains active: " + ex.Message);
                return false;
            }
        }

        internal static string DescribeMode(XSupport xSupport)
        {
            return UseNewRouting(xSupport) ? "NEW" : "LEGACY";
        }

        private static HashSet<int> ParseUserIds(string raw)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            string[] parts = raw.Split(
                new[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                int userId;
                if (int.TryParse(part.Trim(), out userId) && userId > 0)
                    result.Add(userId);
            }

            return result;
        }
    }
}

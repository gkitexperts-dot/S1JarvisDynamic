using System;
using System.Collections.Generic;
using System.Data;
using Softone;

namespace S1Jarvis.Core
{
    internal static class JarvisAuthorization
    {
        public const int AdminsParamCode = 500036;

        public static bool IsCurrentUserAdmin(XSupport xSupport)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                return false;

            int userId = xSupport.ConnectionInfo.UserId;
            HashSet<int> admins = GetAdminUserIds(xSupport);
            bool isAdmin = admins.Contains(userId);
            DebugLog.Log("[JARVIS-AUTH] resolved userId=" + userId +
                " admin=" + (isAdmin ? "true" : "false") +
                " adminCount=" + admins.Count +
                " ParamCode=" + AdminsParamCode);
            return isAdmin;
        }

        public static HashSet<int> GetAdminUserIds(XSupport xSupport)
        {
            var result = new HashSet<int>();
            if (xSupport == null) return result;

            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT ParamValueString FROM cccParams WHERE ParamCode=:1",
                    AdminsParamCode);

                if (t == null || t.Count == 0)
                {
                    DebugLog.Log("[JARVIS-AUTH] admins parameter missing; ParamCode=" + AdminsParamCode);
                    return result;
                }

                DataTable dt = t.CreateDataTable(true);
                if (dt == null || dt.Rows.Count == 0 || !dt.Columns.Contains("ParamValueString"))
                {
                    DebugLog.Log("[JARVIS-AUTH] admins parameter returned no readable ParamValueString; ParamCode=" + AdminsParamCode);
                    return result;
                }

                object rawValue = dt.Rows[0]["ParamValueString"];
                string raw = rawValue == null || rawValue == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(rawValue);

                DebugLog.Log("[JARVIS-AUTH] admins raw='" + (raw ?? string.Empty) + "' ParamCode=" + AdminsParamCode);

                foreach (string part in (raw ?? string.Empty).Split(','))
                {
                    int id;
                    if (int.TryParse((part ?? string.Empty).Trim(), out id) && id > 0)
                        result.Add(id);
                }
            }
            catch (Exception ex)
            {
                // Authorization is fail-closed. If the parameter cannot be read,
                // nobody gains admin rights accidentally.
                DebugLog.Log("[JARVIS-AUTH] admins parameter read failed; admin=false; error=" + ex.Message);
            }

            return result;
        }

        public static void DemandCurrentUserAdmin(XSupport xSupport)
        {
            if (!IsCurrentUserAdmin(xSupport))
                throw new InvalidOperationException("Ο τρέχων χρήστης δεν έχει δικαιώματα Jarvis Admin.");
        }
    }
}

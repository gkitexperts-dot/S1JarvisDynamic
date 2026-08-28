using System;
using System.Collections.Generic;
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
            return GetAdminUserIds(xSupport).Contains(userId);
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

                object rawValue = t.Current["ParamValueString"];
                string raw = rawValue == null || rawValue == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(rawValue);

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

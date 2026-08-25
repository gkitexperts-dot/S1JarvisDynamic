using System;
using System.Data;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic AFM lookup for DR. Returns every active TRDR role for the AFM
    /// instead of an arbitrary TOP 1 record. No write is performed.
    /// </summary>
    internal static class DrTraderRoleResolver
    {
        public static JObject Resolve(XSupport xSupport, string afm)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            afm = NormalizeAfm(afm);
            if (string.IsNullOrWhiteSpace(afm))
                return new JObject { ["success"] = false, ["reason"] = "missing_afm", ["matches"] = new JArray() };

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable raw = xSupport.GetSQLDataSet(
                "SELECT TRDR,CODE,NAME,AFM,SODTYPE FROM TRDR " +
                "WHERE COMPANY=:1 AND AFM=:2 AND ISACTIVE=1 ORDER BY SODTYPE,TRDR",
                company, afm);

            DataTable table = raw != null ? raw.CreateDataTable(true) : null;
            var matches = new JArray();
            JObject preferredIncoming = null;
            int incomingCount = 0;

            if (table != null)
            {
                foreach (DataRow row in table.Rows)
                {
                    int sodType = ToInt(row["SODTYPE"]);
                    var item = new JObject
                    {
                        ["trdrId"] = ToInt(row["TRDR"]),
                        ["code"] = ToString(row["CODE"]),
                        ["name"] = ToString(row["NAME"]),
                        ["afm"] = ToString(row["AFM"]),
                        ["sodType"] = sodType,
                        ["role"] = RoleName(sodType),
                        ["incomingCandidate"] = sodType == 12 || sodType == 16,
                        ["outgoingCandidate"] = sodType == 13 || sodType == 15
                    };
                    matches.Add(item);

                    if (sodType == 12 || sodType == 16)
                    {
                        incomingCount++;
                        // Supplier is preferred over creditor for invoice/expense ingestion.
                        if (preferredIncoming == null || (int)preferredIncoming["sodType"] != 12 && sodType == 12)
                            preferredIncoming = item;
                    }
                }
            }

            return new JObject
            {
                ["success"] = true,
                ["afm"] = afm,
                ["found"] = matches.Count > 0,
                ["matches"] = matches,
                ["hasSupplier"] = HasRole(matches, 12),
                ["hasCustomer"] = HasRole(matches, 13),
                ["hasDebtor"] = HasRole(matches, 15),
                ["hasCreditor"] = HasRole(matches, 16),
                ["incomingAmbiguous"] = incomingCount > 1,
                ["preferredIncoming"] = preferredIncoming
            };
        }

        private static bool HasRole(JArray matches, int sodType)
        {
            foreach (JObject x in matches)
                if ((int?)x["sodType"] == sodType) return true;
            return false;
        }

        private static string RoleName(int sodType)
        {
            switch (sodType)
            {
                case 12: return "Supplier";
                case 13: return "Customer";
                case 15: return "Debtor";
                case 16: return "Creditor";
                default: return "Other";
            }
        }

        private static string NormalizeAfm(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string s = value.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
            if (s.StartsWith("EL") || s.StartsWith("GR")) s = s.Substring(2);
            return s;
        }

        private static int ToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            int x; return int.TryParse(Convert.ToString(value), out x) ? x : 0;
        }

        private static string ToString(object value)
        {
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }
    }
}

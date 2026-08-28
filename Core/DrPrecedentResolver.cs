using System;
using System.Data;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Reads the actual posting of one operator-selected / historical precedent.
    /// Read-only: no Soft1 write and no CCCMAPITEMS learning occurs here.
    /// </summary>
    internal static class DrPrecedentResolver
    {
        public static JObject Resolve(XSupport xSupport, int trdrId, int findocId)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (trdrId <= 0 || findocId <= 0) return Failure("missing_coordinates");

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable headerRaw = xSupport.GetSQLDataSet(
                "SELECT TOP 1 FINDOC,FINCODE,TRNDATE,TRDR,SOSOURCE,SERIES,BUSUNITS " +
                "FROM FINDOC WHERE COMPANY=:1 AND FINDOC=:2 AND TRDR=:3",
                company, findocId, trdrId);
            DataTable header = headerRaw != null ? headerRaw.CreateDataTable(true) : null;
            if (header == null || header.Rows.Count == 0) return Failure("precedent_not_found_for_trader");

            DataRow h = header.Rows[0];
            XTable linesRaw = xSupport.GetSQLDataSet(
                "SELECT L.MTRL,L.QTY1,L.PRICE,M.CODE,M.NAME,M.SODTYPE " +
                "FROM MTRLINES L INNER JOIN MTRL M ON M.COMPANY=L.COMPANY AND M.MTRL=L.MTRL " +
                "WHERE L.COMPANY=:1 AND L.FINDOC=:2 ORDER BY L.LINENUM",
                company, findocId);
            DataTable lines = linesRaw != null ? linesRaw.CreateDataTable(true) : null;
            var arr = new JArray();
            if (lines != null)
            {
                foreach (DataRow r in lines.Rows)
                {
                    int sodtype = ToInt(r["SODTYPE"]);
                    arr.Add(new JObject
                    {
                        ["mtrlId"] = ToInt(r["MTRL"]),
                        ["code"] = ToText(r["CODE"]),
                        ["name"] = ToText(r["NAME"]),
                        ["sodtype"] = sodtype,
                        ["sodtypeName"] = SodtypeName(sodtype),
                        ["qty1"] = ToDecimalToken(r["QTY1"]),
                        ["price"] = ToDecimalToken(r["PRICE"])
                    });
                }
            }

            bool consolidated = arr.Count == 1;
            return new JObject
            {
                ["success"] = true,
                ["resolver"] = "resolve_historical_precedent",
                ["version"] = 1,
                ["readOnly"] = true,
                ["findocId"] = findocId,
                ["fincode"] = ToText(h["FINCODE"]),
                ["trdrId"] = ToInt(h["TRDR"]),
                ["sosource"] = ToInt(h["SOSOURCE"]),
                ["series"] = ToInt(h["SERIES"]),
                ["busunits"] = ToInt(h["BUSUNITS"]),
                ["postedLineCount"] = arr.Count,
                ["postingMode"] = consolidated ? "Consolidated" : "Detailed",
                ["canProposeSingleTarget"] = consolidated && arr.Count == 1,
                ["singleTarget"] = consolidated && arr.Count == 1 ? arr[0] : null,
                ["lines"] = arr
            };
        }

        private static JObject Failure(string reason) => new JObject
        {
            ["success"] = false,
            ["resolver"] = "resolve_historical_precedent",
            ["version"] = 1,
            ["readOnly"] = true,
            ["reason"] = reason,
            ["lines"] = new JArray()
        };

        private static string SodtypeName(int sodtype)
        {
            switch (sodtype)
            {
                case 51: return "Item";
                case 52: return "Service";
                case 53: return "Expense";
                default: return "Unknown";
            }
        }

        private static int ToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            int result;
            return int.TryParse(Convert.ToString(value), out result) ? result : 0;
        }

        private static string ToText(object value)
        {
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private static JToken ToDecimalToken(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            decimal result;
            return decimal.TryParse(Convert.ToString(value), out result) ? JToken.FromObject(result) : JToken.FromObject(Convert.ToString(value));
        }
    }
}

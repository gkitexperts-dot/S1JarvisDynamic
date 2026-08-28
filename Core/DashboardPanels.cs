using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    public static class DashboardPanels
    {
        public const int FirstParamCode = 500040;
        public const int LastParamCode = 500059;

        public const string AiUsageTodayMode = "__AI_USAGE_TODAY__";
        public const string AiUsage30DaysMode = "__AI_USAGE_30D__";
        public const string AiUsagePayloadPrefix = "@@JARVIS_AI_USAGE@@";

        private static readonly Dictionary<int, string> DefaultTitles = new Dictionary<int, string>
        {
            [500040] = "Top 10 πελάτες με τζίρο",
            [500041] = "Top 10 προϊόντα σε τεμάχια",
            [500042] = "Top 10 προϊόντα με τζίρο",
            [500043] = "Τρέχουσες τιμές ανά προϊόν",
        };

        private static string ChartTypeFromNumber(int n)
        {
            switch (n)
            {
                case 2: return "line";
                case 3: return "pie";
                case 4: return "doughnut";
                default: return "bar";
            }
        }

        public static string BuildDashboardText(XSupport xSupport, string date)
        {
            if (string.Equals(date, AiUsageTodayMode, StringComparison.Ordinal))
                return BuildAiUsageTodayPayload(xSupport);

            if (string.Equals(date, AiUsage30DaysMode, StringComparison.Ordinal))
                return BuildAiUsage30DaysPayload(xSupport);

            return BuildCommercialDashboardText(xSupport, date);
        }

        private static string BuildCommercialDashboardText(XSupport xSupport, string date)
        {
            var sb = new StringBuilder();
            int panelsRendered = 0;

            for (int code = FirstParamCode; code <= LastParamCode; code++)
            {
                string sql;
                int chartTypeNum;
                if (!TryReadPanelParam(xSupport, code, out sql, out chartTypeNum))
                    continue;
                if (string.IsNullOrWhiteSpace(sql))
                    continue;

                DataTable dt;
                try
                {
                    XTable result = xSupport.GetSQLDataSet(sql, date);
                    dt = result.CreateDataTable(true);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[DashboardPanels] panel {code} SQL EXCEPTION: {ex.Message}");
                    continue;
                }

                if (dt.Rows.Count == 0 || dt.Columns.Count < 2)
                    continue;

                var labels = new JArray();
                foreach (DataRow row in dt.Rows)
                    labels.Add(row[0] == DBNull.Value ? "" : Convert.ToString(row[0]));

                var datasets = new JArray();
                for (int col = 1; col < dt.Columns.Count; col++)
                {
                    var data = new JArray();
                    foreach (DataRow row in dt.Rows)
                    {
                        object v = row[col];
                        data.Add(v == DBNull.Value ? 0.0 : Convert.ToDouble(v));
                    }
                    datasets.Add(new JObject
                    {
                        ["label"] = dt.Columns[col].ColumnName,
                        ["data"] = data
                    });
                }

                string title;
                DefaultTitles.TryGetValue(code, out title);

                var spec = new JObject
                {
                    ["type"] = ChartTypeFromNumber(chartTypeNum),
                    ["title"] = title ?? "",
                    ["labels"] = labels,
                    ["datasets"] = datasets
                };

                sb.Append("```chart\n").Append(spec.ToString(Formatting.None)).Append("\n```\n");
                panelsRendered++;
            }

            return panelsRendered > 0 ? sb.ToString() : null;
        }

        private static string BuildAiUsageTodayPayload(XSupport xSupport)
        {
            UsageIdentity identity = GetUsageIdentity(xSupport);

            // Soft1 XTable metadata does not reliably expose SQL aggregate
            // expressions (COUNT/SUM) unless the final projected expression has
            // an explicit classic type. Without these CASTs CreateDataTable()
            // reports field type 0 (for example "Calls") and throws.
            const string sql = @"
SET NOCOUNT ON;
DECLARE @Serial varchar(30) = :1;
DECLARE @UserId int = :2;
DECLARE @CanSeeAll bit = CASE WHEN @UserId IN (1,262) THEN 1 ELSE 0 END;
DECLARE @Today date = CONVERT(date, GETDATE());

SELECT
    L.CCCUSERID AS UserId,
    ISNULL(MAX(U.NAME), 'User ' + CONVERT(varchar(20), L.CCCUSERID)) AS UserName,
    ISNULL(L.CCCAGENT, '') AS Agent,
    ISNULL(L.CCCPROVIDER, '') AS Provider,
    ISNULL(L.CCCMODEL, '') AS Model,
    CAST(COUNT(*) AS int) AS Calls,
    CAST(SUM(ISNULL(L.CCCINTOK, 0)) AS int) AS InTokens,
    CAST(SUM(ISNULL(L.CCCOUTTOK, 0)) AS int) AS OutTokens,
    CAST(SUM(ISNULL(L.CCCTOTOK, 0)) AS int) AS TotalTokens,
    CAST(SUM(CASE WHEN L.CCCSUCCESS = 1 THEN 1 ELSE 0 END) AS int) AS OkCalls,
    CAST(SUM(CASE WHEN L.CCCSUCCESS = 1 THEN 0 ELSE 1 END) AS int) AS ErrorCalls
FROM CCCJAILOG L
LEFT JOIN USERS U ON U.USERS = L.CCCUSERID
WHERE L.CCCSERIAL = @Serial
  AND L.CCCDATETIME >= @Today
  AND L.CCCDATETIME < DATEADD(day, 1, @Today)
  AND (@CanSeeAll = 1 OR L.CCCUSERID = @UserId)
GROUP BY
    L.CCCUSERID, L.CCCAGENT, L.CCCPROVIDER, L.CCCMODEL
ORDER BY TotalTokens DESC, Calls DESC;";

            XTable result = xSupport.GetSQLDataSet(sql, identity.Serial, identity.UserId);
            DataTable dt = result.CreateDataTable(true);

            var rows = new JArray();
            long calls = 0, inTokens = 0, outTokens = 0, totalTokens = 0, okCalls = 0, errorCalls = 0;

            foreach (DataRow row in dt.Rows)
            {
                long rowCalls = ToInt64(row["Calls"]);
                long rowIn = ToInt64(row["InTokens"]);
                long rowOut = ToInt64(row["OutTokens"]);
                long rowTotal = ToInt64(row["TotalTokens"]);
                long rowOk = ToInt64(row["OkCalls"]);
                long rowErr = ToInt64(row["ErrorCalls"]);

                calls += rowCalls;
                inTokens += rowIn;
                outTokens += rowOut;
                totalTokens += rowTotal;
                okCalls += rowOk;
                errorCalls += rowErr;

                rows.Add(new JObject
                {
                    ["userId"] = ToInt32(row["UserId"]),
                    ["userName"] = SafeString(row["UserName"]),
                    ["agent"] = SafeString(row["Agent"]),
                    ["provider"] = SafeString(row["Provider"]),
                    ["model"] = SafeString(row["Model"]),
                    ["calls"] = rowCalls,
                    ["inTokens"] = rowIn,
                    ["outTokens"] = rowOut,
                    ["totalTokens"] = rowTotal,
                    ["okCalls"] = rowOk,
                    ["errorCalls"] = rowErr
                });
            }

            var payload = BuildUsageEnvelope(identity, "today");
            payload["date"] = DateTime.Today.ToString("yyyy-MM-dd");
            payload["summary"] = new JObject
            {
                ["calls"] = calls,
                ["inTokens"] = inTokens,
                ["outTokens"] = outTokens,
                ["totalTokens"] = totalTokens,
                ["okCalls"] = okCalls,
                ["errorCalls"] = errorCalls
            };
            payload["rows"] = rows;

            return AiUsagePayloadPrefix + payload.ToString(Formatting.None);
        }

        private static string BuildAiUsage30DaysPayload(XSupport xSupport)
        {
            UsageIdentity identity = GetUsageIdentity(xSupport);

            const string sql = @"
SET NOCOUNT ON;
DECLARE @Serial varchar(30) = :1;
DECLARE @UserId int = :2;
DECLARE @CanSeeAll bit = CASE WHEN @UserId IN (1,262) THEN 1 ELSE 0 END;
DECLARE @Today date = CONVERT(date, GETDATE());
DECLARE @FromDate date = DATEADD(day, -29, @Today);

;WITH UsageRows AS
(
    SELECT
        D.CCCDATE AS UsageDate,
        CAST(SUM(ISNULL(D.CCCCALLS, 0)) AS int) AS Calls,
        CAST(SUM(ISNULL(D.CCCINTOK, 0)) AS int) AS InTokens,
        CAST(SUM(ISNULL(D.CCCOUTTOK, 0)) AS int) AS OutTokens,
        CAST(SUM(ISNULL(D.CCCTOTOK, 0)) AS int) AS TotalTokens,
        CAST(SUM(ISNULL(D.CCCOKCALLS, 0)) AS int) AS OkCalls,
        CAST(SUM(ISNULL(D.CCCERRCALLS, 0)) AS int) AS ErrorCalls
    FROM CCCJAIDAY D
    WHERE D.CCCSERIAL = @Serial
      AND D.CCCDATE >= @FromDate
      AND D.CCCDATE < @Today
      AND (@CanSeeAll = 1 OR D.CCCUSERID = @UserId)
    GROUP BY D.CCCDATE

    UNION ALL

    SELECT
        CONVERT(date, L.CCCDATETIME) AS UsageDate,
        CAST(COUNT(*) AS int) AS Calls,
        CAST(SUM(ISNULL(L.CCCINTOK, 0)) AS int) AS InTokens,
        CAST(SUM(ISNULL(L.CCCOUTTOK, 0)) AS int) AS OutTokens,
        CAST(SUM(ISNULL(L.CCCTOTOK, 0)) AS int) AS TotalTokens,
        CAST(SUM(CASE WHEN L.CCCSUCCESS = 1 THEN 1 ELSE 0 END) AS int) AS OkCalls,
        CAST(SUM(CASE WHEN L.CCCSUCCESS = 1 THEN 0 ELSE 1 END) AS int) AS ErrorCalls
    FROM CCCJAILOG L
    WHERE L.CCCSERIAL = @Serial
      AND L.CCCDATETIME >= @FromDate
      AND L.CCCDATETIME < DATEADD(day, 1, @Today)
      AND (L.CCCDATETIME >= @Today OR L.CCCPROCESS = 0)
      AND (@CanSeeAll = 1 OR L.CCCUSERID = @UserId)
    GROUP BY CONVERT(date, L.CCCDATETIME)
)
SELECT
    UsageDate,
    CAST(SUM(Calls) AS int) AS Calls,
    CAST(SUM(InTokens) AS int) AS InTokens,
    CAST(SUM(OutTokens) AS int) AS OutTokens,
    CAST(SUM(TotalTokens) AS int) AS TotalTokens,
    CAST(SUM(OkCalls) AS int) AS OkCalls,
    CAST(SUM(ErrorCalls) AS int) AS ErrorCalls
FROM UsageRows
GROUP BY UsageDate
ORDER BY UsageDate;";

            XTable result = xSupport.GetSQLDataSet(sql, identity.Serial, identity.UserId);
            DataTable dt = result.CreateDataTable(true);

            var rows = new JArray();
            long calls = 0, inTokens = 0, outTokens = 0, totalTokens = 0, okCalls = 0, errorCalls = 0;

            foreach (DataRow row in dt.Rows)
            {
                long rowCalls = ToInt64(row["Calls"]);
                long rowIn = ToInt64(row["InTokens"]);
                long rowOut = ToInt64(row["OutTokens"]);
                long rowTotal = ToInt64(row["TotalTokens"]);
                long rowOk = ToInt64(row["OkCalls"]);
                long rowErr = ToInt64(row["ErrorCalls"]);

                calls += rowCalls;
                inTokens += rowIn;
                outTokens += rowOut;
                totalTokens += rowTotal;
                okCalls += rowOk;
                errorCalls += rowErr;

                DateTime usageDate = Convert.ToDateTime(row["UsageDate"]);
                rows.Add(new JObject
                {
                    ["date"] = usageDate.ToString("yyyy-MM-dd"),
                    ["calls"] = rowCalls,
                    ["inTokens"] = rowIn,
                    ["outTokens"] = rowOut,
                    ["totalTokens"] = rowTotal,
                    ["okCalls"] = rowOk,
                    ["errorCalls"] = rowErr
                });
            }

            var payload = BuildUsageEnvelope(identity, "30d");
            payload["fromDate"] = DateTime.Today.AddDays(-29).ToString("yyyy-MM-dd");
            payload["toDate"] = DateTime.Today.ToString("yyyy-MM-dd");
            payload["summary"] = new JObject
            {
                ["calls"] = calls,
                ["inTokens"] = inTokens,
                ["outTokens"] = outTokens,
                ["totalTokens"] = totalTokens,
                ["okCalls"] = okCalls,
                ["errorCalls"] = errorCalls
            };
            payload["rows"] = rows;

            return AiUsagePayloadPrefix + payload.ToString(Formatting.None);
        }

        private static JObject BuildUsageEnvelope(UsageIdentity identity, string period)
        {
            return new JObject
            {
                ["kind"] = "ai_usage",
                ["period"] = period,
                ["scope"] = identity.CanSeeAll ? "all" : "self",
                ["currentUserId"] = identity.UserId
            };
        }

        private static UsageIdentity GetUsageIdentity(XSupport xSupport)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                throw new InvalidOperationException("Soft1 connection identity unavailable.");

            var info = xSupport.ConnectionInfo;
            string serial = info.SerialNum == null ? string.Empty : info.SerialNum.ToString();
            int userId = info.UserId;

            if (string.IsNullOrWhiteSpace(serial))
                throw new InvalidOperationException("Soft1 serial unavailable.");

            return new UsageIdentity
            {
                Serial = serial,
                UserId = userId,
                CanSeeAll = userId == 1 || userId == 262
            };
        }

        private static long ToInt64(object value)
        {
            return value == null || value == DBNull.Value ? 0L : Convert.ToInt64(value);
        }

        private static int ToInt32(object value)
        {
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static string SafeString(object value)
        {
            return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
        }

        private sealed class UsageIdentity
        {
            public string Serial;
            public int UserId;
            public bool CanSeeAll;
        }

        private static bool TryReadPanelParam(XSupport xSupport, int paramCode, out string sql, out int chartTypeNum)
        {
            sql = null;
            chartTypeNum = 1;
            try
            {
                XTable p = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValue, ParamValueString FROM cccParams WHERE ParamCode = :1 " +
                    "AND (paramsIsActive = 1 OR paramsIsActive IS NULL) ORDER BY cccParams DESC",
                    paramCode);
                DataTable pt = p.CreateDataTable(true);
                if (pt.Rows.Count == 0) return false;

                DataRow row = pt.Rows[0];
                sql = row["ParamValueString"] == DBNull.Value ? null : Convert.ToString(row["ParamValueString"]);
                chartTypeNum = row["ParamValue"] == DBNull.Value ? 1 : Convert.ToInt32(Convert.ToDouble(row["ParamValue"]));
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[DashboardPanels] param {paramCode} read EXCEPTION: {ex.Message}");
                return false;
            }
        }
    }
}

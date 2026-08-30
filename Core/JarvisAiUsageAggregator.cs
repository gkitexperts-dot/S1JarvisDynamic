using System;
using System.IO;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Closes previous-day AI usage for the current Soft1 serial/user into
    /// CCCJAIDAY. Today's CCCJAILOG rows stay open. The routine is best-effort:
    /// reporting failure must never block Jarvis startup.
    /// </summary>
    internal static class JarvisAiUsageAggregator
    {
        // One SQL transaction: update existing daily buckets, insert missing
        // buckets, then mark exactly the eligible raw rows as processed.
        // If any statement fails, the transaction rolls back and the raw rows
        // remain CCCPROCESS=0 for the next Jarvis boot.
        //
        // IMPORTANT: Soft1 ExecuteSQL binds :1/:2/... positionally PER
        // OCCURRENCE inside the SQL batch. Reusing :1/:2/:3 several times in
        // one multi-statement batch made the host parameter binder read beyond
        // the supplied argument array and raised:
        // "Variant or safe array index out of bounds".
        // Bind every Soft1 parameter exactly once, copy it to SQL variables,
        // and use those variables throughout the transaction.
        private const string AggregateSql = @"
SET XACT_ABORT ON;

DECLARE @Serial varchar(30) = :1;
DECLARE @UserId int = :2;
DECLARE @Before datetime = :3;

BEGIN TRANSACTION;

;WITH U AS
(
    SELECT
        CCCSERIAL,
        CONVERT(date, CCCDATETIME) AS CCCDATE,
        CCCCOMPANY,
        CCCBRANCH,
        CCCUSERID,
        CCCAGENT,
        CCCPROVIDER,
        CCCMODEL,
        COUNT(*) AS CCCCALLS,
        SUM(CCCINTOK) AS CCCINTOK,
        SUM(CCCOUTTOK) AS CCCOUTTOK,
        SUM(CCCTOTOK) AS CCCTOTOK,
        SUM(CASE WHEN CCCSUCCESS = 1 THEN 1 ELSE 0 END) AS CCCOKCALLS,
        SUM(CASE WHEN CCCSUCCESS = 1 THEN 0 ELSE 1 END) AS CCCERRCALLS
    FROM CCCJAILOG
    WHERE CCCSERIAL = @Serial
      AND CCCUSERID = @UserId
      AND CCCPROCESS = 0
      AND CCCDATETIME < @Before
    GROUP BY
        CCCSERIAL, CONVERT(date, CCCDATETIME), CCCCOMPANY, CCCBRANCH,
        CCCUSERID, CCCAGENT, CCCPROVIDER, CCCMODEL
)
UPDATE D
SET D.CCCCALLS = ISNULL(D.CCCCALLS, 0) + U.CCCCALLS,
    D.CCCINTOK = ISNULL(D.CCCINTOK, 0) + U.CCCINTOK,
    D.CCCOUTTOK = ISNULL(D.CCCOUTTOK, 0) + U.CCCOUTTOK,
    D.CCCTOTOK = ISNULL(D.CCCTOTOK, 0) + U.CCCTOTOK,
    D.CCCOKCALLS = ISNULL(D.CCCOKCALLS, 0) + U.CCCOKCALLS,
    D.CCCERRCALLS = ISNULL(D.CCCERRCALLS, 0) + U.CCCERRCALLS
FROM CCCJAIDAY D
INNER JOIN U
    ON D.CCCSERIAL = U.CCCSERIAL
   AND D.CCCDATE = U.CCCDATE
   AND D.CCCCOMPANY = U.CCCCOMPANY
   AND D.CCCBRANCH = U.CCCBRANCH
   AND D.CCCUSERID = U.CCCUSERID
   AND ISNULL(D.CCCAGENT, '') = ISNULL(U.CCCAGENT, '')
   AND ISNULL(D.CCCPROVIDER, '') = ISNULL(U.CCCPROVIDER, '')
   AND ISNULL(D.CCCMODEL, '') = ISNULL(U.CCCMODEL, '');

;WITH U AS
(
    SELECT
        CCCSERIAL,
        CONVERT(date, CCCDATETIME) AS CCCDATE,
        CCCCOMPANY,
        CCCBRANCH,
        CCCUSERID,
        CCCAGENT,
        CCCPROVIDER,
        CCCMODEL,
        COUNT(*) AS CCCCALLS,
        SUM(CCCINTOK) AS CCCINTOK,
        SUM(CCCOUTTOK) AS CCCOUTTOK,
        SUM(CCCTOTOK) AS CCCTOTOK,
        SUM(CASE WHEN CCCSUCCESS = 1 THEN 1 ELSE 0 END) AS CCCOKCALLS,
        SUM(CASE WHEN CCCSUCCESS = 1 THEN 0 ELSE 1 END) AS CCCERRCALLS
    FROM CCCJAILOG
    WHERE CCCSERIAL = @Serial
      AND CCCUSERID = @UserId
      AND CCCPROCESS = 0
      AND CCCDATETIME < @Before
    GROUP BY
        CCCSERIAL, CONVERT(date, CCCDATETIME), CCCCOMPANY, CCCBRANCH,
        CCCUSERID, CCCAGENT, CCCPROVIDER, CCCMODEL
)
INSERT INTO CCCJAIDAY
(
    CCCSERIAL, CCCDATE, CCCCOMPANY, CCCBRANCH, CCCUSERID,
    CCCAGENT, CCCPROVIDER, CCCMODEL, CCCCALLS,
    CCCINTOK, CCCOUTTOK, CCCTOTOK, CCCOKCALLS, CCCERRCALLS
)
SELECT
    U.CCCSERIAL, U.CCCDATE, U.CCCCOMPANY, U.CCCBRANCH, U.CCCUSERID,
    U.CCCAGENT, U.CCCPROVIDER, U.CCCMODEL, U.CCCCALLS,
    U.CCCINTOK, U.CCCOUTTOK, U.CCCTOTOK, U.CCCOKCALLS, U.CCCERRCALLS
FROM U
WHERE NOT EXISTS
(
    SELECT 1
    FROM CCCJAIDAY D
    WHERE D.CCCSERIAL = U.CCCSERIAL
      AND D.CCCDATE = U.CCCDATE
      AND D.CCCCOMPANY = U.CCCCOMPANY
      AND D.CCCBRANCH = U.CCCBRANCH
      AND D.CCCUSERID = U.CCCUSERID
      AND ISNULL(D.CCCAGENT, '') = ISNULL(U.CCCAGENT, '')
      AND ISNULL(D.CCCPROVIDER, '') = ISNULL(U.CCCPROVIDER, '')
      AND ISNULL(D.CCCMODEL, '') = ISNULL(U.CCCMODEL, '')
);

UPDATE CCCJAILOG
SET CCCPROCESS = 1
WHERE CCCSERIAL = @Serial
  AND CCCUSERID = @UserId
  AND CCCPROCESS = 0
  AND CCCDATETIME < @Before;

COMMIT TRANSACTION;";

        public static bool ShouldRunToday(XSupport xSupport)
        {
            try
            {
                string marker = GetMarkerPath(xSupport);
                if (string.IsNullOrEmpty(marker) || !File.Exists(marker))
                    return true;

                string value = File.ReadAllText(marker).Trim();
                return !string.Equals(value, DateTime.Today.ToString("yyyyMMdd"), StringComparison.Ordinal);
            }
            catch
            {
                // Marker is only an optimization. If it cannot be read, run the
                // idempotent DB check instead of risking an unclosed backlog.
                return true;
            }
        }

        public static bool TryAggregatePreviousDays(XSupport xSupport)
        {
            try
            {
                if (xSupport == null || xSupport.ConnectionInfo == null)
                    return false;

                var info = xSupport.ConnectionInfo;
                string serial = info.SerialNum == null ? string.Empty : info.SerialNum.ToString();
                int userId = info.UserId;
                DateTime today = DateTime.Today;

                xSupport.ExecuteSQL(AggregateSql, serial, userId, today);
                WriteMarker(xSupport);

                DebugLog.Log(
                    "[AI-USAGE-AGG] previous-day aggregation completed. serial=" +
                    serial + " user=" + userId.ToString() + " before=" +
                    today.ToString("yyyy-MM-dd"));
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    DebugLog.Log(
                        "[AI-USAGE-AGG] failed; Jarvis startup continues. exception=" +
                        ex.GetType().FullName + " hresult=0x" + ex.HResult.ToString("X8") +
                        " error=" + ex.Message);
                }
                catch { }
                return false;
            }
        }

        private static string GetMarkerPath(XSupport xSupport)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                return null;

            var info = xSupport.ConnectionInfo;
            string serial = Sanitize(info.SerialNum == null ? "unknown" : info.SerialNum.ToString());
            string user = info.UserId.ToString();
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "S1Jarvis", "UsageAggregation");
            return Path.Combine(folder, serial + "_" + user + ".day");
        }

        private static void WriteMarker(XSupport xSupport)
        {
            try
            {
                string marker = GetMarkerPath(xSupport);
                if (string.IsNullOrEmpty(marker)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, DateTime.Today.ToString("yyyyMMdd"));
            }
            catch (Exception ex)
            {
                // A marker failure only means the idempotent DB check may run
                // again later today. It is not an aggregation failure.
                try { DebugLog.Log("[AI-USAGE-AGG] daily marker write failed: " + ex.Message); }
                catch { }
            }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value;
        }
    }
}

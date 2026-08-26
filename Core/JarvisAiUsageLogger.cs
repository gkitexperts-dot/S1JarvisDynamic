using System;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Best-effort local AI usage telemetry for the current Soft1 serial.
    /// A telemetry failure must never affect the Jarvis conversation flow.
    /// Raw events are written to CCCJAILOG and will be aggregated separately
    /// into CCCJAIDAY.
    /// </summary>
    internal static class JarvisAiUsageLogger
    {
        private const string InsertSql =
            "INSERT INTO CCCJAILOG " +
            "(CCCSERIAL, CCCCOMPANY, CCCBRANCH, CCCUSERID, CCCAGENT, " +
            "CCCPROVIDER, CCCMODEL, CCCINTOK, CCCOUTTOK, CCCTOTOK, " +
            "CCCREQID, CCCDATETIME, CCCSUCCESS, CCCERRCODE, CCCPROCESS) " +
            "VALUES (:1,:2,:3,:4,:5,:6,:7,:8,:9,:10,:11,:12,:13,:14,0)";

        public static void TryWrite(
            XSupport xSupport,
            string requestId,
            string agent,
            string provider,
            string model,
            int inputTokens,
            int outputTokens,
            bool success,
            string errorCode)
        {
            try
            {
                if (xSupport == null || xSupport.ConnectionInfo == null)
                {
                    DebugLog.Log("[AI-USAGE] skipped: Soft1 connection identity unavailable");
                    return;
                }

                var info = xSupport.ConnectionInfo;
                string serial = info.SerialNum == null ? null : info.SerialNum.ToString();
                int totalTokens = Math.Max(0, inputTokens) + Math.Max(0, outputTokens);

                xSupport.ExecuteSQL(
                    InsertSql,
                    serial,
                    info.CompanyId,
                    info.BranchId,
                    info.UserId,
                    Truncate(agent, 30),
                    Truncate(provider, 30),
                    Truncate(model, 80),
                    Math.Max(0, inputTokens),
                    Math.Max(0, outputTokens),
                    totalTokens,
                    Truncate(requestId, 64),
                    DateTime.Now,
                    success ? 1 : 0,
                    Truncate(errorCode, 50));
            }
            catch (Exception ex)
            {
                // Reporting must never become a Jarvis runtime dependency.
                // Do not log prompts/responses or provider credentials here.
                try
                {
                    var info = xSupport == null ? null : xSupport.ConnectionInfo;
                    string serial = info == null || info.SerialNum == null
                        ? "?"
                        : info.SerialNum.ToString();
                    string user = info == null ? "?" : info.UserId.ToString();

                    DebugLog.Log(
                        "[AI-USAGE] insert failed; continuing. serial=" + serial +
                        " user=" + user +
                        " provider=" + (provider ?? "") +
                        " model=" + (model ?? "") +
                        " in=" + inputTokens.ToString() +
                        " out=" + outputTokens.ToString() +
                        " error=" + ex.Message);
                }
                catch
                {
                    // Never allow telemetry diagnostics to escape either.
                }
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;

            return value.Substring(0, maxLength);
        }
    }
}

using System;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisAiUsageEvent
    {
        public string RequestId { get; set; }
        public string Agent { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public bool ResponseSuccess { get; set; }
        public bool Logged { get; set; }
    }

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

        // UI-only, non-sensitive event. It lets the chat show the same usage
        // evidence that was just persisted (or show that persistence failed).
        // Prompt/response text and credentials are intentionally never exposed.
        public static event Action<JarvisAiUsageEvent> UsageRecorded;

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
            bool logged = false;
            try
            {
                if (xSupport == null || xSupport.ConnectionInfo == null)
                {
                    DebugLog.Log("[AI-USAGE] skipped: Soft1 connection identity unavailable");
                    return;
                }

                var info = xSupport.ConnectionInfo;
                string serial = info.SerialNum == null ? string.Empty : info.SerialNum.ToString();
                int totalTokens = Math.Max(0, inputTokens) + Math.Max(0, outputTokens);

                // Soft1 ExecuteSQL/ADO does not reliably infer a parameter type
                // when a raw null is supplied. Successful AI calls normally have
                // errorCode == null, which caused 800A0E7C (improperly defined
                // parameter). Pass empty strings for optional text parameters;
                // they retain the same semantic meaning for the reporting table.
                xSupport.ExecuteSQL(
                    InsertSql,
                    serial,
                    info.CompanyId,
                    info.BranchId,
                    info.UserId,
                    Truncate(agent, 30) ?? string.Empty,
                    Truncate(provider, 30) ?? string.Empty,
                    Truncate(model, 80) ?? string.Empty,
                    Math.Max(0, inputTokens),
                    Math.Max(0, outputTokens),
                    totalTokens,
                    Truncate(requestId, 64) ?? string.Empty,
                    DateTime.Now,
                    success ? 1 : 0,
                    Truncate(errorCode, 50) ?? string.Empty);

                logged = true;
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
            finally
            {
                NotifyUsageRecorded(new JarvisAiUsageEvent
                {
                    RequestId = requestId,
                    Agent = agent,
                    Provider = provider,
                    Model = model,
                    InputTokens = Math.Max(0, inputTokens),
                    OutputTokens = Math.Max(0, outputTokens),
                    ResponseSuccess = success,
                    Logged = logged
                });
            }
        }

        private static void NotifyUsageRecorded(JarvisAiUsageEvent usage)
        {
            try
            {
                var handler = UsageRecorded;
                if (handler != null)
                    handler(usage);
            }
            catch (Exception ex)
            {
                // UI telemetry must be just as non-blocking as DB telemetry.
                try { DebugLog.Log("[AI-USAGE] UI notification failed: " + ex.Message); }
                catch { }
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

using System;
using System.IO;
using System.Reflection;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DebugLog — ταυτόσημο pattern με S1Courier.Core.DebugLog.
    //
    // ΕΛΕΓΧΟΣ: cccParams.ParamCode = 500000 (ο ΙΔΙΟΣ καθολικός διακόπτης που
    //   χρησιμοποιεί ήδη το S1Courier - ένα switch για όλα τα custom tools).
    //   ParamValue = 1 → ενεργό (αρχείο), 0/απόν → ανενεργό.
    //
    // ΑΡΧΙΚΟΠΟΙΗΣΗ: DebugLog.Init(XSupport) μία φορά στο JarvisShell_Loaded.
    // ══════════════════════════════════════════════════════════════════════
    public static class DebugLog
    {
        private const int DebugParamCode = 500000;
        private static readonly object _fileLock = new object();

        public static bool Enabled { get; private set; }

        public static void Init(XSupport xSupport)
        {
            try
            {
                Enabled = xSupport != null && GetParamValue(xSupport, DebugParamCode, 0) == 1;
                if (Enabled)
                    Log($"═══ S1Jarvis DEBUG SESSION START — {DateTime.Now:dd/MM/yyyy HH:mm:ss} ═══");
            }
            catch
            {
                Enabled = false;
            }
        }

        private static int GetParamValue(XSupport xSupport, int paramCode, int defaultValue)
        {
            try
            {
                var ds = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValue FROM cccParams WHERE ParamCode = :1",
                    paramCode);

                if (ds == null || ds.Count == 0) return defaultValue;

                var val = ds.Current["ParamValue"];
                return (val == null || val == DBNull.Value)
                    ? defaultValue
                    : Convert.ToInt32(val);
            }
            catch { return defaultValue; }
        }

        public static void Log(string message)
        {
            if (!Enabled) return;

            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, $"S1Jarvis_debug_{DateTime.Now:yyyyMMdd}.log");
                string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";

                lock (_fileLock)
                {
                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // logging ΠΟΤΕ δεν σπάει τη ροή
            }
        }
    }
}

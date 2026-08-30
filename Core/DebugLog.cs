using System;
using System.IO;
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
    //
    // ΑΠΟΘΗΚΕΥΣΗ: τα logs γράφονται σε per-user writable location και όχι
    // δίπλα στο DLL. Αυτό είναι απαραίτητο για installations κάτω από
    // Program Files και για το embedded runtime που φορτώνεται με Assembly.Load(bytes).
    // ══════════════════════════════════════════════════════════════════════
    public static class DebugLog
    {
        private const int DebugParamCode = 500000;
        private static readonly object _fileLock = new object();
        private static string _logDirectory;

        public static bool Enabled { get; private set; }

        public static string LogDirectory
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_logDirectory))
                    _logDirectory = ResolveLogDirectory();
                return _logDirectory;
            }
        }

        public static void Init(XSupport xSupport)
        {
            try
            {
                Enabled = xSupport != null && GetParamValue(xSupport, DebugParamCode, 0) == 1;
                if (Enabled)
                {
                    EnsureLogDirectory();
                    Log("═══ S1Jarvis DEBUG SESSION START — " +
                        DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + " ═══");
                    Log("[DEBUG] logDirectory=" + LogDirectory);
                }
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
            try
            {
                // The UI observer is intentionally fed even when file logging is disabled.
                // It is presentation-only and never changes orchestration state.
                JarvisOrchestrationActivityBus.ObserveLogMessage(message);
            }
            catch { }

            if (!Enabled) return;

            try
            {
                EnsureLogDirectory();
                string path = Path.Combine(
                    LogDirectory,
                    "S1Jarvis_debug_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = DateTime.Now.ToString("HH:mm:ss.fff") +
                              "  " + message + Environment.NewLine;

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

        private static void EnsureLogDirectory()
        {
            string directory = LogDirectory;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }

        private static string ResolveLogDirectory()
        {
            // Primary location: writable per-user application data.
            // Example: C:\Users\<user>\AppData\Local\S1Jarvis\Logs
            try
            {
                string localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(localAppData))
                    return Path.Combine(localAppData, "S1Jarvis", "Logs");
            }
            catch { }

            // Last-resort writable fallback. We deliberately avoid Program Files.
            try
            {
                return Path.Combine(Path.GetTempPath(), "S1Jarvis", "Logs");
            }
            catch
            {
                return ".";
            }
        }
    }
}

using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Presentation-only observer over the authoritative ORCH-CONTROL state stream.
    /// It never mutates orchestration state and never dispatches work.
    /// </summary>
    internal static class JarvisOrchestrationActivityBus
    {
        internal static event Action<string, string> ActivityChanged;

        internal static void ObserveLogMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message) ||
                !message.StartsWith("[ORCH-CONTROL] ", StringComparison.Ordinal))
                return;

            string payload = message.Substring("[ORCH-CONTROL] ".Length).Trim();
            if (payload.Length == 0 || payload[0] != '{')
                return;

            try
            {
                JObject root = JObject.Parse(payload);
                string phase = ((string)root["phase"] ?? string.Empty).Trim();
                string activeObjectId = ((string)root["activeObjectId"] ?? string.Empty).Trim();
                JArray steps = root["steps"] as JArray ?? new JArray();
                JObject active = steps.OfType<JObject>().FirstOrDefault(x =>
                    string.Equals((string)x["objectId"], activeObjectId, StringComparison.OrdinalIgnoreCase));
                string taskType = active == null ? string.Empty : ((string)active["taskType"] ?? string.Empty);

                if (phase.IndexOf("running", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Publish("update", CaptionForTask(taskType));
                    return;
                }

                if (string.Equals(phase, "confirmation_payload_frozen", StringComparison.OrdinalIgnoreCase))
                {
                    Publish("update", "Περιμένω επιβεβαίωση για την αποστολή email…");
                    return;
                }

                if (string.Equals(phase, "confirmation_granted", StringComparison.OrdinalIgnoreCase))
                {
                    Publish("update", "Η αποστολή επιβεβαιώθηκε· εκτελώ την ενέργεια…");
                    return;
                }

                if (phase.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    phase.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Publish("update", "Ελέγχω το επόμενο διαθέσιμο βήμα…");
                    return;
                }

                if (string.Equals(phase, "echo_result_accepted", StringComparison.OrdinalIgnoreCase))
                    Publish("end", string.Empty);
            }
            catch
            {
                // Presentation observation must never affect execution/logging.
            }
        }

        private static string CaptionForTask(string taskType)
        {
            if (string.Equals(taskType, "ReportData", StringComparison.OrdinalIgnoreCase))
                return "Αναζητώ και επαληθεύω τα δεδομένα…";
            if (string.Equals(taskType, "CreateCrmTask", StringComparison.OrdinalIgnoreCase))
                return "Καταχωρώ την εργασία στο Soft1…";
            if (string.Equals(taskType, "CreateCalendarEvent", StringComparison.OrdinalIgnoreCase))
                return "Καταχωρώ το συμβάν στο Outlook calendar…";
            if (string.Equals(taskType, "SendEmail", StringComparison.OrdinalIgnoreCase))
                return "Στέλνω το επιβεβαιωμένο email…";
            return "Εκτελώ το επόμενο βήμα…";
        }

        private static void Publish(string action, string text)
        {
            Action<string, string> handler = ActivityChanged;
            if (handler == null) return;
            try { handler(action ?? "update", text ?? string.Empty); }
            catch { }
        }
    }
}

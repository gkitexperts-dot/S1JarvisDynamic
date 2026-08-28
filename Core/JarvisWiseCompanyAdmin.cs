using System;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisCompanyContextChange
    {
        public string Phase { get; set; }
        public string Action { get; set; }
        public string Context { get; set; }
    }

    internal static class JarvisWiseCompanyAdmin
    {
        private static readonly Regex MarkerRegex = new Regex(
            @"\[\[JARVIS_WISE_COMPANY_CONTEXT\]\]\s*(?<json>\{.*?\})\s*\[\[/JARVIS_WISE_COMPANY_CONTEXT\]\]",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        public static string BuildAdminInstruction(XSupport xSupport)
        {
            if (!JarvisAuthorization.IsCurrentUserAdmin(xSupport))
                return null;

            JarvisCompanyContext company = JarvisCompanyContext.Resolve(xSupport);
            var sb = new StringBuilder();
            sb.AppendLine("[JARVIS_WISE_ADMIN_CONTEXT]");
            sb.AppendLine("Ο τρέχων χειριστής είναι Jarvis Admin (ParamCode 500034). Αυτό δίνει δικαίωμα διαχείρισης του curated COMPANY.cccJWContext ΜΟΝΟ της ενεργής Soft1 εταιρίας.");
            sb.AppendLine("Ενεργή εταιρία: CompanyId=" + company.CompanyId + ", Name=" + (company.CompanyName ?? "?") + ".");
            sb.AppendLine("Υπάρχον curated context:");
            sb.AppendLine(string.IsNullOrWhiteSpace(company.WiseContext) ? "(κενό)" : company.WiseContext.Trim());
            sb.AppendLine("Επιτρεπτές admin ενέργειες: CLEAR, REPLACE, MERGE και RESEARCH.");
            sb.AppendLine("CLEAR: καθαρίζει πλήρως το context. REPLACE: αντικαθιστά όλο το context. MERGE: συνθέτει νέο ολοκληρωμένο context που διατηρεί τις σωστές υπάρχουσες πληροφορίες και προσθέτει τις νέες. RESEARCH: πρώτα κάνε πραγματική έρευνα με διαθέσιμα browser/internet tools, μετά ετοίμασε draft. Αν δεν έχεις browser/internet tool στο τρέχον mode, πες το καθαρά και ΜΗΝ επινοήσεις internet research.");
            sb.AppendLine("ΠΟΤΕ μην κάνεις write αμέσως. Πρώτα εμφάνισε στον admin καθαρό preview του τελικού context και πρόσθεσε machine marker phase=DRAFT.");
            sb.AppendLine("Μόνο σε επόμενο μήνυμα, αν ο admin επιβεβαιώσει ΡΗΤΑ την αποθήκευση, επανάλαβε το τελικό context με phase=COMMIT. Χωρίς ρητή επιβεβαίωση δεν επιτρέπεται COMMIT.");
            sb.AppendLine("Marker format, στο απόλυτο τέλος και χωρίς markdown fence:");
            sb.AppendLine("[[JARVIS_WISE_COMPANY_CONTEXT]]");
            sb.AppendLine("{\"phase\":\"DRAFT\",\"action\":\"MERGE\",\"context\":\"ολόκληρο το τελικό context\"}");
            sb.AppendLine("[[/JARVIS_WISE_COMPANY_CONTEXT]]");
            sb.AppendLine("Για CLEAR το context πρέπει να είναι κενό string. Στο COMMIT βάλε ξανά ΟΛΟ το τελικό context, όχι μόνο τη διαφορά.");
            sb.AppendLine("Σε company-context admin workflow ΜΗΝ παράγεις ταυτόχρονα Jarvis Wise learned-knowledge candidate marker/rating.");
            return sb.ToString();
        }

        public static bool TryExtractChange(string assistantText, out JarvisCompanyContextChange change, out string visibleText)
        {
            change = null;
            visibleText = assistantText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assistantText)) return false;

            Match m = MarkerRegex.Match(assistantText);
            if (!m.Success) return false;

            visibleText = MarkerRegex.Replace(assistantText, string.Empty).Trim();
            try
            {
                JObject obj = JObject.Parse(m.Groups["json"].Value);
                string phase = NormalizePhase(obj.Value<string>("phase"));
                string action = NormalizeAction(obj.Value<string>("action"));
                string context = obj.Value<string>("context") ?? string.Empty;

                if (phase == null || action == null) return false;
                if (action != "CLEAR" && string.IsNullOrWhiteSpace(context)) return false;

                change = new JarvisCompanyContextChange
                {
                    Phase = phase,
                    Action = action,
                    Context = action == "CLEAR" ? string.Empty : context.Trim()
                };
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-WISE-ADMIN] marker parse failed: " + ex.Message);
                return false;
            }
        }

        public static bool IsExplicitConfirmation(string userText)
        {
            string s = (userText ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) return false;

            return s == "ναι" || s == "ναι αποθήκευσέ το" || s == "ναι αποθηκευσε το" ||
                   s == "αποθήκευσέ το" || s == "αποθηκευσε το" ||
                   s == "επιβεβαιώνω" || s == "επιβεβαιωνω" ||
                   s == "προχώρα" || s == "προχωρα" ||
                   s == "save" || s == "confirm" || s == "yes save it";
        }

        public static void Commit(XSupport xSupport, JarvisCompanyContextChange change, string confirmationText)
        {
            if (xSupport == null) throw new ArgumentNullException("xSupport");
            if (change == null) throw new ArgumentNullException("change");
            if (!string.Equals(change.Phase, "COMMIT", StringComparison.Ordinal))
                throw new InvalidOperationException("Μόνο COMMIT change μπορεί να αποθηκευτεί.");
            if (!IsExplicitConfirmation(confirmationText))
                throw new InvalidOperationException("Δεν υπάρχει ρητή επιβεβαίωση αποθήκευσης.");

            // Re-check authorization at the exact write boundary. Boot recognition
            // is for UX; this is the authoritative security gate.
            JarvisAuthorization.DemandCurrentUserAdmin(xSupport);

            int companyId = xSupport.ConnectionInfo.CompanyId;
            int userId = xSupport.ConnectionInfo.UserId;

            XModule module = xSupport.CreateModule("COMPANY");
            XTable company = module.GetTable("COMPANY");
            try
            {
                module.LocateData(companyId);
                company.Current.Edit(companyId);
                company.Current["cccJWContext"] = change.Context ?? string.Empty;
                company.Current["cccJWContextUser"] = userId;
                company.Current["cccJWContextDate"] = DateTime.Now;
                module.PostData();

                DebugLog.Log("[JARVIS-WISE-ADMIN] company context committed; companyId=" + companyId +
                    " userId=" + userId + " action=" + change.Action +
                    " length=" + (change.Context == null ? 0 : change.Context.Length));
            }
            finally
            {
                company.Dispose();
                module.Dispose();
            }
        }

        public static string StripMarker(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? text : MarkerRegex.Replace(text, string.Empty).Trim();
        }

        private static string NormalizePhase(string value)
        {
            string s = (value ?? string.Empty).Trim().ToUpperInvariant();
            return s == "DRAFT" || s == "COMMIT" ? s : null;
        }

        private static string NormalizeAction(string value)
        {
            string s = (value ?? string.Empty).Trim().ToUpperInvariant();
            return s == "CLEAR" || s == "REPLACE" || s == "MERGE" || s == "RESEARCH" ? s : null;
        }
    }
}

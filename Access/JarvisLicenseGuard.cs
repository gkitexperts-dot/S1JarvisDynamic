using System;
using System.Collections.Generic;
using Softone;

namespace S1Jarvis.Access
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisLicenseGuard
    //
    // Ίδιο σκεπτικό με S1Courier.Access.LicenseGuard: per-key cache (5 λεπτά),
    // FAIL-CLOSED. Ο Jarvis έχει ΕΝΑ entry point (το JarvisShell όταν φορτώνει)
    // οπότε χρησιμοποιούμε μόνο τη "silent" παραλλαγή - το UI δείχνει το
    // αποτέλεσμα inline μέσα στη σελίδα (WebView2), όχι σε popup.
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisLicenseGuard
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, (AccessCheckResponse result, DateTime at)> _cache =
            new Dictionary<string, (AccessCheckResponse, DateTime)>();

        // toolName προαιρετικό - default AccessConfig.ToolName ("S1JARVIS",
        // ο γενικός έλεγχος στο NavigationCompleted) ώστε ο υπάρχων call
        // site να ΜΗΝ αλλάξει καθόλου. ΝΕΟ 15/08: περνιέται ρητά
        // "JARVISDOCREADER" για το DR feature - ΞΕΧΩΡΙΣΤΟ entitlement (βλ.
        // README Roadmap #6), ΔΙΚΟ ΤΟΥ toolName (ΟΧΙ το "DOCREADER" του
        // standalone S1DocReader προϊόντος - ξεχωριστό εμπορικό SKU/agent,
        // βλ. AccessConfig.DocReaderToolName) - ΙΔΙΟ Nexus backend,
        // διαφορετικό toolName ώστε να ενεργοποιείται/απενεργοποιείται
        // ανεξάρτητα ανά πελάτη. Το cache key ΠΕΡΙΛΑΜΒΑΝΕΙ
        // το toolName (όχι πια το static AccessConfig.ToolName) - χωρίς αυτό,
        // ο δεύτερος έλεγχος (DOCREADER) θα διάβαζε λάθος το cached
        // αποτέλεσμα του πρώτου (S1JARVIS) για τον ΙΔΙΟ χρήστη/serial.
        public static AccessCheckResponse CheckAccessSilent(XSupport xSupport, string toolName = null)
        {
            toolName = toolName ?? AccessConfig.ToolName;
            var info = xSupport.ConnectionInfo;

            string key = $"{info.SerialNum}|{info.CompanyId}|{info.BranchId}|{info.UserId}|{toolName}";

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) &&
                    DateTime.Now - cached.at < CacheDuration)
                {
                    return cached.result;
                }

                var result = DoCheck(xSupport, toolName);
                _cache[key] = (result, DateTime.Now);
                return result;
            }
        }

        private static AccessCheckResponse DoCheck(XSupport xSupport, string toolName)
        {
            try
            {
                var info = xSupport.ConnectionInfo;

                IAccessControl access = new HttpAccessControl(
                    AccessConfig.ServiceUrl,
                    AccessConfig.ClientKey);

                return access.CheckAccess(new AccessCheckRequest
                {
                    Serial      = info.SerialNum?.ToString(),
                    CompanyCode = info.CompanyId.ToString(),
                    BranchCode  = info.BranchId.ToString(),
                    Soft1UserId = info.UserId.ToString(),
                    ToolName    = toolName,
                });
            }
            catch (Exception ex)
            {
                return AccessCheckResponse.Deny(
                    toolName,
                    "Αποτυχία ελέγχου άδειας χρήσης: " + ex.Message);
            }
        }

        public static string BuildMessage(AccessCheckResponse result)
        {
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "Η άδεια χρήσης έχει λήξει. Παρακαλώ ανανεώστε μέσω του Μεταπωλητή σας."
                : result.Message;

            if (!string.IsNullOrWhiteSpace(result.ValidUntil))
                msg += $" (Ισχύς έως: {result.ValidUntil})";

            return msg;
        }
    }
}

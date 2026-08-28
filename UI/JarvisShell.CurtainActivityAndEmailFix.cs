using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool _curtainActivityBootstrapRegistered = RegisterCurtainActivityBootstrap();
        private bool _curtainActivityCoreHooked;
        private int _curtainActivitySerial;
        private int _emailInboxCompanionSerial;
        private readonly JarvisAgentClient _emailInboxAnalysisClient = new JarvisAgentClient();

        private static bool RegisterCurtainActivityBootstrap()
        {
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(CurtainActivityLoaded), true);
            return true;
        }

        private static void CurtainActivityLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;
            shell.webView.CoreWebView2InitializationCompleted -= shell.CurtainActivityCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.CurtainActivityCoreInitialized;
            if (shell.webView.CoreWebView2 != null) shell.AttachCurtainActivityRouter();
        }

        private void CurtainActivityCoreInitialized(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess) AttachCurtainActivityRouter();
        }

        private void AttachCurtainActivityRouter()
        {
            if (_curtainActivityCoreHooked || webView.CoreWebView2 == null) return;
            _curtainActivityCoreHooked = true;
            webView.CoreWebView2.WebMessageReceived += CurtainActivityWebMessageReceived;
            DebugLog.Log("[JARVIS-ACTIVITY] curtain activity companion attached");
        }

        private void CurtainActivityWebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); } catch { return; }
            if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{') return;

            JObject cmd;
            try { cmd = JObject.Parse(raw); } catch { return; }

            string type = (string)cmd["type"];
            string channel = null;
            string caption = null;

            if (type == "browser_message") { channel = "browser"; caption = "Αναζήτηση και επεξεργασία…"; }
            else if (type == "help_message") { channel = "help"; caption = "Αναζήτηση λύσης…"; }
            else if (type == "email_message")
            {
                string text = (string)cmd["text"] ?? string.Empty;
                if (LooksLikeInboxAnalysisRequest(text))
                {
                    int emailSerial = ++_emailInboxCompanionSerial;
                    _agentClient.CancelCurrent();
                    _ = CancelPrimaryEmailTurnRaceAsync(emailSerial);
                    _ = RunEmailInboxAnalysisAsync(emailSerial, text);
                    return;
                }
                channel = "email"; caption = "Έλεγχος email / calendar…";
            }
            else if (type == "courier_message") { channel = "courier"; caption = "Αναζήτηση παραστατικών…"; }

            if (channel == null) return;
            int serial = ++_curtainActivitySerial;
            _ = StartCurtainActivityUntilReplyAsync(serial, channel, caption);
        }

        private async Task StartCurtainActivityUntilReplyAsync(int serial, string channel, string caption)
        {
            int baseline = await GetAssistantCountAsync(channel);
            PostJarvisActivity("start", channel, caption, false);
            for (int i = 0; i < 600; i++)
            {
                if (serial != _curtainActivitySerial) return;
                await Task.Delay(100);
                int current = await GetAssistantCountAsync(channel);
                if (current > baseline) { PostJarvisActivity("end", channel); return; }
            }
            PostJarvisActivity("end", channel);
        }

        private async Task<int> GetAssistantCountAsync(string channel)
        {
            try
            {
                string id = channel == "browser" ? "browserTranscript"
                    : channel == "help" ? "helpTranscript"
                    : channel == "email" ? "emailTranscript"
                    : channel == "courier" ? "courierTranscript" : "transcript";
                string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                    "(()=>{var h=document.getElementById('" + id + "');return h?h.querySelectorAll('.msg.assistant').length:0;})()");
                int count; return int.TryParse(raw, out count) ? count : 0;
            }
            catch { return 0; }
        }

        private static bool LooksLikeInboxAnalysisRequest(string text)
        {
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) return false;
            bool inbox = s.Contains("email") || s.Contains("mail") || s.Contains("εισερχ") || s.Contains("μηνύμα") || s.Contains("μηνυμα") || s.Contains("inbox");
            bool readOrList = s.Contains("δείξε") || s.Contains("δειξε") || s.Contains("βρες") || s.Contains("τελευτα") || s.Contains("διάβα") || s.Contains("διαβα") || s.Contains("ποια") || s.Contains("απάντη") || s.Contains("απαντη");
            return inbox && readOrList;
        }

        private async Task CancelPrimaryEmailTurnRaceAsync(int serial)
        {
            int[] delays = { 40, 100, 220, 400 };
            for (int i = 0; i < delays.Length; i++)
            {
                await Task.Delay(delays[i]);
                if (serial != _emailInboxCompanionSerial) return;
                _agentClient.CancelCurrent();
            }
        }

        private static JObject ParseJsonObjectLoose(string text)
        {
            string s = (text ?? string.Empty).Trim();
            int first = s.IndexOf('{');
            int last = s.LastIndexOf('}');
            if (first >= 0 && last > first) s = s.Substring(first, last - first + 1);
            return JObject.Parse(s);
        }

        private async Task RunEmailInboxAnalysisAsync(int serial, string userText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_agentAccountRef)) return;
                PostJarvisActivity("start", "email", "Ανάγνωση εισερχόμενων email…", true);

                string inboxJson = await JarvisEmailAccess.ExecuteReadEmail(_xSupport, new JObject { ["count"] = 20 });
                if (serial != _emailInboxCompanionSerial) return;

                JObject inbox = JObject.Parse(inboxJson);
                JArray emails = inbox["emails"] as JArray ?? new JArray();
                PostJarvisActivity("update", "email", "Ανάλυση μηνυμάτων που χρειάζονται απάντηση…");

                var history = new List<JObject>();
                string prompt =
                    "Ανάλυσε ΑΠΟΚΛΕΙΣΤΙΚΑ τα παρακάτω records email. Μην χρησιμοποιήσεις κανένα εργαλείο. " +
                    "Επίλεξε μόνο τα μηνύματα που, με βάση subject, from/fromName, receivedDateTime, isRead και preview, χρειάζονται ανθρώπινη απάντηση. " +
                    "Μην θεωρείς ότι κάθε αδιάβαστο χρειάζεται απάντηση και μην επινοήσεις στοιχεία. " +
                    "ΕΠΕΣΤΡΕΨΕ ΜΟΝΟ έγκυρο JSON, χωρίς markdown και χωρίς άλλο κείμενο, ακριβώς στη μορφή: " +
                    "{\"replyIds\":[\"id1\",\"id2\"]}. Αν κανένα δεν χρειάζεται απάντηση, replyIds να είναι κενό array.\n\n" +
                    "ΑΙΤΗΜΑ ΧΕΙΡΙΣΤΗ:\n" + userText + "\n\nRECORDS:\n" + inboxJson;

                string rawAnalysis = await _emailInboxAnalysisClient.AskAsync(
                    _agentAccountRef, _xSupport, history, prompt,
                    onProgress: t => PostJarvisActivity("update", "email",
                        string.IsNullOrWhiteSpace(t) ? "Ανάλυση εισερχόμενων…" : t));

                if (serial != _emailInboxCompanionSerial) return;

                JObject analysis = ParseJsonObjectLoose(rawAnalysis);
                var wanted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in analysis["replyIds"] as JArray ?? new JArray())
                {
                    string v = id?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) wanted.Add(v);
                }

                var filtered = new JArray();
                foreach (var email in emails)
                {
                    string id = email?["id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(id) && wanted.Contains(id)) filtered.Add(email.DeepClone());
                }

                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_inbox_result",
                    ["success"] = true,
                    ["emails"] = filtered
                }.ToString(Newtonsoft.Json.Formatting.None));

                string notice = filtered.Count == 0
                    ? "Δεν εντόπισα στα πρόσφατα μηνύματα κάποιο που να χρειάζεται σαφή απάντηση."
                    : "Τα μηνύματα που εμφανίζονται τώρα στη λίστα είναι αυτά που χρειάζονται απάντηση.";

                _emailConversation.Add(new JObject { ["role"] = "assistant", ["content"] = notice });
                PostJarvisActivity("complete", "email", notice);
                DebugLog.Log("[email-companion] inbox filtered; replyNeeded=" + filtered.Count + "/" + emails.Count);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email-companion] inbox analysis failed: " + ex);
                PostJarvisActivity("complete", "email", "✖ Αποτυχία ανάγνωσης/ανάλυσης email: " + ex.Message);
            }
        }
    }
}

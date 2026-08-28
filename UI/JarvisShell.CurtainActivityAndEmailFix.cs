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
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(CurtainActivityLoaded),
                true);
            return true;
        }

        private static void CurtainActivityLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;

            shell.webView.CoreWebView2InitializationCompleted -= shell.CurtainActivityCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.CurtainActivityCoreInitialized;

            if (shell.webView.CoreWebView2 != null)
                shell.AttachCurtainActivityRouter();
        }

        private void CurtainActivityCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                AttachCurtainActivityRouter();
        }

        private void AttachCurtainActivityRouter()
        {
            if (_curtainActivityCoreHooked || webView.CoreWebView2 == null) return;
            _curtainActivityCoreHooked = true;
            webView.CoreWebView2.WebMessageReceived += CurtainActivityWebMessageReceived;
            DebugLog.Log("[JARVIS-ACTIVITY] curtain activity companion attached");
        }

        private void CurtainActivityWebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }

            if (string.IsNullOrWhiteSpace(raw) || raw[0] != '{') return;

            JObject cmd;
            try { cmd = JObject.Parse(raw); }
            catch { return; }

            string type = (string)cmd["type"];
            string channel = null;
            string caption = null;

            if (type == "browser_message")
            {
                channel = "browser";
                caption = "Αναζήτηση και επεξεργασία…";
            }
            else if (type == "help_message")
            {
                channel = "help";
                caption = "Αναζήτηση λύσης…";
            }
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

                channel = "email";
                caption = "Έλεγχος email / calendar…";
            }
            else if (type == "courier_message")
            {
                channel = "courier";
                caption = "Αναζήτηση παραστατικών…";
            }

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
                if (current > baseline)
                {
                    PostJarvisActivity("end", channel);
                    return;
                }
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
                    : channel == "courier" ? "courierTranscript"
                    : "transcript";

                string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                    "(()=>{var h=document.getElementById('" + id + "');return h?h.querySelectorAll('.msg.assistant').length:0;})()");

                int count;
                return int.TryParse(raw, out count) ? count : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool LooksLikeInboxAnalysisRequest(string text)
        {
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) return false;

            bool inbox = s.Contains("email") || s.Contains("mail") || s.Contains("εισερχ") ||
                         s.Contains("μηνύμα") || s.Contains("μηνυμα") || s.Contains("inbox");
            bool readOrList = s.Contains("δείξε") || s.Contains("δειξε") || s.Contains("βρες") ||
                              s.Contains("τελευτα") || s.Contains("διάβα") || s.Contains("διαβα") ||
                              s.Contains("ποια") || s.Contains("απάντη") || s.Contains("απαντη");
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

        private async Task RunEmailInboxAnalysisAsync(int serial, string userText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_agentAccountRef)) return;

                PostJarvisActivity("start", "email", "Ανάγνωση εισερχόμενων email…", true);

                string inboxJson = await JarvisEmailAccess.ExecuteReadEmail(
                    _xSupport,
                    new JObject { ["count"] = 20 });

                if (serial != _emailInboxCompanionSerial) return;

                PostJarvisActivity("update", "email", "Ανάλυση μηνυμάτων που χρειάζονται απάντηση…");

                var history = new List<JObject>();
                string prompt =
                    "Ανάλυσε ΑΠΟΚΛΕΙΣΤΙΚΑ τα παρακάτω ήδη ανακτημένα records επικοινωνίας. " +
                    "Μην ζητήσεις ή χρησιμοποιήσεις κανένα εργαλείο και μην πεις ότι δεν έχεις πρόσβαση. " +
                    "Απάντησε στα ελληνικά στο αίτημα του χειριστή. Για το ποια χρειάζονται απάντηση, " +
                    "χρησιμοποίησε θέμα, αποστολέα, ημερομηνία, isRead και bodyPreview και εξήγησε σύντομα το γιατί. " +
                    "Μην επινοήσεις περιεχόμενο που δεν υπάρχει στα records.\n\n" +
                    "ΑΙΤΗΜΑ ΧΕΙΡΙΣΤΗ:\n" + userText + "\n\n" +
                    "RECORDS:\n" + inboxJson;

                string answer = await _emailInboxAnalysisClient.AskAsync(
                    _agentAccountRef,
                    _xSupport,
                    history,
                    prompt,
                    onProgress: t => PostJarvisActivity("update", "email",
                        string.IsNullOrWhiteSpace(t) ? "Ανάλυση εισερχόμενων…" : t));

                if (serial != _emailInboxCompanionSerial) return;
                if (string.IsNullOrWhiteSpace(answer))
                    answer = "Δεν βρέθηκαν αρκετά στοιχεία για ανάλυση των εισερχόμενων μηνυμάτων.";

                // Keep the Email curtain history coherent after suppressing the primary turn.
                _emailConversation.Add(new JObject { ["role"] = "assistant", ["content"] = answer });
                PostJarvisActivity("complete", "email", answer);
                DebugLog.Log("[email-companion] inbox analysis completed; length=" + answer.Length);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email-companion] inbox analysis failed: " + ex);
                PostJarvisActivity("complete", "email", "✖ Αποτυχία ανάγνωσης/ανάλυσης email: " + ex.Message);
            }
        }
    }
}

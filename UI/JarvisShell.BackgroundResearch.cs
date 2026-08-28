using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool _backgroundResearchBootstrapRegistered = RegisterBackgroundResearchBootstrap();

        private readonly JarvisAgentClient _backgroundResearchClient = new JarvisAgentClient();
        private readonly List<JObject> _backgroundResearchConversation = new List<JObject>();
        private bool _backgroundResearchCoreHooked;
        private int _backgroundResearchSerial;

        private static bool RegisterBackgroundResearchBootstrap()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(BackgroundResearchLoaded),
                true);
            return true;
        }

        private static void BackgroundResearchLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;

            shell.webView.CoreWebView2InitializationCompleted -= shell.BackgroundResearchCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.BackgroundResearchCoreInitialized;

            if (shell.webView.CoreWebView2 != null)
                shell.AttachBackgroundResearchRouter();
        }

        private void BackgroundResearchCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                AttachBackgroundResearchRouter();
        }

        private void AttachBackgroundResearchRouter()
        {
            if (_backgroundResearchCoreHooked || webView.CoreWebView2 == null) return;
            _backgroundResearchCoreHooked = true;
            webView.CoreWebView2.WebMessageReceived += BackgroundResearchWebMessageReceived;
            DebugLog.Log("[JARVIS-RESEARCH] hidden-browser companion router attached");
        }

        private void BackgroundResearchWebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }

            if (string.IsNullOrWhiteSpace(raw)) return;
            if (raw[0] == '{' || raw.StartsWith("__JARVIS_", StringComparison.Ordinal)) return;
            if (!LooksLikeInternetResearch(raw)) return;

            int serial = ++_backgroundResearchSerial;
            _ = RunBackgroundResearchAsync(serial, raw);
        }

        private static bool LooksLikeInternetResearch(string text)
        {
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length < 4) return false;

            bool internet =
                s.Contains("internet") || s.Contains("ίντερνετ") || s.Contains("ιντερνετ") ||
                s.Contains("διαδίκτυ") || s.Contains("διαδικτυ") ||
                s.Contains("στο web") || s.Contains("online") || s.Contains("google") ||
                s.Contains("ιστοσελί") || s.Contains("ιστοσελι");

            bool research =
                s.Contains("βρες") || s.Contains("ψάξ") || s.Contains("ψαξ") ||
                s.Contains("αναζήτη") || s.Contains("αναζητ") || s.Contains("έρευν") ||
                s.Contains("ερευν") || s.Contains("research") || s.Contains("search") ||
                s.Contains("τεχνικά χαρακτηριστικά") || s.Contains("τεχνικα χαρακτηριστικα") ||
                s.Contains("πληροφορίες") || s.Contains("πληροφοριες");

            return internet && research;
        }

        private bool IsCompanyContextResearch(string text)
        {
            if (!JarvisAuthorization.IsCurrentUserAdmin(_xSupport)) return false;
            string s = (text ?? string.Empty).ToLowerInvariant();
            return s.Contains("εταιρικό context") || s.Contains("εταιρικο context") ||
                   s.Contains("company context") || s.Contains("jarvis wise context") ||
                   (s.Contains("εταιρικό") && s.Contains("profile")) ||
                   (s.Contains("εταιρικο") && s.Contains("προφίλ")) ||
                   (s.Contains("εταιρικο") && s.Contains("προφιλ"));
        }

        private async Task RunBackgroundResearchAsync(int serial, string userText)
        {
            bool completed = false;
            try
            {
                if (string.IsNullOrWhiteSpace(_agentAccountRef))
                    return;

                bool companyContextResearch = IsCompanyContextResearch(userText);
                PostJarvisActivity("start", "main", "Αναζήτηση στο Internet…", true);
                DebugLog.Log("[JARVIS-RESEARCH] start; companyContext=" + companyContextResearch);

                await EnsureBrowserViewInitializedAsync();
                if (serial != _backgroundResearchSerial) return;

                _backgroundResearchConversation.Clear();
                SeedBackgroundResearchHistory(userText);
                string researchPrompt = BuildBackgroundResearchPrompt(userText, companyContextResearch);

                string answer = await _backgroundResearchClient.AskAsync(
                    _agentAccountRef,
                    _xSupport,
                    _backgroundResearchConversation,
                    researchPrompt,
                    browserMode: true,
                    onNavigate: url =>
                    {
                        PostJarvisActivity("update", "main", "Άνοιγμα δημόσιας πηγής…");
                        NavigateBrowserView(url);
                    },
                    onReadPage: async () =>
                    {
                        PostJarvisActivity("update", "main", "Ανάγνωση και έλεγχος πηγής…");
                        return await ReadBrowserPageContentAsync();
                    },
                    onExtractPageTables: ExtractBrowserPageTablesAsync,
                    onProgress: t =>
                    {
                        string caption = string.IsNullOrWhiteSpace(t)
                            ? "Σύνθεση αποτελέσματος…"
                            : t.Trim();
                        PostJarvisActivity("update", "main", caption);
                    },
                    maxIterations: JarvisTools.GetCrmTaskOptionalParam(_xSupport, 500028, 40));

                if (serial != _backgroundResearchSerial) return;
                if (string.IsNullOrWhiteSpace(answer))
                    throw new InvalidOperationException("Η αναζήτηση δεν επέστρεψε αποτέλεσμα.");

                PostJarvisActivity("update", "main", "Σύνθεση τελικής απάντησης…");
                await WaitForPrimaryMainTurnToFinishAsync(serial);
                if (serial != _backgroundResearchSerial) return;

                string visibleText;
                string conversationText;
                if (companyContextResearch)
                {
                    string profile = answer.Trim();
                    JarvisCompanyContext company = JarvisCompanyContext.Resolve(_xSupport);
                    visibleText =
                        "Προεπισκόπηση εταιρικού profile από δημόσιες πηγές για **" +
                        (company.CompanyName ?? ("Company " + company.CompanyId)) + "**:\n\n" +
                        profile +
                        "\n\n⚠ Η παραπάνω αλλαγή είναι μόνο προεπισκόπηση. Δεν έχει γραφτεί στο εταιρικό context.";

                    var marker = new JObject
                    {
                        ["phase"] = "DRAFT",
                        ["action"] = "RESEARCH",
                        ["context"] = profile
                    };
                    conversationText = visibleText +
                        "\n\n[[JARVIS_WISE_COMPANY_CONTEXT]]\n" +
                        marker.ToString(Formatting.None) +
                        "\n[[/JARVIS_WISE_COMPANY_CONTEXT]]";
                }
                else
                {
                    visibleText = answer.Trim();
                    conversationText = visibleText;
                }

                ReplaceLastAssistantHistory(_conversation, conversationText);
                PostJarvisActivity("complete", "main", visibleText);
                completed = true;
                DebugLog.Log("[JARVIS-RESEARCH] completed; length=" + visibleText.Length);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-RESEARCH] failed: " + ex);
                try
                {
                    await WaitForPrimaryMainTurnToFinishAsync(serial);
                    if (serial == _backgroundResearchSerial)
                    {
                        string error = "✖ Η αναζήτηση στο Internet δεν ολοκληρώθηκε: " + ex.Message;
                        ReplaceLastAssistantHistory(_conversation, error);
                        PostJarvisActivity("complete", "main", error);
                        completed = true;
                    }
                }
                catch { }
            }
            finally
            {
                if (!completed && serial == _backgroundResearchSerial)
                    PostJarvisActivity("end", "main");
            }
        }

        private void SeedBackgroundResearchHistory(string currentUserText)
        {
            if (_conversation == null || _conversation.Count == 0) return;

            int start = Math.Max(0, _conversation.Count - 8);
            for (int i = start; i < _conversation.Count; i++)
            {
                JObject msg = _conversation[i];
                if (msg == null) continue;
                string role = msg.Value<string>("role");
                if (role != "user" && role != "assistant") continue;
                string content = msg.Value<string>("content") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (content.StartsWith("[JARVIS_", StringComparison.Ordinal)) continue;
                if (role == "user" && string.Equals(content.Trim(), (currentUserText ?? string.Empty).Trim(), StringComparison.Ordinal))
                    continue;

                _backgroundResearchConversation.Add(new JObject
                {
                    ["role"] = role,
                    ["content"] = content
                });
            }
        }

        private static string BuildBackgroundResearchPrompt(string userText, bool companyContextResearch)
        {
            string mode = companyContextResearch
                ? "Το αποτέλεσμα θα χρησιμοποιηθεί ως curated εταιρικό profile/context. Κράτησε μόνο επαληθεύσιμα, χρήσιμα και σχετικά στοιχεία, χωρίς marketing υπερβολές."
                : "Δώσε την απάντηση που ζήτησε ο χρήστης, συνοπτικά αλλά επαρκώς.";

            return
                "Εκτέλεσε ΠΡΑΓΜΑΤΙΚΗ έρευνα στο Internet χρησιμοποιώντας τα browser tools που διαθέτεις. " +
                "Μην απαντήσεις από μνήμη και μην ισχυριστείς ότι δεν υπάρχουν browser tools. " +
                "Ξεκίνα με αναζήτηση web, άνοιξε αξιόπιστες πηγές και διάβασε το περιεχόμενό τους. " +
                "Προτίμησε επίσημη ιστοσελίδα κατασκευαστή/εταιρίας και άλλες πρωτογενείς ή αξιόπιστες πηγές. " +
                "Όπου είναι εφικτό διασταύρωσε βασικά στοιχεία σε περισσότερες από μία πηγές. " +
                mode + " Στο τέλος βάλε μικρή ενότητα 'Πηγές' με τις σελίδες που πραγματικά χρησιμοποίησες.\n\n" +
                "Αρχικό αίτημα χρήστη:\n" + userText;
        }

        private async Task WaitForPrimaryMainTurnToFinishAsync(int serial)
        {
            if (webView.CoreWebView2 == null) return;
            for (int i = 0; i < 240; i++)
            {
                if (serial != _backgroundResearchSerial) return;
                try
                {
                    string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                        "(()=>{const o=document.getElementById('orbWrap');return !!(o&&o.classList.contains('thinking'));})()");
                    bool thinking;
                    if (bool.TryParse(raw, out thinking) && !thinking)
                        return;
                    if (raw == "false") return;
                }
                catch { }
                await Task.Delay(250);
            }
        }

        private static void ReplaceLastAssistantHistory(List<JObject> history, string text)
        {
            if (history == null) return;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                JObject msg = history[i];
                if (msg == null) continue;
                if (!string.Equals(msg.Value<string>("role"), "assistant", StringComparison.Ordinal))
                    continue;
                msg["content"] = text ?? string.Empty;
                return;
            }

            history.Add(new JObject
            {
                ["role"] = "assistant",
                ["content"] = text ?? string.Empty
            });
        }
    }
}

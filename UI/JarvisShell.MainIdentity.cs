using System;
using System.Threading.Tasks;
using System.Windows;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool _mainIdentityBootstrapRegistered = RegisterMainIdentityBootstrap();
        private bool _mainIdentityCoreHooked;
        private int _mainIdentitySerial;

        private static bool RegisterMainIdentityBootstrap()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(MainIdentityLoaded),
                true);
            return true;
        }

        private static void MainIdentityLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;

            shell.webView.CoreWebView2InitializationCompleted -= shell.MainIdentityCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.MainIdentityCoreInitialized;

            if (shell.webView.CoreWebView2 != null)
                shell.AttachMainIdentityRouter();
        }

        private void MainIdentityCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
                AttachMainIdentityRouter();
        }

        private void AttachMainIdentityRouter()
        {
            if (_mainIdentityCoreHooked || webView.CoreWebView2 == null) return;
            _mainIdentityCoreHooked = true;
            webView.CoreWebView2.WebMessageReceived += MainIdentityWebMessageReceived;
            DebugLog.Log("[JARVIS-IDENTITY] Main Chat product-identity guard attached");
        }

        private void MainIdentityWebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }

            // Main Chat user messages are plain strings. Structured JSON belongs to curtains/tools.
            if (string.IsNullOrWhiteSpace(raw) || raw[0] == '{' || raw.StartsWith("__JARVIS_", StringComparison.Ordinal))
                return;
            if (!LooksLikeIdentityQuestion(raw))
                return;

            int serial = ++_mainIdentitySerial;

            // Do NOT cancel the primary turn. Cancellation is visible to the user as
            // "Σταμάτησα" before the product-identity answer. Instead, suppress the
            // internal agent response in the Main Chat DOM, let the normal routed turn
            // finish, then replace that completed assistant turn with Jarvis identity.
            PostJarvisActivity("start", "main", "Επεξεργασία…", true);
            _ = CompleteMainIdentityTurnAsync(serial);
        }

        private async Task CompleteMainIdentityTurnAsync(int serial)
        {
            await WaitForSuppressedPrimaryIdentityResponseAsync(serial);
            if (serial != _mainIdentitySerial) return;

            const string answer =
                "Είμαι ο **Jarvis**, ο ψηφιακός βοηθός σου μέσα στο Soft1. " +
                "Στο παρασκήνιο μπορώ να χρησιμοποιώ εξειδικευμένους agents για διαφορετικές εργασίες, " +
                "αλλά στο Main Chat μιλάς πάντα με τον Jarvis.";

            ReplaceLastAssistantHistory(_conversation, answer);
            PostJarvisActivity("complete", "main", answer);
            DebugLog.Log("[JARVIS-IDENTITY] product identity response completed without cancellation");
        }

        private async Task WaitForSuppressedPrimaryIdentityResponseAsync(int serial)
        {
            if (webView.CoreWebView2 == null) return;

            // PostJarvisActivity(suppressAssistant:true) marks the routed agent's
            // assistant bubble with data-jarvis-suppressed as soon as it appears.
            // Waiting for that marker is more reliable than racing the orb/thinking
            // state because the primary handler and this companion receive the same
            // WebMessageReceived event independently.
            for (int i = 0; i < 400; i++)
            {
                if (serial != _mainIdentitySerial) return;
                try
                {
                    string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                        "(()=>{const h=document.getElementById('transcript');return !!(h&&h.querySelector('[data-jarvis-suppressed=\\\"1\\\"]'));})()");
                    bool found;
                    if (bool.TryParse(raw, out found) && found)
                        return;
                    if (raw == "true")
                        return;
                }
                catch { }

                await Task.Delay(250);
            }

            DebugLog.Log("[JARVIS-IDENTITY] timed out waiting for routed identity response; completing product identity");
        }

        private static bool LooksLikeIdentityQuestion(string text)
        {
            string s = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) return false;

            return
                s == "ποιος εισαι" || s == "ποιος είσαι" ||
                s == "τι εισαι" || s == "τι είσαι" ||
                s.Contains("πως σε λενε") || s.Contains("πώς σε λένε") ||
                s.Contains("ποιο ειναι το ονομα σου") || s.Contains("ποιο είναι το όνομά σου") ||
                s == "who are you" || s == "what are you" ||
                s.Contains("what is your name") || s.Contains("what's your name");
        }
    }
}

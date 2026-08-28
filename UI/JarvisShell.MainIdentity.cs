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

            // Internal routing may still choose Atlas/another specialist, but product identity
            // must never leak into the Main Chat surface. Suppress the primary AI turn and
            // answer deterministically as Jarvis.
            _agentClient.CancelCurrent();
            _ = CompleteMainIdentityTurnAsync(serial);
        }

        private async Task CompleteMainIdentityTurnAsync(int serial)
        {
            int[] delaysMs = { 40, 100, 220, 400 };
            for (int i = 0; i < delaysMs.Length; i++)
            {
                await Task.Delay(delaysMs[i]);
                if (serial != _mainIdentitySerial) return;
                _agentClient.CancelCurrent();
            }

            if (serial != _mainIdentitySerial) return;

            const string answer =
                "Είμαι ο **Jarvis**, ο ψηφιακός βοηθός σου μέσα στο Soft1. " +
                "Στο παρασκήνιο μπορώ να χρησιμοποιώ εξειδικευμένους agents για διαφορετικές εργασίες, " +
                "αλλά στο Main Chat μιλάς πάντα με τον Jarvis.";

            ReplaceLastAssistantHistory(_conversation, answer);
            PostJarvisActivity("complete", "main", answer);
            DebugLog.Log("[JARVIS-IDENTITY] product identity response completed");
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

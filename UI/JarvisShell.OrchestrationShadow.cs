using System;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    /// <summary>
    /// Shadow-only Main Chat hook for the new orchestration path.
    ///
    /// This partial intentionally does not change CoreWebView2_WebMessageReceived
    /// or the mature AskAsync path. It observes the same WebView message stream,
    /// ignores typed UI commands, and launches the pilot orchestration pipeline
    /// only for plain Main Chat user messages. Legacy execution remains the only
    /// user-visible execution path.
    /// </summary>
    public partial class JarvisShell
    {
        private bool _orchestrationShadowHookAttached;

        static JarvisShell()
        {
            // Avoid editing the large mature JarvisShell.xaml.cs send pipeline.
            // A class-level Loaded handler waits until CoreWebView2 initialization
            // has completed, then attaches one observer handler.
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnOrchestrationShadowLoaded));
        }

        private static void OnOrchestrationShadowLoaded(object sender, RoutedEventArgs e)
        {
            JarvisShell shell = sender as JarvisShell;
            if (shell == null || shell._orchestrationShadowHookAttached)
                return;

            // Fire-and-forget only for hook installation. The method contains
            // its own catch-all and performs no business action.
            shell.AttachOrchestrationShadowHandlerSafeAsync();
        }

        private async void AttachOrchestrationShadowHandlerSafeAsync()
        {
            try
            {
                if (_orchestrationShadowHookAttached)
                    return;

                // JarvisShell_Loaded initializes CoreWebView2 asynchronously.
                // Wait briefly for that existing initialization instead of
                // creating another WebView environment or changing boot order.
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.WebMessageReceived +=
                            OrchestrationShadow_WebMessageReceived;
                        _orchestrationShadowHookAttached = true;
                        DebugLog.Log("[ORCH-SHADOW] Main Chat observer attached.");
                        return;
                    }

                    await Task.Delay(50);
                }

                DebugLog.Log("[ORCH-SHADOW] Main Chat observer not attached: CoreWebView2 was not ready in time.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] observer attach failed; legacy chat unaffected: " + ex);
            }
        }

        private void OrchestrationShadow_WebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string userText = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(userText))
                    return;

                string trimmed = userText.Trim();
                if (string.Equals(trimmed, "Stop", StringComparison.OrdinalIgnoreCase))
                    return;

                // Every curtain/control action currently travels as a typed
                // JSON command. Observe only ordinary Main Chat text.
                JObject command = null;
                try { command = JObject.Parse(trimmed); }
                catch { /* normal Main Chat text */ }

                if (command != null && command["type"] != null)
                    return;

                // Do not await: the mature CoreWebView2_WebMessageReceived
                // handler continues immediately into _agentClient.AskAsync.
                // The coordinator is feature-gated and catches all failures.
                JarvisOrchestrationShadowCoordinator.RunAndLogSafeAsync(
                    _xSupport,
                    userText);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] observer suppressed exception: " + ex);
            }
        }
    }
}

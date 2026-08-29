using System;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    internal static class JarvisOrchestrationShadowBootstrap
    {
        private static readonly object Sync = new object();
        private static bool _registered;

        internal static bool EnsureRegistered()
        {
            lock (Sync)
            {
                if (_registered)
                    return true;

                EventManager.RegisterClassHandler(
                    typeof(JarvisShell),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnLoaded));

                _registered = true;
                return true;
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            JarvisShell shell = sender as JarvisShell;
            if (shell != null)
                shell.InstallOrchestrationShadowHook();
        }
    }

    public partial class JarvisShell
    {
        private readonly bool _orchestrationShadowBootstrapRegistered =
            JarvisOrchestrationShadowBootstrap.EnsureRegistered();

        // Instance-scoped: a confirmation can resume only the pending plan that
        // belongs to this JarvisShell. No global/static business payload is kept.
        private readonly JarvisPendingConfirmationSession _orchestrationPendingConfirmation =
            new JarvisPendingConfirmationSession();

        private bool _orchestrationShadowHookAttached;
        private bool _orchestrationShadowHookInstalling;

        internal void InstallOrchestrationShadowHook()
        {
            if (_orchestrationShadowHookAttached || _orchestrationShadowHookInstalling)
                return;

            AttachOrchestrationShadowHandlerSafeAsync();
        }

        private async void AttachOrchestrationShadowHandlerSafeAsync()
        {
            if (_orchestrationShadowHookAttached || _orchestrationShadowHookInstalling)
                return;

            _orchestrationShadowHookInstalling = true;
            try
            {
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
            finally
            {
                _orchestrationShadowHookInstalling = false;
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

                JObject command = null;
                try { command = JObject.Parse(trimmed); }
                catch { }

                if (command != null && command["type"] != null)
                    return;

                // A confirmation turn belongs to the already pending plan. Do not
                // decompose it as a fresh orchestration prompt. The legacy handler
                // remains untouched and continues its normal visible flow.
                if (JarvisExecutionShadowHarness.TryResumeConfirmation(
                    _orchestrationPendingConfirmation,
                    userText))
                    return;

                JarvisExecutionShadowHarness.RunAndLogSafeAsync(
                    _xSupport,
                    userText,
                    _orchestrationPendingConfirmation);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] observer suppressed exception: " + ex);
            }
        }
    }
}

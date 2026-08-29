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
                        InstallOrchestrationPrimaryRouter();
                        _orchestrationShadowHookAttached = true;
                        DebugLog.Log("[ORCH-SHADOW] controlled Main Chat primary router attached.");
                        return;
                    }

                    await Task.Delay(50);
                }

                DebugLog.Log("[ORCH-SHADOW] Main Chat router not attached: CoreWebView2 was not ready in time.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] router attach failed; legacy chat unaffected: " + ex);
            }
            finally
            {
                _orchestrationShadowHookInstalling = false;
            }
        }

        internal void InstallOrchestrationPrimaryRouter()
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            // Exactly one primary router. All non-pilot traffic is delegated to
            // the mature DR/legacy router. This prevents the confirmation turn
            // from being processed once by legacy and once by controlled Echo.
            webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.WebMessageReceived -= DrRecognitionFlow_WebMessageReceived;
            webView.CoreWebView2.WebMessageReceived -= OrchestrationPrimary_WebMessageReceived;
            webView.CoreWebView2.WebMessageReceived += OrchestrationPrimary_WebMessageReceived;
        }

        private async void OrchestrationPrimary_WebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string userText = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(userText))
                {
                    DrRecognitionFlow_WebMessageReceived(sender, e);
                    return;
                }

                string trimmed = userText.Trim();

                // Typed UI/DR commands remain entirely on the mature deterministic
                // router. The controlled pilot observes only ordinary Main Chat.
                JObject command = null;
                try { command = JObject.Parse(trimmed); }
                catch { }
                if (command != null && command["type"] != null)
                {
                    DrRecognitionFlow_WebMessageReceived(sender, e);
                    return;
                }

                if (string.Equals(trimmed, "Stop", StringComparison.OrdinalIgnoreCase))
                {
                    DrRecognitionFlow_WebMessageReceived(sender, e);
                    return;
                }

                if (_orchestrationPendingConfirmation.HasPending &&
                    JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText))
                {
                    JarvisControlledPilotOutcome resumed =
                        await JarvisExecutionShadowHarness.TryResumeConfirmationAndExecuteAsync(
                            _xSupport,
                            _orchestrationPendingConfirmation,
                            userText);

                    if (resumed != null && resumed.Handled)
                    {
                        if (!string.IsNullOrWhiteSpace(resumed.UserMessage))
                            webView.CoreWebView2.PostWebMessageAsString(resumed.UserMessage);
                        return;
                    }
                }

                JarvisControlledPilotOutcome pilot =
                    await JarvisExecutionShadowHarness.TryRunControlledPilotAsync(
                        _xSupport,
                        userText,
                        _orchestrationPendingConfirmation);

                if (pilot != null && pilot.Handled)
                {
                    if (!string.IsNullOrWhiteSpace(pilot.UserMessage))
                        webView.CoreWebView2.PostWebMessageAsString(pilot.UserMessage);
                    return;
                }

                // Unsupported/invalid plans continue through the existing product
                // path unchanged.
                DrRecognitionFlow_WebMessageReceived(sender, e);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] primary router suppressed exception: " + ex);
                try
                {
                    DrRecognitionFlow_WebMessageReceived(sender, e);
                }
                catch (Exception fallbackEx)
                {
                    DebugLog.Log("[ORCH-SHADOW] legacy fallback failed: " + fallbackEx);
                }
            }
        }
    }
}

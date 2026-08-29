using System;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
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
                if (_registered) return true;
                EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
                _registered = true;
                return true;
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            JarvisShell shell = sender as JarvisShell;
            if (shell != null) shell.InstallOrchestrationShadowHook();
        }
    }

    public partial class JarvisShell
    {
        private readonly bool _orchestrationShadowBootstrapRegistered = JarvisOrchestrationShadowBootstrap.EnsureRegistered();
        private readonly JarvisPendingConfirmationSession _orchestrationPendingConfirmation = new JarvisPendingConfirmationSession();
        private readonly JarvisDatasetSession _orchestrationDatasetSession = new JarvisDatasetSession();

        private bool _orchestrationShadowHookAttached;
        private bool _orchestrationShadowHookInstalling;

        internal void InstallOrchestrationShadowHook()
        {
            if (_orchestrationShadowHookAttached || _orchestrationShadowHookInstalling) return;
            AttachOrchestrationShadowHandlerSafeAsync();
        }

        private async void AttachOrchestrationShadowHandlerSafeAsync()
        {
            if (_orchestrationShadowHookAttached || _orchestrationShadowHookInstalling) return;
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
            if (webView == null || webView.CoreWebView2 == null) return;
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
                JObject command = null;
                try { command = JObject.Parse(trimmed); } catch { }
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

                // Confirmation always wins over dataset refinement: it resumes the
                // exact frozen action and must never be interpreted as a new filter.
                if (_orchestrationPendingConfirmation.HasPending &&
                    JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText))
                {
                    JarvisControlledPilotOutcome resumed = await JarvisExecutionShadowHarness.TryResumeConfirmationAndExecuteAsync(
                        _xSupport, _orchestrationPendingConfirmation, userText);
                    if (resumed != null && resumed.Handled)
                    {
                        if (!string.IsNullOrWhiteSpace(resumed.UserMessage))
                        {
                            // Echo + final Jarvis validation are local/non-AI on this
                            // turn, so keep the visual usage footer truthful and stable.
                            await PushLocalJarvisUsageMarkerAsync();
                            webView.CoreWebView2.PostWebMessageAsString(resumed.UserMessage);
                        }
                        return;
                    }
                }

                // Cheap lexical gate avoids an AI call on unrelated new prompts.
                // Only likely follow-ups are offered to the local dataset planner.
                if (!_orchestrationPendingConfirmation.HasPending &&
                    _orchestrationDatasetSession.HasDataset &&
                    JarvisDatasetSession.LooksLikeRefinement(userText))
                {
                    JarvisDatasetRefinementOutcome refined = await _orchestrationDatasetSession.TryRefineAsync(_xSupport, userText);
                    if (refined != null && refined.Handled)
                    {
                        if (!string.IsNullOrWhiteSpace(refined.UserMessage))
                            webView.CoreWebView2.PostWebMessageAsString(refined.UserMessage);
                        return;
                    }
                }

                JarvisControlledPilotOutcome pilot = await JarvisExecutionShadowHarness.TryRunControlledPilotAsync(
                    _xSupport, userText, _orchestrationPendingConfirmation, _orchestrationDatasetSession);
                if (pilot != null && pilot.Handled)
                {
                    if (!string.IsNullOrWhiteSpace(pilot.UserMessage))
                        webView.CoreWebView2.PostWebMessageAsString(pilot.UserMessage);
                    return;
                }

                DrRecognitionFlow_WebMessageReceived(sender, e);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] primary router suppressed exception: " + ex);
                try { DrRecognitionFlow_WebMessageReceived(sender, e); }
                catch (Exception fallbackEx) { DebugLog.Log("[ORCH-SHADOW] legacy fallback failed: " + fallbackEx); }
            }
        }

        private async Task PushLocalJarvisUsageMarkerAsync()
        {
            try
            {
                if (webView == null || webView.CoreWebView2 == null) return;
                JObject payload = new JObject
                {
                    ["inputTokens"] = 0,
                    ["outputTokens"] = 0,
                    ["model"] = "JARVIS",
                    ["provider"] = "local",
                    ["logged"] = true
                };
                string script = "if(window.__jarvisUsagePush){window.__jarvisUsagePush(" +
                                payload.ToString(Formatting.None) + ");}";
                await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] local usage marker failed: " + ex.Message);
            }
        }
    }
}

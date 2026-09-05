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
            if (shell == null) return;
            shell.InitializeJarvisSessionIdentity();
            shell.InstallOrchestrationShadowHook();
        }
    }

    public partial class JarvisShell
    {
        private readonly bool _orchestrationShadowBootstrapRegistered = JarvisOrchestrationShadowBootstrap.EnsureRegistered();
        private readonly JarvisPendingConfirmationSession _orchestrationPendingConfirmation = new JarvisPendingConfirmationSession();
        private readonly JarvisDatasetSession _orchestrationDatasetSession = new JarvisDatasetSession();
        private readonly JarvisActiveOrchestrationContext _orchestrationActiveContext = new JarvisActiveOrchestrationContext();
        private JarvisRuntimeContext _orchestrationSessionContext;

        private bool _orchestrationShadowHookAttached;
        private bool _orchestrationShadowHookInstalling;

        internal void InitializeJarvisSessionIdentity()
        {
            if (_orchestrationSessionContext != null) return;
            _orchestrationSessionContext = JarvisRuntimeContext.StartSession(_xSupport);
        }

        internal void InstallOrchestrationShadowHook()
        {
            if (_orchestrationSessionContext == null) InitializeJarvisSessionIdentity();
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
                        DebugLog.Log("[ORCH-SHADOW] unified Main Chat router attached.");
                        return;
                    }
                    await Task.Delay(50);
                }
                DebugLog.Log("[ORCH-SHADOW] Main Chat router not attached: CoreWebView2 was not ready in time.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] unified router attach failed: " + ex);
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
            bool businessTurnActivityStarted = false;
            try
            {
                string userText = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(userText)) return;

                string trimmed = userText.Trim();
                JObject command = null;
                try { command = JObject.Parse(trimmed); } catch { }

                // Structured UI commands are not business-response paths.
                if (command != null && command["type"] != null)
                {
                    DrRecognitionFlow_WebMessageReceived(sender, e);
                    return;
                }
                if (string.Equals(trimmed, "__JARVIS_STOP__", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "HEALTH", StringComparison.OrdinalIgnoreCase))
                {
                    DrRecognitionFlow_WebMessageReceived(sender, e);
                    return;
                }

                // Central policy: every ordinary business turn starts one shared
                // activity lifecycle before decomposition/planning and ends in finally.
                if (JarvisPolicySettings.Orchestration.ActivityLifecycleCoversEveryBusinessTurn)
                {
                    businessTurnActivityStarted = true;
                    JarvisOrchestrationActivityBus.BeginBusinessTurn();
                }

                if (_orchestrationPendingConfirmation.HasPending &&
                    JarvisPendingConfirmationSession.IsAffirmativeConfirmation(userText))
                {
                    JarvisControlledPilotOutcome resumed = await JarvisExecutionShadowHarness.TryResumeConfirmationAndExecuteAsync(
                        _xSupport, _orchestrationPendingConfirmation, userText, _orchestrationActiveContext);
                    if (resumed != null && resumed.Handled)
                    {
                        if (!string.IsNullOrWhiteSpace(resumed.UserMessage))
                        {
                            await PushLocalJarvisUsageMarkerAsync();
                            PostMainChatPresentation(resumed.UserMessage);
                        }
                        return;
                    }
                }

                if (!_orchestrationPendingConfirmation.HasPending &&
                    _orchestrationDatasetSession.HasDataset)
                {
                    JarvisDatasetRefinementOutcome refined = await _orchestrationDatasetSession.TryRefineAsync(
                        _xSupport, _orchestrationActiveContext.RunId, userText);
                    if (refined != null && refined.Handled)
                    {
                        if (!string.IsNullOrWhiteSpace(refined.UserMessage))
                            PostMainChatPresentation(refined.UserMessage);
                        return;
                    }
                }

                JarvisControlledPilotOutcome pilot = null;
                if (JarvisExecutionShadowHarness.ShouldAttemptControlledPilot(
                    userText, _orchestrationActiveContext))
                {
                    pilot = await JarvisExecutionShadowHarness.TryRunControlledPilotAsync(
                        _xSupport, userText, _orchestrationPendingConfirmation, _orchestrationDatasetSession,
                        _orchestrationActiveContext, _orchestrationSessionContext);
                }
                else
                {
                    DebugLog.Log("[ORCH-CONTROL] skipped semantic planner: no promoted task intent hint.");
                }

                if (pilot != null && pilot.Handled)
                {
                    if (!string.IsNullOrWhiteSpace(pilot.UserMessage))
                        PostMainChatPresentation(pilot.UserMessage);
                    return;
                }

                // Compatibility processing may remain for unpromoted tasks, but it
                // cannot own final presentation. Capture only protocol messages
                // added by THIS compatibility turn so verified tool results can be
                // materialized by the same central addressable-link policy.
                int legacyTraceStart = _conversation == null ? 0 : _conversation.Count;
                string fallbackAnswer = await RunLegacyAgentAsProcessingEngineAsync(userText);
                string[] verifiedLinks = JarvisResultLinkMaterializer.BuildMarkdownLinksFromLegacyTrace(
                    _conversation, legacyTraceStart);
                fallbackAnswer = JarvisResultLinkMaterializer.AppendMissingVerifiedLinks(
                    fallbackAnswer, verifiedLinks);
                PostMainChatPresentation(fallbackAnswer);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] unified Main Chat router exception: " + ex);
                PostMainChatPresentation(JarvisPresentationGateway.BuildFailureMessage(
                    "✖ Απρόσμενο σφάλμα - δοκίμασε ξανά ή ξανάνοιξε τον Jarvis.",
                    new[] { ex.Message }));
            }
            finally
            {
                if (businessTurnActivityStarted)
                    JarvisOrchestrationActivityBus.EndBusinessTurn();
            }
        }

        private async Task<string> RunLegacyAgentAsProcessingEngineAsync(string userText)
        {
            if (string.IsNullOrEmpty(_agentAccountRef))
                return "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis.";

            try
            {
                return await _agentClient.AskAsync(
                    _agentAccountRef, _xSupport, _conversation, userText,
                    onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonThinkingUpdate(t)),
                    onShowContactResults: contacts => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "show_contact_results_data", ["contacts"] = contacts }.ToString(Formatting.None)),
                    maxIterations: JarvisTools.GetCrmTaskOptionalParam(_xSupport, 500028, 40),
                    routingHint: _lastMainChatMode,
                    onModeChosen: mode => _lastMainChatMode = mode,
                    onExportShownTable: async (format, rowIndices) =>
                    {
                        try
                        {
                            string rowIndicesJson = rowIndices != null
                                ? JsonConvert.SerializeObject(rowIndices)
                                : "null";
                            string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                                $"window.triggerTableExport(\"{JsEscape(format)}\", {rowIndicesJson})");
                            return JsonConvert.DeserializeObject<string>(raw);
                        }
                        catch (Exception exportEx)
                        {
                            DebugLog.Log("[ORCH-SHADOW] export processing callback failed: " + exportEx);
                            return null;
                        }
                    });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ORCH-SHADOW] legacy processing engine failed: " + ex);
                return JarvisPresentationGateway.BuildFailureMessage("✖ Σφάλμα:", new[] { ex.Message });
            }
        }

        private void PostMainChatPresentation(string rawContent)
        {
            if (webView == null || webView.CoreWebView2 == null) return;
            string finalContent = JarvisPresentationGateway.FinalizeFreeform(rawContent);
            webView.CoreWebView2.PostWebMessageAsString(finalContent);
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

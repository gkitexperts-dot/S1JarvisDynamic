using System;
using System.Threading.Tasks;
using System.Windows;
using S1Jarvis.Access.Verilic;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        // Static field initializer is used instead of another static constructor
        // because JarvisShell.UatRunner already owns the type static constructor.
        private static readonly bool ProviderBootGateClassHandlerRegistered =
            RegisterProviderBootGateClassHandler();

        private bool _providerBootGateStarted;

        private static bool RegisterProviderBootGateClassHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_ProviderBootGateLoaded));
            return true;
        }

        private static void JarvisShell_ProviderBootGateLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null)
                shell.StartProviderBootGate();
        }

        private async void StartProviderBootGate()
        {
            if (_providerBootGateStarted)
                return;

            _providerBootGateStarted = true;
            try
            {
                // First wait only for the WebView DOM. Do NOT wait for licence or
                // provider checks before applying the gate: during boot the user
                // must not be able to attach a file or submit a prompt.
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                        break;
                    await Task.Delay(50);
                }

                if (webView == null || webView.CoreWebView2 == null)
                {
                    DebugLog.Log("[provider-boot-gate] webview_not_ready");
                    return;
                }

                // Local file links are rendered through the WebView2 page. Chromium
                // may URL-encode spaces in a filesystem path (for example
                // "Jarvis Exports" -> "Jarvis%20Exports") before postCommand sends
                // the open_file command to C#. Normalize only that command at the UI
                // boundary so File.Exists/Process.Start always receive a Windows path.
                // This is generic Jarvis UI behavior and is intentionally unrelated
                // to the selected AI provider or agent routing.
                await InstallLocalFilePathNormalizationAsync();

                await SetProviderBootInteractionLockedAsync(true);

                // This class-level Loaded handler is the guaranteed startup entry.
                // Start provisioning here directly; do not wait for a second Loaded
                // subscription that may never be attached or fired.
                if (!_providerHealthCheckEnabled)
                {
                    _providerHealthCheckEnabled = true;
                    Unloaded += ProviderHealthCheck_Unloaded;
                }

                JarvisAgentRuntimeSnapshot.Reset();
                VerilicAiMessagesClient.ResetRuntimeTargetSnapshot();
                DebugLog.Log("[AI-SESSION-REGISTRY] boot provisioning start (boot gate)");
                await RefreshProviderHealthStatusAsync(false);

                // ProviderHealth owns the authoritative startup state. Keep the
                // gate closed through licence/routing/model/provider checks and
                // release only after the final provider state is Ready.
                for (int attempt = 0; attempt < 600; attempt++)
                {
                    if (string.Equals(_providerHealthState, "ready", StringComparison.Ordinal))
                    {
                        await SetProviderBootInteractionLockedAsync(false);
                        return;
                    }

                    // An error is a final visible provider state, but the provider
                    // is not usable. Keep the two action buttons disabled instead
                    // of letting a prompt/file enter a known-broken runtime.
                    if (string.Equals(_providerHealthState, "error", StringComparison.Ordinal))
                        return;

                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[provider-boot-gate] EXCEPTION: " + ex);
                // Fail closed: if the boot gate itself fails, do not deliberately
                // re-enable interaction before provider readiness is established.
            }
        }

        private async Task InstallLocalFilePathNormalizationAsync()
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            string script =
                "(function(){" +
                "if(window.__jarvisLocalFilePathFixInstalled)return;" +
                "if(typeof window.postCommand!=='function')return;" +
                "var originalPostCommand=window.postCommand;" +
                "window.postCommand=function(cmd){" +
                "if(cmd&&cmd.type==='open_file'&&typeof cmd.path==='string'){" +
                "try{cmd=Object.assign({},cmd,{path:decodeURIComponent(cmd.path)});}catch(_e){}" +
                "}" +
                "return originalPostCommand(cmd);" +
                "};" +
                "window.__jarvisLocalFilePathFixInstalled=true;" +
                "})();";

            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private async Task SetProviderBootInteractionLockedAsync(bool locked)
        {
            if (webView == null || webView.CoreWebView2 == null)
                return;

            string lockedJs = locked ? "true" : "false";
            string script =
                "(function(){" +
                "var locked=" + lockedJs + ";" +
                "var root=document.documentElement;" +
                "root.setAttribute('data-jarvis-provider-boot-locked',locked?'1':'0');" +
                "var attach=document.getElementById('attachBtn');" +
                "var file=document.getElementById('fileInput');" +
                "var send=document.getElementById('sendBtn');" +
                "function apply(){" +
                "var isLocked=root.getAttribute('data-jarvis-provider-boot-locked')==='1';" +
                "if(attach){if(attach.disabled!==isLocked)attach.disabled=isLocked;" +
                "attach.style.opacity=isLocked?'0.35':'';attach.style.cursor=isLocked?'default':'';" +
                "attach.setAttribute('aria-disabled',isLocked?'true':'false');}" +
                "if(file&&file.disabled!==isLocked)file.disabled=isLocked;" +
                "if(send&&isLocked){if(!send.disabled)send.disabled=true;send.classList.remove('ready','stoppable');" +
                "send.setAttribute('aria-disabled','true');send.style.opacity='0.35';send.style.cursor='default';}" +
                "if(send&&!isLocked){send.removeAttribute('aria-disabled');send.style.opacity='';send.style.cursor='';" +
                "if(typeof updateSendState==='function')updateSendState();}" +
                "}" +
                "if(!window.__jarvisProviderBootGateInstalled){" +
                "window.__jarvisProviderBootGateInstalled=true;" +
                "document.addEventListener('click',function(ev){" +
                "if(root.getAttribute('data-jarvis-provider-boot-locked')!=='1')return;" +
                "var t=ev.target&&ev.target.closest?ev.target.closest('#attachBtn,#sendBtn'):null;" +
                "if(t){ev.preventDefault();ev.stopImmediatePropagation();}" +
                "},true);" +
                "var mo=new MutationObserver(function(){" +
                "if(root.getAttribute('data-jarvis-provider-boot-locked')==='1')apply();" +
                "});" +
                "if(attach)mo.observe(attach,{attributes:true,attributeFilter:['disabled']});" +
                "if(send)mo.observe(send,{attributes:true,attributeFilter:['disabled']});" +
                "}" +
                "apply();" +
                "})();";

            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}

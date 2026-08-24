using System;
using System.Threading.Tasks;
using System.Windows;
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
                    return;

                await SetProviderBootInteractionLockedAsync(true);

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

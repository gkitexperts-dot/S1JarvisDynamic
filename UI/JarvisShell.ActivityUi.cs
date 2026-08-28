using System;
using System.Windows;
using Newtonsoft.Json;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool _jarvisActivityBootstrapRegistered = RegisterJarvisActivityBootstrap();
        private bool _jarvisActivityCoreHooked;

        private static bool RegisterJarvisActivityBootstrap()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisActivityLoaded),
                true);
            return true;
        }

        private static void JarvisActivityLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;
            shell.webView.CoreWebView2InitializationCompleted -= shell.JarvisActivityCoreInitialized;
            shell.webView.CoreWebView2InitializationCompleted += shell.JarvisActivityCoreInitialized;
            if (shell.webView.CoreWebView2 != null) shell.InstallJarvisActivityUi();
        }

        private void JarvisActivityCoreInitialized(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess) InstallJarvisActivityUi();
        }

        private void InstallJarvisActivityUi()
        {
            if (_jarvisActivityCoreHooked || webView.CoreWebView2 == null) return;
            _jarvisActivityCoreHooked = true;
            DebugLog.Log("[JARVIS-ACTIVITY] shared activity UI installed");
        }

        private async void PostJarvisActivity(string action, string channel, string text = null, bool suppressAssistant = false)
        {
            try
            {
                if (webView.CoreWebView2 == null) return;

                string jsAction = JsonConvert.SerializeObject(action ?? "update");
                string jsChannel = JsonConvert.SerializeObject(channel ?? "main");
                string jsText = JsonConvert.SerializeObject(text ?? string.Empty);

                string script =
                    "(function(){" +
                    "var action=" + jsAction + ",channel=" + jsChannel + ",text=" + jsText + ";" +
                    "var map={main:'transcript',browser:'browserTranscript',help:'helpTranscript',email:'emailTranscript',courier:'courierTranscript',dr:'drTranscript'};" +
                    "var host=document.getElementById(map[channel]||'transcript');if(!host)return;" +
                    "var id='jarvisActivity_'+channel;var el=document.getElementById(id);" +
                    "if(action==='end'||action==='complete'){if(el&&el.parentNode)el.parentNode.removeChild(el);" +
                    "if(action==='complete'&&text){var fn=channel==='main'?window.addMessage:channel==='browser'?window.addBrowserMessage:channel==='help'?window.addHelpMessage:channel==='email'?window.addEmailMessage:channel==='courier'?window.addCourierMessage:null;if(typeof fn==='function')fn('assistant',text);}" +
                    "return;}" +
                    "if(!el){el=document.createElement('div');el.id=id;el.style.cssText='align-self:flex-start;padding:2px 8px 5px 8px;font-size:12px;line-height:1.35;opacity:.62;font-style:italic;';host.appendChild(el);}" +
                    "el.textContent='• '+(text||'Επεξεργασία…');host.scrollTop=host.scrollHeight;" +
                    "})();";

                await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-ACTIVITY] post failed: " + ex.Message);
            }
        }
    }
}

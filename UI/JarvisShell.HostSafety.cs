using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool HostSafetyClassHandlerRegistered = RegisterHostSafetyClassHandler();
        private bool _hostSafetyInitHooked;

        private static bool RegisterHostSafetyClassHandler()
        {
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_HostSafetyLoaded));
            return true;
        }

        private static void JarvisShell_HostSafetyLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell == null) return;
            shell.EnsureHostSafetyRouterEnforcement();
        }

        private void EnsureHostSafetyRouterEnforcement()
        {
            try
            {
                if (webView == null)
                {
                    DebugLog.Log("[host-safety] webView is null; router enforcement deferred.");
                    return;
                }

                if (webView.CoreWebView2 != null)
                {
                    Dispatcher.BeginInvoke(new Action(EnforceSinglePrimaryWebMessageRouter),
                        DispatcherPriority.ContextIdle);
                    return;
                }

                if (_hostSafetyInitHooked) return;
                _hostSafetyInitHooked = true;
                webView.CoreWebView2InitializationCompleted += WebView_HostSafetyInitializationCompleted;
                DebugLog.Log("[host-safety] waiting for CoreWebView2 initialization.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[host-safety] enforcement hook EXCEPTION: " + ex);
            }
        }

        private void WebView_HostSafetyInitializationCompleted(object sender,
            CoreWebView2InitializationCompletedEventArgs e)
        {
            try
            {
                if (webView != null)
                    webView.CoreWebView2InitializationCompleted -= WebView_HostSafetyInitializationCompleted;
                _hostSafetyInitHooked = false;

                if (!e.IsSuccess)
                {
                    DebugLog.Log("[host-safety] CoreWebView2 initialization failed; router not enforced. " +
                        (e.InitializationException == null ? string.Empty : e.InitializationException.ToString()));
                    return;
                }

                Dispatcher.BeginInvoke(new Action(EnforceSinglePrimaryWebMessageRouter),
                    DispatcherPriority.ContextIdle);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[host-safety] initialization-completed EXCEPTION: " + ex);
            }
        }

        private void EnforceSinglePrimaryWebMessageRouter()
        {
            try
            {
                if (webView == null || webView.CoreWebView2 == null)
                {
                    DebugLog.Log("[host-safety] CoreWebView2 unavailable at enforcement time.");
                    return;
                }

                webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.WebMessageReceived -= DrRecognitionFlow_WebMessageReceived;
                webView.CoreWebView2.WebMessageReceived += DrRecognitionFlow_WebMessageReceived;

                DebugLog.Log("[host-safety] single primary WebMessageReceived router enforced; legacy duplicate removed.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[host-safety] router enforcement EXCEPTION: " + ex);
            }
        }
    }
}

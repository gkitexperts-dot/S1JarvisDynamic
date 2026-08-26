using System;
using System.Windows;
using System.Windows.Threading;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool HostSafetyClassHandlerRegistered = RegisterHostSafetyClassHandler();

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

            // Loaded handlers are invoked in registration order. JarvisShell_Loaded may
            // subscribe the legacy async WebMessageReceived handler after CoreWebView2's
            // initialization-completed event has already installed the synchronous DR
            // router. Re-apply routing after the current Loaded dispatch completes so
            // there is exactly one primary message entry point.
            shell.Dispatcher.BeginInvoke(new Action(shell.EnforceSinglePrimaryWebMessageRouter),
                DispatcherPriority.ContextIdle);
        }

        private void EnforceSinglePrimaryWebMessageRouter()
        {
            try
            {
                if (webView == null || webView.CoreWebView2 == null) return;

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

using System;
using System.Reflection;
using System.Windows;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool ExternalDropClassHandlerRegistered = RegisterExternalDropClassHandler();

        private static bool RegisterExternalDropClassHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_ExternalDropLoaded));
            return true;
        }

        private static void JarvisShell_ExternalDropLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null)
                shell.EnsureExternalDropEnabled();
        }

        private void EnsureExternalDropEnabled()
        {
            try
            {
                if (webView == null)
                {
                    DebugLog.Log("[drag-drop] webView is null; external-drop enable skipped.");
                    return;
                }

                // WPF host-level opt-in. This is intentionally explicit even
                // though WebView2 also has its own AllowExternalDrop switch.
                webView.AllowDrop = true;

                // Use reflection so the Jarvis runtime remains compatible with
                // the exact WebView2 WPF assembly supplied by the Soft1 host.
                // Newer WebView2 builds expose AllowExternalDrop directly on the
                // WPF control. Setting it before controller initialization is
                // supported: WebView2 carries the value into the controller.
                PropertyInfo property = webView.GetType().GetProperty(
                    "AllowExternalDrop",
                    BindingFlags.Instance | BindingFlags.Public);

                if (property == null || !property.CanWrite)
                {
                    DebugLog.Log("[drag-drop] WebView2 AllowExternalDrop property unavailable; WPF AllowDrop=true only.");
                    return;
                }

                property.SetValue(webView, true, null);

                object actual = null;
                try { actual = property.GetValue(webView, null); }
                catch { }

                DebugLog.Log("[drag-drop] external drop explicitly enabled; WPF AllowDrop=" +
                    webView.AllowDrop + " WebView2 AllowExternalDrop=" +
                    (actual == null ? "unknown" : Convert.ToString(actual)) + ".");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[drag-drop] external-drop enable EXCEPTION: " + ex);
            }
        }
    }
}

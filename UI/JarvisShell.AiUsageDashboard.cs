using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool AiUsageDashboardClassHandlerRegistered = RegisterAiUsageDashboardClassHandler();
        private bool _aiUsageDashboardStarted;

        private static bool RegisterAiUsageDashboardClassHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_AiUsageDashboardLoaded));
            return true;
        }

        private static void JarvisShell_AiUsageDashboardLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null) shell.StartAiUsageDashboardUi();
        }

        private async void StartAiUsageDashboardUi()
        {
            if (_aiUsageDashboardStarted) return;
            _aiUsageDashboardStarted = true;

            try
            {
                // Same safe pattern used by the DR embedded UI modules: wait
                // until the real embedded index.html has loaded and its
                // Dashboard DOM exists, then inject one idempotent script.
                for (int attempt = 0; attempt < 240; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        string ready = await webView.CoreWebView2.ExecuteScriptAsync(
                            "(document.getElementById('dashboardCurtain')&&document.getElementById('dashboardPagesTrack'))?'ready':''");
                        if (!string.IsNullOrWhiteSpace(ready) &&
                            ready.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            await ExecuteEmbeddedAiUsageDashboardScriptAsync();
                            DebugLog.Log("[ai-usage-dashboard] UI installed");
                            return;
                        }
                    }
                    await Task.Delay(50);
                }

                DebugLog.Log("[ai-usage-dashboard] UI install timed out waiting for dashboard DOM");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[ai-usage-dashboard] startup EXCEPTION: " + ex);
            }
        }

        private async Task ExecuteEmbeddedAiUsageDashboardScriptAsync()
        {
            const string resourceName = "S1Jarvis.web.ai-usage-dashboard.js";
            var asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Missing embedded AI usage dashboard script: " + resourceName);

                using (var reader = new StreamReader(stream))
                {
                    string script = await reader.ReadToEndAsync();
                    await webView.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
        }
    }
}

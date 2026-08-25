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
        private static readonly bool DrWorkflowUiClassHandlerRegistered = RegisterDrWorkflowUiClassHandler();
        private bool _drWorkflowUiStarted;

        private static bool RegisterDrWorkflowUiClassHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_DrWorkflowUiLoaded));
            return true;
        }

        private static void JarvisShell_DrWorkflowUiLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null) shell.StartDrWorkflowUi();
        }

        private async void StartDrWorkflowUi()
        {
            if (_drWorkflowUiStarted) return;
            _drWorkflowUiStarted = true;
            try
            {
                for (int attempt = 0; attempt < 240; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        string ready = await webView.CoreWebView2.ExecuteScriptAsync(
                            "(typeof renderDrLinesPanel==='function'&&document.getElementById('drFileList'))?'ready':''");
                        if (!string.IsNullOrWhiteSpace(ready) &&
                            ready.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            await InstallDrWorkflowUiAsync();
                            return;
                        }
                    }
                    await Task.Delay(50);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-workflow-ui] startup EXCEPTION: " + ex);
            }
        }

        private async Task InstallDrWorkflowUiAsync()
        {
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-workflow-enhancements.js");
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-session-loop.js");
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-recognition-workspace.js");
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-posting-proposal-ui.js");
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-auto-recognition.js");
        }

        private async Task ExecuteEmbeddedDrScriptAsync(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Missing embedded DR workflow script: " + resourceName);
                using (var reader = new StreamReader(stream))
                {
                    string script = await reader.ReadToEndAsync();
                    await webView.CoreWebView2.ExecuteScriptAsync(script);
                }
            }
        }
    }
}

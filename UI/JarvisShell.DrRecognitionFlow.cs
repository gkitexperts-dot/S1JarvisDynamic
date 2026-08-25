using System;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool DrRecognitionFlowClassHandlerRegistered = RegisterDrRecognitionFlowClassHandler();
        private bool _drRecognitionFlowStarted;

        private static bool RegisterDrRecognitionFlowClassHandler()
        {
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_DrRecognitionFlowLoaded));
            return true;
        }

        private static void JarvisShell_DrRecognitionFlowLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null) shell.StartDrRecognitionFlow();
        }

        private async void StartDrRecognitionFlow()
        {
            if (_drRecognitionFlowStarted) return;
            _drRecognitionFlowStarted = true;
            try
            {
                for (int attempt = 0; attempt < 240; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                    {
                        webView.CoreWebView2.WebMessageReceived += DrRecognitionFlow_WebMessageReceived;
                        return;
                    }
                    await Task.Delay(50);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] startup EXCEPTION: " + ex);
            }
        }

        private async void DrRecognitionFlow_WebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject cmd;
            try { cmd = JObject.Parse(e.TryGetWebMessageAsString()); }
            catch { return; }

            string commandType = (string)cmd["type"];
            if (!string.Equals(commandType, "dr_resolve_document_pattern", StringComparison.Ordinal) &&
                !string.Equals(commandType, "dr_analyze_posting", StringComparison.Ordinal)) return;

            string fileId = cmd["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int sourceLineCount = (int?)cmd["sourceLineCount"] ?? 0;
                string documentType = cmd["documentType"]?.ToString();
                string documentSeries = cmd["documentSeries"]?.ToString();

                JObject result = await Task.Run(() =>
                    DrDocumentPatternResolver.Resolve(
                        _xSupport, trdrId, documentType, documentSeries, sourceLineCount));

                result["type"] = "dr_document_pattern_result";
                result["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] resolve_document_pattern EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_document_pattern_result",
                    ["fileId"] = fileId,
                    ["success"] = false,
                    ["resolver"] = "resolve_document_pattern",
                    ["version"] = 3,
                    ["mode"] = "Unknown",
                    ["needsReview"] = true,
                    ["reason"] = "pattern_resolution_failed",
                    ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }
    }
}

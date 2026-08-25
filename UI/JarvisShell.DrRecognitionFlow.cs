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
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
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

        private async void DrRecognitionFlow_WebMessageReceived(
            object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject cmd;
            try
            {
                string raw = e.TryGetWebMessageAsString();
                cmd = JObject.Parse(raw);
            }
            catch
            {
                return;
            }

            if (!string.Equals((string)cmd["type"], "dr_analyze_posting", StringComparison.Ordinal))
                return;

            string fileId = cmd["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int series = (int?)cmd["series"] ?? 0;
                int sosource = (int?)cmd["sosource"] ?? 0;
                int sourceLineCount = (int?)cmd["sourceLineCount"] ?? 0;

                JObject result = await Task.Run(() =>
                    DrPostingProposal.Analyze(_xSupport, trdrId, series, sosource, sourceLineCount));

                result["type"] = "dr_posting_proposal_result";
                result["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] posting proposal EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_posting_proposal_result",
                    ["fileId"] = fileId,
                    ["success"] = false,
                    ["mode"] = "Unknown",
                    ["needsReview"] = true,
                    ["reason"] = "proposal_failed",
                    ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }
    }
}

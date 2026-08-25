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
        private static readonly bool DrTraderRoleFlowClassHandlerRegistered = RegisterDrTraderRoleFlowClassHandler();
        private bool _drTraderRoleFlowStarted;

        private static bool RegisterDrTraderRoleFlowClassHandler()
        {
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_DrTraderRoleFlowLoaded));
            return true;
        }

        private static void JarvisShell_DrTraderRoleFlowLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null) shell.StartDrTraderRoleFlow();
        }

        private async void StartDrTraderRoleFlow()
        {
            if (_drTraderRoleFlowStarted) return;
            _drTraderRoleFlowStarted = true;
            for (int i = 0; i < 240; i++)
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    webView.CoreWebView2.WebMessageReceived += DrTraderRoleFlow_WebMessageReceived;
                    return;
                }
                await Task.Delay(50);
            }
        }

        private async void DrTraderRoleFlow_WebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject cmd;
            try { cmd = JObject.Parse(e.TryGetWebMessageAsString()); }
            catch { return; }
            if (!string.Equals((string)cmd["type"], "dr_resolve_trader_roles", StringComparison.Ordinal)) return;

            string fileId = cmd["fileId"]?.ToString();
            try
            {
                string afm = (string)cmd["afm"];
                JObject result = await Task.Run(() => DrTraderRoleResolver.Resolve(_xSupport, afm));

                JObject preferred = result["preferredIncoming"] as JObject;
                if (preferred != null && (int?)preferred["trdrId"] > 0)
                {
                    int trdrId = (int)preferred["trdrId"];
                    string docType = (string)cmd["docType"];
                    string docNumber = (string)cmd["docNumber"];
                    string docDate = (string)cmd["docDate"];
                    result["seriesHistory"] = JObject.Parse(JarvisTools.ExecuteFindTraderSeriesHistory(_xSupport, trdrId, docType));
                    result["duplicateCheck"] = JObject.Parse(JarvisTools.ExecuteCheckDuplicateDocument(_xSupport, trdrId, docNumber, docDate));
                }

                result["type"] = "dr_trader_roles_result";
                result["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-trader-role] EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_trader_roles_result",
                    ["fileId"] = fileId,
                    ["success"] = false,
                    ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }
    }
}

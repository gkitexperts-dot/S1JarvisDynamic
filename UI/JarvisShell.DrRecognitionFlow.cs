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
                        await InstallDrRegistrationV2BridgeAsync();
                        return;
                    }
                    await Task.Delay(50);
                }
            }
            catch (Exception ex) { DebugLog.Log("[dr-recognition-flow] startup EXCEPTION: " + ex); }
        }

        // Keep the legacy UI untouched, but route the final DR write command to
        // this partial. Non-expense registrations are delegated unchanged to
        // JarvisTools; expense registrations use DrExpenseDocumentRegistrar so
        // LINLINES.LINEVAL is set inside the same Soft1 XModule transaction.
        private async Task InstallDrRegistrationV2BridgeAsync()
        {
            const string script = @"
(function(){
  if(window.__jarvisDrRegistrationV2Installed)return true;
  var original=window.postCommand;
  if(typeof original!=='function')return false;
  window.postCommand=function(payload){
    if(payload&&payload.type==='dr_register_document'){
      var copy=Object.assign({},payload);
      copy.type='dr_register_document_v2';
      return original(copy);
    }
    return original(payload);
  };
  window.__jarvisDrRegistrationV2Installed=true;
  return true;
})();";

            try
            {
                // Loaded can fire before index.html has defined postCommand.
                // Retry briefly instead of installing a dead wrapper once.
                for (int attempt = 0; attempt < 120; attempt++)
                {
                    string result = await webView.CoreWebView2.ExecuteScriptAsync(script);
                    if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                        return;
                    await Task.Delay(50);
                }
                DebugLog.Log("[dr-recognition-flow] registration-v2 bridge timed out waiting for postCommand.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] registration-v2 bridge EXCEPTION: " + ex);
            }
        }

        private async void DrRecognitionFlow_WebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject cmd;
            try { cmd = JObject.Parse(e.TryGetWebMessageAsString()); }
            catch { return; }

            string commandType = (string)cmd["type"];
            if (string.Equals(commandType, "dr_register_document_v2", StringComparison.Ordinal))
            {
                await HandleDrRegisterDocumentV2Async(cmd); return;
            }
            if (string.Equals(commandType, "dr_resolve_line_mappings", StringComparison.Ordinal))
            {
                await HandleDrResolveLineMappingsAsync(cmd); return;
            }
            if (string.Equals(commandType, "dr_select_precedent", StringComparison.Ordinal))
            {
                await HandleDrSelectPrecedentAsync(cmd); return;
            }
            if (string.Equals(commandType, "dr_confirm_precedent_mapping", StringComparison.Ordinal))
            {
                await HandleDrConfirmPrecedentMappingAsync(cmd); return;
            }
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
                    DrDocumentPatternResolver.Resolve(_xSupport, trdrId, documentType, documentSeries, sourceLineCount));
                result["type"] = "dr_document_pattern_result";
                result["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] resolve_document_pattern EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_document_pattern_result", ["fileId"] = fileId,
                    ["success"] = false, ["resolver"] = "resolve_document_pattern", ["version"] = 4,
                    ["mode"] = "Unknown", ["needsReview"] = true, ["reason"] = "pattern_resolution_failed",
                    ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrRegisterDocumentV2Async(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                string result = await Task.Run(() => DrExpenseDocumentRegistrar.Register(_xSupport, cmd));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_register_document_result";
                parsed["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));

                if ((bool?)parsed["success"] == true && (int?)parsed["findocId"] > 0)
                {
                    int sosource = (int?)parsed["sosource"] ?? (int?)cmd["sosource"] ?? 0;
                    int findocId = (int)parsed["findocId"];
                    try
                    {
                        JarvisTools.ExecuteOpenDocument(_xSupport,
                            new JObject { ["sosource"] = sosource, ["mode"] = "locate", ["id"] = findocId });
                    }
                    catch (Exception openEx)
                    {
                        DebugLog.Log("[dr-recognition-flow] registration-v2 auto-open EXCEPTION: " + openEx);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] register_document_v2 EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_register_document_result", ["fileId"] = fileId,
                    ["success"] = false, ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrConfirmPrecedentMappingAsync(JObject cmd)
        {
            string fileId = cmd["fileId"]?.ToString();
            try
            {
                if ((bool?)cmd["confirm"] != true)
                    throw new InvalidOperationException("Operator confirmation is required for CCCMAPITEMS learning.");
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int targetMtrlId = (int?)cmd["targetMtrlId"] ?? 0;
                JArray mappings = cmd["mappings"] as JArray ?? new JArray();
                JObject result = await Task.Run(() =>
                    DrItemCodeResolver.LearnMappings(_xSupport, trdrId, targetMtrlId, mappings));
                result["type"] = "dr_precedent_mapping_confirmed";
                result["fileId"] = fileId;
                result["operatorConfirmed"] = true;
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] confirm_precedent_mapping EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_precedent_mapping_confirmed", ["fileId"] = fileId,
                    ["success"] = false, ["resolver"] = "learn_supplier_code_mapping", ["version"] = 1,
                    ["operatorConfirmed"] = true, ["reason"] = "learning_write_failed", ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrSelectPrecedentAsync(JObject cmd)
        {
            string fileId = cmd["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int findocId = (int?)cmd["findocId"] ?? 0;
                JObject result = await Task.Run(() => DrPrecedentResolver.Resolve(_xSupport, trdrId, findocId));
                result["type"] = "dr_precedent_result";
                result["fileId"] = fileId;
                result["operatorSelected"] = true;
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] select_precedent EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_precedent_result", ["fileId"] = fileId, ["success"] = false,
                    ["resolver"] = "resolve_historical_precedent", ["version"] = 1,
                    ["operatorSelected"] = true, ["reason"] = "precedent_resolution_failed",
                    ["errorMessage"] = ex.Message, ["lines"] = new JArray()
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrResolveLineMappingsAsync(JObject cmd)
        {
            string fileId = cmd["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                JArray requestedLines = cmd["lines"] as JArray ?? new JArray();
                JArray results = await Task.Run(() =>
                {
                    var output = new JArray();
                    foreach (JToken token in requestedLines)
                    {
                        JObject line = token as JObject ?? new JObject();
                        JObject result = DrItemCodeResolver.Resolve(_xSupport, trdrId, line["supplierCode"]?.ToString());
                        result["lineIndex"] = (int?)line["lineIndex"] ?? -1;
                        output.Add(result);
                    }
                    return output;
                });
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_line_mappings_result", ["fileId"] = fileId, ["success"] = true,
                    ["resolver"] = "resolve_supplier_code_mapping", ["version"] = 2,
                    ["readOnly"] = true, ["trdrId"] = trdrId, ["results"] = results
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] resolve_line_mappings EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_line_mappings_result", ["fileId"] = fileId, ["success"] = false,
                    ["resolver"] = "resolve_supplier_code_mapping", ["version"] = 2,
                    ["readOnly"] = true, ["errorMessage"] = ex.Message, ["results"] = new JArray()
                }.ToString(Formatting.None));
            }
        }
    }
}
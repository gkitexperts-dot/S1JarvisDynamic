using System;
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

        // Soft1/XSupport integration is deliberately synchronous. The DR router is
        // installed only after CoreWebView2 exists; JarvisShell_Loaded calls this
        // method again immediately after WebView2 initialization.
        private void StartDrRecognitionFlow()
        {
            if (_drRecognitionFlowStarted) return;
            try
            {
                if (webView == null || webView.CoreWebView2 == null)
                {
                    DebugLog.Log("[dr-recognition-flow] router deferred until WebView2 is ready.");
                    return;
                }

                webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.WebMessageReceived -= DrRecognitionFlow_WebMessageReceived;
                webView.CoreWebView2.WebMessageReceived += DrRecognitionFlow_WebMessageReceived;
                _drRecognitionFlowStarted = true;
                DebugLog.Log("[dr-recognition-flow] installed synchronously as primary WebMessageReceived router.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-recognition-flow] startup EXCEPTION: " + ex);
            }
        }

        private void DrRecognitionFlow_WebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject cmd;
            try { cmd = JObject.Parse(e.TryGetWebMessageAsString()); }
            catch
            {
                CoreWebView2_WebMessageReceived(sender, e);
                return;
            }

            string commandType = (string)cmd["type"];

            if (string.Equals(commandType, "dr_register_document", StringComparison.Ordinal) ||
                string.Equals(commandType, "dr_register_document_v2", StringComparison.Ordinal))
            {
                HandleDrRegisterDocumentV2(cmd);
                return;
            }
            if (string.Equals(commandType, "dr_resolve_line_mappings", StringComparison.Ordinal))
            {
                HandleDrResolveLineMappings(cmd);
                return;
            }
            if (string.Equals(commandType, "dr_select_precedent", StringComparison.Ordinal))
            {
                HandleDrSelectPrecedent(cmd);
                return;
            }
            if (string.Equals(commandType, "dr_confirm_precedent_mapping", StringComparison.Ordinal))
            {
                HandleDrConfirmPrecedentMapping(cmd);
                return;
            }

            if (string.Equals(commandType, "dr_resolve_document_pattern", StringComparison.Ordinal) ||
                string.Equals(commandType, "dr_analyze_posting", StringComparison.Ordinal))
            {
                string fileId = cmd["fileId"]?.ToString();
                try
                {
                    int trdrId = (int?)cmd["trdrId"] ?? 0;
                    int sourceLineCount = (int?)cmd["sourceLineCount"] ?? 0;
                    string documentType = cmd["documentType"]?.ToString();
                    string documentSeries = cmd["documentSeries"]?.ToString();
                    JObject result = DrDocumentPatternResolver.Resolve(
                        _xSupport, trdrId, documentType, documentSeries, sourceLineCount);
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
                return;
            }

            CoreWebView2_WebMessageReceived(sender, e);
        }

        private void HandleDrRegisterDocumentV2(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                DebugLog.Log("[dr-recognition-flow] final registration routed synchronously to DrExpenseDocumentRegistrar. " +
                    "mode=" + (cmd?["mode"]?.ToString() ?? "auto") +
                    " sosource=" + ((int?)cmd?["sosource"] ?? 0));

                string result = DrExpenseDocumentRegistrar.Register(_xSupport, cmd);
                JObject parsed = JObject.Parse(result);

                if ((bool?)parsed["success"] == true && (int?)parsed["findocId"] > 0)
                {
                    string auditError = null;
                    bool auditMarked = DrDocumentAuditMarker.TryMark(
                        _xSupport, cmd, parsed, out auditError);
                    parsed["jarvisAuditMarked"] = auditMarked;
                    parsed["jarvisFlowVersion"] = DrDocumentAuditMarker.FlowVersion;
                    if (!auditMarked && !string.IsNullOrWhiteSpace(auditError))
                        parsed["jarvisAuditError"] = auditError;
                }

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

        private void HandleDrConfirmPrecedentMapping(JObject cmd)
        {
            string fileId = cmd["fileId"]?.ToString();
            try
            {
                if ((bool?)cmd["confirm"] != true)
                    throw new InvalidOperationException("Operator confirmation is required for CCCMAPITEMS learning.");
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int targetMtrlId = (int?)cmd["targetMtrlId"] ?? 0;
                JArray mappings = cmd["mappings"] as JArray ?? new JArray();
                JObject result = DrItemCodeResolver.LearnMappings(
                    _xSupport, trdrId, targetMtrlId, mappings);
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

        private void HandleDrSelectPrecedent(JObject cmd)
        {
            string fileId = cmd["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int findocId = (int?)cmd["findocId"] ?? 0;
                JObject result = DrPrecedentResolver.Resolve(_xSupport, trdrId, findocId);
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

        private void HandleDrResolveLineMappings(JObject cmd)
        {
            string fileId = cmd["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                JArray requestedLines = cmd["lines"] as JArray ?? new JArray();
                var output = new JArray();
                foreach (JToken token in requestedLines)
                {
                    JObject line = token as JObject ?? new JObject();
                    JObject result = DrItemCodeResolver.Resolve(
                        _xSupport, trdrId, line["supplierCode"]?.ToString());
                    result["lineIndex"] = (int?)line["lineIndex"] ?? -1;
                    output.Add(result);
                }

                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_line_mappings_result", ["fileId"] = fileId, ["success"] = true,
                    ["resolver"] = "resolve_supplier_code_mapping", ["version"] = 2,
                    ["readOnly"] = true, ["trdrId"] = trdrId, ["results"] = output
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

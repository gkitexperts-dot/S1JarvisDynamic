using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using S1Jarvis.Access;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisShell.DrBatch — batch mode για τον DR (ΙΔΙΟ flow με S1DocReader).
    //
    // JS → C#:
    //   dr_batch_start   { files: [{ fileId, fileName, base64, mimeType, qrLink }] }
    //   dr_batch_cancel
    // C# → JS:
    //   dr_batch_progress { fileId, fileName, index, total, step, message, score, decision }
    //   dr_batch_complete { results: [FileResult.ToJson()] }
    // ══════════════════════════════════════════════════════════════════════
    public partial class JarvisShell
    {
        private CancellationTokenSource _drBatchCts;

        private bool TryHandleDrBatchCommand(JObject cmd)
        {
            string type = cmd?["type"]?.ToString();
            if (string.Equals(type, "dr_batch_start", StringComparison.Ordinal))
            {
                _ = HandleDrBatchStartAsync(cmd);
                return true;
            }
            if (string.Equals(type, "dr_batch_cancel", StringComparison.Ordinal))
            {
                try { _drBatchCts?.Cancel(); } catch { }
                return true;
            }
            return false;
        }

        private async Task HandleDrBatchStartAsync(JObject cmd)
        {
            try
            {
                if (!_drAllowed)
                {
                    PostToWebView(new JObject { ["type"] = "dr_batch_complete", ["error"] = "Δεν έχετε άδεια DocReader." });
                    return;
                }
                var filesArr = cmd["files"] as JArray ?? new JArray();
                var files = filesArr.Select(f => new DrBatchEngine.FileInput
                {
                    FileId = f["fileId"]?.ToString(),
                    FileName = f["fileName"]?.ToString(),
                    Base64 = f["base64"]?.ToString(),
                    MimeType = f["mimeType"]?.ToString(),
                    QrLink = f["qrLink"]?.ToString()
                }).Where(f => !string.IsNullOrEmpty(f.Base64)).ToList();

                if (files.Count == 0)
                {
                    PostToWebView(new JObject { ["type"] = "dr_batch_complete", ["results"] = new JArray() });
                    return;
                }

                _drBatchCts?.Cancel();
                _drBatchCts = new CancellationTokenSource();
                var ct = _drBatchCts.Token;

                DebugLog.Log("[dr-batch] start files=" + files.Count);
                var engine = new DrBatchEngine(_xSupport, _agentClient, _agentAccountRef,
                    p => PostToWebView(p.ToJson()));

                var results = await engine.RunAsync(files, ct);
                PostToWebView(new JObject
                {
                    ["type"] = "dr_batch_complete",
                    ["cancelled"] = ct.IsCancellationRequested,
                    ["results"] = new JArray(results.Select(r => r.ToJson()))
                });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-batch] EXCEPTION: " + ex);
                PostToWebView(new JObject { ["type"] = "dr_batch_complete", ["error"] = ex.Message });
            }
        }

        private void PostToWebView(JObject payload)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { webView.CoreWebView2?.PostWebMessageAsString(payload.ToString(Newtonsoft.Json.Formatting.None)); }
                    catch (Exception ex) { DebugLog.Log("[dr-batch] post failed: " + ex.Message); }
                }));
            }
            catch { }
        }
    }
}

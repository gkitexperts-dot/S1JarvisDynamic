using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private const string IsolatedPickerOrigin = "https://s1jarvis-picker.local/";
        private static readonly bool IsolatedPickerClassHandlerRegistered = RegisterIsolatedPickerClassHandler();
        private bool _isolatedPickerInstalled;
        private bool _isolatedPickerInitHooked;

        private static bool RegisterIsolatedPickerClassHandler()
        {
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_IsolatedPickerLoaded));
            return true;
        }

        private static void JarvisShell_IsolatedPickerLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null) shell.EnsureIsolatedPickerInstalled();
        }

        private void EnsureIsolatedPickerInstalled()
        {
            if (_isolatedPickerInstalled || webView == null) return;

            try
            {
                if (webView.CoreWebView2 != null)
                {
                    InstallIsolatedPickerBridge();
                    return;
                }

                if (_isolatedPickerInitHooked) return;
                _isolatedPickerInitHooked = true;
                webView.CoreWebView2InitializationCompleted += WebView_IsolatedPickerInitializationCompleted;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[file-picker] install hook EXCEPTION: " + ex);
            }
        }

        private void WebView_IsolatedPickerInitializationCompleted(object sender,
            CoreWebView2InitializationCompletedEventArgs e)
        {
            try
            {
                if (webView != null)
                    webView.CoreWebView2InitializationCompleted -= WebView_IsolatedPickerInitializationCompleted;
                _isolatedPickerInitHooked = false;

                if (!e.IsSuccess)
                {
                    DebugLog.Log("[file-picker] CoreWebView2 initialization failed: " +
                        (e.InitializationException == null ? string.Empty : e.InitializationException.ToString()));
                    return;
                }

                InstallIsolatedPickerBridge();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[file-picker] initialization-completed EXCEPTION: " + ex);
            }
        }

        private void InstallIsolatedPickerBridge()
        {
            if (_isolatedPickerInstalled || webView == null || webView.CoreWebView2 == null) return;

            try
            {
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    IsolatedPickerOrigin + "*", CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += IsolatedPicker_WebResourceRequested;
                webView.CoreWebView2.NavigationCompleted += IsolatedPicker_NavigationCompleted;
                _isolatedPickerInstalled = true;
                DebugLog.Log("[file-picker] isolated external-process picker bridge installed.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[file-picker] bridge install EXCEPTION: " + ex);
            }
        }

        private async void IsolatedPicker_NavigationCompleted(object sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (!e.IsSuccess || webView == null || webView.CoreWebView2 == null) return;

                string script = @"
(function(){
  if(window.__jarvisIsolatedPickerInstalled)return;
  window.__jarvisIsolatedPickerInstalled=true;

  function b64ToFile(item){
    var raw=atob(item.base64||'');
    var bytes=new Uint8Array(raw.length);
    for(var i=0;i<raw.length;i++)bytes[i]=raw.charCodeAt(i);
    return new File([bytes],item.name||'file',{type:item.mime||'application/octet-stream'});
  }

  async function pick(mode,multiple){
    try{
      var url='https://s1jarvis-picker.local/pick?mode='+encodeURIComponent(mode)+'&multiple='+(multiple?'1':'0');
      var r=await fetch(url,{method:'GET',cache:'no-store'});
      var d=await r.json();
      if(!d||!d.ok||!d.files||!d.files.length)return;
      var files=d.files.map(b64ToFile);
      if(mode==='dr'){
        if(typeof addDrFiles==='function')addDrFiles(files);
      }else{
        if(typeof loadAttachment==='function'&&files[0])loadAttachment(files[0]);
      }
    }catch(err){
      try{console.error('Jarvis isolated file picker failed',err);}catch(_e){}
    }
  }

  // Only click/browse is intercepted. Native Chromium drag/drop must remain
  // untouched so the existing drDropzone dragenter/dragover/drop handlers
  // receive DataTransfer.files directly and feed the same addDrFiles pipeline.
  document.addEventListener('click',function(ev){
    var t=ev.target;
    if(!t)return;
    var attach=t.closest?t.closest('#attachBtn'):null;
    var drBrowse=t.closest?t.closest('#drBrowseBtn'):null;
    var drDrop=t.closest?t.closest('#drDropzone'):null;
    var nativeInput=t.closest?t.closest('#fileInput,#drFileInput'):null;
    if(nativeInput){ev.preventDefault();ev.stopImmediatePropagation();return;}
    if(attach){ev.preventDefault();ev.stopImmediatePropagation();pick('chat',false);return;}
    if(drBrowse||drDrop){ev.preventDefault();ev.stopImmediatePropagation();pick('dr',true);return;}
  },true);

  var dz=document.getElementById('drDropzone');
  if(dz){
    dz.addEventListener('dragenter',function(ev){
      if(ev.dataTransfer&&ev.dataTransfer.types&&Array.prototype.indexOf.call(ev.dataTransfer.types,'Files')>=0){
        dz.classList.add('dragover');
      }
    },true);
    dz.addEventListener('drop',function(){dz.classList.remove('dragover');},true);
  }
})();";

                await webView.CoreWebView2.ExecuteScriptAsync(script);
                DebugLog.Log("[file-picker] UI interception installed for click/browse; native DR drag/drop preserved.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[file-picker] navigation injection EXCEPTION: " + ex);
            }
        }

        private async void IsolatedPicker_WebResourceRequested(object sender,
            CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            try { uri = new Uri(e.Request.Uri); }
            catch { return; }

            if (!string.Equals(uri.Host, "s1jarvis-picker.local", StringComparison.OrdinalIgnoreCase)) return;

            var deferral = e.GetDeferral();
            try
            {
                if (!string.Equals(uri.AbsolutePath, "/pick", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = CreateIsolatedPickerResponse(404,
                        new JObject { ["ok"] = false, ["error"] = "not_found" });
                    return;
                }

                string mode = GetQueryValue(uri.Query, "mode") ?? "chat";
                bool multiple = string.Equals(GetQueryValue(uri.Query, "multiple"), "1", StringComparison.Ordinal);
                DebugLog.Log("[file-picker] REQUEST mode=" + mode + " multiple=" + multiple +
                    " (external PowerShell STA process; Soft1 UI not used).");

                string[] selected = await Task.Run(() => RunExternalFilePicker(mode, multiple));
                var files = new JArray();

                foreach (string path in selected)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                        var info = new FileInfo(path);
                        long maxBytes = string.Equals(mode, "dr", StringComparison.OrdinalIgnoreCase)
                            ? 20L * 1024L * 1024L
                            : 15L * 1024L * 1024L;
                        if (info.Length > maxBytes)
                        {
                            DebugLog.Log("[file-picker] skipped oversized file: " + info.Name + " bytes=" + info.Length);
                            continue;
                        }

                        byte[] bytes = File.ReadAllBytes(path);
                        files.Add(new JObject
                        {
                            ["name"] = info.Name,
                            ["size"] = info.Length,
                            ["mime"] = GuessMimeType(info.Extension),
                            ["base64"] = Convert.ToBase64String(bytes)
                        });
                    }
                    catch (Exception fileEx)
                    {
                        DebugLog.Log("[file-picker] file read EXCEPTION path=" + path + " error=" + fileEx);
                    }
                }

                DebugLog.Log("[file-picker] RESULT mode=" + mode + " selected=" + selected.Length +
                    " delivered=" + files.Count);
                e.Response = CreateIsolatedPickerResponse(200,
                    new JObject { ["ok"] = true, ["files"] = files });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[file-picker] REQUEST EXCEPTION: " + ex);
                try
                {
                    e.Response = CreateIsolatedPickerResponse(500,
                        new JObject { ["ok"] = false, ["error"] = "picker_failed", ["message"] = ex.Message });
                }
                catch { }
            }
            finally
            {
                try { deferral.Complete(); } catch { }
            }
        }

        private CoreWebView2WebResourceResponse CreateIsolatedPickerResponse(int statusCode, JObject payload)
        {
            string json = (payload ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var stream = new MemoryStream(bytes, false);
            string reason = statusCode == 200 ? "OK" : statusCode == 404 ? "Not Found" : "Internal Server Error";
            string headers =
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: https://s1jarvis.local\r\n";
            return webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, statusCode, reason, headers);
        }

        private static string[] RunExternalFilePicker(string mode, bool multiple)
        {
            string filter = string.Equals(mode, "dr", StringComparison.OrdinalIgnoreCase)
                ? "Παραστατικά|*.pdf;*.xlsx;*.xls;*.doc;*.docx;*.png;*.jpg;*.jpeg|Όλα τα αρχεία|*.*"
                : "Υποστηριζόμενα αρχεία|*.pdf;*.xlsx;*.xls;*.docx;*.doc;*.csv;*.json;*.xml;*.png;*.jpg;*.jpeg;*.md;*.txt|Όλα τα αρχεία|*.*";

            string ps =
                "$ErrorActionPreference='Stop';" +
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "$d=New-Object System.Windows.Forms.OpenFileDialog;" +
                "$d.Multiselect=" + (multiple ? "$true;" : "$false;") +
                "$d.Filter='" + filter.Replace("'", "''") + "';" +
                "$d.CheckFileExists=$true;$d.CheckPathExists=$true;" +
                "$d.RestoreDirectory=$true;" +
                "if($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK){$d.FileNames|ForEach-Object{[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))}}";

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(shell)) shell = "powershell.exe";

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var p = Process.Start(psi))
            {
                if (p == null) return new string[0];
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();

                if (p.ExitCode != 0)
                    throw new InvalidOperationException("External file picker failed: " + stderr.Trim());

                var result = new List<string>();
                foreach (string line in (stdout ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string path = Encoding.UTF8.GetString(Convert.FromBase64String(line.Trim()));
                        if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
                    }
                    catch { }
                }
                return result.ToArray();
            }
        }

        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string q = query.TrimStart('?');
            foreach (string part in q.Split('&'))
            {
                string[] kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 0) continue;
                if (!string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase)) continue;
                return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            }
            return null;
        }

        private static string GuessMimeType(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".pdf": return "application/pdf";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".json": return "application/json";
                case ".xml": return "application/xml";
                case ".csv": return "text/csv";
                case ".txt":
                case ".md": return "text/plain";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xls": return "application/vnd.ms-excel";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".doc": return "application/msword";
                default: return "application/octet-stream";
            }
        }
    }
}

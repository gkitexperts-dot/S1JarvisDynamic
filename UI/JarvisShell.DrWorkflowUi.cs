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
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-precedent-proposal-ui.js");
            await ExecuteEmbeddedDrScriptAsync("S1Jarvis.web.dr-trader-role-ui.js");

            // Safety bridge for the high-value precedent-learning action.  We use
            // pointerdown in capture phase and post directly to WebView2 instead of
            // depending on another JS helper/click bubbling path.  The existing
            // click handler remains as a fallback; setting status=writing here makes
            // it a no-op, so the write command cannot be sent twice.
            await webView.CoreWebView2.ExecuteScriptAsync(@"
(function(){
  if(window.__jarvisDrPrecedentPointerBridgeInstalled)return;
  window.__jarvisDrPrecedentPointerBridgeInstalled=true;
  document.addEventListener('pointerdown',function(ev){
    var btn=ev.target&&ev.target.closest?ev.target.closest('[data-dr-confirm-precedent-mapping]'):null;
    if(!btn)return;
    ev.preventDefault();ev.stopImmediatePropagation();
    try{
      var files=(typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles:[];
      var f=files.filter(function(x){return x&&!x.standalone&&!(x.registerResult&&x.registerResult.success);})[0]||null;
      var p=f&&f.precedentMappingProposal;
      if(!f||!p||p.status!=='proposed'||!p.target||!p.target.mtrlId){
        btn.textContent='Η πρόταση δεν είναι διαθέσιμη';btn.disabled=true;return;
      }
      p.status='writing';p.feedback='Αποθήκευση CCCMAPITEMS σε εξέλιξη…';
      btn.textContent='Αποθήκευση mappings…';btn.disabled=true;
      var card=btn.closest('.dr-precedent-proposal');
      if(card){
        var badge=card.querySelector('.dr-precedent-badge');if(badge)badge.textContent='Αποθήκευση…';
        var fb=card.querySelector('.dr-precedent-feedback');
        if(!fb){fb=document.createElement('div');fb.className='dr-precedent-feedback';card.appendChild(fb);}
        fb.textContent='Στέλνω τα mappings στο Soft1 και περιμένω verification…';
      }
      if(!window.chrome||!window.chrome.webview)throw new Error('webview_bridge_not_available');
      window.chrome.webview.postMessage(JSON.stringify({
        type:'dr_confirm_precedent_mapping',fileId:String(f.id),confirm:true,
        trdrId:Number(f.trdrId),findocId:Number(p.findocId),
        targetMtrlId:Number(p.target.mtrlId),mappings:p.tokens||[]
      }));
      setTimeout(function(){
        if(p.status==='writing'){
          p.status='blocked';p.errorMessage='Δεν ελήφθη απάντηση από το Soft1 για την αποθήκευση mappings.';
          p.feedback='Timeout κατά την αποθήκευση CCCMAPITEMS.';
          if(typeof rerenderDrEntry==='function')rerenderDrEntry(f);
          if(typeof window.refreshDrRecognitionWorkspace==='function')setTimeout(window.refreshDrRecognitionWorkspace,0);
        }
      },15000);
    }catch(err){
      try{
        var files2=(typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles:[];
        var f2=files2.filter(function(x){return x&&!x.standalone&&!(x.registerResult&&x.registerResult.success);})[0]||null;
        if(f2&&f2.precedentMappingProposal){f2.precedentMappingProposal.status='blocked';f2.precedentMappingProposal.errorMessage=String(err&&err.message||err);f2.precedentMappingProposal.feedback='Το command δεν στάλθηκε.';}
        btn.textContent='Σφάλμα αποστολής';btn.disabled=true;
        if(typeof rerenderDrEntry==='function'&&f2)rerenderDrEntry(f2);
      }catch(_ignored){}
    }
  },true);
})();");
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

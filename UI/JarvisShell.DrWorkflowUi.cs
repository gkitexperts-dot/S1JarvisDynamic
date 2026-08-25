using System;
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
            EventManager.RegisterClassHandler(typeof(JarvisShell), FrameworkElement.LoadedEvent,
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
                        if (!string.IsNullOrWhiteSpace(ready) && ready.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            await InstallDrWorkflowUiAsync();
                            return;
                        }
                    }
                    await Task.Delay(50);
                }
            }
            catch (Exception ex) { DebugLog.Log("[dr-workflow-ui] startup EXCEPTION: " + ex); }
        }

        private async Task InstallDrWorkflowUiAsync()
        {
            string script = @"
(function(){
 if(window.__jarvisDrWorkflowUiInstalled)return;
 var style=document.createElement('style');style.id='drWorkflowEnhancedStyle';style.textContent=`
 .dr-stage-strip{display:flex;gap:5px;flex-wrap:wrap;margin:4px 0 10px}.dr-stage{font-size:10.5px;padding:4px 7px;border-radius:999px;background:rgba(255,255,255,.06);color:#9a9ab0;border:1px solid rgba(255,255,255,.07)}.dr-stage.ok{color:#8fe0b7;background:rgba(76,201,138,.10);border-color:rgba(76,201,138,.25)}.dr-stage.warn{color:#ffd78a;background:rgba(255,190,70,.10);border-color:rgba(255,190,70,.25)}
 .dr-enhanced-lines{display:flex;flex-direction:column;gap:7px;margin:4px 0 8px}.dr-enhanced-line{padding:9px 10px;border:1px solid rgba(255,255,255,.08);border-radius:10px;background:rgba(255,255,255,.025)}.dr-enhanced-line.resolved{border-color:rgba(76,201,138,.22)}.dr-enhanced-line.unresolved{border-color:rgba(255,107,107,.24);background:rgba(255,107,107,.04)}
 .dr-line-top{display:flex;gap:8px;align-items:flex-start}.dr-line-source{flex:1;min-width:0}.dr-line-source strong{display:block;font-size:12.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.dr-line-source small{display:block;color:#9a9ab0;font-size:10.5px;margin-top:2px}.dr-line-state{font-size:10.5px;padding:3px 7px;border-radius:999px;background:rgba(255,255,255,.06);white-space:nowrap}.resolved .dr-line-state{color:#8fe0b7}.unresolved .dr-line-state{color:#ff9c9c}
 .dr-line-target{margin-top:6px;font-size:11.5px;color:#d8d5ff}.dr-line-actions{display:flex;gap:6px;flex-wrap:wrap;margin-top:7px}.dr-line-action{border:1px solid rgba(255,255,255,.12);background:rgba(255,255,255,.06);color:#fff;border-radius:8px;padding:5px 9px;font-size:11px;cursor:pointer}.dr-line-action.primary{background:rgba(117,102,239,.28);border-color:rgba(139,123,255,.4)}
 .dr-line-inline{margin-top:8px;padding:8px;border-radius:8px;background:rgba(0,0,0,.14)}.dr-line-search{display:flex;gap:6px}.dr-line-search input{flex:1;min-width:0;background:#1f1f30;color:#fff;border:1px solid rgba(255,255,255,.12);border-radius:7px;padding:6px 8px;font-size:11.5px}.dr-line-results{display:flex;flex-direction:column;gap:5px;margin-top:6px}.dr-line-result{display:flex;justify-content:space-between;gap:8px;align-items:center;padding:5px 7px;border-radius:7px;background:rgba(255,255,255,.04);font-size:11px}.dr-line-result button{flex:none}
 .dr-create-form{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:6px;margin-top:7px}.dr-create-form label{font-size:10px;color:#9a9ab0}.dr-create-form input{width:100%;margin-top:2px;background:#1f1f30;color:#fff;border:1px solid rgba(255,255,255,.12);border-radius:7px;padding:6px 7px;font-size:11px}.dr-create-checks{display:flex;gap:12px;grid-column:1/-1;font-size:11px;color:#d8d5ff}.dr-create-confirm{grid-column:1/-1}.dr-lines-panel .dr-line-row{display:none!important}`;document.head.appendChild(style);

 var baseRender=window.renderDrLinesPanel;
 function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v).replace(/[&<>\"]/g,s=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;'}[s]));}
 function entryById(id){return (typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles.find(x=>String(x.id)===String(id)):null;}
 function chosenName(f,i){return f.manualPickedNames&&f.manualPickedNames[i]!=null?f.manualPickedNames[i]:'';}
 function searchResults(f,key){return f.manualSearchResults&&Array.isArray(f.manualSearchResults[key])?f.manualSearchResults[key]:[];}
 function stages(f,lines,resolved){var s=[];function add(label,ok,warn){s.push('<span class="dr-stage '+(ok?'ok':warn?'warn':'')+'">'+label+'</span>');}add('1 · Ανάγνωση',!!f.detection,false);add('2 · ΑΦΜ',!!(f.detection&&f.detection.issuerAfm),false);add('3 · Συναλλασσόμενος',!!(f.trader||f.trdrId),!!f.pendingCreate);add('4 · Duplicate',!!f.duplicateCheck&&!f.duplicateCheck.isDuplicate,!!(f.duplicateCheck&&f.duplicateCheck.isDuplicate));add('5 · Γραμμές',!!f.extraction,false);add('6 · Matching',lines.length>0&&resolved===lines.length,lines.length>0&&resolved<lines.length);add('7 · Ready',!!f.extraction&&lines.length>0&&resolved===lines.length&&!f.registerError,false);return '<div class="dr-stage-strip">'+s.join('')+'</div>';}
 function renderSearch(f,i,mode){var key=(mode==='create'?'create':'map')+i;var results=searchResults(f,key);var html='<div class="dr-line-inline"><div class="dr-line-search"><input data-dr-search-input="'+f.id+':'+key+'" placeholder="Αναζήτηση είδους με κωδικό ή περιγραφή"><button type="button" class="dr-line-action primary" data-dr-search="'+f.id+'" data-dr-key="'+key+'">Αναζήτηση</button></div>';
   if(results.length){html+='<div class="dr-line-results">'+results.slice(0,12).map(r=>'<div class="dr-line-result"><span>['+esc(r.code||'')+'] '+esc(r.name||'')+'</span><button type="button" class="dr-line-action" '+(mode==='create'?'data-dr-create-template':'data-manual-pick-line')+'="'+f.id+'" data-line-index="'+i+'" data-mtrl="'+r.mtrlId+'" data-mtrl-name="'+esc(r.name||'')+'">'+(mode==='create'?'Πρότυπο':'Επιλογή')+'</button></div>').join('')+'</div>';}
   html+='</div>';return html;}
 function renderCreateForm(f,i,state){var t=state.template||{},code=state.suggestedCode||'',name=((f.extraction&&f.extraction.line_items||[])[i]||{}).description||'';return '<div class="dr-line-inline"><div class="dr-line-target">Πρότυπο: ['+esc(t.code||'')+'] '+esc(t.name||'')+'</div><div class="dr-create-form">'+
   '<label>Νέος κωδικός<input data-dr-create-field="code" value="'+esc(code)+'"></label><label>Περιγραφή<input data-dr-create-field="name" value="'+esc(name)+'"></label><label>ΜΜ (MTRUNIT1)<input data-dr-create-field="mtrunit1" type="number" value="'+esc(t.mtrunit1||'')+'"></label><label>ΦΠΑ (VAT id)<input data-dr-create-field="vat" type="number" value="'+esc(t.vat||'')+'"></label><label>Λογαριασμός (MTRACN)<input data-dr-create-field="mtracn" type="number" value="'+esc(t.mtracn||'')+'"></label><label>ΜΜ αγορών<input data-dr-create-field="mtrunit3" type="number" value="'+esc(t.mtrunit3||t.mtrunit1||'')+'"></label><div class="dr-create-checks"><label><input data-dr-create-field="mtrlotuse" type="checkbox" '+(Number(t.mtrlotuse||0)!==0?'checked':'')+'> Παρτίδα</label><label><input data-dr-create-field="mtrsnuse" type="checkbox" '+(Number(t.mtrsnuse||0)!==0?'checked':'')+'> Serial Number</label></div><button type="button" class="dr-line-action primary dr-create-confirm" data-dr-create-confirm="'+f.id+'" data-line-index="'+i+'">Δημιουργία είδους</button></div></div>';}
 function enhanced(f){if(!f||!f.extraction)return '';var lines=f.extraction.line_items||[],resolved=0;lines.forEach((li,i)=>{if(li.matched||(f.manualLineMtrlIds&&f.manualLineMtrlIds[i]))resolved++;});var html=stages(f,lines,resolved)+'<div class="dr-enhanced-lines">';lines.forEach((li,i)=>{var m=li.matched,manual=f.manualLineMtrlIds&&f.manualLineMtrlIds[i],ok=!!(m||manual),target=m?('MTRSUPCODE → ['+esc(m.code||'')+'] '+esc(m.name||'')):(manual?('Επιλογή χειριστή → '+esc(chosenName(f,i)||('MTRL '+manual))):'Δεν βρέθηκε αντιστοίχιση');html+='<div class="dr-enhanced-line '+(ok?'resolved':'unresolved')+'"><div class="dr-line-top"><div class="dr-line-source"><strong>'+esc(li.description||'Χωρίς περιγραφή')+'</strong><small>Κωδ. εκδότη: '+esc(li.code||'—')+' · Ποσ.: '+esc(li.quantity||'')+' · Τιμή: '+esc(li.unit_price||'')+' · ΦΠΑ: '+esc(li.vat_rate||'')+'</small></div><span class="dr-line-state">'+(ok?'✓ Αντιστοιχισμένο':'⚠ Εκκρεμεί')+'</span></div><div class="dr-line-target">'+target+'</div>';
   if(!ok){html+='<div class="dr-line-actions"><button type="button" class="dr-line-action" data-dr-map-line="'+f.id+'" data-line-index="'+i+'">Αντιστοίχιση</button><button type="button" class="dr-line-action primary" data-dr-create-line="'+f.id+'" data-line-index="'+i+'">Δημιουργία</button></div>';}
   if(f.drLineAction&&f.drLineAction.index===i&&f.drLineAction.mode==='map')html+=renderSearch(f,i,'map');
   if(f.drLineAction&&f.drLineAction.index===i&&f.drLineAction.mode==='create'){var st=f.drCreateState&&f.drCreateState[i];html+=st&&st.template?renderCreateForm(f,i,st):renderSearch(f,i,'create');}
   html+='</div>';});return html+'</div>';}
 window.renderDrLinesPanel=function(f){return enhanced(f)+baseRender(f);};

 var list=document.getElementById('drFileList');
 list.addEventListener('click',async function(ev){
   var map=ev.target.closest('[data-dr-map-line]');if(map){var f=entryById(map.getAttribute('data-dr-map-line'));if(f){f.drLineAction={mode:'map',index:Number(map.getAttribute('data-line-index'))};rerenderDrEntry(f);}return;}
   var create=ev.target.closest('[data-dr-create-line]');if(create){var f=entryById(create.getAttribute('data-dr-create-line'));if(f){f.drLineAction={mode:'create',index:Number(create.getAttribute('data-line-index'))};rerenderDrEntry(f);}return;}
   var search=ev.target.closest('[data-dr-search]');if(search){var id=search.getAttribute('data-dr-search'),key=search.getAttribute('data-dr-key'),inp=document.querySelector('[data-dr-search-input="'+id+':'+key+'"]');if(inp&&inp.value.trim())postCommand({type:'dr_search_items',fileId:String(id),requestId:String(id)+':'+key,query:inp.value.trim()});return;}
   var tpl=ev.target.closest('[data-dr-create-template]');if(tpl){ev.preventDefault();ev.stopImmediatePropagation();var f=entryById(tpl.getAttribute('data-dr-create-template')),i=Number(tpl.getAttribute('data-line-index')),mtrl=Number(tpl.getAttribute('data-mtrl'));if(!f)return;try{var r=await fetch('https://s1jarvis-dr.local/item-template',{method:'POST',headers:{'Content-Type':'text/plain;charset=UTF-8'},body:JSON.stringify({templateMtrl:mtrl})});var d=await r.json();if(!f.drCreateState)f.drCreateState={};f.drCreateState[i]=d;rerenderDrEntry(f);}catch(e){alert('Αποτυχία φόρτωσης προτύπου: '+e.message);}return;}
   var confirm=ev.target.closest('[data-dr-create-confirm]');if(confirm){ev.preventDefault();ev.stopImmediatePropagation();var f=entryById(confirm.getAttribute('data-dr-create-confirm')),i=Number(confirm.getAttribute('data-line-index'));if(!f)return;var card=confirm.closest('.dr-enhanced-line'),get=n=>card.querySelector('[data-dr-create-field="'+n+'"]'),st=f.drCreateState&&f.drCreateState[i]||{};var input={code:get('code').value.trim(),name:get('name').value.trim(),mtrunit1:Number(get('mtrunit1').value),vat:Number(get('vat').value),mtracn:Number(get('mtracn').value),mtrunit3:Number(get('mtrunit3').value)||Number(get('mtrunit1').value),mtrlotuse:get('mtrlotuse').checked,mtrsnuse:get('mtrsnuse').checked,copiedFields:st.copiedFields||{}};if(!input.code||!input.name||!input.mtrunit1||!input.vat||!input.mtracn){alert('Συμπλήρωσε κωδικό, περιγραφή, ΜΜ, ΦΠΑ και λογαριασμό.');return;}if(!confirm('Να δημιουργηθεί νέο είδος στο Soft1;'))return;confirm.disabled=true;try{var r=await fetch('https://s1jarvis-dr.local/create-item',{method:'POST',headers:{'Content-Type':'text/plain;charset=UTF-8'},body:JSON.stringify({confirm:true,input:input})});var d=await r.json();if(!d.ok)throw new Error(d.message||d.error||'Αποτυχία δημιουργίας');if(!f.manualLineMtrlIds)f.manualLineMtrlIds={};if(!f.manualPickedNames)f.manualPickedNames={};f.manualLineMtrlIds[i]=Number(d.mtrlId);f.manualPickedNames[i]=(d.code?'['+d.code+'] ':'')+(d.name||input.name);if(!f.drCreatedItems)f.drCreatedItems={};f.drCreatedItems[i]=d;f.drLineAction=null;rerenderDrEntry(f);}catch(e){alert('Αποτυχία δημιουργίας είδους: '+e.message);}finally{confirm.disabled=false;}return;}
 },true);
 window.__jarvisDrWorkflowUiInstalled=true;
})();";
            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
}

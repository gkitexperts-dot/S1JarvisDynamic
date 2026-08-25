(function(){
  if(window.__jarvisDrPrecedentProposalUiInstalled)return;
  var style=document.createElement('style');style.id='drPrecedentProposalUiStyle';style.textContent=`
    .dr-pattern-row{grid-template-columns:minmax(0,1fr) auto auto auto!important}
    .dr-pattern-use{border:1px solid rgba(76,201,138,.30);background:rgba(76,201,138,.10);color:#a8e9c7;border-radius:6px;padding:3px 7px;font-size:9.5px;cursor:pointer;white-space:nowrap}
    .dr-precedent-proposal{grid-column:1/-1;border:1px solid rgba(76,201,138,.22);background:rgba(76,201,138,.045);border-radius:10px;padding:9px 10px;margin-top:8px}.dr-precedent-proposal.blocked{border-color:rgba(255,107,107,.28);background:rgba(255,107,107,.04)}
    .dr-precedent-head{display:flex;justify-content:space-between;gap:8px;align-items:center;font-size:11px;font-weight:700;color:#dff8ea;margin-bottom:6px}.dr-precedent-badge{font-size:9.5px;padding:3px 7px;border-radius:999px;background:rgba(76,201,138,.12);color:#a8e9c7}.dr-precedent-proposal.blocked .dr-precedent-badge{color:#ffb1b1;background:rgba(255,107,107,.10)}
    .dr-precedent-target{font-size:11px;color:#efedf8;margin-bottom:5px}.dr-precedent-meta{font-size:10px;color:#aaa9bd;line-height:1.45}.dr-precedent-tokens{margin-top:6px;font-family:Consolas,monospace;font-size:9.8px;color:#c9c6e9;word-break:break-all}
    .dr-precedent-confirm{margin-top:8px;border:1px solid rgba(76,201,138,.42);background:rgba(76,201,138,.16);color:#dff8ea;border-radius:7px;padding:6px 10px;font-size:10.5px;font-weight:650;cursor:pointer}.dr-precedent-confirm:disabled{opacity:.5;cursor:default}
    .dr-register-gated{opacity:.42!important;cursor:not-allowed!important;filter:saturate(.35)}
  `;document.head.appendChild(style);

  function active(){if(typeof drFiles==='undefined'||!Array.isArray(drFiles))return null;return drFiles.filter(function(f){return f&&!f.standalone&&!(f.registerResult&&f.registerResult.success);})[0]||null;}
  function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v);}
  function codeOf(line){return String((line&&(line.supplier_code||line.supplierCode||line.item_code||line.itemCode||line.code))||'').trim();}
  function rerender(f){if(typeof rerenderDrEntry==='function')rerenderDrEntry(f);else if(typeof renderDrFileList==='function')renderDrFileList();if(typeof window.refreshDrRecognitionWorkspace==='function')setTimeout(window.refreshDrRecognitionWorkspace,0);}

  function readiness(f){
    if(!f||!f.extraction)return {ready:false,total:0,resolved:0,reason:'Δεν έχει ολοκληρωθεί η ανάγνωση.'};
    var duplicate=!!(f.duplicateCheck&&(f.duplicateCheck.found||f.duplicateCheck.isDuplicate));if(duplicate)return {ready:false,total:0,resolved:0,reason:'Το παραστατικό είναι διπλότυπο.'};
    var lines=f.extraction.line_items||[],states=f.lineRecognitionStates||{},resolved=0;
    lines.forEach(function(line,i){var s=Number(states[String(i)]||0),manual=f.manualLineMtrlIds&&f.manualLineMtrlIds[i];if((line.matched&&line.matched.mtrlId)||manual||s===20||s===30||s===50||s===60||s===90||s===100)resolved++;});
    var pendingProposal=!!(f.precedentMappingProposal&&['proposed','writing'].indexOf(f.precedentMappingProposal.status)>=0);
    return {ready:lines.length>0&&resolved===lines.length&&!pendingProposal,total:lines.length,resolved:resolved,reason:pendingProposal?'Η πρόταση precedent χρειάζεται επιβεβαίωση και αποθήκευση mappings.':(resolved<lines.length?(lines.length-resolved)+' γραμμές δεν είναι ακόμη Ready.':'')};
  }
  window.getDrRegistrationReadiness=readiness;

  function gateRegistration(){
    var f=active(),r=readiness(f);document.querySelectorAll('[data-register-doc],[data-manual-register-perline],[data-manual-register-consolidate]').forEach(function(btn){
      var id=btn.getAttribute('data-register-doc')||btn.getAttribute('data-manual-register-perline')||btn.getAttribute('data-manual-register-consolidate');if(!f||String(id)!==String(f.id))return;
      if(!r.ready){btn.disabled=true;btn.classList.add('dr-register-gated');btn.title=r.reason||'Το παραστατικό δεν είναι Ready.';}else{btn.disabled=false;btn.classList.remove('dr-register-gated');btn.title='';}
    });
  }

  function addUseButtons(){var f=active(),evidence=f&&f.postingProposal&&Array.isArray(f.postingProposal.evidence)?f.postingProposal.evidence:[];document.querySelectorAll('.dr-pattern-row').forEach(function(row){var existing=row.querySelector('[data-dr-use-precedent]'),open=row.querySelector('[data-dr-open-precedent]');if(!open){if(existing)existing.remove();return;}var id=Number(open.getAttribute('data-dr-open-precedent')||0),candidate=evidence.find(function(x){return Number(x.findocId)===id;});var allowed=!!candidate&&Number(candidate.lineCount)===1;if(!allowed){if(existing)existing.remove();return;}if(existing)return;var b=document.createElement('button');b.type='button';b.className='dr-pattern-use';b.setAttribute('data-dr-use-precedent',String(id));b.textContent='Χρήση ως πρότυπο';row.appendChild(b);});}

  function renderProposal(){
    var f=active();if(!f)return;var ws=document.querySelector('[data-dr-session-active="'+String(f.id).replace(/"/g,'\\"')+'"] .dr-rec-workspace');if(!ws)return;var old=ws.querySelector('.dr-precedent-proposal');if(old)old.remove();var p=f.precedentMappingProposal;if(!p)return;
    var card=document.createElement('div');card.className='dr-precedent-proposal '+(p.status==='blocked'||p.status==='manual_required'?'blocked':'');var target=p.target||{};
    if(p.status==='manual_required')card.innerHTML='<div class="dr-precedent-head"><span>Selected precedent</span><span class="dr-precedent-badge">Manual matching</span></div><div class="dr-precedent-meta">Το FINDOC '+esc(p.findocId)+' έχει '+esc(p.postedLineCount)+' Soft1 γραμμές. Δεν επιτρέπεται αυτόματη αντιστοίχιση.</div>';
    else if(p.status==='blocked')card.innerHTML='<div class="dr-precedent-head"><span>Single-line precedent · FINDOC '+esc(p.findocId)+'</span><span class="dr-precedent-badge">Blocked</span></div><div class="dr-precedent-meta">'+esc(p.errorMessage||'Υπάρχει σύγκρουση mapping. Απαιτείται έλεγχος χειριστή.')+'</div>';
    else{
      var tokens=(p.tokens||[]).map(function(x){return x.mappingToken;});var learned=p.status==='confirmed';var writing=p.status==='writing';
      card.innerHTML='<div class="dr-precedent-head"><span>Single-line precedent proposal · FINDOC '+esc(p.findocId)+'</span><span class="dr-precedent-badge">'+(learned?'Mappings αποθηκεύτηκαν':writing?'Αποθήκευση…':'Πρόταση · όχι write')+'</span></div><div class="dr-precedent-target">Target: ['+esc(target.code||'')+'] '+esc(target.name||('MTRL '+target.mtrlId))+' · '+esc(target.sodtypeName||'')+' · SODTYPE '+esc(target.sodtype||'—')+' · MTRL '+esc(target.mtrlId||'—')+'</div><div class="dr-precedent-meta">'+(learned?'Οι supplier codes αντιστοιχίστηκαν στο CCCMAPITEMS. Το παραστατικό είναι πλέον έτοιμο για consolidated καταχώρηση, εφόσον δεν υπάρχει άλλος blocker.':'Όλες οι unresolved source γραμμές θα αντιστοιχιστούν στο μοναδικό MTRL του precedent. Με επιβεβαίωση θα αποθηκευτούν τα mappings στο CCCMAPITEMS.')+'</div><div class="dr-precedent-tokens">'+esc(tokens.join('; '))+'</div>'+(learned?'':'<button type="button" class="dr-precedent-confirm" data-dr-confirm-precedent-mapping="'+esc(f.id)+'" '+(writing?'disabled':'')+'>Επιβεβαίωση αντιστοίχισης & μάθηση</button>');
    }
    var validation=ws.querySelector('[data-dr-rec-section="validation"]');if(validation)validation.insertAdjacentElement('beforebegin',card);else ws.appendChild(card);
  }

  function refresh(){addUseButtons();renderProposal();gateRegistration();}

  document.addEventListener('click',function(e){
    var reg=e.target.closest('[data-register-doc],[data-manual-register-perline],[data-manual-register-consolidate]');if(reg){var f0=active(),rr=readiness(f0);if(!rr.ready){e.preventDefault();e.stopImmediatePropagation();if(rr.reason)alert('Δεν μπορεί να γίνει καταχώρηση ακόμη. '+rr.reason);return;}}
    var use=e.target.closest('[data-dr-use-precedent]');if(use){var id=Number(use.getAttribute('data-dr-use-precedent')||0);if(id&&typeof window.selectDrHistoricalPrecedent==='function')window.selectDrHistoricalPrecedent(id);return;}
    var confirm=e.target.closest('[data-dr-confirm-precedent-mapping]');if(!confirm)return;var f=active(),p=f&&f.precedentMappingProposal;if(!f||!p||p.status!=='proposed'||!p.target||!p.target.mtrlId)return;
    if(!window.confirm('Να αντιστοιχιστούν οι κωδικοί του παραστατικού στο μοναδικό MTRL του επιλεγμένου precedent και να αποθηκευτούν στο CCCMAPITEMS;'))return;
    p.status='writing';rerender(f);postCommand({type:'dr_confirm_precedent_mapping',fileId:String(f.id),confirm:true,trdrId:Number(f.trdrId),findocId:Number(p.findocId),targetMtrlId:Number(p.target.mtrlId),mappings:p.tokens||[]});
  },true);

  if(window.chrome&&window.chrome.webview)window.chrome.webview.addEventListener('message',function(ev){
    var d=ev.data;try{if(typeof d==='string')d=JSON.parse(d);}catch(_e){return;}if(!d||d.type!=='dr_precedent_mapping_confirmed')return;var f=(typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles.find(function(x){return String(x.id)===String(d.fileId);}):null;if(!f||!f.precedentMappingProposal)return;var p=f.precedentMappingProposal;
    if(!d.success){p.status='blocked';p.errorMessage=d.errorMessage||d.reason||'Αποτυχία αποθήκευσης CCCMAPITEMS.';rerender(f);return;}
    var target=p.target||{},learned={};(d.learnedTokens||[]).concat(d.alreadyPresent||[]).forEach(function(t){learned[String(t)]=true;});var lines=f.extraction&&f.extraction.line_items||[];f.lineRecognitionStates=f.lineRecognitionStates||{};
    lines.forEach(function(line,i){var code=codeOf(line),token=String(f.trdrId)+'|'+code.replace(/\s+/g,'').toUpperCase();if(!learned[token])return;line.matched={mtrlId:Number(target.mtrlId),mtrl:Number(target.mtrlId),code:target.code||d.mtrlCode||'',name:target.name||d.mtrlName||'',sodtype:Number(target.sodtype||d.sodtype||0),sodtypeName:target.sodtypeName||d.sodtypeName||'',matchSource:'CCCMAPITEMS',mappingToken:token};delete line.proposedMatch;delete line.mappingNotFound;f.lineRecognitionStates[String(i)]=30;});
    p.status='confirmed';p.verified=true;p.learnedTokens=d.learnedTokens||[];f.needsManualInput=true;f.manualStep='consolidate';f.manualMode='consolidate';f.manualSosource=Number((f.postingProposal&&f.postingProposal.resolvedSosource)||0);f.manualSeries=Number((f.postingProposal&&f.postingProposal.resolvedSeries)||0);f.manualConsolidateMtrlId=Number(target.mtrlId);f.manualConsolidateChosenName=target.name||d.mtrlName||('MTRL '+target.mtrlId);f.detail='CCCMAPITEMS mappings επιβεβαιώθηκαν. Έτοιμο για consolidated καταχώρηση.';rerender(f);setTimeout(refresh,0);
  });

  var scheduled=false;function schedule(){if(scheduled)return;scheduled=true;requestAnimationFrame(function(){scheduled=false;refresh();});}
  var list=document.getElementById('drFileList');if(list)new MutationObserver(schedule).observe(list,{childList:true,subtree:true});setInterval(schedule,500);setTimeout(schedule,0);window.refreshDrPrecedentProposalUi=schedule;window.__jarvisDrPrecedentProposalUiInstalled=true;
})();

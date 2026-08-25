(function(){
  if(window.__jarvisDrPostingProposalUiInstalled)return;
  var style=document.createElement('style');style.id='drPostingProposalStyle';style.textContent=`
    .dr-posting-card{grid-column:1/-1;border:1px solid rgba(139,123,255,.16);background:rgba(139,123,255,.035);border-radius:10px;padding:9px 10px}
    .dr-posting-head{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:7px;font-size:11px;font-weight:700;color:#d8d5ff}
    .dr-posting-mode{font-size:9.5px;padding:3px 7px;border-radius:999px;background:rgba(255,255,255,.06);color:#aaa9bd}.dr-posting-mode.consolidated{color:#ffd78a;background:rgba(255,190,70,.10)}.dr-posting-mode.detailed{color:#8fe0b7;background:rgba(76,201,138,.10)}
    .dr-posting-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px 10px}.dr-posting-cell label{display:block;font-size:9.5px;color:#85859a}.dr-posting-cell div{font-size:11px;color:#efedf8;margin-top:1px}.dr-posting-note{font-size:10.5px;color:#aaa9bd;line-height:1.4;margin-top:7px}.dr-posting-evidence{margin-top:6px;font-size:9.5px;color:#77778c}
    @media(max-width:900px){.dr-posting-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
  `;document.head.appendChild(style);

  function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v);}
  function active(){if(typeof drFiles==='undefined'||!Array.isArray(drFiles))return null;return drFiles.filter(function(f){return f&&!f.standalone&&!(f.registerResult&&f.registerResult.success);})[0]||null;}
  function modeLabel(p){if(!p)return'Αναμονή';if(p.mode==='Consolidated')return'Consolidated';if(p.mode==='Detailed')return'Detailed';return'Χρειάζεται έλεγχο';}
  function render(p){
    var confidence=p&&p.confidence!=null?Math.round(Number(p.confidence)*100)+'%':'—';
    var cls=p&&p.mode==='Consolidated'?'consolidated':p&&p.mode==='Detailed'?'detailed':'';
    var classification=p&&p.classification==='ExpenseCandidate'?'Πιθανή δαπάνη':'—';
    var source=p&&p.sourceLineCount!=null?p.sourceLineCount:'—';
    var target=p&&p.proposedTargetLineCount!=null?p.proposedTargetLineCount:'—';
    var samples=p&&p.sampleSize!=null?p.sampleSize:'—';
    var note='Η πρόταση είναι read-only και βασίζεται στο ιστορικό καταχώρησης του ίδιου trader/σειράς. Δεν αποτελεί καταχώρηση.';
    if(p&&p.mode==='Consolidated')note='Το ιστορικό δείχνει συγκεντρωτική καταχώρηση: οι source γραμμές παραμένουν ορατές, αλλά η προτεινόμενη Soft1 μορφή είναι '+target+' γραμμή'+(Number(target)===1?'':'ές')+'.';
    else if(p&&p.mode==='Detailed')note='Το ιστορικό δείχνει αναλυτική καταχώρηση. Οι source γραμμές θα συνεχίσουν σε item resolution.';
    else if(p&&p.reason==='series_selection_required')note='Δεν υπάρχει ακόμη μοναδική σειρά/SOSOURCE. Χρειάζεται επιλογή πριν εξαχθεί ιστορικό posting pattern.';
    else if(p&&p.reason==='no_historical_documents')note='Δεν βρέθηκε ιστορικό για ασφαλή πρόταση. Το workflow παραμένει σε review.';
    var ev=(p&&Array.isArray(p.evidence)?p.evidence:[]).slice(0,4).map(function(x){return 'FINDOC '+esc(x.findocId)+' · '+esc(x.lineCount)+' γραμμές';}).join(' | ');
    return '<div class="dr-posting-card"><div class="dr-posting-head"><span>Πρόταση καταχώρησης</span><span class="dr-posting-mode '+cls+'">'+esc(modeLabel(p))+'</span></div><div class="dr-posting-grid">'+
      '<div class="dr-posting-cell"><label>Classification</label><div>'+esc(classification)+'</div></div><div class="dr-posting-cell"><label>Source lines</label><div>'+esc(source)+'</div></div><div class="dr-posting-cell"><label>Proposed Soft1 lines</label><div>'+esc(target)+'</div></div><div class="dr-posting-cell"><label>Confidence</label><div>'+esc(confidence)+'</div></div><div class="dr-posting-cell"><label>Historical samples</label><div>'+esc(samples)+'</div></div><div class="dr-posting-cell"><label>Single-line samples</label><div>'+esc(p&&p.singleLineSampleSize!=null?p.singleLineSampleSize:'—')+'</div></div><div class="dr-posting-cell"><label>Dominant line count</label><div>'+esc(p&&p.dominantHistoricalLineCount!=null?p.dominantHistoricalLineCount:'—')+'</div></div><div class="dr-posting-cell"><label>Threshold</label><div>'+esc(p&&p.threshold!=null?Math.round(Number(p.threshold)*100)+'%':'—')+'</div></div></div><div class="dr-posting-note">'+note+'</div>'+(ev?'<div class="dr-posting-evidence">Evidence: '+ev+'</div>':'')+'</div>';
  }
  function augment(){var f=active();if(!f)return;var ws=document.querySelector('[data-dr-session-active="'+String(f.id).replace(/"/g,'\\"')+'"] .dr-rec-workspace');if(!ws)return;var old=ws.querySelector('.dr-posting-card');if(old)old.remove();var wrap=document.createElement('div');wrap.innerHTML=render(f.postingProposal||null);var node=wrap.firstElementChild;var lines=ws.querySelector('.dr-rec-card.full');if(lines)ws.insertBefore(node,lines);else ws.appendChild(node);}
  var list=document.getElementById('drFileList');if(list)new MutationObserver(function(){setTimeout(augment,0);}).observe(list,{childList:true,subtree:true});setTimeout(augment,0);window.refreshDrPostingProposalUi=augment;window.__jarvisDrPostingProposalUiInstalled=true;
})();

(function(){
  if(window.__jarvisDrPostingProposalUiInstalled)return;
  var style=document.createElement('style');style.id='drPostingProposalStyle';style.textContent=`
    .dr-posting-card{grid-column:1/-1;border:1px solid rgba(139,123,255,.16);background:rgba(139,123,255,.035);border-radius:10px;padding:9px 10px}
    .dr-posting-head{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:7px;font-size:11px;font-weight:700;color:#d8d5ff}
    .dr-posting-mode{font-size:9.5px;padding:3px 7px;border-radius:999px;background:rgba(255,255,255,.06);color:#aaa9bd}.dr-posting-mode.consolidated{color:#ffd78a;background:rgba(255,190,70,.10)}.dr-posting-mode.detailed{color:#8fe0b7;background:rgba(76,201,138,.10)}
    .dr-posting-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:6px 10px}.dr-posting-cell label{display:block;font-size:9.5px;color:#85859a}.dr-posting-cell div{font-size:11px;color:#efedf8;margin-top:1px}.dr-posting-note{font-size:10.5px;color:#aaa9bd;line-height:1.4;margin-top:7px}
    .dr-pattern-evidence{margin-top:8px;display:flex;flex-direction:column;gap:5px}.dr-pattern-row{display:grid;grid-template-columns:minmax(0,1fr) auto auto;align-items:center;gap:8px;padding:5px 7px;border-radius:7px;background:rgba(255,255,255,.025);border:1px solid rgba(255,255,255,.05);font-size:9.8px;color:#aaa9bd}.dr-pattern-row strong{color:#e9e7f7;font-size:10.5px}.dr-pattern-score{white-space:nowrap;color:#8fe0b7}.dr-pattern-open{border:1px solid rgba(139,123,255,.30);background:rgba(117,102,239,.14);color:#e9e7ff;border-radius:6px;padding:3px 7px;font-size:9.5px;cursor:pointer}
    @media(max-width:900px){.dr-posting-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
  `;document.head.appendChild(style);

  function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v);}
  function active(){if(typeof drFiles==='undefined'||!Array.isArray(drFiles))return null;return drFiles.filter(function(f){return f&&!f.standalone&&!(f.registerResult&&f.registerResult.success);})[0]||null;}
  function modeLabel(p){if(!p)return'Αναμονή';if(p.mode==='Consolidated')return'Consolidated';if(p.mode==='Detailed')return'Detailed';return'Χρειάζεται έλεγχο';}
  function pct(v){return v!=null?Math.round(Number(v)*100)+'%':'—';}
  function evRatio(v){return v&&v.sampleSize?String(v.matches)+'/'+String(v.sampleSize)+' · '+pct(v.ratio):'—';}
  function renderEvidence(p){
    var rows=p&&Array.isArray(p.evidence)?p.evidence.slice(0,5):[];if(!rows.length)return'';
    return '<div class="dr-pattern-evidence">'+rows.map(function(x){
      var meta=['FINDOC '+x.findocId,x.fincode||'',x.lineCount!=null?x.lineCount+' γραμμές':'','SOSOURCE '+(x.sosource||'—'),'SERIES '+(x.series||'—'),'BUNIT '+(x.bunit||'—')].filter(Boolean).join(' · ');
      return '<div class="dr-pattern-row"><div><strong>'+(x.isStrongPrecedent?'Ισχυρό precedent · ':'')+esc(meta)+'</strong></div><span class="dr-pattern-score">'+pct(x.similarity)+'</span><button type="button" class="dr-pattern-open" data-dr-open-precedent="'+esc(x.findocId)+'" data-dr-open-sosource="'+esc(x.sosource||'')+'">Άνοιγμα</button></div>';
    }).join('')+'</div>';
  }
  function render(p){
    var confidence=p&&p.confidence!=null?pct(p.confidence):'—';
    var cls=p&&p.mode==='Consolidated'?'consolidated':p&&p.mode==='Detailed'?'detailed':'';
    var source=p&&p.sourceLineCount!=null?p.sourceLineCount:'—',target=p&&p.proposedTargetLineCount!=null?p.proposedTargetLineCount:'—';
    var reason=p&&p.reason||'';
    var note='Το resolve_document_pattern διαβάζει μόνο πραγματικό ιστορικό Soft1 του συγκεκριμένου συναλλασσόμενου. Δεν πραγματοποιεί write.';
    if(reason==='resolving_historical_pattern')note='Αναζητώ παρόμοιες προηγούμενες εγγραφές του συγκεκριμένου συναλλασσόμενου…';
    else if(reason==='similar_historical_precedent')note='Βρέθηκε ισχυρό παρόμοιο προηγούμενο παραστατικό. Η πρόταση βασίζεται στο πραγματικό posting του precedent και στο consensus των κοντινότερων ιστορικών εγγραφών.';
    else if(reason==='historical_consensus')note='Δεν υπάρχει ένα μόνο αρκετά ισχυρό precedent. Η πρόταση βασίζεται σε deterministic consensus των πιο παρόμοιων ιστορικών εγγραφών.';
    else if(reason==='no_historical_documents')note='Δεν υπάρχει ιστορικό για αυτόν τον συναλλασσόμενο. Το document παραμένει Needs review.';
    return '<div class="dr-posting-card"><div class="dr-posting-head"><span>Historical classification · resolve_document_pattern</span><span class="dr-posting-mode '+cls+'">'+esc(modeLabel(p))+'</span></div><div class="dr-posting-grid">'+
      '<div class="dr-posting-cell"><label>Source lines</label><div>'+esc(source)+'</div></div><div class="dr-posting-cell"><label>Proposed Soft1 lines</label><div>'+esc(target)+'</div></div><div class="dr-posting-cell"><label>Confidence</label><div>'+esc(confidence)+'</div></div><div class="dr-posting-cell"><label>Similar samples</label><div>'+esc(p&&p.similarSampleSize!=null?p.similarSampleSize:'—')+'</div></div>'+
      '<div class="dr-posting-cell"><label>SOSOURCE</label><div>'+esc(p&&p.resolvedSosource!=null?p.resolvedSosource:'—')+' · '+esc(evRatio(p&&p.sosourceEvidence))+'</div></div><div class="dr-posting-cell"><label>SERIES</label><div>'+esc(p&&p.resolvedSeries!=null?p.resolvedSeries:'—')+' · '+esc(evRatio(p&&p.seriesEvidence))+'</div></div><div class="dr-posting-cell"><label>BUNIT</label><div>'+esc(p&&p.resolvedBunit!=null?p.resolvedBunit:'—')+' · '+esc(evRatio(p&&p.bunitEvidence))+'</div></div><div class="dr-posting-cell"><label>Precedent</label><div>'+(p&&p.precedentFindocId?'FINDOC '+esc(p.precedentFindocId)+' · '+pct(p.precedentSimilarity):'—')+'</div></div></div><div class="dr-posting-note">'+note+'</div>'+renderEvidence(p)+'</div>';
  }
  function augment(){var f=active();if(!f)return;var ws=document.querySelector('[data-dr-session-active="'+String(f.id).replace(/"/g,'\\"')+'"] .dr-rec-workspace');if(!ws)return;var old=ws.querySelector('.dr-posting-card');if(old)old.remove();var wrap=document.createElement('div');wrap.innerHTML=render(f.postingProposal||null);var node=wrap.firstElementChild;var lines=ws.querySelector('.dr-rec-card.full');if(lines)ws.insertBefore(node,lines);else ws.appendChild(node);}
  document.addEventListener('click',function(e){var b=e.target.closest('[data-dr-open-precedent]');if(!b)return;var id=Number(b.getAttribute('data-dr-open-precedent')||0),sosource=Number(b.getAttribute('data-dr-open-sosource')||0);if(!id||!sosource)return;postCommand({type:'courier_open_document',sosource:sosource,mode:'locate',id:id});},true);
  var list=document.getElementById('drFileList');if(list)new MutationObserver(function(){setTimeout(augment,0);}).observe(list,{childList:true,subtree:true});setTimeout(augment,0);window.refreshDrPostingProposalUi=augment;window.__jarvisDrPostingProposalUiInstalled=true;
})();

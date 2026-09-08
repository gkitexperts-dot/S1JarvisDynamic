(function(){
  // ══════════════════════════════════════════════════════════════════════
  // DR Batch Mode — ΙΔΙΟ flow/UI με S1DocReader.
  //
  // Toggle "Αυτόματη λειτουργία" πάνω από το dropzone. Όταν ενεργό:
  //   • Το "Επεξεργασία" στέλνει ΟΛΑ τα αρχεία μαζί στο dr_batch_start
  //   • Ο C# DrBatchEngine τρέχει τα 10 βήματα ανά αρχείο (QR→ΑΑΔΕ→ΑΦΜ→
  //     profile→history→extract→match→confidence→autocommit→learn)
  //   • Live progress ανά αρχείο (icon + score + step)
  //   • Στο τέλος: 3 tabs — Καταχωρήθηκαν | Για Έλεγχο | Αποκλείστηκαν
  //   • "Για Έλεγχο": κλικ → φορτώνει το αρχείο στο κανονικό DR review flow
  // ══════════════════════════════════════════════════════════════════════
  if(window.__jarvisDrBatchModeInstalled)return;
  if(typeof drFiles==='undefined'||typeof renderDrFileList!=='function')return;
  window.__jarvisDrBatchModeInstalled=true;

  var batchEnabled=false, batchRunning=false, batchResults=null;

  var style=document.createElement('style');
  style.textContent=`
    .dr-batch-toggle{display:flex;align-items:center;gap:10px;margin:6px 0 8px;padding:8px 12px;border:1px solid rgba(51,153,255,.28);border-radius:10px;background:rgba(51,153,255,.05)}
    .dr-batch-toggle label{display:flex;align-items:center;gap:8px;font-size:12px;color:#e8e6ff;cursor:pointer;font-weight:600}
    .dr-batch-toggle input{accent-color:#3399ff;width:16px;height:16px}
    .dr-batch-toggle .hint{font-size:10.5px;color:#9493aa;margin-left:auto}
    .dr-batch-progress{margin:8px 0;padding:10px 12px;border-radius:10px;background:rgba(255,255,255,.035);border:1px solid rgba(255,255,255,.09);font-size:11px}
    .dr-batch-progress .file{color:#3399ff;font-weight:650;margin-bottom:4px}
    .dr-batch-progress .bar{height:5px;background:rgba(255,255,255,.08);border-radius:3px;overflow:hidden;margin:6px 0}
    .dr-batch-progress .bar>div{height:100%;background:#3399ff;transition:width .3s}
    .dr-batch-progress .step{color:#c8c6dd}.dr-batch-progress .score{color:#78e68c;font-weight:650}
    .dr-batch-log{max-height:110px;overflow:auto;font-family:Consolas,monospace;font-size:10px;color:#9493aa;margin-top:6px;padding:6px;background:rgba(0,0,0,.2);border-radius:6px}
    .dr-batch-results{margin-top:10px}
    .dr-batch-tabs{display:flex;gap:2px;margin-bottom:0}
    .dr-batch-tab{padding:7px 14px;font-size:11.5px;font-weight:650;border-radius:8px 8px 0 0;cursor:pointer;background:rgba(255,255,255,.04);color:#9493aa;border:1px solid transparent;border-bottom:none}
    .dr-batch-tab.active{background:rgba(51,153,255,.14);color:#e8e6ff;border-color:rgba(51,153,255,.3)}
    .dr-batch-pane{border:1px solid rgba(51,153,255,.3);border-radius:0 8px 8px 8px;padding:10px;background:rgba(255,255,255,.025);min-height:80px}
    .dr-batch-table{width:100%;border-collapse:collapse;font-size:11px}
    .dr-batch-table th{text-align:left;color:#3399ff;font-weight:650;padding:5px 8px;border-bottom:1px solid rgba(255,255,255,.1)}
    .dr-batch-table td{padding:6px 8px;border-bottom:1px solid rgba(255,255,255,.05);color:#e8e6ff}
    .dr-batch-table tr:hover td{background:rgba(255,255,255,.03)}
    .dr-batch-table .muted{color:#9493aa}
    .dr-batch-btn{border:1px solid rgba(51,153,255,.42);background:rgba(51,153,255,.18);color:#e8e6ff;border-radius:6px;padding:3px 9px;font-size:10.5px;cursor:pointer;font-weight:600}
    .dr-batch-btn.green{border-color:rgba(40,167,69,.5);background:rgba(40,167,69,.2)}
    .dr-batch-empty{color:#9493aa;font-size:11px;padding:14px;text-align:center}
    .dr-batch-reason{color:#ffc107;font-size:10.5px}
  `;
  document.head.appendChild(style);

  // ── Toggle ────────────────────────────────────────────────────────────
  var uploadArea=document.getElementById('drUploadArea');
  var dropzone=document.getElementById('drDropzone');
  if(!uploadArea||!dropzone)return;

  var toggle=document.createElement('div');
  toggle.className='dr-batch-toggle';
  toggle.innerHTML='<label><input type="checkbox" id="drBatchToggle"> Αυτόματη λειτουργία (batch)</label><span class="hint">Αναγνώριση + αυτόματη καταχώρηση με confidence ≥90%</span>';
  uploadArea.insertBefore(toggle,dropzone);
  document.getElementById('drBatchToggle').addEventListener('change',function(e){batchEnabled=e.target.checked;});

  var progressEl=document.createElement('div');progressEl.className='dr-batch-progress';progressEl.style.display='none';
  var resultsEl=document.createElement('div');resultsEl.className='dr-batch-results';resultsEl.style.display='none';
  var fileList=document.getElementById('drFileList');
  fileList.parentNode.insertBefore(progressEl,fileList.nextSibling);
  progressEl.parentNode.insertBefore(resultsEl,progressEl.nextSibling);

  // ── Hijack Επεξεργασία when batch enabled ────────────────────────────
  var processBtn=document.getElementById('drProcessBtn');
  processBtn.addEventListener('click',function(e){
    if(!batchEnabled||batchRunning)return;
    e.stopImmediatePropagation();e.preventDefault();
    startBatch();
  },true);

  function fileToBase64(file){return new Promise(function(res,rej){var r=new FileReader();r.onload=function(){var s=String(r.result||''),p=s.indexOf(',');res(p>=0?s.substring(p+1):s);};r.onerror=function(){rej(r.error);};r.readAsDataURL(file);});}
  function mime(file){return (typeof mimeTypeForDrFile==='function')?mimeTypeForDrFile(file):(file.type||'application/octet-stream');}

  async function startBatch(){
    var queue=drFiles.filter(function(f){return f&&!f.standalone&&f.status==='pending';});
    if(queue.length===0)return;
    batchRunning=true;processBtn.disabled=true;
    resultsEl.style.display='none';progressEl.style.display='';
    progressEl.innerHTML='<div class="file">Προετοιμασία '+queue.length+' αρχείων…</div><div class="bar"><div style="width:0"></div></div><div class="dr-batch-log" id="drBatchLog"></div>';
    var payload=[];
    for(var i=0;i<queue.length;i++){
      var f=queue[i];f.status='processing';
      var m=mime(f.file);
      if(m!=='application/pdf'&&m.indexOf('image/')!==0){f.status='error';f.detail='Μη υποστηριζόμενος τύπος';continue;}
      try{payload.push({fileId:String(f.id),fileName:f.name,base64:await fileToBase64(f.file),mimeType:m,qrLink:f.qrLink||''});}
      catch(_e){f.status='error';f.detail='Αποτυχία ανάγνωσης αρχείου';}
    }
    renderDrFileList();
    if(payload.length===0){finish({results:[]});return;}
    postCommand({type:'dr_batch_start',files:payload});
  }

  function log(msg){var l=document.getElementById('drBatchLog');if(!l)return;var d=document.createElement('div');d.textContent=new Date().toLocaleTimeString('el-GR')+'  '+msg;l.appendChild(d);l.scrollTop=l.scrollHeight;}

  // ── Progress ─────────────────────────────────────────────────────────
  function onProgress(p){
    var f=drFiles.find(function(x){return String(x.id)===String(p.fileId);});
    var icon=p.decision==='AutoCommit'?'✅':p.decision==='Blocked'?'🚫':p.decision==='NeedsReview'?'⚠️':'🔄';
    if(f){f.statusText=icon+' '+(p.step==='done'?(p.score?Math.round(p.score*100)+'%':''):p.message);}
    progressEl.querySelector('.file').textContent='['+p.index+'/'+p.total+'] '+p.fileName;
    progressEl.querySelector('.bar>div').style.width=Math.round((p.index-(p.step==='done'?0:1))/p.total*100)+'%';
    log(p.step==='done'?(icon+' '+p.message):p.message);
    renderDrFileList();
  }

  // ── Complete → 3 tabs ────────────────────────────────────────────────
  function finish(msg){
    batchRunning=false;processBtn.disabled=false;
    progressEl.style.display='none';
    if(msg.error){resultsEl.style.display='';resultsEl.innerHTML='<div class="dr-batch-empty" style="color:#ff6b6b">'+esc(msg.error)+'</div>';return;}
    batchResults=msg.results||[];
    batchResults.forEach(function(r){var f=drFiles.find(function(x){return String(x.id)===String(r.fileId);});if(!f)return;
      f.batchResult=r;f.status=r.decision==='AutoCommit'?'registered':r.decision==='Blocked'?'error':'identified';
      if(r.decision==='AutoCommit'){f.registerResult={success:true,findocId:r.findocId};}
      f.detail=r.confidence?r.confidence.breakdown:(r.errorMessage||'');
    });
    renderDrFileList();
    renderResults('review');
  }

  function renderResults(tab){
    var auto=batchResults.filter(function(r){return r.decision==='AutoCommit';});
    var review=batchResults.filter(function(r){return r.decision==='NeedsReview';});
    var blocked=batchResults.filter(function(r){return r.decision==='Blocked';});
    resultsEl.style.display='';
    resultsEl.innerHTML='<div class="dr-batch-tabs">'+
      '<div class="dr-batch-tab'+(tab==='auto'?' active':'')+'" data-tab="auto">✅ Καταχωρήθηκαν ('+auto.length+')</div>'+
      '<div class="dr-batch-tab'+(tab==='review'?' active':'')+'" data-tab="review">⚠️ Για Έλεγχο ('+review.length+')</div>'+
      '<div class="dr-batch-tab'+(tab==='blocked'?' active':'')+'" data-tab="blocked">🚫 Αποκλείστηκαν ('+blocked.length+')</div></div>'+
      '<div class="dr-batch-pane" id="drBatchPane"></div>';
    resultsEl.querySelectorAll('.dr-batch-tab').forEach(function(t){t.addEventListener('click',function(){renderResults(t.dataset.tab);});});
    var pane=document.getElementById('drBatchPane');
    if(tab==='auto')pane.innerHTML=auto.length?table(['Κύκλωμα','Κωδικός','Ημερομηνία','Συναλλασσόμενος','Score',''],auto.map(function(r){
      var e=r.extraction||{},di=e.document_info||{},hp=r.historyProfile||{};
      return [soLabel(hp.sosource),(di.series||'')+(di.number||''),di.date||'',r.trader&&r.trader.name||'',pct(r.confidence),'<button class="dr-batch-btn" data-open="'+r.findocId+'" data-so="'+(hp.sosource||'')+'">Άνοιγμα</button>'];
    })):'<div class="dr-batch-empty">Καμία αυτόματη καταχώρηση.</div>';
    else if(tab==='review')pane.innerHTML=review.length?table(['Αρχείο','Συναλλασσόμενος','Score','Λόγος',''],review.map(function(r){
      return [r.fileName,r.trader&&r.trader.name||'<span class="muted">—</span>',pct(r.confidence),'<span class="dr-batch-reason">'+esc((r.confidence&&r.confidence.reasons||[]).join(' | '))+'</span>','<button class="dr-batch-btn green" data-review="'+r.fileId+'">Έλεγχος →</button>'];
    })):'<div class="dr-batch-empty">Όλα καταχωρήθηκαν αυτόματα! 🎉</div>';
    else pane.innerHTML=blocked.length?table(['Αρχείο','Αιτία','Score'],blocked.map(function(r){
      return [r.fileName,'<span class="dr-batch-reason">'+esc((r.confidence&&r.confidence.reasons||[]).join(' | ')||r.errorMessage||'')+'</span>',pct(r.confidence)];
    })):'<div class="dr-batch-empty">Κανένα αποκλεισμένο.</div>';
    pane.querySelectorAll('[data-open]').forEach(function(b){b.addEventListener('click',function(){postCommand({type:'open_document',id:parseInt(b.dataset.open),sosource:parseInt(b.dataset.so)||0});});});
    pane.querySelectorAll('[data-review]').forEach(function(b){b.addEventListener('click',function(){
      var f=drFiles.find(function(x){return String(x.id)===b.dataset.review;});
      if(f){f.status='identified';if(f.batchResult){var r=f.batchResult;f.detection=r.detection;f.extraction=r.extraction;f.issuerAfm=r.detection&&r.detection.issuerAfm;f.trdrId=r.trader&&r.trader.trdrId;f.traderName=r.trader&&r.trader.name;}
        if(typeof setDrActiveFile==='function')setDrActiveFile(f);renderDrFileList();
        if(typeof window.refreshDrRecognitionWorkspace==='function')setTimeout(window.refreshDrRecognitionWorkspace,0);}
    });});
  }

  function table(hdr,rows){return '<table class="dr-batch-table"><thead><tr>'+hdr.map(function(h){return '<th>'+h+'</th>';}).join('')+'</tr></thead><tbody>'+rows.map(function(r){return '<tr>'+r.map(function(c){return '<td>'+c+'</td>';}).join('')+'</tr>';}).join('')+'</tbody></table>';}
  function pct(c){return c&&typeof c.finalScore==='number'?Math.round(c.finalScore*100)+'%':'—';}
  function soLabel(s){return s===1351||s===1353?'Πωλήσεις':'Αγορές';}
  function esc(s){return String(s||'').replace(/[&<>"]/g,function(ch){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[ch];});}

  // ── Message hook ──────────────────────────────────────────────────────
  window.chrome&&window.chrome.webview&&window.chrome.webview.addEventListener('message',function(ev){
    var d=ev.data;if(typeof d==='string'){try{d=JSON.parse(d);}catch(_e){return;}}
    if(!d||typeof d.type!=='string')return;
    if(d.type==='dr_batch_progress')onProgress(d);
    else if(d.type==='dr_batch_complete')finish(d);
  });
})();

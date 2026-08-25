(function(){
  if(window.__jarvisDrSessionLoopInstalled)return;
  if(typeof renderDrEntryRow!=='function'||typeof renderDrFileList!=='function')return;

  // Stable numeric contract for per-line recognition/mapping state.
  // Multiples of 10 intentionally leave room for future intermediate states.
  var DR_LINE_STATE={
    Pending:0,
    Extracted:10,
    ExactMatched:20,
    SupplierMapped:30,
    Proposed:40,
    ManualResolved:50,
    NewItemCreated:60,
    NeedsReview:70,
    Blocked:80,
    Ready:90,
    Registered:100
  };
  window.DR_LINE_RECOGNITION_STATE=DR_LINE_STATE;

  var style=document.createElement('style');
  style.id='drSessionLoopStyle';
  style.textContent=`
    .dr-session-stack{display:flex;flex-direction:column;gap:9px}
    .dr-session-complete,.dr-session-queued{display:flex;align-items:center;gap:10px;padding:9px 11px;border-radius:10px;border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.035)}
    .dr-session-complete{border-color:rgba(76,201,138,.26);background:rgba(76,201,138,.055)}
    .dr-session-complete-icon{color:#8fe0b7;font-size:15px;font-weight:700;flex:none}
    .dr-session-summary{flex:1;min-width:0}
    .dr-session-title{font-size:12.5px;font-weight:650;color:#f2f0ff;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .dr-session-meta{font-size:10.8px;color:#9a9ab0;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .dr-session-open{flex:none;border:1px solid rgba(139,123,255,.38);background:rgba(117,102,239,.22);color:#fff;border-radius:8px;padding:5px 9px;font-size:11px;cursor:pointer}
    .dr-session-active{border:1px solid rgba(139,123,255,.23);border-radius:12px;padding:8px;background:rgba(139,123,255,.025)}
    .dr-session-active-head{display:flex;align-items:center;justify-content:space-between;gap:8px;margin:0 1px 7px;color:#d8d5ff;font-size:11px;font-weight:650}
    .dr-session-active-badge{font-size:10px;padding:3px 7px;border-radius:999px;background:rgba(139,123,255,.16);border:1px solid rgba(139,123,255,.28)}
    .dr-session-queued{opacity:.62}
    .dr-session-queued .dr-session-title{color:#c3c0d8}
    .dr-session-queue-badge{flex:none;font-size:10px;color:#9a9ab0;padding:3px 7px;border-radius:999px;background:rgba(255,255,255,.05)}
    .dr-session-chat-slot{margin-top:9px}
    .dr-session-chat-slot #drAssistantPanel{position:relative!important;bottom:auto!important;width:100%!important;margin:0!important;box-shadow:none!important}
  `;
  document.head.appendChild(style);

  var baseRenderDrFileList=window.renderDrFileList;

  function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v);}
  function visibleFiles(){return (typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles.filter(function(f){return f&&!f.standalone;}):[];}
  function done(f){return !!(f&&f.registerResult&&f.registerResult.success);}
  function fileName(f){return (f&&f.file&&f.file.name)||f.name||('Παραστατικό '+(f&&f.id!=null?f.id:''));}
  function docInfo(f){return f&&f.extraction&&f.extraction.document_info||{};}
  function totals(f){return f&&f.extraction&&f.extraction.totals||{};}
  function firstValue(obj,names){for(var i=0;i<names.length;i++){var v=obj&&obj[names[i]];if(v!==undefined&&v!==null&&String(v).trim()!=='')return v;}return '';}

  function deriveLineState(f,line,index){
    if(done(f))return DR_LINE_STATE.Registered;
    if(f.registering)return DR_LINE_STATE.Ready;
    if(f.registerError)return DR_LINE_STATE.Blocked;
    if(!f.extraction)return DR_LINE_STATE.Pending;
    if(!line)return DR_LINE_STATE.NeedsReview;
    if(f.drCreatedItems&&f.drCreatedItems[index])return DR_LINE_STATE.NewItemCreated;
    if(f.manualLineMtrlIds&&f.manualLineMtrlIds[index]!=null)return DR_LINE_STATE.ManualResolved;
    if(line.matched){
      var source=String(line.matchSource||line.match_source||line.matched.source||line.matched.matchSource||'').toLowerCase();
      if(source.indexOf('supplier')>=0||source.indexOf('mtrsup')>=0)return DR_LINE_STATE.SupplierMapped;
      return DR_LINE_STATE.ExactMatched;
    }
    if(line.proposed||line.candidates||line.matchProposal||line.match_proposal)return DR_LINE_STATE.Proposed;
    return DR_LINE_STATE.Extracted;
  }

  function syncLineRecognitionStates(f){
    if(!f)return {};
    var lines=f.extraction&&Array.isArray(f.extraction.line_items)?f.extraction.line_items:[];
    var states={};
    for(var i=0;i<lines.length;i++)states[String(i)]=deriveLineState(f,lines[i],i);
    f.lineRecognitionStates=states;
    return states;
  }

  window.getDrLineRecognitionStates=function(documentOrId){
    var f=documentOrId;
    if(!f||typeof f!=='object')f=visibleFiles().find(function(x){return String(x.id)===String(documentOrId);});
    return f?syncLineRecognitionStates(f):{};
  };

  function completedTitle(f){
    var d=docInfo(f),r=f.registerResult||{};
    var number=firstValue(d,['number','document_number','invoice_number','fincode'])||r.fincode||('FINDOC #'+r.findocId);
    var trader=(f.trader&&(f.trader.name||f.trader.NAME))||firstValue(d,['issuer_name','counterparty_name','supplier_name'])||'';
    return (number?String(number):fileName(f))+(trader?' · '+String(trader):'');
  }
  function completedMeta(f){
    var d=docInfo(f),t=totals(f),r=f.registerResult||{};
    var date=firstValue(d,['date','document_date','invoice_date']);
    var total=firstValue(t,['gross','total','gross_total','total_amount']);
    var parts=[];
    if(date)parts.push(String(date));
    if(total)parts.push(String(total));
    if(r.findocId)parts.push('FINDOC '+r.findocId);
    if(r.fincode)parts.push(String(r.fincode));
    return parts.join(' · ');
  }
  function completedRow(f){
    var r=f.registerResult||{};
    return '<div class="dr-session-complete" data-dr-session-complete="'+esc(f.id)+'">'+
      '<div class="dr-session-complete-icon">✓</div><div class="dr-session-summary">'+
      '<div class="dr-session-title">'+esc(completedTitle(f))+'</div><div class="dr-session-meta">'+esc(completedMeta(f))+'</div></div>'+
      (r.findocId?'<button type="button" class="dr-session-open" data-dr-open-completed="'+esc(f.id)+'">Άνοιγμα</button>':'')+'</div>';
  }
  function queuedRow(f,index){
    return '<div class="dr-session-queued"><div class="dr-session-summary"><div class="dr-session-title">'+esc(fileName(f))+'</div><div class="dr-session-meta">Θα ξεκινήσει μόλις ολοκληρωθεί το ενεργό παραστατικό.</div></div><span class="dr-session-queue-badge">Αναμονή '+index+'</span></div>';
  }
  function activeBlock(f){
    return '<section class="dr-session-active" data-dr-session-active="'+esc(f.id)+'"><div class="dr-session-active-head"><span>Ενεργό παραστατικό · '+esc(fileName(f))+'</span><span class="dr-session-active-badge">Σε επεξεργασία</span></div>'+renderDrEntryRow(f)+'<div class="dr-session-chat-slot" data-dr-chat-slot="'+esc(f.id)+'"></div></section>';
  }
  function chooseActive(files){
    var pending=files.filter(function(f){return !done(f);});
    return pending.length?pending[0]:null;
  }
  function moveAssistant(active){
    var panel=document.getElementById('drAssistantPanel');
    if(!panel||!active)return;
    var slot=document.querySelector('[data-dr-chat-slot="'+String(active.id).replace(/"/g,'\\"')+'"]');
    if(slot&&panel.parentNode!==slot)slot.appendChild(panel);
    var summary=panel.querySelector('summary');
    if(summary)summary.textContent='Διευκρινίσεις για · '+fileName(active);
  }

  window.renderDrFileList=function(){
    var files=visibleFiles();
    files.forEach(syncLineRecognitionStates);
    if(!files.length){baseRenderDrFileList();return;}
    var completed=files.filter(done);
    var active=chooseActive(files);
    var queued=active?files.filter(function(f){return !done(f)&&f!==active;}):[];
    var html='<div class="dr-session-stack">';
    completed.forEach(function(f){html+=completedRow(f);});
    if(active)html+=activeBlock(active);
    queued.forEach(function(f,i){html+=queuedRow(f,i+1);});
    html+='</div>';
    drFileListEl.innerHTML=html;
    if(active&&typeof setDrActiveFile==='function'&&String(drActiveFileId)!==String(active.id))setDrActiveFile(active);
    setTimeout(function(){moveAssistant(active);},0);
  };

  var list=document.getElementById('drFileList');
  if(list){
    list.addEventListener('click',function(ev){
      var btn=ev.target.closest('[data-dr-open-completed]');
      if(!btn)return;
      var id=btn.getAttribute('data-dr-open-completed');
      var f=visibleFiles().find(function(x){return String(x.id)===String(id);});
      if(!f||!f.registerResult||!f.registerResult.findocId)return;
      var d=docInfo(f),r=f.registerResult||{};
      var sosource=r.sosource||firstValue(d,['sosource','SOSOURCE'])||f.manualSosource||(f.seriesGuess&&f.seriesGuess.sosource);
      if(!sosource){alert('Το παραστατικό δημιουργήθηκε, αλλά δεν υπάρχει διαθέσιμο SOSOURCE για άνοιγμα από αυτή την προβολή.');return;}
      postCommand({type:'courier_open_document',sosource:Number(sosource),mode:'locate',id:Number(r.findocId)});
    },true);
  }

  window.renderDrFileList();
  window.__jarvisDrSessionLoopInstalled=true;
})();

(function(){
  if(window.__jarvisDrRecognitionWorkspaceInstalled)return;

  var STATES=window.DR_LINE_RECOGNITION_STATE||{
    Pending:0,Extracted:10,ExactMatched:20,SupplierMapped:30,Proposed:40,
    ManualResolved:50,NewItemCreated:60,NeedsReview:70,Blocked:80,Ready:90,Registered:100
  };
  var LABELS={
    0:'Αναμονή',10:'Αναγνωρίστηκε',20:'Exact match',30:'Supplier mapping',40:'Πρόταση',
    50:'Επιλογή χειριστή',60:'Νέο είδος',70:'Χρειάζεται έλεγχο',80:'Μπλοκαρισμένο',90:'Έτοιμο',100:'Καταχωρήθηκε'
  };

  var style=document.createElement('style');
  style.id='drRecognitionWorkspaceStyle';
  style.textContent=`
    .dr-rec-workspace{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.1fr);gap:8px;margin:0 0 9px}
    .dr-rec-card{border:1px solid rgba(255,255,255,.08);background:rgba(255,255,255,.025);border-radius:10px;padding:9px 10px;min-width:0}
    .dr-rec-card.full{grid-column:1/-1}.dr-rec-title{display:flex;align-items:center;justify-content:space-between;gap:8px;font-size:11px;font-weight:700;color:#d8d5ff;margin-bottom:7px}
    .dr-rec-status{font-size:9.5px;font-weight:600;padding:3px 6px;border-radius:999px;background:rgba(255,255,255,.06);color:#aaa9bd}
    .dr-rec-status.ok{color:#8fe0b7;background:rgba(76,201,138,.10)}.dr-rec-status.warn{color:#ffd78a;background:rgba(255,190,70,.10)}
    .dr-rec-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:5px 10px}.dr-rec-field{min-width:0}.dr-rec-field label{display:block;font-size:9.5px;color:#85859a;margin-bottom:1px}.dr-rec-field div{font-size:11.5px;color:#f0eff8;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .dr-rec-empty{font-size:11px;color:#85859a;line-height:1.35}.dr-rec-lines{display:flex;flex-direction:column;gap:5px}
    .dr-rec-line{display:grid;grid-template-columns:28px minmax(0,1fr) auto;gap:7px;align-items:center;padding:6px 7px;border-radius:8px;background:rgba(255,255,255,.025);border:1px solid rgba(255,255,255,.055)}
    .dr-rec-line.warn{border-color:rgba(255,190,70,.22)}.dr-rec-line.block{border-color:rgba(255,107,107,.25)}.dr-rec-line.ok{border-color:rgba(76,201,138,.18)}
    .dr-rec-line-no{font-size:10px;color:#77778c;text-align:center}.dr-rec-line-main{min-width:0}.dr-rec-line-main strong{display:block;font-size:11px;color:#ecebf5;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.dr-rec-line-main small{display:block;font-size:9.5px;color:#85859a;margin-top:1px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
    .dr-rec-line-state{font-size:9.5px;padding:3px 6px;border-radius:999px;background:rgba(255,255,255,.06);color:#aaa9bd;white-space:nowrap}.dr-rec-line-state b{font-weight:700;color:#d8d5ff;margin-right:3px}
    @media(max-width:900px){.dr-rec-workspace{grid-template-columns:1fr}.dr-rec-card.full{grid-column:auto}}
  `;
  document.head.appendChild(style);

  function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v);}
  function first(o,names){for(var i=0;i<names.length;i++){var v=o&&o[names[i]];if(v!==undefined&&v!==null&&String(v).trim()!=='')return v;}return '';}
  function active(){
    if(typeof drFiles==='undefined'||!Array.isArray(drFiles))return null;
    var files=drFiles.filter(function(f){return f&&!f.standalone&&!(f.registerResult&&f.registerResult.success);});
    return files.length?files[0]:null;
  }
  function field(label,value){return '<div class="dr-rec-field"><label>'+esc(label)+'</label><div title="'+esc(value||'—')+'">'+esc(value||'—')+'</div></div>';}
  function headerCard(f){
    var ex=f.extraction||{},d=ex.document_info||{},t=ex.totals||{},det=f.detection||{};
    var number=first(d,['number','document_number','invoice_number','fincode']);
    var date=first(d,['date','document_date','invoice_date']);
    var afm=first(d,['issuer_afm','vat_number','issuer_vat'])||det.issuerAfm||'';
    var issuer=first(d,['issuer_name','supplier_name','counterparty_name'])||det.issuerName||'';
    var net=first(t,['net','net_total','net_amount']);
    var vat=first(t,['vat','vat_total','vat_amount']);
    var gross=first(t,['gross','total','gross_total','total_amount']);
    var ready=!!(f.detection||f.extraction);
    return '<div class="dr-rec-card"><div class="dr-rec-title"><span>Αναγνώριση Header</span><span class="dr-rec-status '+(ready?'ok':'')+'">'+(ready?'Δεδομένα διαθέσιμα':'Αναμονή')+'</span></div>'+
      (ready?'<div class="dr-rec-grid">'+field('Αριθμός',number)+field('Ημερομηνία',date)+field('Εκδότης',issuer)+field('ΑΦΜ',afm)+field('Καθαρή αξία',net)+field('ΦΠΑ',vat)+field('Σύνολο',gross)+'</div>':'<div class="dr-rec-empty">Το header θα γεμίσει μόλις ολοκληρωθεί η πρώτη αναγνώριση του παραστατικού.</div>')+'</div>';
  }
  function traderCard(f){
    var tr=f.trader||{},resolved=!!(f.trdrId||tr.trdrId||tr.TRDR),warn=!!(f.ambiguous||f.pendingCreate||f.notFound);
    var id=f.trdrId||tr.trdrId||tr.TRDR||'';
    var code=tr.code||tr.CODE||'';
    var name=tr.name||tr.NAME||'';
    var afm=tr.afm||tr.AFM||'';
    var label=resolved?'Resolved':warn?'Χρειάζεται έλεγχο':'Αναμονή';
    return '<div class="dr-rec-card"><div class="dr-rec-title"><span>Soft1 Συναλλασσόμενος</span><span class="dr-rec-status '+(resolved?'ok':warn?'warn':'')+'">'+label+'</span></div>'+
      (resolved?'<div class="dr-rec-grid">'+field('TRDR',id)+field('Κωδικός',code)+field('Επωνυμία',name)+field('ΑΦΜ',afm)+'</div>':'<div class="dr-rec-empty">'+(warn?'Η αντιστοίχιση χρειάζεται απόφαση χειριστή.':'Θα γίνει αναζήτηση με βάση τα αναγνωρισμένα στοιχεία του παραστατικού.')+'</div>')+'</div>';
  }
  function stateClass(s){if(s>=80&&s<90)return'block';if(s>=70&&s<80)return'warn';if(s>=20||s===100)return'ok';return'';}
  function linesCard(f){
    var lines=f.extraction&&f.extraction.line_items||[];
    var states=f.lineRecognitionStates||{};
    var html='<div class="dr-rec-card full"><div class="dr-rec-title"><span>Γραμμές παραστατικού</span><span class="dr-rec-status '+(lines.length?'ok':'')+'">'+(lines.length?lines.length+' γραμμές':'Αναμονή')+'</span></div>';
    if(!lines.length)return html+'<div class="dr-rec-empty">Οι γραμμές και η κατάσταση αναγνώρισης κάθε γραμμής θα εμφανιστούν εδώ.</div></div>';
    html+='<div class="dr-rec-lines">';
    lines.forEach(function(li,i){
      var s=Number(states[String(i)]!=null?states[String(i)]:STATES.Extracted),m=li.matched||{};
      var desc=li.description||li.name||'Χωρίς περιγραφή';
      var src=(li.code?'Κωδ. '+li.code+' · ':'')+(li.quantity!=null?'Ποσ. '+li.quantity+' · ':'')+(li.unit_price!=null?'Τιμή '+li.unit_price+' · ':'')+(li.vat_rate!=null?'ΦΠΑ '+li.vat_rate:'');
      var target=(m.code||m.name)?(' → ['+(m.code||'')+'] '+(m.name||'')):'';
      html+='<div class="dr-rec-line '+stateClass(s)+'"><div class="dr-rec-line-no">#'+(i+1)+'</div><div class="dr-rec-line-main"><strong>'+esc(desc)+'</strong><small>'+esc(src+target)+'</small></div><span class="dr-rec-line-state"><b>'+s+'</b>'+esc(LABELS[s]||'Άγνωστη')+'</span></div>';
    });
    return html+'</div></div>';
  }
  function validationCard(f){
    var dup=f.duplicateCheck,reg=f.registerResult;
    var blockers=[];
    if(dup&&dup.isDuplicate)blockers.push('Πιθανό duplicate');
    if(f.ambiguous)blockers.push('Αμφίσημος συναλλασσόμενος');
    if(f.pendingCreate)blockers.push('Νέος συναλλασσόμενος σε εκκρεμότητα');
    if(f.registerError)blockers.push(f.registerError);
    var states=f.lineRecognitionStates||{},unresolved=0;
    Object.keys(states).forEach(function(k){var s=Number(states[k]);if(s<90&&s!==100)unresolved++;});
    if(unresolved)blockers.push(unresolved+' γραμμές δεν είναι ακόμη Ready');
    var ok=!blockers.length&&!!f.extraction;
    return '<div class="dr-rec-card full"><div class="dr-rec-title"><span>Validation / Blockers</span><span class="dr-rec-status '+(ok?'ok':blockers.length?'warn':'')+'">'+(ok?'Ready for review':blockers.length?blockers.length+' εκκρεμότητες':'Αναμονή')+'</span></div><div class="dr-rec-empty">'+(blockers.length?blockers.map(function(x){return '• '+esc(x);}).join('<br>'):ok?'Δεν υπάρχει ενεργό blocker στο τρέχον state.':'Οι έλεγχοι θα ενημερώνονται καθώς προχωρά η αναγνώριση.')+'</div></div>';
  }
  function build(f){return '<div class="dr-rec-workspace">'+headerCard(f)+traderCard(f)+linesCard(f)+validationCard(f)+'</div>';}
  function augment(){
    var f=active();if(!f)return;
    var host=document.querySelector('[data-dr-session-active="'+String(f.id).replace(/"/g,'\\"')+'"]');if(!host)return;
    var old=host.querySelector('.dr-rec-workspace');if(old)old.remove();
    var head=host.querySelector('.dr-session-active-head');if(!head)return;
    var wrap=document.createElement('div');wrap.innerHTML=build(f);var node=wrap.firstElementChild;if(node)head.insertAdjacentElement('afterend',node);
  }
  var list=document.getElementById('drFileList');
  if(list)new MutationObserver(function(){setTimeout(augment,0);}).observe(list,{childList:true,subtree:true});
  setTimeout(augment,0);
  window.refreshDrRecognitionWorkspace=augment;
  window.__jarvisDrRecognitionWorkspaceInstalled=true;
})();

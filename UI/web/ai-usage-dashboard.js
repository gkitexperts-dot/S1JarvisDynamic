(function(){
  'use strict';
  if(window.__jarvisAiUsageDashboardInstalled)return;
  window.__jarvisAiUsageDashboardInstalled=true;

  var tabsHost=document.querySelector('.dashboard-tabs');
  var track=document.getElementById('dashboardPagesTrack');
  var curtain=document.getElementById('dashboardCurtain');
  if(!tabsHost||!track||!curtain)return;

  var PREFIX='@@JARVIS_AI_USAGE@@';
  var MODE_TODAY='__AI_USAGE_TODAY__';
  var MODE_30D='__AI_USAGE_30D__';
  var pageBase=track.querySelectorAll('.dashboard-page').length;
  var pending={};
  var seq=0;
  var chart30=null;

  function style(){
    if(document.getElementById('aiUsageDashboardStyle'))return;
    var s=document.createElement('style');
    s.id='aiUsageDashboardStyle';
    s.textContent='\
.ai-usage-page{padding:0!important;overflow:hidden;}\
.ai-usage-shell{height:100%;display:flex;flex-direction:column;min-width:0;}\
.ai-usage-toolbar{display:flex;align-items:center;gap:10px;padding:10px 14px;border-bottom:1px solid var(--surface-border);background:rgba(255,255,255,.015);}\
.ai-usage-title{font-weight:650;font-size:14px;}\
.ai-usage-scope{font-size:11px;color:var(--text-dim);padding:3px 8px;border:1px solid var(--surface-border);border-radius:999px;}\
.ai-usage-refresh{margin-left:auto;border:1px solid var(--surface-border);background:var(--surface);color:var(--text);border-radius:8px;padding:6px 10px;cursor:pointer;font:inherit;font-size:12px;}\
.ai-usage-refresh:hover{background:rgba(255,255,255,.07);}\
.ai-usage-body{flex:1 1 auto;overflow:auto;padding:14px;}\
.ai-usage-cards{display:grid;grid-template-columns:repeat(4,minmax(120px,1fr));gap:10px;margin-bottom:14px;}\
.ai-usage-card{background:var(--surface);border:1px solid var(--surface-border);border-radius:12px;padding:12px;}\
.ai-usage-card-label{font-size:11px;color:var(--text-dim);margin-bottom:5px;}\
.ai-usage-card-value{font-size:20px;font-weight:700;line-height:1.1;}\
.ai-usage-section{background:var(--surface);border:1px solid var(--surface-border);border-radius:12px;padding:12px;margin-top:12px;}\
.ai-usage-section-title{font-size:12px;font-weight:650;margin-bottom:10px;}\
.ai-usage-table-wrap{overflow:auto;max-height:390px;border-radius:8px;}\
.ai-usage-table{width:100%;border-collapse:collapse;font-size:12px;white-space:nowrap;}\
.ai-usage-table th,.ai-usage-table td{padding:7px 9px;text-align:right;border-bottom:1px solid var(--surface-border);}\
.ai-usage-table th{position:sticky;top:0;background:#33334a;font-weight:600;z-index:1;}\
.ai-usage-table tbody tr:hover{background:rgba(139,123,255,.08);}\
.ai-usage-empty,.ai-usage-loading,.ai-usage-error{padding:28px;text-align:center;color:var(--text-dim);}\
.ai-usage-error{color:#ff9a9a;}\
.ai-usage-chart-wrap{height:260px;position:relative;}\
@media(max-width:850px){.ai-usage-cards{grid-template-columns:repeat(2,minmax(120px,1fr));}}';
    document.head.appendChild(s);
  }

  function makeTab(label,index,period){
    var b=document.createElement('button');
    b.type='button';
    b.className='dashboard-tab';
    b.dataset.dashboardPage=String(index);
    b.dataset.aiUsageTab=period;
    b.textContent=label;
    b.addEventListener('click',function(){activate(index,b);load(period,false);});
    tabsHost.appendChild(b);
    return b;
  }

  function makePage(id,title,period){
    var p=document.createElement('div');
    p.className='dashboard-page ai-usage-page';
    p.id=id;
    p.innerHTML='<div class="ai-usage-shell">'+
      '<div class="ai-usage-toolbar"><span class="ai-usage-title">'+title+'</span><span class="ai-usage-scope" data-ai-scope>—</span><button type="button" class="ai-usage-refresh">Ανανέωση</button></div>'+
      '<div class="ai-usage-body"><div class="ai-usage-loading">Φόρτωση usage…</div></div></div>';
    p.querySelector('.ai-usage-refresh').addEventListener('click',function(){load(period,true);});
    track.appendChild(p);
    return p;
  }

  function activate(index,tab){
    document.querySelectorAll('.dashboard-tab').forEach(function(t){t.classList.toggle('active',t===tab);});
    track.style.transform='translateX(-'+(index*100)+'%)';
  }

  // Existing tabs have their original handlers. Capture only ensures our
  // newly-added tabs do not remain visually active when Commercial/Tasks is clicked.
  document.addEventListener('click',function(ev){
    var t=ev.target&&ev.target.closest?ev.target.closest('.dashboard-tab'):null;
    if(t&&!t.dataset.aiUsageTab){
      document.querySelectorAll('.dashboard-tab[data-ai-usage-tab]').forEach(function(x){x.classList.remove('active');});
    }
  },true);

  style();
  var todayIndex=pageBase;
  var daysIndex=pageBase+1;
  var todayTab=makeTab('AI Usage · Σήμερα',todayIndex,'today');
  var daysTab=makeTab('AI Usage · 30 ημέρες',daysIndex,'30d');
  var todayPage=makePage('dashboardPageAiUsageToday','AI Usage · Σήμερα','today');
  var daysPage=makePage('dashboardPageAiUsage30Days','AI Usage · Τελευταίες 30 ημέρες','30d');

  function load(period,force){
    var page=period==='today'?todayPage:daysPage;
    if(!force&&page.dataset.loaded==='1')return;
    var body=page.querySelector('.ai-usage-body');
    body.innerHTML='<div class="ai-usage-loading">Φόρτωση usage…</div>';
    var id='ai_usage_'+period+'_'+Date.now()+'_'+(++seq);
    pending[id]={period:period,page:page};
    var mode=period==='today'?MODE_TODAY:MODE_30D;
    if(window.chrome&&window.chrome.webview){
      window.chrome.webview.postMessage(JSON.stringify({type:'dashboard_query',date:mode,requestId:id}));
    }else{
      body.innerHTML='<div class="ai-usage-error">Δεν είναι διαθέσιμο το WebView bridge.</div>';
    }
  }

  function parseMessage(data){
    if(typeof data==='string'){
      try{return JSON.parse(data);}catch(_){return null;}
    }
    return data&&typeof data==='object'?data:null;
  }

  if(window.chrome&&window.chrome.webview){
    window.chrome.webview.addEventListener('message',function(ev){
      var msg=parseMessage(ev.data);
      if(!msg||msg.type!=='dashboard_result')return;
      var id=String(msg.requestId||'');
      var req=pending[id];
      if(!req)return;
      delete pending[id];
      var text=String(msg.text||'');
      if(text.indexOf(PREFIX)!==0){
        req.page.querySelector('.ai-usage-body').innerHTML='<div class="ai-usage-error">'+esc(text||'Αποτυχία φόρτωσης usage.')+'</div>';
        return;
      }
      try{
        var payload=JSON.parse(text.substring(PREFIX.length));
        render(req.period,req.page,payload);
        req.page.dataset.loaded='1';
      }catch(err){
        req.page.querySelector('.ai-usage-body').innerHTML='<div class="ai-usage-error">Μη έγκυρη απάντηση usage: '+esc(err&&err.message||err)+'</div>';
      }
    });
  }

  function render(period,page,p){
    var scope=page.querySelector('[data-ai-scope]');
    scope.textContent=p.scope==='all'?'Όλοι οι χρήστες':'Μόνο δικά σου';
    if(period==='today')renderToday(page,p);else render30(page,p);
  }

  function cards(summary){
    summary=summary||{};
    return '<div class="ai-usage-cards">'+
      card('Calls',num(summary.calls))+
      card('Input tokens',num(summary.inTokens))+
      card('Output tokens',num(summary.outTokens))+
      card('Total tokens',num(summary.totalTokens))+
      '</div>';
  }
  function card(label,value){return '<div class="ai-usage-card"><div class="ai-usage-card-label">'+label+'</div><div class="ai-usage-card-value">'+value+'</div></div>';}

  function renderToday(page,p){
    var rows=Array.isArray(p.rows)?p.rows:[];
    var body=page.querySelector('.ai-usage-body');
    var html=cards(p.summary);
    html+='<div class="ai-usage-section"><div class="ai-usage-section-title">Ανάλυση ημέρας · '+esc(p.date||'')+'</div>';
    if(!rows.length){html+='<div class="ai-usage-empty">Δεν υπάρχουν usage εγγραφές για σήμερα.</div>';}
    else{
      html+='<div class="ai-usage-table-wrap"><table class="ai-usage-table"><thead><tr><th>Χρήστης</th><th>Agent</th><th>Provider</th><th>Model</th><th>Calls</th><th>IN</th><th>OUT</th><th>Total</th><th>Errors</th></tr></thead><tbody>';
      rows.forEach(function(r){html+='<tr><td>'+esc((r.userName||'')+' ('+r.userId+')')+'</td><td>'+esc(r.agent)+'</td><td>'+esc(r.provider)+'</td><td>'+esc(r.model)+'</td><td>'+num(r.calls)+'</td><td>'+num(r.inTokens)+'</td><td>'+num(r.outTokens)+'</td><td>'+num(r.totalTokens)+'</td><td>'+num(r.errorCalls)+'</td></tr>';});
      html+='</tbody></table></div>';
    }
    html+='</div>';
    body.innerHTML=html;
  }

  function render30(page,p){
    var rows=Array.isArray(p.rows)?p.rows:[];
    var body=page.querySelector('.ai-usage-body');
    var html=cards(p.summary);
    html+='<div class="ai-usage-section"><div class="ai-usage-section-title">Total tokens ανά ημέρα · '+esc(p.fromDate||'')+' → '+esc(p.toDate||'')+'</div><div class="ai-usage-chart-wrap"><canvas id="aiUsage30Chart"></canvas></div></div>';
    html+='<div class="ai-usage-section"><div class="ai-usage-section-title">Ημερήσια στοιχεία</div>';
    if(!rows.length){html+='<div class="ai-usage-empty">Δεν υπάρχουν usage δεδομένα για τις τελευταίες 30 ημέρες.</div>';}
    else{
      html+='<div class="ai-usage-table-wrap"><table class="ai-usage-table"><thead><tr><th>Ημερομηνία</th><th>Calls</th><th>IN</th><th>OUT</th><th>Total</th><th>Errors</th></tr></thead><tbody>';
      rows.forEach(function(r){html+='<tr><td>'+esc(r.date)+'</td><td>'+num(r.calls)+'</td><td>'+num(r.inTokens)+'</td><td>'+num(r.outTokens)+'</td><td>'+num(r.totalTokens)+'</td><td>'+num(r.errorCalls)+'</td></tr>';});
      html+='</tbody></table></div>';
    }
    html+='</div>';
    body.innerHTML=html;
    mount30Chart(rows);
  }

  function mount30Chart(rows){
    if(chart30){try{chart30.destroy();}catch(_){}chart30=null;}
    var canvas=document.getElementById('aiUsage30Chart');
    if(!canvas||typeof Chart==='undefined')return;
    chart30=new Chart(canvas.getContext('2d'),{
      type:'line',
      data:{labels:rows.map(function(r){return r.date;}),datasets:[{label:'Total tokens',data:rows.map(function(r){return Number(r.totalTokens||0);}),tension:.25,fill:false}]},
      options:{responsive:true,maintainAspectRatio:false,plugins:{legend:{display:true}},scales:{y:{beginAtZero:true}}}
    });
  }

  function num(v){var n=Number(v||0);return Number.isFinite(n)?n.toLocaleString('el-GR',{maximumFractionDigits:0}):'0';}
  function esc(v){return String(v==null?'':v).replace(/[&<>"']/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}

  // If the curtain is reopened, existing product behavior defaults to Tasks.
  // Keep visual state consistent with that default.
  var observer=new MutationObserver(function(){
    if(curtain.classList.contains('open')&&track.style.transform==='translateX(-100%)'){
      todayTab.classList.remove('active');daysTab.classList.remove('active');
    }
  });
  observer.observe(curtain,{attributes:true,attributeFilter:['class']});
})();

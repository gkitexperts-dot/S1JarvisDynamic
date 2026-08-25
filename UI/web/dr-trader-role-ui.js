(function(){
  if(window.__jarvisDrTraderRoleUiInstalled)return;
  var style=document.createElement('style');
  style.textContent='.dr-role-extra{margin-top:7px;padding-top:7px;border-top:1px solid rgba(255,255,255,.06);font-size:9.5px;color:#8f90a4;line-height:1.45}.dr-role-extra strong{color:#d8d5ff}.dr-role-list{margin-top:4px;display:flex;flex-wrap:wrap;gap:4px}.dr-role-chip{padding:2px 6px;border-radius:999px;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.06)}.dr-role-chip.selected{color:#8fe0b7;border-color:rgba(76,201,138,.22);background:rgba(76,201,138,.08)}';
  document.head.appendChild(style);

  function esc(v){return typeof escapeHtml==='function'?escapeHtml(v==null?'':String(v)):String(v==null?'':v);}
  function current(){if(typeof drFiles==='undefined'||!Array.isArray(drFiles))return null;return drFiles.filter(function(f){return f&&!f.standalone&&!(f.registerResult&&f.registerResult.success);})[0]||null;}
  function augment(){
    var f=current();if(!f)return;
    var host=document.querySelector('[data-dr-session-active="'+String(f.id).replace(/"/g,'\\"')+'"]');if(!host)return;
    var card=host.querySelector('[data-dr-rec-section="trader"]');if(!card)return;
    var old=card.querySelector('.dr-role-extra');if(old)old.remove();
    var roles=f.traderRoles;if(!roles||!roles.success)return;
    var pref=roles.preferredIncoming||null,matches=Array.isArray(roles.matches)?roles.matches:[];
    var html='<div class="dr-role-extra">';
    if(pref){html+='<div><strong>Επιλεγμένος ρόλος:</strong> '+esc(pref.role||'—')+' · SODTYPE '+esc(pref.sodType||'—')+'</div>';}
    if(matches.length){html+='<div class="dr-role-list">'+matches.map(function(x){var sel=pref&&String(pref.trdrId)===String(x.trdrId);return '<span class="dr-role-chip '+(sel?'selected':'')+'">'+esc(x.role||'Other')+' · SODTYPE '+esc(x.sodType)+' · TRDR '+esc(x.trdrId)+'</span>';}).join('')+'</div>';}
    html+='</div>';
    card.insertAdjacentHTML('beforeend',html);
  }
  var oldRefresh=window.refreshDrRecognitionWorkspace;
  if(typeof oldRefresh==='function')window.refreshDrRecognitionWorkspace=function(){oldRefresh();setTimeout(augment,40);};
  if(window.chrome&&window.chrome.webview)window.chrome.webview.addEventListener('message',function(ev){var p=ev.data;try{if(typeof p==='string')p=JSON.parse(p);}catch(_e){return;}if(p&&p.type==='dr_trader_roles_result')setTimeout(augment,80);});
  setInterval(augment,900);
  setTimeout(augment,100);
  window.__jarvisDrTraderRoleUiInstalled=true;
})();

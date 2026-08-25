(function(){
  if(window.__jarvisDrAutoRecognitionInstalled)return;

  function files(){return (typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles.filter(function(f){return f&&!f.standalone;}):[];}
  function active(){return files().find(function(f){return !(f.registerResult&&f.registerResult.success);})||null;}
  function rerender(f){if(typeof rerenderDrEntry==='function')rerenderDrEntry(f);else if(typeof renderDrFileList==='function')renderDrFileList();if(typeof window.refreshDrRecognitionWorkspace==='function')setTimeout(window.refreshDrRecognitionWorkspace,0);if(typeof window.refreshDrPostingProposalUi==='function')setTimeout(window.refreshDrPostingProposalUi,0);}
  function fileToBase64(file){return new Promise(function(resolve,reject){var r=new FileReader();r.onload=function(){var s=String(r.result||''),p=s.indexOf(',');resolve(p>=0?s.substring(p+1):s);};r.onerror=function(){reject(r.error||new Error('file_read_failed'));};r.readAsDataURL(file);});}
  function supplierCodeOf(line){return String((line&&(line.supplier_code||line.supplierCode||line.item_code||line.itemCode||line.code))||'').trim();}
  function normalizeText(v){return String(v||'').toLocaleLowerCase('el-GR').normalize?String(v||'').toLocaleLowerCase('el-GR').normalize('NFD').replace(/[\u0300-\u036f]/g,''):String(v||'').toLocaleLowerCase('el-GR');}

  function appendChat(f,kind,text,meta){
    if(!f)return;if(!f.drAssistantMessages)f.drAssistantMessages=[];
    f.drAssistantMessages.push({kind:kind,text:text,meta:meta||''});
    var log=document.getElementById('drAssistantLog');if(!log)return;
    var current=(typeof drActiveFileId!=='undefined'&&drActiveFileId!==null)?String(drActiveFileId):String(f.id);if(current!==String(f.id))return;
    var b=document.createElement('div');b.className='dr-assistant-msg '+(kind==='user'?'user':'agent');b.textContent=text||'';
    if(meta){var m=document.createElement('div');m.className='dr-assistant-meta';m.textContent=meta;b.appendChild(m);}log.appendChild(b);log.scrollTop=log.scrollHeight;
  }

  function requestTraderRoles(f){
    if(!f||f.traderRolesRequested||f.traderRolesResolved)return;
    var det=f.detection||{},afm=det.issuerAfm||f.issuerAfm||'';
    if(!afm){f.traderRolesResolved=true;f.traderRoleAmbiguous=true;rerender(f);return;}
    f.traderRolesRequested=true;f.detail='Έλεγχος ρόλου συναλλασσόμενου στο Soft1…';
    postCommand({type:'dr_resolve_trader_roles',fileId:String(f.id),afm:afm,docType:det.docType||'',docNumber:det.docNumber||'',docDate:det.docDate||''});
    rerender(f);
  }

  async function identify(f){
    if(!f||!f.file||f.status!=='pending'||f.autoIdentifyStarted)return;
    if(typeof identifyDrFile!=='function'||typeof applyDrIssuerResult!=='function')return;
    f.autoIdentifyStarted=true;f.status='processing';f.statusText='Αναγνώριση';f.detail='Αναγνώριση header και εκδότη…';rerender(f);
    try{
      var result=await identifyDrFile(f);applyDrIssuerResult(f,result);f.autoIdentifyFinished=true;
      f.legacyTraderId=f.trdrId||null;
      f.trdrId=null;f.trader=null;f.traderName=null;f.sodType=0;f.objectName=null;
      f.notFound=false;f.ambiguous=false;f.traderRoleAmbiguous=false;
      f.seriesGuess=null;f.seriesCandidates=[];f.duplicateCheck=null;f.postingProposal=null;f.documentPattern=null;
      f.traderRolesResolved=false;f.traderRolesRequested=false;requestTraderRoles(f);
    }catch(err){f.status='error';f.statusText='Σφάλμα';f.detail=String(err&&err.message||err);}
    rerender(f);
  }

  async function extract(f){
    if(!f||!f.file||f.extraction||f.linesExtracting||f.autoExtractStarted)return;
    if(f.status!=='identified'||!f.trdrId||!f.traderRolesResolved||f.traderRoleAmbiguous)return;
    var dup=f.duplicateCheck;if(dup&&(dup.found||dup.isDuplicate)){f.autoFlowBlocked='duplicate';rerender(f);return;}
    f.autoExtractStarted=true;f.linesExtracting=true;f.linesError=null;f.detail='Πλήρης ανάγνωση παραστατικού και εξαγωγή γραμμών…';rerender(f);
    try{var base64=await fileToBase64(f.file);postCommand({type:'dr_extract_lines',fileId:String(f.id),base64:base64,mimeType:f.file.type||'application/octet-stream',trdrId:f.trdrId});}
    catch(err){f.linesExtracting=false;f.linesError=String(err&&err.message||err);rerender(f);}
  }

  function requestLineMappings(f){
    if(!f||!f.extraction||!f.trdrId||f.lineMappingsRequested||f.lineMappingsResolved)return;
    var lines=f.extraction.line_items||[];
    f.lineRecognitionStates=f.lineRecognitionStates||{};
    f.lineMappingResults=f.lineMappingResults||{};
    var request=[];
    lines.forEach(function(line,index){
      if(line&&line.matched){line.legacyMatched=line.matched;line.matched=null;}
      var code=supplierCodeOf(line);f.lineRecognitionStates[String(index)]=10;
      if(code)request.push({lineIndex:index,supplierCode:code});
    });
    if(!request.length){f.lineMappingsResolved=true;f.lineMappingsRequested=false;f.detail='Οι γραμμές αναγνωρίστηκαν, αλλά δεν βρέθηκαν supplier codes για exact mapping.';rerender(f);return;}
    f.lineMappingsRequested=true;f.detail='Έλεγχος '+request.length+' supplier codes στο CCCMAPITEMS…';
    postCommand({type:'dr_resolve_line_mappings',fileId:String(f.id),trdrId:Number(f.trdrId),lines:request});rerender(f);
  }

  function postingCoordinates(){return {series:0,sosource:0};}
  function extractionDocInfo(f){var ex=f&&f.extraction||{},d=ex.document_info||{},det=f&&f.detection||{};return {type:d.type||d.document_type||det.docType||'',series:d.series||d.document_series||det.docSeries||'',number:d.number||d.document_number||det.docNumber||''};}
  function analyzePosting(f){
    if(!f||!f.extraction||!f.trdrId||!f.traderRolesResolved||f.traderRoleAmbiguous||f.postingProposalRequested)return;
    if(f.postingProposal&&f.postingProposal.reason!=='resolving_historical_pattern')return;
    var coord=postingCoordinates(f),doc=extractionDocInfo(f),lines=f.extraction.line_items||[];
    f.postingProposalRequested=true;f.postingProposal={success:true,resolver:'resolve_document_pattern',version:4,mode:'Unknown',needsReview:true,reason:'resolving_historical_pattern',sourceLineCount:lines.length};rerender(f);
    postCommand({type:'dr_resolve_document_pattern',fileId:String(f.id),trdrId:Number(f.trdrId),series:coord.series,sosource:coord.sosource,documentType:doc.type,documentSeries:doc.series,documentNumber:doc.number,sourceLineCount:lines.length});
  }

  function selectPrecedent(f,findocId,source){
    if(!f||!f.trdrId||!findocId||f.precedentResolving)return false;
    f.precedentResolving=true;f.selectedPrecedentFindocId=Number(findocId);f.selectedPrecedentSource=source||'operator';
    f.detail='Διαβάζω την πραγματική καταχώρηση του precedent FINDOC '+findocId+'…';rerender(f);
    postCommand({type:'dr_select_precedent',fileId:String(f.id),trdrId:Number(f.trdrId),findocId:Number(findocId)});return true;
  }
  window.selectDrHistoricalPrecedent=function(findocId){var f=active();return selectPrecedent(f,findocId,'ui');};

  function buildSingleTargetProposal(f,p){
    var target=p&&p.singleTarget||null,lines=f.extraction&&f.extraction.line_items||[];
    if(!target||!target.mtrlId||[51,52,53].indexOf(Number(target.sodtype))<0)return {ok:false,reason:'invalid_single_target'};
    var conflicts=[],tokens=[],seen={};f.lineRecognitionStates=f.lineRecognitionStates||{};
    lines.forEach(function(line,index){
      var code=supplierCodeOf(line);if(!code)return;
      var existing=line&&line.matched;
      if(existing&&existing.mtrlId&&Number(existing.mtrlId)!==Number(target.mtrlId)){
        conflicts.push({lineIndex:index,supplierCode:code,existingMtrlId:Number(existing.mtrlId),targetMtrlId:Number(target.mtrlId)});f.lineRecognitionStates[String(index)]=80;return;
      }
      if(!seen[code]){seen[code]=true;tokens.push({supplierCode:code,mappingToken:String(f.trdrId)+'|'+String(code).replace(/\s+/g,'').toUpperCase(),targetMtrlId:Number(target.mtrlId)});}
      if(!(existing&&existing.mtrlId)){
        line.proposedMatch={mtrlId:Number(target.mtrlId),mtrl:Number(target.mtrlId),code:target.code||'',name:target.name||'',sodtype:Number(target.sodtype),sodtypeName:target.sodtypeName||'',matchSource:'HistoricalPrecedent',precedentFindocId:Number(p.findocId)};
        f.lineRecognitionStates[String(index)]=40;
      }
    });
    f.precedentMappingProposal={status:conflicts.length?'blocked':'proposed',operatorApprovedPrecedent:true,findocId:Number(p.findocId),fincode:p.fincode||'',target:target,sourceLineCount:lines.length,tokens:tokens,conflicts:conflicts,requiresFinalWriteConfirmation:true};
    return {ok:conflicts.length===0,reason:conflicts.length?'mapping_conflict':'single_line_precedent',tokens:tokens,conflicts:conflicts,target:target};
  }

  function candidateFromText(f,text){
    var p=f&&f.postingProposal||{},evidence=Array.isArray(p.evidence)?p.evidence:[],n=normalizeText(text);
    if(!(n.indexOf('καταχωρ')>=0||n.indexOf('χρησιμοποι')>=0||n.indexOf('προτυπ')>=0))return null;
    if(!(n.indexOf('οπως')>=0||n.indexOf('βαση')>=0||n.indexOf('προτυπ')>=0||n.indexOf('precedent')>=0||n.indexOf('confidence')>=0))return null;
    var m=n.match(/findoc\s*#?\s*(\d+)/i);if(m){var byId=evidence.find(function(x){return Number(x.findocId)===Number(m[1]);});if(byId)return byId;}
    var nums=(n.match(/\b\d+\b/g)||[]).map(Number);
    for(var i=0;i<evidence.length;i++){
      var fin=String(evidence[i].fincode||'');var digits=(fin.match(/\d+/g)||[]);for(var j=0;j<digits.length;j++){if(nums.indexOf(Number(digits[j]))>=0&&Number(digits[j])!==Math.round(Number(evidence[i].similarity||0)*100))return evidence[i];}
    }
    if(p.precedentStrong&&p.precedentFindocId)return evidence.find(function(x){return Number(x.findocId)===Number(p.precedentFindocId);})||{findocId:p.precedentFindocId,fincode:'',similarity:p.precedentSimilarity};
    return evidence.length?evidence[0]:null;
  }

  function interceptAssistantIntent(ev){
    var sendBtn=ev.target&&ev.target.closest?ev.target.closest('#drAssistantSend'):null;if(!sendBtn)return;
    var input=document.getElementById('drAssistantInput'),f=active();if(!input||!f)return;var text=(input.value||'').trim(),candidate=candidateFromText(f,text);if(!candidate||!candidate.findocId)return;
    ev.preventDefault();ev.stopImmediatePropagation();appendChat(f,'user',text,'');input.value='';
    appendChat(f,'agent','Επιλέγω ως πρότυπο το '+(candidate.fincode||('FINDOC '+candidate.findocId))+'. Ελέγχω τώρα την πραγματική Soft1 καταχώρησή του πριν προτείνω αντιστοιχίσεις.','DR · deterministic precedent');
    selectPrecedent(f,Number(candidate.findocId),'chat');
  }
  document.addEventListener('click',interceptAssistantIntent,true);
  document.addEventListener('keydown',function(ev){if(ev.key!=='Enter'||ev.shiftKey||!ev.target||ev.target.id!=='drAssistantInput')return;var f=active(),candidate=candidateFromText(f,ev.target.value||'');if(!candidate)return;ev.preventDefault();ev.stopImmediatePropagation();var text=(ev.target.value||'').trim();appendChat(f,'user',text,'');ev.target.value='';appendChat(f,'agent','Επιλέγω ως πρότυπο το '+(candidate.fincode||('FINDOC '+candidate.findocId))+'. Ελέγχω τώρα την πραγματική Soft1 καταχώρησή του πριν προτείνω αντιστοιχίσεις.','DR · deterministic precedent');selectPrecedent(f,Number(candidate.findocId),'chat');},true);

  async function tick(){var f=active();if(!f)return;if(f.status==='pending'){await identify(f);return;}if(f.status==='identified'&&!f.traderRolesResolved&&!f.traderRolesRequested){requestTraderRoles(f);return;}if(f.status==='identified'&&f.traderRolesResolved&&!f.extraction&&!f.linesExtracting){await extract(f);return;}if(f.extraction&&!f.lineMappingsResolved&&!f.lineMappingsRequested){requestLineMappings(f);return;}if(f.extraction&&!f.postingProposalRequested&&(!f.postingProposal||f.postingProposal.reason==='resolving_historical_pattern'))analyzePosting(f);}

  if(window.chrome&&window.chrome.webview){window.chrome.webview.addEventListener('message',function(ev){var p=ev.data;try{if(typeof p==='string')p=JSON.parse(p);}catch(_e){return;}if(!p)return;
    if(p.type==='dr_trader_roles_result'){
      var f=files().find(function(x){return String(x.id)===String(p.fileId);});if(!f)return;f.traderRolesRequested=false;f.traderRoles=p;f.traderRolesResolved=!!p.success;var pref=p.preferredIncoming||null;
      if(pref&&pref.trdrId){f.trdrId=Number(pref.trdrId);f.trader=pref;f.traderName=pref.name||'';f.sodType=Number(pref.sodType||0);f.objectName=f.sodType===12?'SUPPLIER':f.sodType===16?'CREDITOR':null;f.notFound=false;f.traderRoleAmbiguous=!!p.incomingAmbiguous;f.seriesGuess=p.seriesHistory&&p.seriesHistory.bestGuess||null;f.seriesCandidates=p.seriesHistory&&p.seriesHistory.candidates||[];f.duplicateCheck=p.duplicateCheck||null;f.detail='Βρέθηκε '+(pref.role||'συναλλασσόμενος')+': '+(pref.name||('TRDR '+pref.trdrId));}
      else{f.traderRoleAmbiguous=true;f.seriesGuess=null;f.seriesCandidates=[];f.duplicateCheck=null;f.detail=p.found?'Το ΑΦΜ υπάρχει στο Soft1, αλλά όχι σε επιβεβαιωμένο incoming ρόλο (Supplier/Creditor).':'Δεν βρέθηκε συναλλασσόμενος για το ΑΦΜ.';}rerender(f);return;
    }
    if(p.type==='dr_line_mappings_result'){
      var fm=files().find(function(x){return String(x.id)===String(p.fileId);});if(!fm)return;fm.lineMappingsRequested=false;fm.lineMappingsResolved=!!p.success;fm.lineMappingError=p.success?null:(p.errorMessage||'Αποτυχία CCCMAPITEMS lookup.');fm.lineRecognitionStates=fm.lineRecognitionStates||{};fm.lineMappingResults=fm.lineMappingResults||{};var lines=fm.extraction&&fm.extraction.line_items||[];
      (p.results||[]).forEach(function(r){var index=Number(r.lineIndex);if(index<0||index>=lines.length)return;fm.lineMappingResults[String(index)]=r;if(r.found&&!r.ambiguous&&r.mtrlId){fm.lineRecognitionStates[String(index)]=30;lines[index].matched={mtrlId:Number(r.mtrlId),mtrl:Number(r.mtrlId),code:r.mtrlCode||'',name:r.mtrlName||'',sodtype:Number(r.sodtype||0),sodtypeName:r.sodtypeName||'',matchSource:'CCCMAPITEMS',mappingToken:r.mappingToken||''};}else if(r.ambiguous){fm.lineRecognitionStates[String(index)]=70;lines[index].mappingAmbiguous=true;lines[index].mappingCandidates=r.matches||[];}else{fm.lineRecognitionStates[String(index)]=10;lines[index].mappingNotFound=true;}});
      var mapped=Object.keys(fm.lineRecognitionStates).filter(function(k){return Number(fm.lineRecognitionStates[k])===30;}).length,ambiguous=Object.keys(fm.lineRecognitionStates).filter(function(k){return Number(fm.lineRecognitionStates[k])===70;}).length;fm.detail='CCCMAPITEMS: '+mapped+' exact mappings'+(ambiguous?' · '+ambiguous+' αμφίσημα':'')+'.';rerender(fm);return;
    }
    if(p.type==='dr_document_pattern_result'||p.type==='dr_posting_proposal_result'){
      var fp=files().find(function(x){return String(x.id)===String(p.fileId);});if(!fp)return;fp.postingProposalRequested=false;fp.postingProposal=p;fp.documentPattern=p;rerender(fp);return;
    }
    if(p.type==='dr_precedent_result'){
      var fr=files().find(function(x){return String(x.id)===String(p.fileId);});if(!fr)return;fr.precedentResolving=false;fr.precedentSelection=p;
      if(!p.success){fr.detail='Αποτυχία ανάγνωσης precedent: '+(p.errorMessage||p.reason||'άγνωστο σφάλμα');appendChat(fr,'agent','Δεν μπόρεσα να διαβάσω με ασφάλεια το επιλεγμένο precedent. '+(p.errorMessage||p.reason||''),'DR');rerender(fr);return;}
      if(Number(p.postedLineCount)!==1||!p.canProposeSingleTarget){fr.precedentMappingProposal={status:'manual_required',findocId:Number(p.findocId),postedLineCount:Number(p.postedLineCount||0)};fr.detail='Το precedent έχει '+p.postedLineCount+' γραμμές. Απαιτείται χειροκίνητη αντιστοίχιση ανά γραμμή.';appendChat(fr,'agent','Το '+(p.fincode||('FINDOC '+p.findocId))+' έχει '+p.postedLineCount+' πραγματικές Soft1 γραμμές. Δεν θα κάνω αυτόματη αντιστοίχιση. Χρειάζεται Αντιστοίχιση ή Δημιουργία ανά unresolved γραμμή.','DR · safety rule');rerender(fr);return;}
      var proposal=buildSingleTargetProposal(fr,p),t=p.singleTarget||{};
      if(!proposal.ok){fr.detail='Το single-line precedent βρέθηκε, αλλά υπάρχει blocker στην αυτόματη πρόταση.';appendChat(fr,'agent','Βρήκα τη μοναδική γραμμή του precedent, αλλά υπάρχει σύγκρουση με ήδη επιβεβαιωμένο mapping. Δεν θα το παρακάμψω.','DR · blocker');rerender(fr);return;}
      fr.detail='Precedent '+(p.fincode||p.findocId)+': όλες οι unresolved source lines προτείνονται στο MTRL '+t.mtrlId+'.';
      appendChat(fr,'agent','Το '+(p.fincode||('FINDOC '+p.findocId))+' έχει ακριβώς 1 Soft1 γραμμή: ['+(t.code||'')+'] '+(t.name||'MTRL '+t.mtrlId)+' · '+(t.sodtypeName||'')+' (SODTYPE '+t.sodtype+'). Προτείνω όλες τις unresolved γραμμές του PDF σε αυτό το MTRL. Learning proposal: '+proposal.tokens.map(function(x){return x.mappingToken;}).join('; ')+'. Δεν έχει γίνει ακόμη write στο CCCMAPITEMS ή καταχώρηση παραστατικού.','DR · precedent proposal');rerender(fr);return;
    }
  });}

  setInterval(function(){tick().catch(function(){});},650);setTimeout(function(){tick().catch(function(){});},200);window.__jarvisDrAutoRecognitionInstalled=true;
})();

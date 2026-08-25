(function(){
  if(window.__jarvisDrAutoRecognitionInstalled)return;

  function files(){return (typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles.filter(function(f){return f&&!f.standalone;}):[];}
  function active(){return files().find(function(f){return !(f.registerResult&&f.registerResult.success);})||null;}
  function rerender(f){if(typeof rerenderDrEntry==='function')rerenderDrEntry(f);else if(typeof renderDrFileList==='function')renderDrFileList();if(typeof window.refreshDrRecognitionWorkspace==='function')setTimeout(window.refreshDrRecognitionWorkspace,0);}
  function fileToBase64(file){return new Promise(function(resolve,reject){var r=new FileReader();r.onload=function(){var s=String(r.result||''),p=s.indexOf(',');resolve(p>=0?s.substring(p+1):s);};r.onerror=function(){reject(r.error||new Error('file_read_failed'));};r.readAsDataURL(file);});}
  function supplierCodeOf(line){return String((line&&(line.supplier_code||line.supplierCode||line.item_code||line.itemCode||line.code))||'').trim();}

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
      // Legacy AFM lookup is not authoritative because one AFM may exist in
      // multiple TRDR roles. Clear every legacy trader/posting decision and
      // wait for the deterministic multi-role resolver before proceeding.
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
      // The old extraction path may have populated matched from MTRSUPCODE.
      // CCCMAPITEMS is now the authoritative supplier-code mapping layer.
      if(line&&line.matched){line.legacyMatched=line.matched;line.matched=null;}
      var code=supplierCodeOf(line);
      f.lineRecognitionStates[String(index)]=10; // Extracted
      if(code)request.push({lineIndex:index,supplierCode:code});
    });

    if(!request.length){
      f.lineMappingsResolved=true;
      f.lineMappingsRequested=false;
      f.detail='Οι γραμμές αναγνωρίστηκαν, αλλά δεν βρέθηκαν supplier codes για exact mapping.';
      rerender(f);return;
    }

    f.lineMappingsRequested=true;
    f.detail='Έλεγχος '+request.length+' supplier codes στο CCCMAPITEMS…';
    postCommand({type:'dr_resolve_line_mappings',fileId:String(f.id),trdrId:Number(f.trdrId),lines:request});
    rerender(f);
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

  async function tick(){
    var f=active();if(!f)return;
    if(f.status==='pending'){await identify(f);return;}
    if(f.status==='identified'&&!f.traderRolesResolved&&!f.traderRolesRequested){requestTraderRoles(f);return;}
    if(f.status==='identified'&&f.traderRolesResolved&&!f.extraction&&!f.linesExtracting){await extract(f);return;}
    if(f.extraction&&!f.lineMappingsResolved&&!f.lineMappingsRequested){requestLineMappings(f);return;}
    if(f.extraction&&!f.postingProposalRequested&&(!f.postingProposal||f.postingProposal.reason==='resolving_historical_pattern'))analyzePosting(f);
  }

  if(window.chrome&&window.chrome.webview){window.chrome.webview.addEventListener('message',function(ev){var p=ev.data;try{if(typeof p==='string')p=JSON.parse(p);}catch(_e){return;}if(!p)return;
    if(p.type==='dr_trader_roles_result'){
      var f=files().find(function(x){return String(x.id)===String(p.fileId);});if(!f)return;
      f.traderRolesRequested=false;f.traderRoles=p;f.traderRolesResolved=!!p.success;var pref=p.preferredIncoming||null;
      if(pref&&pref.trdrId){
        f.trdrId=Number(pref.trdrId);f.trader=pref;f.traderName=pref.name||'';f.sodType=Number(pref.sodType||0);f.objectName=f.sodType===12?'SUPPLIER':f.sodType===16?'CREDITOR':null;f.notFound=false;f.traderRoleAmbiguous=!!p.incomingAmbiguous;
        f.seriesGuess=p.seriesHistory&&p.seriesHistory.bestGuess||null;f.seriesCandidates=p.seriesHistory&&p.seriesHistory.candidates||[];f.duplicateCheck=p.duplicateCheck||null;f.detail='Βρέθηκε '+(pref.role||'συναλλασσόμενος')+': '+(pref.name||('TRDR '+pref.trdrId));
      }else{f.traderRoleAmbiguous=true;f.seriesGuess=null;f.seriesCandidates=[];f.duplicateCheck=null;f.detail=p.found?'Το ΑΦΜ υπάρχει στο Soft1, αλλά όχι σε επιβεβαιωμένο incoming ρόλο (Supplier/Creditor).':'Δεν βρέθηκε συναλλασσόμενος για το ΑΦΜ.';}
      rerender(f);return;
    }
    if(p.type==='dr_line_mappings_result'){
      var fm=files().find(function(x){return String(x.id)===String(p.fileId);});if(!fm)return;
      fm.lineMappingsRequested=false;fm.lineMappingsResolved=!!p.success;fm.lineMappingError=p.success?null:(p.errorMessage||'Αποτυχία CCCMAPITEMS lookup.');
      fm.lineRecognitionStates=fm.lineRecognitionStates||{};fm.lineMappingResults=fm.lineMappingResults||{};
      var lines=fm.extraction&&fm.extraction.line_items||[];
      (p.results||[]).forEach(function(r){
        var index=Number(r.lineIndex);if(index<0||index>=lines.length)return;
        fm.lineMappingResults[String(index)]=r;
        if(r.found&&!r.ambiguous&&r.mtrlId){
          fm.lineRecognitionStates[String(index)]=30;
          lines[index].matched={
            mtrlId:Number(r.mtrlId),mtrl:Number(r.mtrlId),code:r.mtrlCode||'',name:r.mtrlName||'',
            sodtype:Number(r.sodtype||0),sodtypeName:r.sodtypeName||'',matchSource:'CCCMAPITEMS',mappingToken:r.mappingToken||''
          };
        }else if(r.ambiguous){
          fm.lineRecognitionStates[String(index)]=70;
          lines[index].mappingAmbiguous=true;lines[index].mappingCandidates=r.matches||[];
        }else{
          fm.lineRecognitionStates[String(index)]=10;
          lines[index].mappingNotFound=true;
        }
      });
      var mapped=Object.keys(fm.lineRecognitionStates).filter(function(k){return Number(fm.lineRecognitionStates[k])===30;}).length;
      var ambiguous=Object.keys(fm.lineRecognitionStates).filter(function(k){return Number(fm.lineRecognitionStates[k])===70;}).length;
      fm.detail='CCCMAPITEMS: '+mapped+' exact mappings'+(ambiguous?' · '+ambiguous+' αμφίσημα':'')+'.';
      rerender(fm);return;
    }
    if(p.type==='dr_document_pattern_result'||p.type==='dr_posting_proposal_result'){
      var fp=files().find(function(x){return String(x.id)===String(p.fileId);});if(!fp)return;fp.postingProposalRequested=false;fp.postingProposal=p;fp.documentPattern=p;rerender(fp);if(typeof window.refreshDrPostingProposalUi==='function')setTimeout(window.refreshDrPostingProposalUi,0);return;
    }
  });}

  setInterval(function(){tick().catch(function(){});},650);setTimeout(function(){tick().catch(function(){});},200);window.__jarvisDrAutoRecognitionInstalled=true;
})();

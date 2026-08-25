(function(){
  if(window.__jarvisDrAutoRecognitionInstalled)return;

  function files(){return (typeof drFiles!=='undefined'&&Array.isArray(drFiles))?drFiles.filter(function(f){return f&&!f.standalone;}):[];}
  function active(){return files().find(function(f){return !(f.registerResult&&f.registerResult.success);})||null;}
  function rerender(f){if(typeof rerenderDrEntry==='function')rerenderDrEntry(f);else if(typeof renderDrFileList==='function')renderDrFileList();}
  function fileToBase64(file){return new Promise(function(resolve,reject){var r=new FileReader();r.onload=function(){var s=String(r.result||''),p=s.indexOf(',');resolve(p>=0?s.substring(p+1):s);};r.onerror=function(){reject(r.error||new Error('file_read_failed'));};r.readAsDataURL(file);});}

  async function identify(f){
    if(!f||!f.file||f.status!=='pending'||f.autoIdentifyStarted)return;
    if(typeof identifyDrFile!=='function'||typeof applyDrIssuerResult!=='function')return;
    f.autoIdentifyStarted=true;f.status='processing';f.statusText='Αναγνώριση';f.detail='Αναγνώριση header και συναλλασσόμενου…';rerender(f);
    try{
      var result=await identifyDrFile(f);
      applyDrIssuerResult(f,result);
      f.autoIdentifyFinished=true;
    }catch(err){f.status='error';f.statusText='Σφάλμα';f.detail=String(err&&err.message||err);}
    rerender(f);
  }

  async function extract(f){
    if(!f||!f.file||f.extraction||f.linesExtracting||f.autoExtractStarted)return;
    if(f.status!=='identified'||!f.trdrId)return;
    var dup=f.duplicateCheck;if(dup&&(dup.found||dup.isDuplicate)){f.autoFlowBlocked='duplicate';rerender(f);return;}
    f.autoExtractStarted=true;f.linesExtracting=true;f.linesError=null;f.detail='Εξαγωγή γραμμών παραστατικού…';rerender(f);
    try{
      var base64=await fileToBase64(f.file);
      postCommand({type:'dr_extract_lines',fileId:String(f.id),base64:base64,mimeType:f.file.type||'application/octet-stream',trdrId:f.trdrId});
    }catch(err){f.linesExtracting=false;f.linesError=String(err&&err.message||err);rerender(f);}
  }

  function postingCoordinates(f){
    if(f.seriesGuess&&f.seriesGuess.series&&f.seriesGuess.sosource)return {series:Number(f.seriesGuess.series),sosource:Number(f.seriesGuess.sosource)};
    var c=f.seriesCandidates||[];
    if(c.length===1&&c[0].series&&c[0].sosource)return {series:Number(c[0].series),sosource:Number(c[0].sosource)};
    return null;
  }

  function analyzePosting(f){
    if(!f||!f.extraction||!f.trdrId||f.postingProposal||f.postingProposalRequested)return;
    var coord=postingCoordinates(f);
    if(!coord){
      f.postingProposal={success:true,mode:'Unknown',needsReview:true,reason:'series_selection_required',sourceLineCount:(f.extraction.line_items||[]).length};
      rerender(f);return;
    }
    f.postingProposalRequested=true;
    postCommand({type:'dr_analyze_posting',fileId:String(f.id),trdrId:Number(f.trdrId),series:coord.series,sosource:coord.sosource,sourceLineCount:(f.extraction.line_items||[]).length});
  }

  async function tick(){
    var f=active();if(!f)return;
    if(f.status==='pending'){await identify(f);return;}
    if(f.status==='identified'&&!f.extraction&&!f.linesExtracting){await extract(f);return;}
    if(f.extraction&&!f.postingProposal)analyzePosting(f);
  }

  if(window.chrome&&window.chrome.webview){
    window.chrome.webview.addEventListener('message',function(ev){
      var p=ev.data;try{if(typeof p==='string')p=JSON.parse(p);}catch(_e){return;}
      if(!p||p.type!=='dr_posting_proposal_result')return;
      var f=files().find(function(x){return String(x.id)===String(p.fileId);});if(!f)return;
      f.postingProposalRequested=false;f.postingProposal=p;rerender(f);
      if(typeof window.refreshDrRecognitionWorkspace==='function')setTimeout(window.refreshDrRecognitionWorkspace,0);
    });
  }

  setInterval(function(){tick().catch(function(){});},450);
  setTimeout(function(){tick().catch(function(){});},150);
  window.__jarvisDrAutoRecognitionInstalled=true;
})();

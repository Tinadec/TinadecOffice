'use strict';
const electron=require('electron'),{app,BrowserWindow}=electron;
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),Module=require('node:module');
const repo=path.resolve(__dirname,'../../..'),desktop=path.join(repo,'apps/desktop/electron'),owned=process.env.TRANSCRIPT_REAL_ROOT;
const gateway=process.env.TRANSCRIPT_GATEWAY_URL,core=process.env.TRANSCRIPT_CORE_URL;
const services=require(path.join(desktop,'serviceManager.cjs')),identity=require(path.join(desktop,'hostIdentity.cjs')),hostConnection=require(path.join(desktop,'hostConnection.cjs')),load=Module._load,realFetch=global.fetch;
let window;const requests=[],streamHeaders=[],events=[],rounds=[],consoleErrors=[],proofRequests=[],hostStates=[];
// Isolated timing adapter only: authentic proofs and production authorization stay in use.
const hostProofBudgets={endpoint_ms:20000,total_ms:30000,production_endpoint_ms:2000,production_total_ms:10000};
function progress(stage){fs.appendFileSync(path.join(owned,'electron-progress.jsonl'),JSON.stringify({stage,at:new Date().toISOString()})+'\n');}
function FixtureWindow(options){
 window=new BrowserWindow({...options,width:1440,height:900,show:false,webPreferences:{...options.webPreferences,offscreen:true,backgroundThrottling:false}});
 window.show=()=>{};window.webContents.openDevTools=()=>{};
 window.webContents.on('did-start-loading',()=>progress('document_loading'));
 window.webContents.on('did-finish-load',()=>progress('document_loaded'));
 window.webContents.on('did-fail-load',(_e,code,message)=>progress({failed:code,message}));
 window.webContents.on('console-message',(_event,level,message)=>{if(level>=2)consoleErrors.push(message);});
 const wr=window.webContents.session.webRequest,before=wr.onBeforeSendHeaders.bind(wr);
 wr.onBeforeSendHeaders=(filter,callback)=>before(filter,(details,done)=>{
  const url=new URL(details.url);let mapped=details.url;
  if(url.origin===gateway)mapped='http://127.0.0.1:48730'+url.pathname+url.search;
  else if(url.origin===core)mapped='http://127.0.0.1:48731'+url.pathname+url.search;
  else if(url.port==='48730'||url.port==='48731')mapped='http://unowned.invalid'+url.pathname;
  callback({...details,url:mapped},done);
 });
 wr.onCompleted({urls:[gateway+'/*']},d=>requests.push({method:d.method,path:new URL(d.url).pathname+new URL(d.url).search,status:d.statusCode}));
 wr.onHeadersReceived({urls:[gateway+'/*']},(d,done)=>{const type=Object.entries(d.responseHeaders??{}).find(([k])=>k.toLowerCase()==='content-type')?.[1]?.[0]??'';if(type.includes('text/event-stream'))streamHeaders.push({method:d.method,path:new URL(d.url).pathname+new URL(d.url).search,status:d.statusCode,contentType:type});done({});});
 return window;
}
FixtureWindow.getAllWindows=BrowserWindow.getAllWindows;FixtureWindow.fromId=BrowserWindow.fromId;FixtureWindow.fromWebContents=BrowserWindow.fromWebContents;
Module._load=function(request,parent,isMain){
 if(parent?.filename===path.join(desktop,'main.cjs')){
  if(request==='electron')return {...electron,BrowserWindow:FixtureWindow};
  if(request==='./serviceManager.cjs')return {...services,canonicalLocalGatewayUrl:url=>url===gateway?gateway:null,ensureLocalServices:async()=>({started:false}),stopLocalServices:async()=>{}};
  if(request==='./hostConnection.cjs')return {...hostConnection,createHostConnection:options=>hostConnection.createHostConnection({...options,verificationTimeoutMs:hostProofBudgets.total_ms,onStatusChange:status=>{hostStates.push({...status,observed_at:new Date().toISOString()});options.onStatusChange?.(status);}})};
  if(request==='./hostIdentity.cjs')return {...identity,verifyManagedHost:async(key,options)=>{
   const started=Date.now();
   try{return await identity.verifyManagedHost(key,{...options,timeoutMs:hostProofBudgets.endpoint_ms,fetchImpl:async(url,init)=>{
    const u=new URL(url),role=u.port==='48731'?'core':'gateway',observation={role,started_at:new Date().toISOString()};proofRequests.push(observation);const began=Date.now();
    try{const response=await realFetch((role==='core'?core:gateway)+u.pathname+u.search,init);observation.http_status=response.status;return response;}
    catch(error){observation.error={name:error?.name,code:error?.code,signal_aborted:init?.signal?.aborted===true};throw error;}
    finally{observation.elapsed_ms=Date.now()-began;}
   }});}catch(error){proofRequests.push({kind:'managed_verification_failure',elapsed_ms:Date.now()-started,error:{name:error?.name,code:error?.code,signal_aborted:options?.signal?.aborted===true},observed_at:new Date().toISOString()});throw error;}
  }};
 }
 return load.call(this,request,parent,isMain);
};
async function js(expression){return window.webContents.executeJavaScript(expression);}
async function wait(expression,ms=360000){const deadline=Date.now()+ms;while(Date.now()<deadline){if(window&&!window.isDestroyed()&&await js(expression))return;await new Promise(r=>setTimeout(r,100));}throw new Error('UI timeout: '+expression);}
async function capture(name){await js('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');fs.writeFileSync(path.join(__dirname,name+'.png'),(await window.webContents.capturePage()).toPNG());}
async function dispatch(command){return js('(()=>{const u=window.__transcriptReal.uie;return u.dispatch({command:{...'+JSON.stringify(command)+',scope:u.scope.value},source:"user",expectedRevision:u.snapshot.value.revision});})()');}
async function checkTerminal(id,step,{theme,zoomFactor}={}){
 const encoded=JSON.stringify(id),marker='TRANSCRIPT_'+step.toUpperCase();
 await wait('(()=>{const t=window.__transcriptReal.terminal.getTerminal('+encoded+'),h=t?.term?.element?.parentElement;return t?.ready&&!t.exited&&h?.isConnected&&h.clientWidth>0&&h.clientHeight>0;})()');
 await js('(async()=>{await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));window.__transcriptReal.terminal.fitAllTerminals();await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));})()');
 await js('window.tinadec.terminal.write('+encoded+','+JSON.stringify('echo '+marker+'\r')+')');
 await wait('(()=>{const b=window.__transcriptReal.terminal.getTerminal('+encoded+')?.term?.buffer.active;return b&&Array.from({length:b.length},(_,i)=>b.getLine(i)?.translateToString(true).trim()).some(line=>line==='+JSON.stringify(marker)+');})()',60000);
 const state=await js('(async()=>{const t=window.__transcriptReal.terminal.getTerminal('+encoded+'),term=t.term,h=term.element.parentElement,root=document.documentElement,css=getComputedStyle(root),pty=await window.tinadec.terminal.snapshot('+encoded+'),listed=(await window.tinadec.terminal.list()).find(p=>p.id==='+encoded+');return{id:t.id,sourceKind:t.sourceKind,ready:t.ready,exited:t.exited,cols:term.cols,rows:term.rows,host:{connected:h.isConnected,width:h.clientWidth,height:h.clientHeight},theme:{mode:root.getAttribute("data-theme"),background:term.options.theme?.background,foreground:term.options.theme?.foreground,black:term.options.theme?.black},css:{background:css.getPropertyValue("--bg-primary").trim(),foreground:css.getPropertyValue("--text-primary").trim()},ptyExists:pty!==null,ptyExited:pty?.exited,ptyListId:listed?.id,ptyListExited:listed?.exited};})()');
 assert.equal(state.id,id);assert.equal(state.sourceKind,'local');assert.equal(state.ready,true);assert.equal(state.exited,false);
 assert.equal(state.host.connected,true);assert.ok(state.host.width>0&&state.host.height>0);assert.ok(Number.isInteger(state.cols)&&state.cols>2);assert.ok(Number.isInteger(state.rows)&&state.rows>1);
 assert.equal(state.ptyExists,true);assert.equal(state.ptyExited,false);assert.equal(state.ptyListId,id);assert.equal(state.ptyListExited,false);
 if(theme)assert.equal(state.theme.mode,theme);
 const dark=state.theme.mode==='dark';assert.equal(state.theme.background,state.css.background||(dark?'#0a0e14':'#ffffff'));assert.equal(state.theme.foreground,state.css.foreground||(dark?'#c9d1d9':'#1f2328'));assert.equal(state.theme.black,dark?'#484f58':'#1f2328');
 state.zoomFactor=window.webContents.getZoomFactor();if(zoomFactor!==undefined)assert.ok(Math.abs(state.zoomFactor-zoomFactor)<1e-6);
 events.push({step:'terminal_state_'+step,output_marker:marker,exact_output_line:true,...state});return state;
}
async function safeState(){return js('(()=>{const f=window.__transcriptReal;if(!f)return{earlyErrors:window.__transcriptEarlyErrors};const c=f.c;return{ready:f.ready,errors:f.errors,fallback:document.querySelector("#renderer-error-fallback")!==null,hostState:f.hostAccess.status.value?.state,hostStatus:f.hostAccess.status.value,businessReady:f.connection.businessReady.value,round:f.round,sessionId:c.currentSession.value?.id,messages:c.messages.value.map(m=>({id:m.id,role:m.role,textLength:m.content?.length,runId:m.run_id})),runs:c.runs.value.map(r=>({id:r.id,status:r.status})),working:c.working.value,busy:c.busy.value,messagesLoading:c.messagesLoading.value,invokeError:c.invokeError.value,streamingReplyLength:c.streamingReply.value.length,streamUpdates:f.streamUpdates,eventTypes:f.eventTypes,eventStreams:f.eventStreams,eventFetches:f.eventFetches,activityCount:Object.keys(c.agentTurnActivities.value).length};})()');}
async function sendRound(content,index){
 const preflight=await js('window.tinadec.retryHostConnection()');assert.equal(preflight.state,'ready','Fresh host proof refused round '+index);assert.ok(preflight.checked_at);
 await wait('window.__transcriptReal.connection.businessReady.value && window.__transcriptReal.hostAccess.status.value?.state==="ready" && !window.__transcriptReal.c.busy.value && !window.__transcriptReal.c.messagesLoading.value');
 const begin=Date.now();
 await js('(()=>{const f=window.__transcriptReal,started=performance.now();f.round={index:'+index+',started,errorCount:f.errors.length,updateStart:f.streamUpdates.length,optimisticVisibleMs:null,handleSettledMs:null};const round=f.round;const frame=()=>{if(f.round!==round)return;const visible=Array.from(document.querySelectorAll(".message-wrapper.user .message-content")).some(el=>el.textContent.includes('+JSON.stringify(content)+'));if(visible)round.optimisticVisibleMs=performance.now()-started;else requestAnimationFrame(frame);};requestAnimationFrame(frame);void f.c.handleWelcomeSend({content:'+JSON.stringify(content)+'}).then(()=>{round.handleSettledMs=performance.now()-started;},error=>{round.error=String(error);});})()');
 await wait('window.__transcriptReal.round.optimisticVisibleMs!==null',300000);
 await wait('window.__transcriptReal.c.invokeError.value!==null || window.__transcriptReal.round.error!==undefined || window.__transcriptReal.c.runs.value.some(r=>["failed","cancelled"].includes(r.status)) || (window.__transcriptReal.c.messages.value.some(m=>m.role==="assistant") && !window.__transcriptReal.c.working.value && !window.__transcriptReal.c.messagesLoading.value && window.__transcriptReal.c.runs.value.some(r=>r.status==="completed"))',300000);
 const state=await safeState();assert.deepEqual(state.errors,[]);assert.equal(state.fallback,false);assert.equal(state.invokeError,null);assert.ok(state.runs.some(r=>r.status==='completed'));assert.equal(state.messages.filter(m=>m.role==='user').length,1);assert.equal(state.messages.filter(m=>m.role==='assistant').length,1);assert.ok(state.messages.some(m=>m.role==='tool_evidence'),'Completed worker evidence was not retained');assert.ok(state.messages.every(m=>['user','assistant','tool_evidence'].includes(m.role)));assert.equal(state.round.error,undefined);assert.ok(state.streamUpdates.length>state.round.updateStart,'No live production reply update in round '+index);
 const round={index,content,elapsedMs:Date.now()-begin,optimisticVisibleMs:state.round.optimisticVisibleMs,handleSettledMs:state.round.handleSettledMs,freshHostCheckedAt:preflight.checked_at,sessionId:state.sessionId,runId:state.runs.find(r=>r.status==='completed').id,messageIds:state.messages.map(m=>m.id),messageRoles:state.messages.map(m=>m.role),liveReplyUpdates:state.streamUpdates.length-state.round.updateStart,errorCount:state.errors.length};rounds.push(round);progress({round:index,elapsed_ms:round.elapsedMs,optimistic_visible_ms:round.optimisticVisibleMs});return round;
}
const timeout=setTimeout(()=>{app.exit(1);},5400000);
async function run(){try{
 progress('await_renderer_ready');
 await wait('window.__transcriptReal?.ready===true && !window.__transcriptReal.c.busy.value && document.querySelector(".conversation")!==null');
 progress('renderer_ready');
 assert.equal((await js('window.tinadec.getHostStatus()')).state,'ready');
 const terminalId=await js('(async()=>{const f=window.__transcriptReal;await f.terminal.loadShells();const t=await f.terminal.createTerminal({cwd:f.config.workspace,title:"isolated real terminal"});if(!t)throw new Error(JSON.stringify(f.terminal.creationError.value));return t.id;})()');
 let terminalCard=await js('Object.values(window.__transcriptReal.uie.snapshot.value.cards).find(c=>c.descriptorId==="terminal").id');
 await dispatch({type:'activateCard',instanceId:terminalCard});
 await wait('document.querySelector(".terminal-view .xterm")!==null');
 await checkTerminal(terminalId,'real_pty',{zoomFactor:1});
 events.push({step:'actual_main_preload_pty',terminal_created:true});
 progress('actual_pty_ready');
 await dispatch({type:'closeCard',instanceId:terminalCard});
 await dispatch({type:'openCard',descriptorId:'terminal',slotId:'right'});
 terminalCard=await js('Object.values(window.__transcriptReal.uie.snapshot.value.cards).find(c=>c.descriptorId==="terminal").id');
 await wait('document.querySelector(".terminal-view .xterm")!==null && window.__transcriptReal.terminal.getTerminal('+JSON.stringify(terminalId)+').ready');
 await checkTerminal(terminalId,'card_restore',{zoomFactor:1});
 events.push({step:'terminal_card_restore_before_first_send',same_pty:true});
 const firstRound=await sendRound('你好',1);
 const first=await js('({messages:window.__transcriptReal.c.messages.value.map(m=>({id:m.id,role:m.role,content:m.content,run_id:m.run_id})),runs:window.__transcriptReal.c.runs.value.map(r=>({id:r.id,status:r.status})),streamDeltas:window.__transcriptReal.streamDeltas,streamUpdates:window.__transcriptReal.streamUpdates,eventTypes:window.__transcriptReal.eventTypes,eventStreams:window.__transcriptReal.eventStreams,eventFetches:window.__transcriptReal.eventFetches})');
 assert.equal(first.messages[0].content,'你好');assert.ok(first.messages.some(m=>m.role==='assistant'&&m.content.includes('TRANSCRIPT_CONTROLLED_OK')));assert.ok(first.runs.some(r=>r.status==='completed'));
 assert.ok(first.streamUpdates.length>0,'Production run SSE did not project any live reply');
 assert.ok(first.eventTypes['stream.ready']>0,'Production session SSE never delivered stream.ready: '+JSON.stringify({eventStreams:first.eventStreams,eventFetches:first.eventFetches}));
 assert.ok(first.eventTypes['user.response']>0,'Production session SSE did not project the completed reply lifecycle');
 events.push({step:'production_homecontroller_first_send',...firstRound,...first});
 progress('first_reply_complete');
 await capture('real-first-answer-terminal');
 const sessionId=firstRound.sessionId,pagePath='/api/v1/sessions/'+sessionId+'/messages/page';
 const reconnectBefore=await js('(()=>{const f=window.__transcriptReal,streamIndex=f.eventStreams.findLastIndex(s=>s.wireSessionId==='+JSON.stringify(sessionId)+'&&!s.closed),s=f.eventStreams[streamIndex];return{streamIndex,fetches:f.eventFetches.length,ready:s?.rawEventTypes["stream.ready"]??0,errors:s?.errors??0};})()');
 assert.ok(reconnectBefore.streamIndex>=0);assert.ok(reconnectBefore.ready>0);
 const pagesBefore=requests.filter(r=>r.path.startsWith(pagePath)&&r.status===200).length;
 const interactionsBefore=requests.filter(r=>r.method==='POST'&&r.path.endsWith('/interactions')).length;
 assert.equal(await js('window.__transcriptReal.cutLatestSessionStream('+JSON.stringify(sessionId)+')'),true);
 await wait('(()=>{const f=window.__transcriptReal,s=f.eventStreams['+reconnectBefore.streamIndex+'];return s&&!s.closed&&f.eventFetches.slice('+reconnectBefore.fetches+').some(r=>r.status===200&&new URL(r.path,location.href).searchParams.get("session_id")==='+JSON.stringify(sessionId)+')&&(s.rawEventTypes["stream.ready"]??0)>'+reconnectBefore.ready+'&&s.errors>'+reconnectBefore.errors+'&&!f.c.messagesLoading.value&&JSON.stringify(f.c.messages.value.map(m=>m.id))==='+JSON.stringify(JSON.stringify(firstRound.messageIds))+';})()',120000);
 // ready arrives before the coalesced snapshot read marks messagesLoading.
 const pageDeadline=Date.now()+120000;while(Date.now()<pageDeadline&&requests.filter(r=>r.path.startsWith(pagePath)&&r.status===200).length<=pagesBefore)await new Promise(r=>setTimeout(r,100));
 await wait('!window.__transcriptReal.c.messagesLoading.value&&JSON.stringify(window.__transcriptReal.c.messages.value.map(m=>m.id))==='+JSON.stringify(JSON.stringify(firstRound.messageIds)),120000);
 const reconnected=await safeState();assert.deepEqual(reconnected.errors,[]);assert.equal(reconnected.fallback,false);assert.deepEqual(reconnected.messages.map(m=>m.id),firstRound.messageIds);assert.equal(new Set(reconnected.messages.map(m=>m.id)).size,firstRound.messageIds.length);assert.ok(reconnected.runs.some(r=>r.id===firstRound.runId&&r.status==='completed'));
 const pagesAfter=requests.filter(r=>r.path.startsWith(pagePath)&&r.status===200).length;assert.ok(pagesAfter>pagesBefore,'Session reconnect did not refresh the real message page');
 const interactionsAfter=requests.filter(r=>r.method==='POST'&&r.path.endsWith('/interactions')).length;assert.equal(interactionsAfter,interactionsBefore,'Session reconnect replayed an interaction');
 events.push({step:'real_session_http_read_disconnect_reconnect',sessionId,write_replayed:false,before:reconnectBefore,pages_before:pagesBefore,pages_after:pagesAfter,interactions_before:interactionsBefore,interactions_after:interactionsAfter,exact_message_ids:firstRound.messageIds,eventStreams:reconnected.eventStreams,eventFetches:reconnected.eventFetches});
 await dispatch({type:'splitStack',slotId:'right',instanceId:terminalCard,ratio:0.55});
 await wait('window.__transcriptReal.uie.snapshot.value.columns.right.secondary!==null');
 await checkTerminal(terminalId,'split',{zoomFactor:1});
 await dispatch({type:'mergeStack',slotId:'right'});
 await wait('window.__transcriptReal.uie.snapshot.value.columns.right.secondary===null');
 await checkTerminal(terminalId,'merge',{zoomFactor:1});
 const picker=await js('Object.values(window.__transcriptReal.uie.snapshot.value.cards).find(c=>c.descriptorId==="homePicker").id');
 for(let i=0;i<20;i++){await dispatch({type:'activateCard',instanceId:picker});await dispatch({type:'activateCard',instanceId:terminalCard});}
 await wait('document.querySelector(".terminal-view .xterm")!==null');
 await checkTerminal(terminalId,'card_switch',{zoomFactor:1});
 events.push({step:'production_uie_split_merge_terminal_switch',split_merge:true,switch_cycles:20});
 for(const theme of ['light','dark']){await js('(async()=>{const m=await import("/src/composables/useTheme.ts");m.useTheme().setTheme('+JSON.stringify(theme)+');})()');await wait('document.documentElement.getAttribute("data-theme")==='+JSON.stringify(theme)+'&&window.__transcriptReal.terminal.getTerminal('+JSON.stringify(terminalId)+').term.options.theme?.black==='+JSON.stringify(theme==='dark'?'#484f58':'#1f2328'));await checkTerminal(terminalId,'theme_'+theme,{theme,zoomFactor:1});}
 for(const factor of [0.9,1.15,1]){window.webContents.setZoomFactor(factor);await checkTerminal(terminalId,'zoom_'+String(factor).replace('.','_'),{theme:'dark',zoomFactor:factor});}
 events.push({step:'real_terminal_theme_zoom',themes:['light','dark'],zoom_factors:[0.9,1.15,1]});
 const page=await js('window.__transcriptReal.api.listMessagePage('+JSON.stringify(sessionId)+',{limit:2})');
 assert.deepEqual(page.items.map(m=>m.id),firstRound.messageIds.slice(-2));assert.equal(page.has_more,true);assert.equal(typeof page.next_before_sequence,'number');
 const olderPage=await js('window.__transcriptReal.api.listMessagePage('+JSON.stringify(sessionId)+','+JSON.stringify({limit:2,before_sequence:page.next_before_sequence,expected_revert_epoch:page.revert_epoch})+')');
 assert.deepEqual([...olderPage.items,...page.items].map(m=>m.id),firstRound.messageIds);assert.equal(olderPage.has_more,false);
 events.push({step:'real_message_page',page,olderPage,exact_message_ids:firstRound.messageIds});
 await js('window.__transcriptReal.c.startNewConversation(null)');
 await wait('window.__transcriptReal.c.messages.value.length===0');
 await sendRound('你好，第二个会话',2);
 const otherId=await js('window.__transcriptReal.c.currentSession.value.id');assert.notEqual(otherId,sessionId);
 await js('window.__transcriptReal.c.setSelectedSession('+JSON.stringify('user::'+sessionId)+')');
 await wait('JSON.stringify(window.__transcriptReal.c.messages.value.map(m=>m.id))==='+JSON.stringify(JSON.stringify(firstRound.messageIds))+' && window.__transcriptReal.c.messages.value[0].content==="你好" && !window.__transcriptReal.c.messagesLoading.value');
 events.push({step:'actual_session_switch',distinct_sessions:2,first_history_restored:true});
 for(let index=3;index<=100;index++){
  await js('window.__transcriptReal.c.startNewConversation(null)');
  await wait('window.__transcriptReal.c.messages.value.length===0 && !window.__transcriptReal.c.busy.value');
  await sendRound('你好，受控首发 '+index,index);
 }
 assert.equal(new Set(rounds.map(r=>r.sessionId)).size,100);
 events.push({step:'actual_first_send_100_sessions',cycles:100,distinct_sessions:100,errors:0,optimistic_visible_max_ms:Math.max(...rounds.map(r=>r.optimisticVisibleMs)),reply_complete_max_ms:Math.max(...rounds.map(r=>r.elapsedMs))});
 const {verifyRealHistory}=await import(require('node:url').pathToFileURL(path.join(__dirname,'seed-real-history.mjs')).href);
 const largeHistory=await verifyRealHistory({ownedRoot:owned,js,wait,requests,onProgress:progress});
 fs.writeFileSync(path.join(__dirname,'real-history-acceptance.json'),JSON.stringify(largeHistory,null,2));
 events.push({step:'actual_10000_message_history',...largeHistory});
 await js('window.__transcriptReal.c.setSelectedSession('+JSON.stringify('user::'+sessionId)+')');
 await wait('JSON.stringify(window.__transcriptReal.c.messages.value.map(m=>m.id))==='+JSON.stringify(JSON.stringify(firstRound.messageIds))+' && !window.__transcriptReal.c.messagesLoading.value');
 const beforeReloadErrors=await js('window.__transcriptReal.errors');assert.deepEqual(beforeReloadErrors,[]);
 assert.equal(await js('document.querySelector("#renderer-error-fallback")!==null'),false);
 await js('window.__transcriptReal.terminal.closeAllTerminals()');
 const reloaded=new Promise(r=>window.webContents.once('did-finish-load',r));window.webContents.reload();await reloaded;
 await wait('window.__transcriptReal?.ready===true && !window.__transcriptReal.c.busy.value');
 await js('window.__transcriptReal.c.setSelectedSession('+JSON.stringify('user::'+sessionId)+')');
 await wait('window.__transcriptReal.c.messages.value.filter(m=>m.role==="assistant").length===1 && !window.__transcriptReal.c.messagesLoading.value');
 const restored=await js('({messageCount:window.__transcriptReal.c.messages.value.length,messageIds:window.__transcriptReal.c.messages.value.map(m=>m.id),messageRoles:window.__transcriptReal.c.messages.value.map(m=>m.role),activityCount:Object.keys(window.__transcriptReal.c.agentTurnActivities.value).length})');
 assert.deepEqual(restored.messageIds,firstRound.messageIds);events.push({step:'renderer_reload_real_persisted_history',...restored});
 await capture('real-restored-history');
 progress('history_restored');
 const errors=[...beforeReloadErrors,...await js('window.__transcriptReal.errors')];assert.deepEqual(errors,[]);
 assert.equal(await js('document.querySelector("#renderer-error-fallback")!==null'),false);
 assert.equal(consoleErrors.some(message=>message.includes('[renderer] uncaught')),false);
 assert.ok(requests.some(r=>r.method==='POST'&&r.path.endsWith('/interactions')&&r.status===201));
 assert.ok(requests.some(r=>r.path.includes('/messages/page')&&r.status===200));
 const receipt={accepted:true,electron:process.versions.electron,actual_main:true,actual_preload:true,actual_home_controller:true,actual_homepage_uie_chat_terminal:true,real_core_gateway:true,real_provider_http:true,isolated_user_root:true,random_ports:true,managed_endpoint_adapter:true,session_stream_transport_adapter:{path:'/api/v1/events',fault:'one owned AbortController cut of the real HTTP reader',production_source_and_reconnect:true,other_requests_unchanged:true,write_replayed:false,gateway_process_crash_tested:false},host_proof_budgets:hostProofBudgets,default_production_admission_under_pressure:false,proofRequests,hostStates,externally_started_service_lifecycle:true,controlled_model_provider:'controlled-provider.mjs',full_app_root:false,unused_registry_cards_excluded:['Git','approvals','orchestration','organization','events','doctor','browser','agent','spatial','market'],events,rounds,requests,streamHeaders,errors,consoleErrors,checked_at:new Date().toISOString()};
 fs.writeFileSync(path.join(__dirname,'real-app-acceptance.json'),JSON.stringify(receipt,null,2));clearTimeout(timeout);app.exit(0);
}catch(error){console.error(error.stack);await capture('real-app-failure').catch(()=>{});const state=await safeState().catch(e=>({unavailable:e.message}));fs.writeFileSync(path.join(owned,'electron-failure.json'),JSON.stringify({message:error.message,events,rounds,requests,streamHeaders,consoleErrors,state,host_proof_budgets:hostProofBudgets,proofRequests,hostStates},null,2));clearTimeout(timeout);app.exit(1);}}
app.on('browser-window-created',(_event,win)=>win.webContents.once('did-finish-load',()=>{void run();}));
require(path.join(desktop,'main.cjs'));

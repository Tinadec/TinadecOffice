// Isolated acceptance provider. Only the known text task can receive a pass.
import http from 'node:http';
import assert from 'node:assert/strict';
import { appendFileSync } from 'node:fs';
const criterion='回复正文含固定标记 TRANSCRIPT_CONTROLLED_OK';
const summary='executor：已输出受控验收标记 TRANSCRIPT_CONTROLLED_OK。';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function textOf(body){return (body.messages??[]).map(m=>typeof m.content==='string'?m.content:(m.content??[]).map(p=>p.text??'').join('\n')).join('\n');}
export function replyFor(body){
 const text=textOf(body),lower=text.toLowerCase();
 if(lower.includes('监督智能体')||lower.includes('pass/revise/escalate')){
  const tasks=text.slice(text.lastIndexOf('任务列表:\n')+6).split('\n\n执行证据')[0];
  const evidence=text.slice(text.lastIndexOf('\n\n执行证据'));
  const verdicts=[...tasks.matchAll(/key=([^|\n]+) \|[^\n]*criteria: ([^\n]+)/g)].flatMap(match=>match[2].split(';').map(c=>({task_key:match[1].trim(),criterion:c.trim(),satisfied:c.trim()===criterion&&evidence.includes('[completed]')&&evidence.includes(summary),evidence:summary})));
  const pass=verdicts.length===1&&verdicts.every(v=>v.satisfied);
  return {stage:'supervisor',content:JSON.stringify({decision:pass?'pass':'escalate',reasons:[pass?'受控任务的已完成正文含固定标记':'请求任务或证据超出此受控provider的验收范围'],revise_task_indexes:[],criteria_verdicts:verdicts})};
 }
 if(lower.includes('任务规划智能体')||lower.includes('仅输出 json 数组')){
  assert.match(text,/- worker\.general:/,'Expected controlled worker in frozen roster');
  return {stage:'planner',content:JSON.stringify([{task_key:'transcript-controlled-task',assignee:'worker.general',title:'输出受控验收标记',description:'只在回复正文输出 TRANSCRIPT_CONTROLLED_OK，用于隔离的传输和界面验收。',success_criteria:[criterion],dependencies:[],required_capabilities:[],required_tools:[],priority:1,risk:'low'}])};
 }
 if(lower.includes('执行层 agent')||lower.includes('只输出完成摘要')){
  const actual=[...text.matchAll(/成功标准：([^\n]*)/g)].at(-1)?.[1].split(';').map(c=>c.trim())??[];
  const known=actual.length===1&&actual[0]===criterion;
  return {stage:'worker',content:known?`${summary}\nCRITERION_EVIDENCE: ${criterion} || 回复正文中的字面标记 TRANSCRIPT_CONTROLLED_OK\nTASK_OUTCOME: completed`:'任务受阻：成功标准超出此受控provider的验收范围。\nTASK_OUTCOME: blocked'};
 }
 if(lower.includes('interaction kind:')||lower.includes('user-facing response')||(lower.includes('you are the conversation agent responsible for this run\'s reply')&&lower.includes('execution evidence:'))){
  assert.ok(text.includes(summary),'Meeting requires the known completed task evidence');
  return {stage:'meeting',content:'meeting：你好，受控对话链路已完成。\n\nTRANSCRIPT_CONTROLLED_OK\n\n- 规划与执行通过真实 Core。\n- 此回复由真实 Gateway SSE 传输。\n\n```text\nTRANSCRIPT_CONTROLLED_OK\n```\n\n| 验收项 | 状态 |\n| --- | --- |\n| 受控正文 | 完成 |'};
 }
 return {stage:'auxiliary',content:'fixture：收到受控验收请求。'};
}
function json(response,status,value){response.writeHead(status,{'content-type':'application/json'});response.end(JSON.stringify(value));}
if(process.argv.includes('--check')){
 const messages=content=>({messages:[{role:'system',content}]});
 const task=JSON.parse(replyFor(messages('任务规划智能体\n- worker.general: worker')).content)[0];
 assert.equal(task.success_criteria[0],criterion);
 assert.match(replyFor(messages('执行层 agent\n成功标准：'+criterion)).content,/TASK_OUTCOME: completed$/);
 assert.match(replyFor(messages('执行层 agent\n成功标准：未知工具操作')).content,/TASK_OUTCOME: blocked$/);
 const request='监督智能体\n任务列表:\n0. key=transcript-controlled-task | title=输出 | criteria: '+criterion+'\n\n执行证据 (第 0 轮修正后):\ntask 1: [completed] '+summary;
 assert.equal(JSON.parse(replyFor(messages(request)).content).decision,'pass');
 assert.equal(JSON.parse(replyFor(messages(request.replace(summary,'缺证据'))).content).decision,'escalate');
 assert.equal(replyFor(messages('You are the conversation agent responsible for this run\'s reply\nCurrent user goal:\n你好\n\nExecution evidence:\n'+summary)).stage,'meeting');
 assert.throws(()=>replyFor(messages('You are the conversation agent responsible for this run\'s reply\nExecution evidence:\n未知任务')),/known completed task evidence/);
 console.log('Controlled provider protocol checks passed.');
}else{
 let id=0;
 const server=http.createServer(async(request,response)=>{
  let stage;
  response.once('finish',()=>appendFileSync(process.env.TRANSCRIPT_PROVIDER_METRICS,JSON.stringify({method:request.method,path:new URL(request.url,'http://fixture').pathname,status:response.statusCode,stage})+'\n'));
  const route=request.method+' '+new URL(request.url,'http://fixture').pathname;
  if(route==='GET /healthz')return json(response,200,{status:'ok'});
  if(route==='GET /v1/models')return json(response,200,{object:'list',data:[{id:'tinadec-fixture-1',object:'model',created:1700000000,owned_by:'controlled-fixture'}]});
  if(route!=='POST /v1/chat/completions')return json(response,404,{error:{message:'Unknown controlled fixture route'}});
  if(!request.headers.authorization)return json(response,401,{error:{message:'Missing authorization'}});
  try{
   const chunks=[];for await(const piece of request)chunks.push(piece);
   const body=JSON.parse(Buffer.concat(chunks).toString('utf8')),reply=replyFor(body);stage=reply.stage;
   const requestId='chatcmpl-controlled-'+(++id),model=body.model??'tinadec-fixture-1';
   if(!body.stream)return json(response,200,{id:requestId,object:'chat.completion',created:1700000000,model,choices:[{index:0,message:{role:'assistant',content:reply.content},finish_reason:'stop'}],usage:{prompt_tokens:1,completion_tokens:reply.content.length,total_tokens:reply.content.length+1}});
   response.writeHead(200,{'content-type':'text/event-stream; charset=utf-8','cache-control':'no-cache',connection:'keep-alive'});
   const emit=(delta,finish=null)=>response.write('data: '+JSON.stringify({id:requestId,object:'chat.completion.chunk',created:1700000000,model,choices:[{index:0,delta,finish_reason:finish}]})+'\n\n');
   emit({role:'assistant',content:''});
   const step=Math.max(8,Math.ceil(reply.content.length/4));
   for(let offset=0;offset<reply.content.length;offset+=step){await sleep(Number(process.env.FIXTURE_STEP_DELAY_MS??20));emit({content:reply.content.slice(offset,offset+step)});}
   emit({},'stop');response.end('data: [DONE]\n\n');
  }catch(error){if(!response.headersSent)json(response,500,{error:{message:error.message}});else response.destroy();}
 });
 server.listen(Number(process.env.FIXTURE_PORT),'127.0.0.1',()=>console.log('Owned controlled provider ready.'));
 process.on('SIGTERM',()=>server.close(()=>process.exit(0)));
}

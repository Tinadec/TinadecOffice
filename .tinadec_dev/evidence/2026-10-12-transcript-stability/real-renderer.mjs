import { createApp, h, watch, vaporInteropPlugin } from '/src/lib/vue-shim.ts';
import { createPinia } from 'pinia';
import { createRouter, createWebHashHistory, RouterView } from 'vue-router';
import i18n from '/src/i18n.ts';
import HomePage from '/src/pages/HomePage.vue';
import HostAvailabilityBanner from '/src/components/HostAvailabilityBanner.vue';
import { homeController as c } from '/src/controllers/HomeController.ts';
import { api } from '/src/api.ts';
import { useConnection } from '/src/composables/useConnection.ts';
import { useTerminal } from '/src/composables/useTerminal.ts';
import { ensureProductionUie } from '/src/lib/uiEngine.ts';
import { useTheme } from '/src/composables/useTheme.ts';
import { installRendererErrorFallback } from '/src/lib/rendererErrorFallback.ts';
import { useHostAccess } from '/src/lib/hostAccess.ts';
import '/src/styles.css';
const errors=window.__transcriptEarlyErrors??[];
window.addEventListener('error', e=>errors.push({message:e.message,stack:e.error?.stack}));
window.addEventListener('unhandledrejection', e=>errors.push({message:String(e.reason),stack:e.reason?.stack}));
const connection=useConnection();
window.__transcriptReal={ c,api,errors,ready:false,terminal:useTerminal(),uie:ensureProductionUie(),hostAccess:useHostAccess(),connection,streamDeltas:0,streamUpdates:[],eventTypes:{},eventStreams:[],eventFetches:[] };
const actualFetch=window.fetch.bind(window);
// Owned acceptance fault only: preserve the production source and its cancellation.
const sessionStreamCuts=new Map();
window.__transcriptReal.cutLatestSessionStream=sessionId=>sessionStreamCuts.get(sessionId)?.()??false;
window.fetch=async(...args)=>{
 const url=new URL(args[0] instanceof Request?args[0].url:String(args[0]),window.location.href);
 if(url.pathname!=='/api/v1/events')return actualFetch(...args);
 const observation={path:url.pathname+url.search,startedAt:performance.now()};window.__transcriptReal.eventFetches.push(observation);
 const [input,init]=args,callerSignal=init?.signal===undefined&&input instanceof Request?input.signal:init?.signal;
 const cut=new AbortController(),signal=callerSignal?AbortSignal.any([callerSignal,cut.signal]):cut.signal;
 sessionStreamCuts.set(url.searchParams.get('session_id'),()=>{
  if(cut.signal.aborted||callerSignal?.aborted)return false;
  observation.cutAt=performance.now();cut.abort(new DOMException('Owned acceptance HTTP read cut','AbortError'));return true;
 });
 try{const response=await actualFetch(input,{...init,signal});observation.status=response.status;return response;}catch(error){observation.error={name:error?.name,message:String(error?.message??error)};throw error;}
};
const actualConnectEvents=api.connectEvents.bind(api);
api.connectEvents=(sessionId,onEvent,options)=>{
 const observation={sessionId,options,createdAt:performance.now(),rawEventTypes:{},errors:0,closed:false};window.__transcriptReal.eventStreams.push(observation);
 try{
  const source=actualConnectEvents(sessionId,event=>{observation.rawEventTypes[event.type]=(observation.rawEventTypes[event.type]??0)+1;onEvent(event);},options);
  observation.wireSessionId=new URL(source.url,window.location.href).searchParams.get('session_id');
  source.onerror=()=>{observation.errors++;observation.readyState=source.readyState;};
  const actualClose=source.close.bind(source);source.close=()=>{observation.closed=true;actualClose();};
  return source;
 }catch(error){observation.error={name:error?.name,message:String(error?.message??error),stack:error?.stack};throw error;}
};
watch(c.streamingReply,text=>{if(text)window.__transcriptReal.streamUpdates.push(text.length);});
c.onEvent(e=>{const f=window.__transcriptReal;f.eventTypes[e.type]=(f.eventTypes[e.type]??0)+1;if(e.type.includes('delta'))f.streamDeltas++;});
useTheme().setTheme('dark');
const router=createRouter({history:createWebHashHistory(),routes:[{path:'/:pathMatch(.*)*',component:HomePage}]});
const app=createApp({render:()=>h('main',{style:'height:100vh;width:100vw'},[h(RouterView),h(HostAvailabilityBanner)])});
installRendererErrorFallback(app);
const handleComponentError=app.config.errorHandler;
app.config.errorHandler=(error,instance,info)=>{errors.push({message:String(error),stack:error?.stack,info});handleComponentError?.(error,instance,info);};
app.use(vaporInteropPlugin).use(createPinia()).use(router).use(i18n).mount('#app');
await connection.start();
const config=await(await fetch('/__config')).json();
window.__transcriptReal.config=config;
const installed=await api.listAgentPacks();
if(!installed.some(p=>p.pack_id===config.envelope.manifest.metadata.pack_id)){
  const provider=await api.createModelProvider({driver:'openai',display_name:'transcript-controlled-provider',connection_kind:'api-key',base_url:config.modelUrl+'/v1',model:'tinadec-fixture-1',api_key:'fixture-local-secret'});
  const routes=await api.listModelRoutes();const chat=routes.find(r=>r.purpose==='chat');
  await api.saveModelRoute('chat',{candidates:[{provider_instance_id:provider.id,model:'tinadec-fixture-1'}]},undefined,{expected_revision:chat?.revision});
  const preview=await api.previewAgentPackInstall(config.envelope);
  await api.installAgentPack(config.envelope.manifest.metadata.pack_id,{preview_id:preview.preview_id,envelope:config.envelope},{idempotency_key:'transcript-real-isolated'});
}
c.start();
window.__transcriptReal.ready=true;

// Observe the existing controlled model fixture without inspecting auth headers/body.
import http from 'node:http';
import { appendFileSync } from 'node:fs';
const create=http.createServer;
http.createServer=function(handler,...args){return create.call(this,(request,response)=>{
  response.once('finish',()=>appendFileSync(process.env.TRANSCRIPT_PROVIDER_METRICS,JSON.stringify({method:request.method,path:new URL(request.url,'http://fixture').pathname,status:response.statusCode})+'\n'));
  handler(request,response);
},...args);};

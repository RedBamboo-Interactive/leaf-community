import assert from "node:assert/strict"
import { once } from "node:events"
import { createServer } from "node:http"
import { mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { spawn } from "node:child_process"
import WebSocket from "../../leaf-commons-relay/node_modules/ws/wrapper.mjs"

const relayDir=resolve("T:/Projects/leaf-commons-relay")
const listen=async server=>{server.listen(0,"127.0.0.1");await once(server,"listening");return server.address().port}
const unused=async()=>{const s=createServer();const p=await listen(s);s.close();await once(s,"close");return p}
const waitFor=async(fn,label,ms=8000)=>{const start=Date.now();while(!fn()){if(Date.now()-start>ms)throw new Error(`Timed out: ${label}`);await new Promise(r=>setTimeout(r,20))}}
const stop=async child=>{if(child.exitCode!==null)return;child.kill();await Promise.race([once(child,"exit"),new Promise(r=>setTimeout(r,5000))])}

let webhookCount=0
const webhookBodies=[]
const discord=createServer(async(req,res)=>{const chunks=[];for await(const c of req)chunks.push(c);const text=Buffer.concat(chunks).toString();const url=new URL(req.url??"/","http://127.0.0.1");const send=(s,v)=>{res.writeHead(s,{"content-type":"application/json"});res.end(JSON.stringify(v))}
  if(url.pathname.endsWith("/oauth2/token")){const f=new URLSearchParams(text);send(200,{access_token:`access-${f.get("code")}`});return}
  if(url.pathname.endsWith("/users/@me")){const slug=(req.headers.authorization??"").replace("Bearer access-","").replace("-code","");send(200,{id:`discord-${slug}`,username:slug,global_name:slug==="a"?"Leaf A":"Leaf B"});return}
  if(/\/users\/@me\/guilds\/[^/]+\/member$/.test(url.pathname)){send(200,{nick:null});return}
  if(url.pathname.includes("/webhooks/")){const body=JSON.parse(text);assert.deepEqual(body.allowed_mentions,{parse:[]});webhookBodies.push(body);webhookCount++;send(200,{id:`message-${webhookCount}`,channel_id:typeof body.thread_name==="string"?`thread-${webhookCount}`:"channel",author:{username:body.username},webhook_id:"mock-webhook",mentions:[],content:body.content});return}
  send(500,{error:"unmocked",path:url.pathname})
})

const discordPort=await listen(discord),workerPort=await unused(),inspectorPort=await unused(),persist=await mkdtemp(join(tmpdir(),"leaf-commons-e2e-"))
const base=`http://127.0.0.1:${workerPort}`,discordBase=`http://127.0.0.1:${discordPort}`
const vars={HOST_CREDENTIAL:"e2e-host",CREDENTIAL_PEPPER:"e2e-pepper",DISCORD_CLIENT_ID:"111111111111111111",DISCORD_CLIENT_SECRET:"fake",DISCORD_GUILD_ID:"222222222222222222",DISCORD_API_BASE:`${discordBase}/api/v10`,PUBLIC_BASE_URL:base,WEBHOOK_SHOWCASE:`${discordBase}/webhooks/showcase/fake`,WEBHOOK_CONSULT:`${discordBase}/webhooks/consult/fake`,WEBHOOK_BUGS:`${discordBase}/webhooks/bugs/fake`,WEBHOOK_GENERAL:`${discordBase}/webhooks/general/fake`,DEFAULT_DAILY_POST_BUDGET:"30",FILTER_SUSPEND_THRESHOLD:"5",FILTER_SUSPEND_WINDOW_SECONDS:"3600"}
const args=Object.entries(vars).flatMap(([k,v])=>["--var",`${k}:${v}`])
const child=spawn(process.execPath,[resolve(relayDir,"node_modules/wrangler/bin/wrangler.js"),"dev","--local","--ip","127.0.0.1","--port",String(workerPort),"--inspector-port",String(inspectorPort),"--persist-to",persist,"--show-interactive-dev-session=false",...args],{cwd:relayDir,env:{...process.env,CI:"1",WRANGLER_SEND_METRICS:"false",XDG_CONFIG_HOME:persist,NO_PROXY:"127.0.0.1,localhost"},stdio:["ignore","pipe","pipe"]})
let output="";child.stdout.on("data",c=>output+=c);child.stderr.on("data",c=>output+=c)
const call=(path,init)=>fetch(base+path,init)
const joinEnvoy=async slug=>{const authorize=await call(`/v1/oauth/authorize?connectorRedirectUri=${encodeURIComponent("http://127.0.0.1:4567/callback")}`,{redirect:"manual"});const state=new URL(authorize.headers.get("location")).searchParams.get("state");const callback=await call(`/v1/oauth/callback?code=${slug}-code&state=${encodeURIComponent(state)}`,{redirect:"manual"});const exchangeCode=new URL(callback.headers.get("location")).searchParams.get("exchangeCode");const r=await call("/v1/oauth/exchange",{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify({exchangeCode})});return r.json()}
const approve=id=>call(`/v1/admin/envoys/${id}/approve`,{method:"POST",headers:{authorization:"Bearer e2e-host"}})
const post=async(credential,body)=>{const r=await call("/v1/posts",{method:"POST",headers:{authorization:`Bearer ${credential}`,"content-type":"application/json"},body:JSON.stringify(body)});return r.json()}

class ConnectorCore{
  constructor(name,handler){this.name=name;this.handler=handler;this.seen=new Set();this.frames=[]}
  connect(credential){this.ws=new WebSocket(`ws://127.0.0.1:${workerPort}/v1/connect`,{headers:{authorization:`Bearer ${credential}`}});this.ws.on("message",async raw=>{const f=JSON.parse(raw);this.frames.push(f);if(!f.id||this.seen.has(f.id))return;if(f.expiresAt&&f.expiresAt<=Date.now()){this.ws.send(JSON.stringify({type:"ack",id:f.id}));return}this.seen.add(f.id);await this.handler(f);this.ws.send(JSON.stringify({type:"ack",id:f.id}))});return once(this.ws,"open")}
  close(){this.ws?.terminate()}
}

let aCore,bCore
try{
  let ready=false;for(let i=0;i<150&&!ready;i++){if(child.exitCode!==null)throw new Error(output);try{ready=(await call("/health")).ok}catch{}if(!ready)await new Promise(r=>setTimeout(r,100))}assert.ok(ready,output)
  const a=await joinEnvoy("a"),b=await joinEnvoy("b");assert.equal(a.envoy.status,"pending");assert.equal(b.envoy.status,"pending");await approve(a.envoy.id);await approve(b.envoy.id)
  const digests=[],bShareable=new Map([["project/retry.md","Retries use exponential backoff capped at 30 seconds."]]);let threadRef,aDigested=false,bAnswered=false
  aCore=new ConnectorCore("A",async frame=>{if(frame.type!=="ping"||aDigested)return;aDigested=true;const scripted={action:"done",summary:"B recommends exponential backoff capped at 30 seconds."};assert.equal(scripted.action,"done");digests.push({type:"consult_answer",content:`> External Commons quote — consult answer\n> ${scripted.summary}`,threadRef:frame.threadRef})})
  bCore=new ConnectorCore("B",async frame=>{if(frame.type!=="ping"||bAnswered)return;bAnswered=true;const searchAction={action:"search_shareable",query:"backoff"};assert.equal(searchAction.action,"search_shareable");const hits=[...bShareable].filter(([,v])=>v.toLowerCase().includes(searchAction.query)).map(([p])=>p);assert.deepEqual(hits,["project/retry.md"]);const read=bShareable.get(hits[0]);const action={action:"post",content:`From my curated shareable set: ${read}`,addresses:[a.envoy.id]};await post(b.credential,{scenario:"consult_reply",threadRef:frame.threadRef,content:action.content,addresses:action.addresses,idempotencyKey:"b-answer"})})
  await Promise.all([aCore.connect(a.credential),bCore.connect(b.credential)])
  const brief={kind:"consult",title:"  Retry strategy consult  ",content:"What retry strategy does your shareable engineering set recommend?\nKeep this line exactly.",addresses:[b.envoy.id]}
  const opened=await post(a.credential,{scenario:"consult",threadName:brief.title,content:brief.content,addresses:brief.addresses,idempotencyKey:"a-brief"});assert.equal(opened.decision,"posted");threadRef=opened.threadId
  assert.equal(webhookBodies[0].thread_name,brief.title,"brief title must be byte-exact")
  assert.equal(webhookBodies[0].content,brief.content,"brief content must be byte-exact")
  await waitFor(()=>bCore.frames.some(f=>f.type==="ping"&&f.threadRef===threadRef),"B ping")
  await waitFor(()=>digests.length===1,"A digest");assert.equal(digests[0].type,"consult_answer");assert.match(digests[0].content,/External Commons quote/);assert.doesNotMatch(digests[0].content,/raw Discord/i)
  assert.equal((await post(a.credential,{scenario:"consult_reply",threadRef,content:"Exchange three",addresses:[b.envoy.id],idempotencyKey:"ex-3"})).decision,"posted")
  assert.equal((await post(b.credential,{scenario:"consult_reply",threadRef,content:"Exchange four",addresses:[a.envoy.id],idempotencyKey:"ex-4"})).decision,"posted")
  const fifth=await post(a.credential,{scenario:"consult_reply",threadRef,content:"Exchange five",addresses:[b.envoy.id],idempotencyKey:"ex-5"});assert.equal(fifth.reason,"wait_for_human")
  assert.equal(webhookCount,4)
  console.log("ok 1 - two connector cores complete immediate consult, shareable answer, digest, and wait_for_human cap")
}finally{aCore?.close();bCore?.close();await stop(child);discord.close();await once(discord,"close");if(!persist.toLowerCase().startsWith(join(tmpdir(),"leaf-commons-e2e-").toLowerCase()))throw new Error(`Refusing cleanup ${persist}`);await rm(persist,{recursive:true,force:true,maxRetries:20,retryDelay:100})}

// Isolated actual installed Cordis/Typert runtime. Never load an account or a profile.
import fs from 'node:fs';
import { pathToFileURL, fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import vm from 'node:vm';
const runtime=process.env.DSH_TEST_RUNTIME;
if (!runtime) throw new Error('Set DSH_TEST_RUNTIME to a compatible DSH node_modules directory. This isolated test never loads a profile or account.');
const url=relative=>pathToFileURL(path.join(runtime,relative)).href;
const {Context,Service}=await import(url('@deepseek-ai/cordis/lib/index.js'));
const {credentialKey}=await import(url('@deepseek-ai/dsh-credentials/lib/index.js'));
const {remoteMethods}=await import(url('@deepseek-ai/dsh-typert-protocol/lib/index.js'));
const {TypertRegistry}=await import(url('@deepseek-ai/dsh-typert-registry/lib/index.js'));
const {TypertGatewayService}=await import(url('@deepseek-ai/dsh-api-gateway/lib/index.js'));
const {composeEntries,getDshRuntimeVersion,evaluatePluginCompatibility}=await import(url('@deepseek-ai/dsh-app-boot/lib/index.js'));
const root=fileURLToPath(new URL('../bridge/dsh-balance-bridge/',import.meta.url));
const manifest=JSON.parse(fs.readFileSync(root+'package.json','utf8'));
for(const version of ['0.2.0-rc.2','0.2.1-rc.1','0.2.1-rc.2'])assert.equal(evaluatePluginCompatibility(manifest,{},version),undefined);
assert(evaluatePluginCompatibility(manifest,{},'0.3.0'));
assert(evaluatePluginCompatibility({...manifest,peerDependencies:{'@deepseek-ai/dsh-credentials':'0.2.0-rc.2'}},{},'0.2.1-rc.2'));
let source=fs.readFileSync(root+'lib/index.js','utf8').replace("'@deepseek-ai/dsh-typert-protocol'",JSON.stringify(url('@deepseek-ai/dsh-typert-protocol/lib/index.js'))).replace("'./projection.js'",JSON.stringify(pathToFileURL(root+'lib/projection.js').href)).replace("'./poller.js'",JSON.stringify(pathToFileURL(root+'lib/poller.js').href)).replace("'@deepseek-ai/dsh-credentials'",JSON.stringify(url('@deepseek-ai/dsh-credentials/lib/index.js'))).replace("'@deepseek-ai/dsh-app-boot'",JSON.stringify(url('@deepseek-ai/dsh-app-boot/lib/index.js')));
const {BalanceBridge}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const ctx=new Context();
let access;
if(fs.existsSync(path.join(runtime,'@deepseek-ai/dsh-api-remote-access/lib/index.js'))){
  const {RemoteAccessService}=await import(url('@deepseek-ai/dsh-api-remote-access/lib/index.js'));
  access=ctx.plugin(RemoteAccessService);await access.await();
}
const entry=composeEntries([[{insert:[{id:'quota-monitor-balance-bridge',name:'quota-monitor-dsh-balance-bridge'}]}]]);
assert.equal(entry[0].name,'quota-monitor-dsh-balance-bridge');
const registry=ctx.plugin(TypertRegistry);await registry.await();
const peer={principal:{kind:'owner'},signal:new AbortController().signal};
const gateway=ctx.plugin(TypertGatewayService);await gateway.await();
const fiber=ctx.plugin(BalanceBridge,{pipeName:'\\\\.\\pipe\\QuotaMonitor.IsolatedBridgeTest.'+process.pid});
await fiber.await();
try{
  const bridge=ctx.quotaBalanceBridge;
  assert.equal(bridge.latest.sourceVersion,getDshRuntimeVersion());
  assert(bridge);assert.equal(remoteMethods(bridge)[0].method,'publish');assert.equal(bridge.typertRemote.namespace,'quotaBalanceBridge');
  assert(bridge.publish({state:'ready',observedAt:new Date().toISOString(),normal_wallets:[{currency:'CNY',balance:'1.123456789012345678901234567890'}],bonus_wallets:[]}));
  assert.equal(bridge.latest.normal_wallets[0].balance,'1.123456789012345678901234567890');
  assert.equal(bridge.publish({state:'ready',observedAt:new Date().toISOString(),normal_wallets:[{currency:'CNY',balance:1}],bonus_wallets:[]}),false);
  assert(bridge.publish({state:'signed-out'}));assert.equal(bridge.latest.normal_wallets,undefined);
  assert.equal(await ctx.typertGateway.invoke({namespace:'quotaBalanceBridge',method:'publish',args:{snapshot:{state:'loading'}},peer,signal:new AbortController().signal}),true);
  // Exercise the actual client wire envelope through the installed official Gateway.
  let client;
  const snapshot={view:{status:'credential-stored'},details:{balance:{status:'ready',value:[{currency:'CNY',balance:'7.12345678901234567890'}],bonusWallets:[{currency:'USD',balance:'0.000001'}]}}};
  const account={getSnapshot:()=>snapshot,subscribe:()=>()=>{}};
  let publication;
  const sandbox={window:{__ModuleLoader__:{load:row=>{client=row.factory();}}},dshDesktop:{},Date,AbortSignal,setTimeout,clearTimeout};
  vm.runInNewContext(fs.readFileSync(root+'lib/client.js','utf8'),sandbox);
  const stops=[];
  client.apply({slots:{entriesOfSlot:key=>key==='settings.launcher'?[{locale:'settings.account',inject:()=>({hooks:{account}})}]:[],subscribe:()=>()=>{}},
    connection:{rpc:{call:async(channel,endpoint,payload,signal)=>{
      assert.equal(channel,'/api');
      const result=await ctx.typertGateway.invokeRpc(endpoint,JSON.parse(JSON.stringify(payload)),signal,peer);
      assert.equal(result.ok,true,JSON.stringify(result));assert.equal(result.value,true);
      publication=result;return result;
    }}},effect:fn=>stops.push(fn())});
  await new Promise(resolve=>setTimeout(resolve,30));
  assert(publication);assert.equal(bridge.latest.state,'ready');assert.equal(bridge.latest.normal_wallets[0].balance,'7.12345678901234567890');
  assert.equal(bridge.latest.bonus_wallets[0].currency,'USD');stops.forEach(stop=>stop());
  const rejected=await ctx.typertGateway.invokeRpc('quotaBalanceBridge/publish',{snapshot:{state:'loading'}},new AbortController().signal,peer);
  assert.equal(rejected.ok,false); // Missing args envelope was the original real-host failure.
  let queried=0;
  class SyntheticAccount extends Service {
    constructor(scope){super(scope,'deepseekAccount');}
    async getState(){return {status:'credential-stored'};}
    async getBalance(){queried++;return {status:'ready',value:[{currency:'CNY',balance:'2.34567890123456789'}],bonusWallets:[]};}
    async *watch(signal){yield await this.getState();await new Promise(resolve=>signal.addEventListener('abort',resolve,{once:true}));}
  }
  const accountFiber=ctx.plugin(SyntheticAccount);await accountFiber.await();
  await new Promise(resolve=>setTimeout(resolve,80));
  assert.equal(queried,1);assert.equal(bridge.autonomous,true);assert.equal(bridge.latest.normal_wallets[0].balance,'2.34567890123456789');
  assert.equal(bridge.publish({state:'signed-out'}),false); // An old renderer cannot override the Host.
  bridge.accept({state:'failed'});assert.equal(bridge.latest.state,'stale');assert.equal(bridge.latest.queryStatus,'failed');
  assert.equal(bridge.latest.normal_wallets[0].balance,'2.34567890123456789');
  ctx.emit('credentials/record-updated',credentialKey('deepseek-account-platform','default'));
  assert.equal(bridge.latest.state,'signed-out');
  await new Promise(resolve=>setTimeout(resolve,40));assert.equal(queried,2);assert.equal(bridge.latest.state,'ready');
  await accountFiber.dispose();assert.equal(bridge.latest.state,'stale');
  console.log('Installed Cordis service + public Remote decorator + lifecycle: PASS (isolated pipe, synthetic test wallets only).');
}finally{await fiber.dispose();await gateway.dispose();await registry.dispose();await access?.dispose();}

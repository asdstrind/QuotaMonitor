// Actual installed Cordis and Gateway; isolated synthetic service, never a profile/account.
import fs from 'node:fs';
import {pathToFileURL,fileURLToPath} from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import net from 'node:net';
const runtime=process.env.DSH_TEST_RUNTIME;
if(!runtime)throw new Error('DSH_TEST_RUNTIME required');
const url=p=>pathToFileURL(path.join(runtime,p)).href;
const {Context,Service}=await import(url('@deepseek-ai/cordis/lib/index.js'));
const {credentialKey}=await import(url('@deepseek-ai/dsh-credentials/lib/index.js'));
const {getDshRuntimeVersion}=await import(url('@deepseek-ai/dsh-app-boot/lib/index.js'));
const {TypertRegistry}=await import(url('@deepseek-ai/dsh-typert-registry/lib/index.js'));
const {TypertGatewayService}=await import(url('@deepseek-ai/dsh-api-gateway/lib/index.js'));
const root=fileURLToPath(new URL('../bridge/dsh-balance-bridge/',import.meta.url));
let source=fs.readFileSync(root+'lib/index.js','utf8');
for(const name of ['cordis','dsh-credentials','dsh-app-boot'])source=source.replace(`'@deepseek-ai/${name}'`,JSON.stringify(url(`@deepseek-ai/${name}/lib/index.js`)));
for(const name of ['projection','poller'])source=source.replace(`'./${name}.js'`,JSON.stringify(pathToFileURL(root+`lib/${name}.js`).href));
const {BalanceBridge}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const ctx=new Context();
const {RemoteAccessService}=await import(url('@deepseek-ai/dsh-api-remote-access/lib/index.js'));
const access=ctx.plugin(RemoteAccessService);await access.await();
const registry=ctx.plugin(TypertRegistry);await registry.await();
const gateway=ctx.plugin(TypertGatewayService);await gateway.await();
const pipeName='\\\\.\\pipe\\QuotaMonitor.IsolatedBridgeTest.'+process.pid;
const frames=[];
const server=net.createServer(socket=>{let pending='';socket.on('data',b=>{pending+=b;let at;while((at=pending.indexOf('\n'))>=0){frames.push(JSON.parse(pending.slice(0,at)));pending=pending.slice(at+1);}});});
await new Promise((resolve,reject)=>server.listen(pipeName,resolve).once('error',reject));
const fiber=ctx.plugin(BalanceBridge,{pipeName});await fiber.await();
let accountFiber;
try {
 const bridge=ctx.quotaBalanceBridge;
 assert(bridge);assert.equal(bridge.latest.sourceVersion,getDshRuntimeVersion());
 assert.equal(bridge.publish,undefined);assert.equal(bridge.accountAttached,undefined);
 const peer={principal:{kind:'owner'},signal:new AbortController().signal};
 const rejected=await ctx.typertGateway.invokeRpc('quotaBalanceBridge/publish',{args:{snapshot:{state:'ready'}}},peer.signal,peer);
 assert.equal(rejected.ok,false); // No remote method can forge a balance, even from an owner renderer.
 let queried=0;
 class SyntheticAccount extends Service {
  constructor(scope){super(scope,'deepseekAccount');}
  async getState(){return {status:'credential-stored'};}
  async getBalance(){queried++;return {status:'ready',value:[{currency:'CNY',balance:'2.34567890123456789'}],bonusWallets:[]};}
  async *watch(signal){yield await this.getState();await new Promise(resolve=>signal.addEventListener('abort',resolve,{once:true}));}
 }
 accountFiber=ctx.plugin(SyntheticAccount);await accountFiber.await();
 await new Promise(resolve=>setTimeout(resolve,150));
 assert.equal(queried,1);assert.equal(bridge.latest.state,'ready');
 assert.equal(bridge.latest.normal_wallets[0].balance,'2.34567890123456789');
 assert(frames.some(frame=>frame.state==='ready'&&frame.protocol===1));
 bridge.accept({state:'failed'});assert.equal(bridge.latest.state,'stale');assert.equal(bridge.latest.queryStatus,'failed');
 const at=bridge.latest.observedAt;
 ctx.emit('credentials/record-updated',credentialKey('deepseek-account-platform','default'));
 assert.equal(bridge.latest.state,'signed-out');assert.equal(bridge.success,undefined);
 await new Promise(resolve=>setTimeout(resolve,50));assert.equal(queried,2);assert.equal(bridge.latest.state,'ready');
 assert(bridge.latest.observedAt>=at);
 await accountFiber.dispose();accountFiber=undefined;assert.equal(bridge.latest.state,'stale');assert.equal(bridge.accountAttached,false);
 console.log('Installed Cordis/Gateway + Bridge lifecycle/account switch/real isolated pipe: PASS (synthetic wallets).');
} finally {await accountFiber?.dispose();await fiber.dispose();await gateway.dispose();await registry.dispose();await access.dispose();await new Promise(resolve=>server.close(resolve));}

// Isolated tests: no production Context/profile/account or authentication modules are loaded.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const {pathToFileURL}=require('node:url');
const root=path.resolve(__dirname,'../bridge/dsh-balance-bridge');
function harness(slotKey='settings.section') {
  let exports, slotListener, sourceListener, value={view:{status:'credential-stored'},details:{balance:{status:'ready',value:[{currency:'CNY',balance:'12.123456789012345678901234567890'}],bonusWallets:[{currency:'USD',balance:'0.000000001'}]}}};
  const calls=[],disposers=[];
  const source={getSnapshot:()=>value,subscribe:fn=>{sourceListener=fn;return()=>{sourceListener=undefined;}}};
  const entry={options:{id:'account'},locale:'settings.account',inject:()=>({hooks:{account:source},refresh:()=>{throw new Error('must not refresh account');},start:()=>{throw new Error('must not log in');}})};
  const ctx={slots:{entriesOfSlot:key=>key===slotKey?[entry]:[],subscribe:(_,fn)=>{slotListener=fn;return()=>{slotListener=undefined;};}},connection:{rpc:{call:async(channel,endpoint,payload)=>{assert.deepEqual(Object.keys(payload),['args']);calls.push({channel,endpoint,payload});return {ok:true,value:true};}}},effect:fn=>{disposers.push(fn());}};
  const sandbox={window:{__ModuleLoader__:{load:row=>{exports=row.factory();}}},dshDesktop:{},Date,AbortSignal,setTimeout,clearTimeout};
  vm.runInNewContext(fs.readFileSync(path.join(root,'lib/client.js'),'utf8'),sandbox);
  exports.apply(ctx);
  return {calls,set:next=>{value=next;sourceListener?.();},get:()=>value,dispose:()=>disposers.forEach(fn=>fn()),hasSubscription:()=>!!sourceListener};
}
test('passive client forwards only wallets, raw precision and observation time',async()=>{
  const h=harness();await new Promise(resolve=>setImmediate(resolve));
  assert.equal(h.calls.length,1);const p=h.calls[0].payload.args.snapshot;
  assert.equal(h.calls[0].endpoint,'quotaBalanceBridge/publish');assert.equal(p.normal_wallets[0].balance,'12.123456789012345678901234567890');assert.equal(p.bonus_wallets[0].currency,'USD');
  assert.deepEqual(Object.keys(p).sort(),['bonus_wallets','normal_wallets','observedAt','state']);
  const original=h.get();h.set({...original,details:{...original.details,profile:{status:'ready',value:{contact:'synthetic'}}}});await new Promise(resolve=>setImmediate(resolve));assert.equal(h.calls.length,1);
  h.set({...original,details:{balance:{...original.details.balance}}});await new Promise(resolve=>setImmediate(resolve));assert.equal(h.calls.length,2);h.dispose();assert.equal(h.hasSubscription(),false);
});
test('signed-out does not forward a prior account balance; failed does not fabricate zero',async()=>{
  const h=harness();await new Promise(resolve=>setImmediate(resolve));h.set({view:{status:'signed-out'},details:h.get().details});await new Promise(resolve=>setImmediate(resolve));assert.deepEqual(JSON.parse(JSON.stringify(h.calls.at(-1).payload.args.snapshot)),{state:'signed-out'});
  h.set({view:{status:'credential-stored'},details:{balance:{status:'failed'}}});await new Promise(resolve=>setImmediate(resolve));assert.deepEqual(JSON.parse(JSON.stringify(h.calls.at(-1).payload.args.snapshot)),{state:'failed'});h.dispose();
});
test('passive account source remains available through official slots with Settings closed',async()=>{
  for(const key of ['settings.launcher','shell.quota-notice']) {
    const h=harness(key);await new Promise(resolve=>setImmediate(resolve));
    assert.equal(h.calls[0].payload.args.snapshot.state,'ready');h.dispose();
  }
});
test('public wallet projection rejects malformed amount and preserves currency',async()=>{
  const {project}=await import(pathToFileURL(path.join(root,'lib/projection.js')).href);
  assert.throws(()=>project({view:{status:'credential-stored'},details:{balance:{status:'ready',value:[{currency:'USD',balance:1}],bonusWallets:[]}}}));
  assert.deepEqual(project({view:{status:'signed-out'}}),{state:'signed-out'});
});

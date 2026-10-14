// Synthetic accounts and manual timers: never load credentials or a production profile.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { pathToFileURL } = require('node:url');
const path = require('node:path');
const tick = async () => { for (let i=0;i<12;i++) await Promise.resolve(); };
const ready = () => ({ status:'ready',value:[{currency:'CNY',balance:'1.234567890123456789'}],bonusWallets:[{currency:'USD',balance:'0.001'}] });
async function harness() {
  const {BalancePoller}=await import(pathToFileURL(path.resolve(__dirname,'../bridge/dsh-balance-bridge/lib/poller.js')).href);
  let serial=0,calls=0,time=Date.parse('2026-10-13T00:00:00Z');
  const timers=new Map(),messages=[];
  const account={getState:async()=>({status:'credential-stored'}),getBalance:async client=>{calls++;assert.equal(client.version,'0.2.0-rc.2');return ready();},async *watch(signal){await new Promise(resolve=>signal.addEventListener('abort',resolve,{once:true}));}};
  const poller=new BalancePoller(account,p=>messages.push(p),{setTimer:(fn,ms)=>{const id=++serial;timers.set(id,{fn,ms});return id;},clearTimer:id=>timers.delete(id),now:()=>new Date(time)});
  const run=async ms=>{const row=[...timers].find(([,v])=>v.ms===ms);assert(row,'timer '+ms);timers.delete(row[0]);time+=ms;row[1].fn();await tick();};
  poller.start();
  return {poller,account,messages,timers,run,calls:()=>calls,advance:ms=>time+=ms};
}
test('Host queries at startup and every three minutes without any client or Slot',async()=>{
  const h=await harness();await h.run(0);assert.equal(h.calls(),1);assert.equal(h.messages.at(-1).state,'ready');
  const at=h.messages.at(-1).observedAt;await h.run(180000);assert.equal(h.calls(),2);assert.notEqual(h.messages.at(-1).observedAt,at);
  assert.equal(h.messages.at(-1).normal_wallets[0].balance,'1.234567890123456789');h.poller.dispose();assert.equal(h.timers.size,0);
});
test('failures including sanitized HTTP 202 never fabricate zero and back off then recover',async()=>{
  const h=await harness();await h.run(0);h.account.getBalance=async()=>({status:'failed'});
  await h.run(180000);assert.equal(h.messages.at(-1).state,'failed');await h.run(30000);await h.run(60000);await h.run(120000);await h.run(240000);await h.run(480000);await h.run(900000);
  assert([...h.timers.values()].some(v=>v.ms===900000));h.account.getBalance=async()=>ready();await h.run(900000);assert.equal(h.messages.at(-1).state,'ready');assert.equal(h.poller.failures,0);h.poller.dispose();
});
test('watchdog retains single-flight and ignores late success',async()=>{
  const h=await harness();let settle;h.account.getBalance=()=>new Promise(resolve=>settle=resolve);
  await h.run(0);await h.poller.refresh();assert.equal(h.poller.flight,true);await h.run(125000);assert.equal(h.messages.at(-1).state,'failed');
  await h.poller.refresh();assert.equal(h.timers.size,0);settle(ready());await tick();assert.equal(h.messages.at(-1).state,'failed');assert([...h.timers.values()].some(v=>v.ms===30000));h.poller.dispose();
});
test('same-status account switch clears old wallets and discards in-flight result',async()=>{
  const h=await harness();await h.run(0);let settle;h.account.getBalance=()=>new Promise(resolve=>settle=resolve);
  await h.run(180000);h.poller.changed();assert.deepEqual(h.messages.at(-1),{state:'signed-out'});await h.run(0);settle(ready());await tick();assert.equal(h.messages.at(-1).state,'signed-out');
  h.account.getBalance=async()=>ready();await h.run(0);assert.equal(h.messages.at(-1).state,'ready');h.poller.dispose();
});
test('signed-out does not query and disposal rejects delayed results',async()=>{
  const h=await harness();h.account.getState=async()=>({status:'signed-out'});await h.run(0);assert.equal(h.calls(),0);assert.equal(h.messages.at(-1).state,'signed-out');
  h.account.getState=async()=>({status:'credential-stored'});let settle;h.account.getBalance=()=>new Promise(resolve=>settle=resolve);h.poller.changed();await h.run(0);h.poller.dispose();const n=h.messages.length;settle(ready());await tick();assert.equal(h.messages.length,n);assert.equal(h.timers.size,0);
});
test('malformed wallet and thrown network errors fail safely',async()=>{
  const h=await harness();h.account.getBalance=async()=>({...ready(),value:[{currency:'CNY',balance:0}]});await h.run(0);assert.equal(h.messages.at(-1).state,'failed');
  h.account.getBalance=async()=>{throw new Error('synthetic network');};await h.run(30000);assert.equal(h.messages.at(-1).state,'failed');h.poller.dispose();
});

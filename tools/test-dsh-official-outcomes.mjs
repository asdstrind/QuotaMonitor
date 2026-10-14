// Exercise the installed official getBalance implementation without constructing
// an account service, reading a credential store, or making a network request.
import assert from 'node:assert/strict';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const runtime=process.env.DSH_TEST_RUNTIME;
if(!runtime)throw new Error('DSH_TEST_RUNTIME required');
const {PlatformAccount}=await import(pathToFileURL(path.join(runtime,'@deepseek-ai/dsh-deepseek-account-platform/lib/index.js')).href);
let expired=0;
const account={origin:'https://synthetic-balance.invalid',detailsLifetime:new AbortController(),balanceTimeout:20,
  readCurrentGrant:async()=>({token:'synthetic-not-a-credential'}),detailHeaders:()=>({}),
  getDetail:PlatformAccount.prototype.getDetail,expireCredential:async()=>expired++};
const client={version:'0.2.0-rc.2',locale:'en-US',timezoneOffsetSeconds:0};
const originalFetch=globalThis.fetch;
const query=()=>PlatformAccount.prototype.getBalance.call(account,client);
try{
  globalThis.fetch=async(url)=>{assert.equal(new URL(url).hostname,'synthetic-balance.invalid');return new Response('',{status:202});};
  assert.deepEqual(await query(),{status:'failed'});
  globalThis.fetch=async()=>new Response('{"status":"pending"}',{status:202});
  assert.deepEqual(await query(),{status:'failed'});
  const envelope={code:0,data:{biz_code:0,biz_data:{normal_wallets:[{currency:'CNY',balance:'8.1234567890123456789'}],bonus_wallets:[]}}};
  globalThis.fetch=async()=>Response.json(envelope,{status:200});
  assert.equal((await query()).value[0].balance,'8.1234567890123456789');
  globalThis.fetch=async()=>{throw new Error('synthetic offline');};
  assert.deepEqual(await query(),{status:'failed'});assert.equal(expired,0);
  globalThis.fetch=async(_,options)=>new Promise((_,reject)=>{options.signal.addEventListener('abort',()=>reject(new Error('synthetic timeout')),{once:true});});
  // Official timers are unref'ed; keep this isolated test alive for the timeout.
  const keepAlive=setTimeout(()=>{},200);
  try{assert.deepEqual(await query(),{status:'failed'});}finally{clearTimeout(keepAlive);}
  globalThis.fetch=async()=>{account.detailsLifetime=new AbortController();return Response.json(envelope);};
  assert.equal(await query(),null);assert.equal(expired,0);
  console.log('Installed official getBalance: HTTP 202 empty/pending, valid wallets, offline, timeout, account change PASS (synthetic fetch only).');
}finally{globalThis.fetch=originalFetch;}

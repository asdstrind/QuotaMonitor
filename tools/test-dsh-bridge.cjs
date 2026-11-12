// Synthetic wallets only. No profile, credentials, network or production pipe.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url');
const path=require('node:path');
const fs=require('node:fs');
const root=path.resolve(__dirname,'../bridge/dsh-balance-bridge');
const projection=()=>import(pathToFileURL(path.join(root,'lib/projection.js')).href);
test('wallet projection preserves arbitrary decimal precision and strips unrelated account fields',async()=>{
  const {wallets}=await projection();
  assert.deepEqual(wallets([{currency:'USD',balance:'1.123456789012345678901234567890',identity:'synthetic'}]),[{currency:'USD',balance:'1.123456789012345678901234567890'}]);
});
for(const [i,value] of [null,{},[{currency:'USD',balance:0}],[{currency:'EUR',balance:'1'}],[{currency:'CNY',balance:'NaN'}],[{currency:'USD',balance:'1e999999999999'}],[{currency:'USD',balance:'١٢'}],Array(9).fill({currency:'USD',balance:'1'}),[{currency:'USD',balance:'1'.repeat(257)}]].entries()) {
  test('malformed wallets fail without fabricating zero '+i,async()=>{const {wallets}=await projection();assert.throws(()=>wallets(value));});
}
test('only necessary Host peers remain; no remote balance publication entry point',()=>{
  const manifest=JSON.parse(fs.readFileSync(path.join(root,'package.json')));
  assert.equal(manifest.engines.dsh,'>=0.2.0-rc.2 <0.3.0-0');
  assert.deepEqual(Object.keys(manifest.peerDependencies).sort(),['@deepseek-ai/cordis','@deepseek-ai/dsh-app-boot','@deepseek-ai/dsh-credentials']);
  const source=fs.readFileSync(path.join(root,'lib/index.js'),'utf8');
  assert(!source.includes('TypertRemoteService'));assert(!source.includes('publish(snapshot)'));
});
test('amounts with trailing newlines are rejected rather than accepted by regex end anchors',async()=>{
 const {wallets}=await projection();for(const balance of ['1\n','1\r','1\r\n'])assert.throws(()=>wallets([{currency:'USD',balance}]));
});

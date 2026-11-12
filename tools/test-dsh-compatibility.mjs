// Run the actual installed Host checker, without booting a profile/account.
import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
if(!process.env.DSH_TEST_RUNTIME)throw new Error('DSH_TEST_RUNTIME required (installed official runtime node_modules)');
const {evaluatePluginCompatibility:check,getDshRuntimeVersion}=await import(pathToFileURL(path.join(process.env.DSH_TEST_RUNTIME,'@deepseek-ai/dsh-app-boot/lib/index.js')));
const manifest=JSON.parse(fs.readFileSync(new URL('../bridge/dsh-balance-bridge/package.json',import.meta.url)));
for(const version of ['0.2.0-rc.2','0.2.1-rc.1','0.2.1-rc.2','0.2.1-rc.5','0.2.1-rc.6','0.2.2-rc.1','0.2.2','0.2.99'])
  test('actual checker '+getDshRuntimeVersion()+' accepts version scenario '+version,()=>assert.equal(check(manifest,{},version),undefined));
for(const version of ['0.2.0-rc.1','0.1.9','0.3.0-rc.1','0.3.0','1.0.0'])
  test('actual checker rejects out-of-branch '+version,()=>{assert(check(manifest,{},version));assert.equal(check(manifest,{},version).exempted,false);});
test('engines.dsh takes priority over DSH peers in actual checker',()=>{
  assert.equal(check({...manifest,peerDependencies:{'@deepseek-ai/dsh-app-boot':'0.0.1'}},{},'0.2.1-rc.5'),undefined);
  assert(check({...manifest,engines:{dsh:'<0.2.0-0'}},{},'0.2.1-rc.5'));
});
test('peer fallback covers existing and next rc; historical whitelist reproduces failure',()=>{
  const {engines,...fallback}=manifest;
  for(const v of ['0.2.0-rc.2','0.2.1-rc.1','0.2.1-rc.2','0.2.1-rc.5','0.2.1-rc.6'])assert.equal(check(fallback,{},v),undefined);
  assert(check({...fallback,peerDependencies:{'@deepseek-ai/dsh-app-boot':'0.2.0-rc.2 || 0.2.1-rc.1 || 0.2.1-rc.2'}},{},'0.2.1-rc.5'));
});

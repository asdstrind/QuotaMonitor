// Opaque RSA-OAEP envelope from the UI reader. Never pass a plaintext balance.
const net=require('node:net');
const encrypted=process.argv[2];
if(!encrypted||!/^[A-Za-z0-9+/=]{344}$/.test(encrypted))throw new Error('RSA-2048 encrypted envelope required');
const socket=net.createConnection('\\\\.\\pipe\\QuotaMonitor.Verify165');
socket.on('error',()=>{console.log('comparison IPC failed');process.exitCode=1;});
socket.on('connect',()=>socket.write(JSON.stringify({encrypted})+'\n'));
socket.on('data',b=>{const result=b.toString().trim();console.log('Official page display equals receiver:',result==='true');if(result!=='true')process.exitCode=1;socket.end();});
socket.setTimeout(3000,()=>{socket.destroy();process.exitCode=1;});

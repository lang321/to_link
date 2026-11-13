// Run in the isolated Windows test build beside the real relay, client and browser paste module.
const { createRelay } = require('./server');
const { createTextInput } = require('./public/text-input');
const { hashPassword } = require('./auth');
const { randomBytes } = require('crypto');
const { spawn } = require('child_process');
const { once } = require('events');
const { WebSocket } = require('ws');
(async () => {
  const origin='http://localhost', password=randomBytes(20).toString('hex'),token=randomBytes(32).toString('hex');
  const relay=createRelay({origins:[origin],devices:[{id:'fixture',token,credential:await hashPassword(password)}]});
  relay.server.listen(0,'127.0.0.1');await once(relay.server,'listening');
  let viewer, child;
  try {
    const port=relay.server.address().port,host='127.0.0.1:'+port;
    const r=await fetch('http://'+host+'/api/login',{method:'POST',headers:{Origin:origin},body:JSON.stringify({deviceId:'fixture',password})});
    if(!r.ok)throw Error('Login failed');
    viewer=new WebSocket('ws://'+host+'/ws/viewer?device=fixture',{headers:{Cookie:r.headers.get('set-cookie').split(';')[0],Origin:origin}});
    let issued=false; const send=m=>viewer.send(JSON.stringify(m));
    viewer.on('message',(raw,binary)=>{
      if(binary){send({type:'ack',bytes:raw.length});return;}
      const m=JSON.parse(raw);
      if(m.type==='status' && m.device?.filePaste && !issued){
        issued=true;
        createTextInput({enabled:()=>true,supportsPaste:()=>true,send,message:console.error}).paste({preventDefault(){},clipboardData:{getData:()=> 'ToLink 端到端\n原生粘贴 😀'}});
      }
    });
    await once(viewer,'open');
    child=spawn(require('path').join(__dirname,'NativePasteTest.exe'),[String(port),token,'--restore-plaintext'],{stdio:['ignore','pipe','inherit']});
    const ended=once(child,'exit');
    const chunks=[Buffer.alloc(65536),Buffer.alloc(4464),Buffer.from('hello')];for(let i=0;i<70000;i++)chunks[i<65536?0:1][i%65536]=i%251;
    const id=randomBytes(16).toString('hex');let step=0,complete=false;
    child.stdout.on('data',data=>{process.stdout.write(data);if(data.toString().includes('TEXT_OK'))send({type:'filesBegin',id,files:[{name:'样例.bin',size:70000},{name:'empty.txt',size:0},{name:'sample.txt',size:5}]});});
    viewer.on('message',(raw,binary)=>{if(binary)return;const m=JSON.parse(raw);if(m.type!=='filesAck')return;if(!m.ok){console.error(m.message);return;}
      if(step<chunks.length)viewer.send(Buffer.concat([Buffer.from(id),chunks[step++]]));
      else if(step++===chunks.length)send({type:'filesFinish',id});else complete=true;
    });
    const [code]=await ended;if(code!==0 || !complete)throw Error('Private clipboard test failed');
  } finally {if(viewer)viewer.terminate();await relay.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});

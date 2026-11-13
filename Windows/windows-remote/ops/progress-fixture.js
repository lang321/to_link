// REQ-REMOTE-20261111-01: local browser fixture; no real computer or clipboard is modified.
// Start from repository root: node Windows/windows-remote/ops/progress-fixture.js
const { createRelay } = require('../server');
const { hashPassword } = require('../auth');
const { WebSocket } = require('ws');
const fs = require('node:fs');
(async () => {
  const origin='http://127.0.0.1:19091', token='progress-fixture-token-'.repeat(3);
  const relay=createRelay({origins:[origin],devices:[{id:'123456789',token,credential:await hashPassword('progress-test-only')}]});
  await new Promise(resolve=>relay.server.listen(19091,'127.0.0.1',resolve));
  const agent=new WebSocket('ws://127.0.0.1:19091/ws/agent?device=123456789',{headers:{Authorization:'Bearer '+token}});
  const send=m=>agent.send(JSON.stringify(m));
  let batch=null, timer;
  const frame=fs.readFileSync('output/playwright/progress-files/desktop.jpg');
  agent.on('open',()=>{send({type:'hello',name:'Transfer fixture',width:1200,height:700,filePaste:true,nativePaste:true,streamingFilePaste:process.env.NATIVE_PASTE==='1'});timer=setInterval(()=>{send({type:'agentHeartbeat'});agent.send(frame);},1000);});
  agent.on('message',(raw,binary)=>{
    if(binary){
      const current=batch;if(!current)return;
      setTimeout(()=>{
        if(batch!==current)return;
        if(current.fail){send({type:'filesAck',id:current.id,ok:false,received:current.received,message:'测试：远端磁盘写入失败'});return;}
        current.received+=raw.length-32;
        send({type:'filesAck',id:current.id,ok:true,received:current.received});
      },500);
      return;
    }
    const m=JSON.parse(raw);
    if(m.type==='viewing'&&m.active)send({type:'stream',format:'jpeg',codec:'jpeg'});
    if(m.type==='probe')send({type:'probeAck',nonce:m.nonce});
    if(m.type==='filesBegin'){
      batch={id:m.id,received:0,fail:m.files[0].name.startsWith('failure'),pasted:!m.files[0].name.startsWith('empty')};
      send({type:'filesAck',id:m.id,ok:true,received:0,pasted:m.streaming===true});
    }
    if(m.type==='filesFinish'){
      const current=batch;
      setTimeout(()=>{if(batch===current&&current)send({type:'filesAck',id:m.id,ok:true,received:current.received,pasted:current.pasted});},2000);
    }
    if(m.type==='filesCancel')batch=null;
  });
  process.on('SIGTERM',async()=>{clearInterval(timer);agent.terminate();await relay.close();process.exit(0);});
  console.log('Progress fixture ready: '+origin);
})().catch(e=>{console.error(e);process.exitCode=1;});

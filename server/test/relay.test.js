'use strict';
// REQ-REMOTE-20261105-01: authentication, device isolation and bounded frame delivery.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { once } = require('node:events');
const { WebSocket } = require('ws');
const { createRelay } = require('../server');
const { validCursor } = require('../server');
const { hashPassword } = require('../auth');
const secret = 't'.repeat(40), password = 'test-password-long';
const origin = 'http://localhost';
test('cursor metadata validates shapes and never accepts arbitrary CSS', () => {
  const m = { type: 'cursor', x: .3, y: .6, visible: true, shape: 'text' };
  assert.equal(validCursor(m), true);
  assert.equal(validCursor({ ...m, shape: 'url(https://invalid.test)' }), false);
  assert.equal(validCursor({ ...m, x: 2 }), false);
});
async function fixture(t, options = {}, elapsed) {
  const credential = await hashPassword(password);
  const relay = createRelay({ enrollmentToken: 'e'.repeat(40), origins: [origin], devices: [{ id: 'a', token: secret, credential }, { id: 'b', token: 'b'.repeat(40), credential }], ...options }, elapsed);
  relay.server.listen(0, '127.0.0.1'); await once(relay.server, 'listening');
  t.after(() => relay.close());
  const base = `http://127.0.0.1:${relay.server.address().port}`;
  const login = await fetch(base + '/api/login', { method: 'POST', headers: { Origin: origin }, body: JSON.stringify({ deviceId: 'a', password }) });
  const cookie = login.headers.get('set-cookie').split(';')[0];
  async function connect(role, id, token = secret, query = '') {
    const ws = new WebSocket(base.replace('http:', 'ws:') + `/ws/${role}?device=${id}${query}`, { headers: role === 'agent' ? { Authorization: `Bearer ${token}` } : { Cookie: cookie, Origin: origin } });
    const messages = []; ws.on('message', (data, binary) => messages.push(binary ? data : JSON.parse(data.toString())));
    ws.messages = messages; await once(ws, 'open'); return ws;
  }
  return { relay, base, cookie, connect };
}
const pause = ms => new Promise(r => setTimeout(r, ms));
async function until(fn) { for (let i = 0; i < 100; i++) { if (fn()) return; await pause(10); } assert.fail('condition timed out'); }
test('rejects missing auth, bad origin and incorrect device key', async t => {
  const f = await fixture(t);
  assert.equal((await fetch(f.base + '/api/status')).status, 401);
  assert.equal((await fetch(f.base + '/api/login', { method: 'POST', headers: { Origin: 'http://evil.test' }, body: JSON.stringify({ deviceId: 'a', password }) })).status, 403);
  assert.equal((await fetch(f.base + '/api/login', { method: 'POST', headers: { Origin: origin }, body: JSON.stringify({ password: 'wrong' }) })).status, 401);
  await assert.rejects(f.connect('agent', 'a', 'incorrect'), /401/);
  await assert.rejects(f.connect('agent', 'b', secret), /401/);
  await assert.rejects(f.connect('viewer', 'b'), /401/);
  const visible = await (await fetch(f.base + '/api/status', { headers: { Cookie: f.cookie } })).json();
  assert.deepEqual(visible.devices.map(d => d.id), ['a']);
});
test('routes frames and controls only to their device; rejects duplicate controller', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), b = await f.connect('agent', 'b', 'b'.repeat(40));
  await assert.rejects(f.connect('agent', 'a'), /409/);
  a.send(JSON.stringify({ type: 'hello', name: 'a', width: 1600, height: 900 })); b.send(JSON.stringify({ type: 'hello', name: 'b', width: 800, height: 600 }));
  const v = await f.connect('viewer', 'a');
  await assert.rejects(f.connect('viewer', 'a'), /409/);
  await until(() => a.messages.some(m => m.type === 'viewing' && m.active));
  v.send(JSON.stringify({ type: 'text', text: '中文验收' }));
  await until(() => a.messages.some(m => m.type === 'text'));
  assert.equal(b.messages.some(m => m.type === 'text'), false);
  const frame = Buffer.from([0xff, 0xd8, 0xff, 0xd9]); a.send(frame);
  await until(() => v.messages.some(Buffer.isBuffer));
  a.send(frame); a.send(frame); await pause(40);
  assert.equal(v.messages.filter(Buffer.isBuffer).length, 1, 'no new frames before browser ack');
  v.send(JSON.stringify({ type: 'ack' })); await pause(20); a.send(frame);
  await until(() => v.messages.filter(Buffer.isBuffer).length === 2);
  v.close(); await once(v, 'close');
  await until(() => a.messages.some(m => m.type === 'release'));
  assert.equal(b.messages.some(m => m.type === 'release'), false);
});
test('invalid input closes controller and releases held keys', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), v = await f.connect('viewer', 'a');
  v.send(JSON.stringify({ type: 'move', x: 9, y: 0 }));
  const [code] = await once(v, 'close'); assert.equal(code, 1008);
  await until(() => a.messages.some(m => m.type === 'release'));
});
test('logout revokes existing websocket and blocks further access', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), v = await f.connect('viewer', 'a');
  const closed = once(v, 'close');
  await fetch(f.base + '/api/logout', { method: 'POST', headers: { Origin: origin, Cookie: f.cookie } });
  assert.equal((await closed)[0], 4001);
  assert.equal((await fetch(f.base + '/api/status', { headers: { Cookie: f.cookie } })).status, 401);
  await until(() => a.messages.some(m => m.type === 'release'));
});

test('password change revokes device sessions; old password fails and new password succeeds', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), v = await f.connect('viewer', 'a');
  const closed = once(v, 'close');
  const updated = await fetch(f.base + '/api/device/password', { method: 'POST', headers: { Authorization: 'Bearer ' + secret }, body: JSON.stringify({ deviceId: 'a', password: 'new-device-password' }) });
  assert.equal(updated.status, 200); assert.equal((await closed)[0], 4001);
  await until(() => a.messages.some(m => m.type === 'release'));
  const login = password => fetch(f.base + '/api/login', { method: 'POST', headers: { Origin: origin }, body: JSON.stringify({ deviceId: 'a', password }) });
  assert.equal((await login(password)).status, 401);
  assert.equal((await login('new-device-password')).status, 200);
  assert.equal((await fetch(f.base + '/api/status', { headers: { Cookie: f.cookie } })).status, 401);
});
test('registration allocates a numeric ID and returns a private agent token', async t => {
  const f = await fixture(t);
  const register = token => fetch(f.base + '/api/register', { method: 'POST', headers: { Authorization: 'Bearer ' + token }, body: JSON.stringify({ name: 'new-client', password: 'independent-password' }) });
  assert.equal((await register('wrong')).status, 401);
  const response = await register('e'.repeat(40)); assert.equal(response.status, 201);
  const d = await response.json(); assert.match(d.deviceId, /^[0-9]{9}$/); assert.equal(d.token.length, 64);
  const agent = await f.connect('agent', d.deviceId, d.token); assert.equal(agent.readyState, 1);
  await assert.rejects(f.connect('viewer', d.deviceId), /401/);
});
test('registered ID and password survive a relay restart without storing plaintext', async t => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tolink-auth-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const config = { stateFile: path.join(dir, 'devices.json'), enrollmentToken: 'e'.repeat(40), origins: [origin] };
  let relay = createRelay(config); relay.server.listen(0, '127.0.0.1'); await once(relay.server, 'listening');
  let base = `http://127.0.0.1:${relay.server.address().port}`;
  const registered = await (await fetch(base + '/api/register', { method: 'POST', headers: { Authorization: 'Bearer ' + config.enrollmentToken }, body: JSON.stringify({ name: 'persistent', password: 'stable-password' }) })).json();
  assert.equal(fs.readFileSync(config.stateFile, 'utf8').includes('stable-password'), false);
  await relay.close();
  relay = createRelay(config); relay.server.listen(0, '127.0.0.1'); await once(relay.server, 'listening'); t.after(() => relay.close());
  base = `http://127.0.0.1:${relay.server.address().port}`;
  const response = await fetch(base + '/api/login', { method: 'POST', headers: { Origin: origin }, body: JSON.stringify({ deviceId: registered.deviceId, password: 'stable-password' }) });
  assert.equal(response.status, 200); assert.equal((await response.json()).deviceId, registered.deviceId);
});
test('video bursts stay ordered; sustained lag restarts only media and preserves input', async t => {
  let clock = 1;
  const f = await fixture(t, {}, () => clock), a = await f.connect('agent', 'a'), v = await f.connect('viewer', 'a', secret, '&recover=1&codec=h265');
  a.send(JSON.stringify({ type: 'hello', name: 'a', width: 1600, height: 900 }));
  a.send(JSON.stringify({ type: 'stream', format: 'mp4', codec: 'h265', mime: 'video/mp4; codecs="hvc1.1.6.L93.B0"', width: 1280, height: 720 }));
  await until(() => v.messages.some(m => m.type === 'stream'));
  const one = Buffer.alloc(160 * 1024, 1), two = Buffer.alloc(160 * 1024, 2);
  a.send(one); a.send(two);
  await until(() => v.messages.filter(Buffer.isBuffer).length === 2);
  assert.equal(v.messages.filter(Buffer.isBuffer)[0][0], 1); assert.equal(v.messages.filter(Buffer.isBuffer)[1][0], 2);
  v.send(JSON.stringify({ type: 'ack', bytes: one.length + two.length })); await pause(20);
  a.send(one); a.send(one); a.send(one);
  await until(() => v.messages.filter(Buffer.isBuffer).length === 5);
  a.send(one); await until(() => v.messages.filter(Buffer.isBuffer).length === 6);
  clock += 2200; a.send(one); await until(() => v.messages.some(m => m.type === 'videoReset'));
  a.send(two); v.send(JSON.stringify({type:'move',x:.2,y:.2}));
  await until(() => a.messages.some(m=>m.type==='move'));
  assert.equal(v.messages.filter(Buffer.isBuffer).length,6);
  v.send(JSON.stringify({type:'videoReady'}));
  await until(()=>a.messages.filter(m=>m.type==='viewing' && m.active).length===2);
  a.send(JSON.stringify({type:'stream',format:'mp4',codec:'h265',mime:'video/mp4; codecs="hvc1.1.6.L93.B0"'}));
  a.send(two); await until(()=>v.messages.filter(Buffer.isBuffer).length===7);
  assert.equal(v.readyState,WebSocket.OPEN);
});
test('cursor movement is isolated by device and does not count as video traffic', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), b = await f.connect('agent', 'b', 'b'.repeat(40)), v = await f.connect('viewer', 'a');
  const cursor = { type: 'cursor', x: .25, y: .75, shape: 'text', visible: true };
  b.send(JSON.stringify({ ...cursor, x: .8 })); a.send(JSON.stringify(cursor));
  await until(() => v.messages.some(m => m.type === 'cursor'));
  assert.deepEqual(v.messages.filter(m => m.type === 'cursor'), [cursor]);
  assert.equal(f.relay.status().find(d => d.id === 'a').bytes, 0);
});
test('native paste reaches only the capable authenticated device', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), v = await f.connect('viewer', 'a');
  const paste = { type: 'paste', text: '中文\n😀' };
  v.send(JSON.stringify(paste));
  await until(() => v.messages.some(m => m.type === 'error'));
  assert.equal(a.messages.some(m => m.type === 'paste'), false);
  a.send(JSON.stringify({ type:'hello', name:'host', width:1600, height:900, nativePaste:true }));
  await until(() => v.messages.some(m => m.device?.nativePaste));
  v.send(JSON.stringify(paste));
  await until(() => a.messages.some(m => m.type === 'paste'));
  assert.deepEqual(a.messages.find(m => m.type === 'paste'),paste);
});
test('file batch forwards bounded binary chunks only to the selected agent and acknowledges commit', async t => {
 const f=await fixture(t), a=await f.connect('agent','a'), b=await f.connect('agent','b','b'.repeat(40)),v=await f.connect('viewer','a');
 const send=(ws,m)=>ws.send(JSON.stringify(m)), id='1'.repeat(32);
 send(a,{type:'hello',name:'a',width:800,height:600,filePaste:true});
 await until(()=>v.messages.some(m=>m.device?.filePaste));
 send(v,{type:'filesBegin',id,files:[{name:'zero.txt',size:0},{name:'样例.bin',size:3}]});
 await until(()=>a.messages.some(m=>m.type==='filesBegin'));
 send(a,{type:'filesAck',id,ok:true,received:0});
 await until(()=>v.messages.some(m=>m.type==='filesAck' && m.ok));
 const chunk=Buffer.concat([Buffer.from(id),Buffer.from([0,255,1])]);v.send(chunk);
 await until(()=>a.messages.some(Buffer.isBuffer));assert.deepEqual(a.messages.find(Buffer.isBuffer),chunk);
 send(a,{type:'filesAck',id,ok:true,received:3});await until(()=>v.messages.some(m=>m.received===3));
 send(v,{type:'filesFinish',id});await until(()=>a.messages.some(m=>m.type==='filesFinish'));
 send(a,{type:'filesAck',id,ok:true,received:3,pasted:true});await until(()=>v.messages.some(m=>m.pasted===true));
 assert.equal(b.messages.some(m=>Buffer.isBuffer(m)||m.type==='filesBegin'),false);
});
test('file batches reject unsupported agents, oversized manifests and unsolicited bytes',async t=>{
 const f=await fixture(t),a=await f.connect('agent','a'),v=await f.connect('viewer','a'),id='2'.repeat(32);
 v.send(JSON.stringify({type:'filesBegin',id,files:[{name:'a',size:1}]}));await until(()=>v.messages.some(m=>m.type==='filesAck'&&!m.ok));
 a.send(JSON.stringify({type:'hello',name:'a',width:800,height:600,filePaste:true}));await until(()=>v.messages.some(m=>m.device?.filePaste));
 v.send(JSON.stringify({type:'filesBegin',id,files:[{name:'big',size:104857601}]}));await until(()=>v.messages.filter(m=>m.type==='filesAck'&&!m.ok).length===2);
 const closed=once(v,'close');v.send(Buffer.alloc(33));assert.equal((await closed)[0],1008);
 assert.equal(a.messages.some(Buffer.isBuffer),false);
});

test('browser pong keeps viewer alive without JS timers; HTTP session expiry still closes it', async t=>{
  let clock=1;
  const f=await fixture(t,{heartbeatMs:30,sessionMs:650},()=>clock),v=await f.connect('viewer','a');
  const pongSent=once(v,'ping'); clock+=25000; await pongSent; await pause(50);
  clock+=25000; await pause(60); assert.equal(v.readyState,WebSocket.OPEN);
  const [code]=await once(v,'close');assert.equal(code,4001);
});
test('reconnect from the same page can replace its stale socket, different pages cannot',async t=>{
  const f=await fixture(t); const page='a'.repeat(32);
  const v=await f.connect('viewer','a',secret,'&page='+page);
  await assert.rejects(f.connect('viewer','a',secret,'&page='+'b'.repeat(32)),/409/);
  const closed=once(v,'close'); const replacement=await f.connect('viewer','a',secret,'&page='+page);
  await closed;assert.equal(replacement.readyState,WebSocket.OPEN);
});
test('decoder recovery waits for a running file batch before asking the encoder to restart', async t=>{
  const f=await fixture(t), a=await f.connect('agent','a'), v=await f.connect('viewer','a',secret,'&recover=1&codec=h265');
  const send=(ws,m)=>ws.send(JSON.stringify(m)),id='9'.repeat(32);
  send(a,{type:'hello',name:'a',width:1600,height:900,filePaste:true});
  send(a,{type:'stream',format:'mp4',codec:'h265',mime:'video/mp4; codecs="hvc1.1.6.L120.B0"'});
  await until(()=>v.messages.some(m=>m.type==='stream'));
  send(v,{type:'filesBegin',id,files:[{name:'empty.txt',size:0}]});
  await until(()=>a.messages.some(m=>m.type==='filesBegin'));
  send(a,{type:'filesAck',id,ok:true,received:0});await until(()=>v.messages.some(m=>m.type==='filesAck'));
  send(v,{type:'videoFault'}); await until(()=>v.messages.some(m=>m.type==='videoReset'));
  send(v,{type:'videoReady'});send(v,{type:'probe',nonce:1});
  await until(()=>a.messages.some(m=>m.type==='probe'));
  assert.equal(a.messages.filter(m=>m.type==='viewing' && m.active).length,1);
  send(v,{type:'filesFinish',id});await until(()=>a.messages.some(m=>m.type==='filesFinish'));
  send(a,{type:'filesAck',id,ok:true,received:0,pasted:true});
  await until(()=>a.messages.filter(m=>m.type==='viewing' && m.active).length===2);
  assert.equal(v.readyState,WebSocket.OPEN);
});
test('a click with unusable coordinates releases held input and preserves the viewer', async t => {
  const f = await fixture(t), a = await f.connect('agent','a'), v = await f.connect('viewer','a');
  const button = { type:'button',button:0,down:true,x:.4,y:.6 };
  for (const message of [button,{...button,down:false,x:null,y:null},{type:'move',x:null,y:null},
                         {...button,x:900,y:600},button,{...button,down:false}]) v.send(JSON.stringify(message));
  await until(() => a.messages.filter(m => m.type === 'button').length === 3);
  assert.deepEqual(a.messages.filter(m => ['button','release','move'].includes(m.type)),
                   [button,{type:'release'},{type:'release'},button,{...button,down:false}]);
  assert.equal(v.readyState,WebSocket.OPEN);
});

test('wake requires device capability, stays isolated, throttles repeats and forwards result', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), b = await f.connect('agent', 'b', 'b'.repeat(40));
  a.send(JSON.stringify({ type: 'hello', name: 'a', width: 800, height: 600 }));
  const v = await f.connect('viewer', 'a');
  await until(() => v.messages.some(m => m.type === 'status' && m.device));
  v.send(JSON.stringify({ type: 'wake' }));
  await until(() => v.messages.some(m => m.type === 'wakeResult' && !m.ok));
  assert.equal(a.messages.some(m => m.type === 'wake'), false);
  a.send(JSON.stringify({ type: 'hello', name: 'a', width: 800, height: 600, wake: true }));
  await until(() => v.messages.some(m => m.type === 'status' && m.device?.wake));
  v.send(JSON.stringify({ type: 'wake' }));
  await until(() => a.messages.some(m => m.type === 'wake'));
  v.send(JSON.stringify({ type: 'wake' }));
  await pause(30);
  assert.equal(a.messages.filter(m => m.type === 'wake').length, 1);
  assert.equal(b.messages.some(m => m.type === 'wake'), false);
  a.send(JSON.stringify({ type: 'wakeResult', ok: true, message: 'display awake' }));
  await until(() => v.messages.some(m => m.type === 'wakeResult' && m.ok));
});
test('wake never forwards arbitrary command arguments', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a'), v = await f.connect('viewer', 'a');
  const closed = once(v, 'close');
  v.send(JSON.stringify({ type: 'wake', command: 'anything' }));
  assert.equal((await closed)[0], 1008);
  assert.equal(a.messages.some(m => m.type === 'wake'), false);
});

test('streaming paste negotiates capability, publishes before data and releases cancelled batch', async t => {
  const f = await fixture(t), a = await f.connect('agent', 'a');
  a.send(JSON.stringify({type:'hello',name:'streaming',width:800,height:600,filePaste:true,streamingFilePaste:true}));
  const v=await f.connect('viewer','a');
  await until(()=>v.messages.some(m=>m.device?.streamingFilePaste));
  const id='c'.repeat(32);
  v.send(JSON.stringify({type:'filesBegin',id,streaming:true,files:[{name:'stream.bin',size:4}]}));
  await until(()=>a.messages.some(m=>m.type==='filesBegin'&&m.streaming));
  a.send(JSON.stringify({type:'filesAck',id,ok:true,received:0,pasted:true}));
  await until(()=>v.messages.some(m=>m.type==='filesAck'&&m.pasted&&m.received===0));
  v.send(Buffer.concat([Buffer.from(id),Buffer.from('data')]));
  await until(()=>a.messages.some(Buffer.isBuffer));
  a.send(JSON.stringify({type:'filesAck',id,ok:false,received:0,message:'Windows 文件复制已取消'}));
  await until(()=>v.messages.some(m=>m.type==='filesAck'&&!m.ok));
  v.send(JSON.stringify({type:'filesBegin',id:'d'.repeat(32),streaming:true,files:[{name:'retry.txt',size:0}]}));
  await until(()=>a.messages.some(m=>m.type==='filesBegin'&&m.id==='d'.repeat(32)));
});
test('streaming paste cannot be requested from an older agent', async t => {
  const f=await fixture(t),a=await f.connect('agent','a');
  a.send(JSON.stringify({type:'hello',name:'old',width:800,height:600,filePaste:true}));
  const v=await f.connect('viewer','a');
  await until(()=>v.messages.some(m=>m.device?.filePaste));
  v.send(JSON.stringify({type:'filesBegin',id:'e'.repeat(32),streaming:true,files:[{name:'file.txt',size:1}]}));
  await until(()=>v.messages.some(m=>m.type==='filesAck'&&!m.ok));
  assert.equal(a.messages.some(m=>m.type==='filesBegin'),false);
});

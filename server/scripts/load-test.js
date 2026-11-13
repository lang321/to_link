'use strict';
// REQ-REMOTE-20261105-01. Synthetic forwarding benchmark, NOT screen capture/codec/browser testing.
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const { WebSocket } = require('ws');
const config = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const base = process.argv[3] || 'http://10.211.55.3:19002';
const seconds = Number(process.argv[4] || 30), fps = Number(process.argv[6] || 8), frameSize = Number(process.argv[5] || 64) * 1024;
const videoMode = process.argv[7] === 'video';
const origin = config.origins[0];
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
(async () => {
  const sockets = [], timers = [], latencies = [];
  let sent = 0, received = 0, inputs = 0, replies = 0, errors = 0;
  for (const d of config.devices) {
    const login = await fetch(base + '/api/login', { method: 'POST', headers: { Origin: origin }, body: JSON.stringify({ deviceId: d.id, password: d.password }) });
    if (!login.ok) throw Error('Login: ' + login.status);
    const cookie = login.headers.get('set-cookie').split(';')[0];
    const address = base.replace('http:', 'ws:');
    const a = new WebSocket(address + '/ws/agent?device=' + d.id, { headers: { Authorization: 'Bearer ' + d.token } });
    sockets.push(a); a.on('error', () => errors++); await once(a, 'open');
    a.send(JSON.stringify({ type: 'hello', name: d.id, width: 1600, height: 900 }));
    a.on('message', data => {
      const m = JSON.parse(data.toString());
      if (m.type === 'text' && m.text.startsWith('probe:')) { replies++; latencies.push(Date.now() - Number(m.text.slice(6))); }
    });
    const v = new WebSocket(address + '/ws/viewer?device=' + d.id, { headers: { Cookie: cookie, Origin: origin } });
    sockets.push(v); v.on('error', () => errors++);
    v.on('message', (data, binary) => { if (binary) { received++; v.send(JSON.stringify({ type: 'ack', bytes: data.length })); } });
    await once(v, 'open');
    if (videoMode) a.send(JSON.stringify({ type: 'stream', format: 'mp4', codec: 'h265', mime: 'video/mp4; codecs="hvc1.1.6.L120.B0"', width: 1600, height: 900 }));
    const frame = Buffer.alloc(frameSize, 0x55); frame[0] = 0xff; frame[1] = 0xd8; frame[frame.length - 2] = 0xff; frame[frame.length - 1] = 0xd9;
    d._agent = a; d._viewer = v; d._frame = frame;
  }
  const started = performance.now();
  for (const d of config.devices) {
    timers.push(setInterval(() => { if (d._agent.readyState === 1 && d._agent.bufferedAmount < frameSize * 2) { d._agent.send(d._frame); sent++; } }, 1000 / fps));
    timers.push(setInterval(() => { if (d._viewer.readyState === 1) { d._viewer.send(JSON.stringify({ type: 'heartbeat' })); d._viewer.send(JSON.stringify({ type: 'text', text: 'probe:' + Date.now() })); inputs++; } }, 2000));
  }
  console.log(JSON.stringify({ phase: 'running', devices: config.devices.length, fps, frameBytes: frameSize, seconds }));
  await wait(seconds * 1000); timers.forEach(clearInterval); await wait(500);
  const duration = (performance.now() - started - 500) / 1000;
  latencies.sort((a, b) => a - b);
  const result = { devices: config.devices.length, durationSeconds: duration, targetFps: fps, frameBytes: frameSize, sentFrames: sent, receivedFrames: received, deliveryPercent: received / sent * 100, receivedFpsPerDevice: received / duration / config.devices.length, ingressMbps: sent * frameSize * 8 / duration / 1e6, egressMbps: received * frameSize * 8 / duration / 1e6, inputProbes: inputs, inputReplies: replies, inputLatencyP50Ms: latencies[Math.floor(latencies.length * .5)], inputLatencyP95Ms: latencies[Math.floor(latencies.length * .95)], inputLatencyMaxMs: latencies.at(-1), errors };
  console.log(JSON.stringify(result, null, 2));
  const output = path.join(__dirname, '../../dist/load-result.json');
  fs.mkdirSync(path.dirname(output), { recursive: true });
  fs.writeFileSync(output, JSON.stringify(result, null, 2));
  sockets.forEach(s => s.close());
})().catch(e => { console.error(e.message); process.exitCode = 1; });

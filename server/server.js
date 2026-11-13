'use strict';
// REQ-REMOTE-20261105-01: isolated device channels, one controller per device.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { WebSocketServer, WebSocket } = require('ws');
const { hashPassword, verifyPassword, validPassword } = require('./auth');

function equal(a, b) {
  const x = Buffer.from(String(a || '')), y = Buffer.from(String(b || ''));
  return x.length === y.length && crypto.timingSafeEqual(x, y);
}
function validInput(m) {
  if (!m || typeof m !== 'object') return false;
  const position = () => Number.isFinite(m.x) && Number.isFinite(m.y) && m.x >= 0 && m.x <= 1 && m.y >= 0 && m.y <= 1;
  switch (m.type) {
    case 'move': return position();
    case 'button': return position() && [0, 1, 2].includes(m.button) && typeof m.down === 'boolean';
    case 'wheel': return Number.isInteger(m.delta) && Math.abs(m.delta) <= 1200;
    case 'key': return Number.isInteger(m.vk) && m.vk >= 8 && m.vk <= 254 && typeof m.down === 'boolean';
    case 'paste': return typeof m.text === 'string' && m.text.length > 0 && m.text.length <= 1000 && !m.text.includes('\0');
    case 'text': return typeof m.text === 'string' && m.text.length > 0 && m.text.length <= 1000;
    case 'release': return true;
    case 'wake': return Object.keys(m).length === 1;
    case 'probe': return Number.isFinite(m.nonce) && m.nonce >= 0;
    default: return false;
  }
}
// VideoToolbox reports the actual profile and level in its sample description.
function validVideoMime(codec, mime) {
  if (typeof mime !== 'string' || mime.length > 76) return false;
  if (codec === 'h264') return /^video\/mp4; codecs="avc1\.[0-9a-f]{6}"$/i.test(mime);
  return codec === 'h265' && /^video\/mp4; codecs="hvc1\.1\.[0-9a-f]{1,8}\.[LH][0-9]{1,3}(?:\.[0-9a-f]{2}){0,6}"$/i.test(mime);
}
function validCursor(m) {
  return m && m.type === 'cursor' && Number.isFinite(m.x) && Number.isFinite(m.y) && m.x >= 0 && m.x <= 1 && m.y >= 0 && m.y <= 1 && typeof m.visible === 'boolean'
    && ['default', 'text', 'pointer', 'wait', 'progress', 'crosshair', 'ew-resize', 'ns-resize', 'move', 'nesw-resize', 'nwse-resize', 'not-allowed'].includes(m.shape);
}
function createRelay(config, elapsed = () => performance.now()) {
  const channels = new Map();
  function addDevice(d) {
    if (!/^[a-zA-Z0-9_-]{1,64}$/.test(d.id) || !d.token || d.token.length < 32 || !d.credential || channels.has(d.id)) throw new Error('Invalid device configuration');
    channels.set(d.id, { id: d.id, name: d.name || d.id, token: d.token, credential: d.credential, agent: null, viewer: null, device: null, lastFrameAt: 0, stats: { frames: 0, bytes: 0, inputs: 0, dropped: 0 } });
  }
  const initial = config.stateFile && fs.existsSync(config.stateFile) ? JSON.parse(fs.readFileSync(config.stateFile, 'utf8')) : config.devices || [];
  initial.forEach(addDevice);
  function persist() {
    if (!config.stateFile) return;
    const records = [...channels.values()].map(c => ({ id: c.id, name: c.name, token: c.token, credential: c.credential }));
    fs.writeFileSync(config.stateFile + '.tmp', JSON.stringify(records), { mode: 0o600 });
    fs.renameSync(config.stateFile + '.tmp', config.stateFile);
  }
  const dummyCredential = { salt: '00000000000000000000000000000000', hash: '0'.repeat(64) };
  let authenticating = 0;
  const origins = new Set(config.origins || []);
  const sessions = new Map(), attempts = new Map();
  const sessionMs = config.sessionMs || 8 * 3600_000;
  const publicDir = path.join(__dirname, 'public');
  const files = new Map([
    ['/', ['index.html', 'text/html; charset=utf-8']],
    ['/app.js', ['app.js', 'text/javascript; charset=utf-8']],
    ['/geometry.js', ['geometry.js', 'text/javascript; charset=utf-8']],
    ['/text-input.js', ['text-input.js', 'text/javascript; charset=utf-8']],
    ['/style.css', ['style.css', 'text/css; charset=utf-8']]
  ]);
  const send = (ws, value) => { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value)); };
  const status = c => ({ type: 'status', id: c.id, name: c.name, online: !!c.agent, controlling: !!c.viewer, device: c.device, lastFrameAt: c.lastFrameAt, ...c.stats });
  const allStatus = () => [...channels.values()].map(status);
  const sessionId = req => /(?:^|;\s*)tolink=([a-f0-9]{64})(?:;|$)/.exec(req.headers.cookie || '')?.[1];
  const session = req => { const value = sessions.get(sessionId(req)); return value && value.expiry > Date.now() ? value : null; };
  const authenticated = req => !!session(req);
  const allowedOrigin = req => origins.has(req.headers.origin);
  function reply(res, code, data, extra = {}) {
    res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...extra });
    res.end(JSON.stringify(data));
  }
  const server = http.createServer(async (req, res) => {
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('Content-Security-Policy', "default-src 'self'; connect-src 'self'; img-src 'self' blob:; media-src 'self' blob:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
    const pathname = req.url?.split('?')[0];
    if (req.method === 'GET' && pathname === '/health') return reply(res, 200, { ok: true, service: 'tolink-relay' });
    if (req.method === 'POST' && ['/api/login', '/api/register', '/api/device/password'].includes(pathname)) {
      const login = pathname === '/api/login', registration = pathname === '/api/register';
      if (login && !allowedOrigin(req)) return reply(res, 403, { error: '访问来源不允许' });
      const ip = req.socket.remoteAddress;
      function limited(key, limit) {
        let a = attempts.get(key);
        if (!a || Date.now() > a.until) { a = { count: 0, until: Date.now() + 60_000 }; attempts.set(key, a); }
        return ++a.count > limit;
      }
      if (limited(ip, 300) || authenticating >= 8) return reply(res, 429, { error: '请求过多，请稍后重试' });
      req.setTimeout(5000, () => req.destroy());
      authenticating++;
      try {
        const chunks = []; let size = 0;
        for await (const chunk of req) { size += chunk.length; if (size > 2048) return reply(res, 413, { error: '请求过大' }); chunks.push(chunk); }
        const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        const deviceId = String(body.deviceId || '').replace(/\s/g, '');
        if (limited(ip + ':' + (registration ? 'register' : deviceId), registration ? 150 : 10)) return reply(res, 429, { error: '尝试过多，请一分钟后重试' });
        if (registration) {
          if (!config.enrollmentToken || !equal(req.headers.authorization, `Bearer ${config.enrollmentToken}`)) return reply(res, 401, { error: '注册凭据无效' });
          if (!validPassword(body.password)) return reply(res, 400, { error: '密码需为 8～128 个字符' });
          const credential = await hashPassword(body.password);
          if (channels.size >= (config.maxDevices || 1000)) return reply(res, 409, { error: '设备数量达到上限' });
          let id; do { id = String(crypto.randomInt(100000000, 1000000000)); } while (channels.has(id));
          const token = crypto.randomBytes(32).toString('hex');
          addDevice({ id, token, name: String(body.name || id).slice(0, 80), credential });
          try { persist(); } catch (e) { channels.delete(id); throw e; }
          console.log('[ToLink] device registered: ' + id);
          return reply(res, 201, { deviceId: id, token });
        }
        const c = channels.get(deviceId);
        if (!login) {
          if (!c || !equal(req.headers.authorization, `Bearer ${c.token}`)) return reply(res, 401, { error: '设备凭据无效' });
          if (!validPassword(body.password)) return reply(res, 400, { error: '密码需为 8～128 个字符' });
          const credential = await hashPassword(body.password), previous = c.credential;
          c.credential = credential;
          try { persist(); } catch (e) { c.credential = previous; throw e; }
          for (const [id, value] of sessions) if (value.deviceId === c.id) sessions.delete(id);
          send(c.agent, { type: 'release' }); send(c.agent, { type: 'viewing', active: false });
          if (c.viewer) c.viewer.close(4001, 'Password changed');
          return reply(res, 200, { ok: true });
        }
        const credential = c ? c.credential : dummyCredential;
        const accepted = await verifyPassword(body.password, credential);
        if (!c || !accepted || c.credential !== credential) return reply(res, 401, { error: '设备 ID 或密码错误' });
        const oldId = sessionId(req); sessions.delete(oldId);
        for (const other of channels.values()) if (other.viewer && other.viewer.sessionId === oldId) other.viewer.close(4001, 'Switched device');
        const id = crypto.randomBytes(32).toString('hex');
        if (sessions.size >= 4096) return reply(res, 503, { error: '会话已满，请稍后重试' });
        sessions.set(id, { expiry: Date.now() + sessionMs, deviceId: c.id });
        return reply(res, 200, { ok: true, deviceId: c.id }, { 'Set-Cookie': `tolink=${id}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${Math.floor(sessionMs / 1000)}${config.secureCookie ? '; Secure' : ''}` });
      } catch (e) { console.log('[ToLink] authentication request failed: ' + e.name); return reply(res, 400, { error: '请求无法处理，请检查配置或稍后重试' }); }
      finally { authenticating--; }
    }
    if (req.method === 'POST' && pathname === '/api/logout') {
      if (!allowedOrigin(req)) return reply(res, 403, { error: '访问来源不允许' });
      const id = sessionId(req);
      sessions.delete(id);
      for (const c of channels.values()) if (c.viewer && c.viewer.sessionId === id) c.viewer.close(4001, 'Logged out');
      return reply(res, 200, { ok: true }, { 'Set-Cookie': 'tolink=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0' });
    }
    if (req.method === 'GET' && pathname === '/api/status') {
      if (!authenticated(req)) return reply(res, 401, { error: '请登录' });
      const channel = channels.get(session(req).deviceId);
      const ownPage = !!channel.viewer?.pageId && channel.viewer.sessionId === sessionId(req) && channel.viewer.pageId === req.headers['x-tolink-page'];
      return reply(res, 200, { devices: [status(channel)], ownPage });
    }
    if (req.method === 'GET' && files.has(pathname)) {
      const [file, type] = files.get(pathname);
      let content;
      try { content = fs.readFileSync(path.join(publicDir, file)); }
      catch (e) { console.error('[ToLink] static file unavailable: ' + file + ' ' + e.code); return reply(res, 503, { error: '页面文件暂不可用' }); }
      res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-store' });
      return res.end(content);
    }
    return reply(res, 404, { error: 'Not found' });
  });
  // Preserve the control socket. Resume from a new encoder init segment after the
  // browser discards the old decoder queue. Restarting the legacy encoder also
  // resets its file receiver, so defer that command until any active batch ends.
  function restartVideoIfReady(c) {
    const v = c.viewer;
    if (!c.agent || !v || v.recovery !== 'ready' || v.batch) return;
    v.recovery = 'starting';
    send(c.agent, { type: 'viewing', active: true, codec: v.codec });
  }
  function resetVideo(c, reason) {
    const v = c.viewer;
    if (!v || v.recovery || v.readyState !== WebSocket.OPEN) return;
    if (!v.canRecover) { v.close(4002, reason); return; }
    v.recovery = 'draining';
    console.log('[ToLink] video resync ' + c.id + ' pending=' + v.videoPending + ' buffered=' + v.bufferedAmount + ' reason=' + reason);
    send(v, { type: 'videoReset', codec: v.codec });
  }
  const wss = new WebSocketServer({ noServer: true, maxPayload: 4 * 1024 * 1024, perMessageDeflate: false });
  server.on('upgrade', (req, socket, head) => {
    socket.on('error', () => {});
    const reject = code => { socket.end(`HTTP/1.1 ${code}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`); };
    const url = new URL(req.url, 'http://localhost');
    const role = url.pathname === '/ws/agent' ? 'agent' : url.pathname === '/ws/viewer' ? 'viewer' : null;
    const c = channels.get(url.searchParams.get('device'));
    if (!c) return reject('404 Not Found');
    if (!role) return reject('404 Not Found');
    if (role === 'agent' && !equal(req.headers.authorization, `Bearer ${c.token}`)) return reject('401 Unauthorized');
    if (role === 'viewer' && (!authenticated(req) || session(req).deviceId !== c.id || !allowedOrigin(req))) return reject('401 Unauthorized');
    const pageId = url.searchParams.get('page');
    const samePage = role === 'viewer' && c.viewer && /^[0-9a-f]{32}$/.test(pageId || '') && c.viewer.pageId === pageId && c.viewer.sessionId === sessionId(req);
    if ((role === 'agent' && c.agent) || (role === 'viewer' && c.viewer && !samePage)) return reject('409 Conflict');
    wss.handleUpgrade(req, socket, head, ws => {
      ws.lastActivity = elapsed(); ws.role = role;
      ws.label = role + ':' + c.id;
      ws.on('pong', () => { ws.lastActivity = elapsed(); });
      ws.on('error', e => console.log('[ToLink] socket error ' + ws.label + ': ' + e.message));
      ws.on('close', (code, reason) => console.log('[ToLink] close ' + ws.label + ': ' + code + ' ' + reason.toString().slice(0, 100)));
      if (role === 'agent') {
        c.agent = ws; c.device = null; c.stream = null; c.lastFrameAt = 0;
        let sourceStart = Date.now(), sourceFrames = 0, sourceBytes = 0;
        console.log('[ToLink] agent connected: ' + c.id);
        send(c.agent, { type: 'viewing', active: !!c.viewer, codec: c.viewer?.codec || 'jpeg' });
        send(c.viewer, status(c));
        ws.on('message', (data, binary) => {
          ws.lastActivity = elapsed();
          if (binary) {
            const video = c.stream?.format === 'mp4';
            if (!c.device || data.length < 4 || (!video && (data[0] !== 0xff || data[1] !== 0xd8))) return ws.close(1008, 'Invalid frame');
            if (Date.now() - sourceStart >= 1000) { sourceStart = Date.now(); sourceFrames = 0; sourceBytes = 0; }
            sourceBytes += data.length;
            if (++sourceFrames > 60 || sourceBytes > 8 * 1024 * 1024) return ws.close(1008, 'Source rate limit');
            c.stats.frames++; c.stats.bytes += data.length; c.lastFrameAt = Date.now();
            if (video) {
              if (c.viewer?.readyState === WebSocket.OPEN) {
                const v = c.viewer;
                if (v.recovery) { c.stats.dropped++; return; }
                const pending = v.videoPending + data.length;
                const stalled = v.videoPending > 0 && elapsed() - v.lastVideoAck > 2000;
                if (pending > 4 * 1024 * 1024 + 65536 || (stalled && (pending > 512 * 1024 || v.bufferedAmount > 256 * 1024))) {
                  c.stats.dropped++;
                  return resetVideo(c, 'video backlog');
                }
                if (!v.videoPending) v.lastVideoAck = elapsed();
                c.viewer.videoPending += data.length; c.viewer.send(data, { binary: true });
              }
              return;
            }
            // Only one unacknowledged JPEG: slow browsers never accumulate stale desktop frames.
            if (c.viewer?.readyState === WebSocket.OPEN && !c.viewer.recovery && !c.viewer.framePending && c.viewer.bufferedAmount < 1024 * 1024) {
              c.viewer.framePending = elapsed(); c.viewer.send(data, { binary: true });
            } else if (c.viewer) c.stats.dropped++;
            return;
          }
          try {
            if (data.length > 4096) throw new Error('oversize');
            const m = JSON.parse(data.toString());
            if (m.type === 'hello' && typeof m.name === 'string' && Number.isInteger(m.width) && Number.isInteger(m.height) && m.width > 0 && m.width <= 32768 && m.height > 0 && m.height <= 32768) {
              c.device = { platform: m.platform === 'macos' ? 'macos' : 'windows', name: m.name.slice(0, 80), width: m.width, height: m.height, nativePaste: m.nativePaste === true, filePaste: m.filePaste === true, streamingFilePaste: m.streamingFilePaste === true, wake: m.wake === true }; send(c.viewer, status(c));
            } else if (m.type === 'stream' && (m.format === 'jpeg' || (m.format === 'mp4' && validVideoMime(m.codec, m.mime)))) {
              c.stream = m;
              if (c.viewer && (!c.viewer.recovery || c.viewer.recovery === 'starting')) {
                c.viewer.recovery = null; c.viewer.framePending = 0; c.viewer.videoPending = 0; c.viewer.lastVideoAck = elapsed();
                send(c.viewer, m);
              }
            } else if (m.type === 'idle' && typeof m.idle === 'boolean') send(c.viewer, m);
            else if (m.type === 'filesAck') {
              const batch = c.viewer?.batch;
              if (batch && batch.id === m.id && batch.waiting) {
                const ok = m.ok === true && m.received === batch.sent;
                const finished = batch.waiting === 'finish';
                batch.waiting = null;
                if (!ok || finished) c.viewer.batch = null;
                send(c.viewer, {type:'filesAck',id:m.id,ok,received:m.received,pasted:m.pasted === true,message:String(m.message || '').slice(0,160)});
                restartVideoIfReady(c);
              }
            }
            else if (validCursor(m)) send(c.viewer, m);
            else if (m.type === 'probeAck' && Number.isFinite(m.nonce)) send(c.viewer, m);
            else if (m.type === 'error' && typeof m.message === 'string') send(c.viewer, { type: 'error', message: m.message.slice(0, 200) });
            else if (m.type === 'wakeResult' && typeof m.ok === 'boolean') send(c.viewer, { type: 'wakeResult', ok: m.ok, message: String(m.message || '').slice(0, 200) });
          } catch { ws.close(1008, 'Invalid message'); }
        });
        ws.on('close', () => {
          if (c.agent !== ws) return;
          c.agent = null; c.device = null; c.lastFrameAt = 0; if(c.viewer) { c.viewer.batch=null; c.viewer.recovery=null; }
          send(c.viewer, status(c)); console.log('[ToLink] agent disconnected: ' + c.id);
        });
      } else {
        if (samePage) { send(c.agent, {type:'release'}); c.viewer.terminate(); }
        ws.pageId = /^[0-9a-f]{32}$/.test(pageId || '') ? pageId : null;
        c.viewer = ws; ws.sessionId = sessionId(req); ws.canRecover = url.searchParams.get('recover') === '1'; ws.lastVideoAck = elapsed(); ws.framePending = 0; ws.videoPending = 0;
        ws.codec = ['h265', 'h264'].includes(url.searchParams.get('codec')) ? url.searchParams.get('codec') : 'jpeg';
        let count = 0, start = Date.now();
        send(c.viewer, status(c)); send(c.agent, { type: 'viewing', active: true, codec: ws.codec });
        console.log('[ToLink] controller connected: ' + c.id);
        ws.on('message', (data, binary) => {
          if (c.viewer !== ws) return;
          ws.lastActivity = elapsed();
          if ((sessions.get(ws.sessionId)?.expiry || 0) <= Date.now()) return ws.close(4001, 'Session expired');
          if (binary) {
            const batch = ws.batch, bytes = data.length - 32;
            if (!c.agent || !batch || batch.waiting || bytes < 1 || bytes > 65536 || batch.sent + bytes > batch.total || data.toString('ascii',0,32) !== batch.id)
              return ws.close(1008, 'Invalid file chunk');
            batch.sent += bytes; batch.waiting = 'chunk'; c.agent.send(data,{binary:true}); return;
          }
          if (data.length > 32768) return ws.close(1008, 'Invalid command');
          if (Date.now() - start >= 1000) { count = 0; start = Date.now(); }
          if (++count > 300) return ws.close(1008, 'Rate limit');
          try {
            const m = JSON.parse(data.toString());
            if (m.type === 'filesBegin') {
              if (!c.device?.filePaste || ws.batch || !/^[0-9a-f]{32}$/.test(m.id) || !Array.isArray(m.files) || !m.files.length || m.files.length > 32 ||
                  !m.files.every(f=>f && typeof f.name==='string' && f.name.length>0 && f.name.length<=100 && Number.isSafeInteger(f.size) && f.size>=0) ||
                  m.files.reduce((sum,f)=>sum+f.size,0)>104857600)
                return send(ws,{type:'filesAck',id:m.id,ok:false,message:'客户端不可用或文件数量、大小超限'});
              if (m.streaming === true && !c.device?.streamingFilePaste) return send(ws,{type:'filesAck',id:m.id,ok:false,message:'请更新 Windows 客户端以支持流式粘贴'});
              ws.batch={id:m.id,sent:0,total:m.files.reduce((sum,f)=>sum+f.size,0),waiting:'begin'};
              send(c.agent,{type:'filesBegin',id:m.id,files:m.files.map(f=>({name:f.name,size:f.size})),streaming:m.streaming === true && c.device.streamingFilePaste}); return;
            }
            if (m.type === 'filesCancel' || m.type === 'filesFinish') {
              const b=ws.batch;
              if (!b || b.id!==m.id) return;
              if (m.type==='filesCancel') ws.batch=null;
              else { if(b.waiting || b.sent!==b.total) return ws.close(1008,'Incomplete batch'); b.waiting='finish'; }
              send(c.agent,{type:m.type,id:b.id}); restartVideoIfReady(c); return;
            }
            if (data.length > 8192) return ws.close(1008,'Invalid control size');
            if (m.type === 'videoFault' && ws.canRecover) {
              if (!ws.recovery && c.stream?.format === 'mp4') {
                const now = elapsed();
                if (ws.lastDecoderFault !== undefined && now - ws.lastDecoderFault < 30000) ws.codec = 'jpeg';
                ws.lastDecoderFault = now; resetVideo(c, 'decoder error');
              }
              return;
            }
            if (m.type === 'videoReady') {
              if (ws.recovery === 'draining') { ws.recovery = 'ready'; restartVideoIfReady(c); } return;
            }
            if (m.type === 'ack') { ws.lastVideoAck = elapsed(); ws.framePending = 0; if (Number.isInteger(m.bytes) && m.bytes > 0) ws.videoPending = Math.max(0, ws.videoPending - m.bytes); return; }
            if (m.type === 'heartbeat') { send(c.agent, { type: 'heartbeat' }); return; }
            if (!validInput(m)) {
              // Older viewers can send an unusable position on release during a video resize.
              // Drop it before forwarding and clear held input without ending the stream.
              if (m.type === 'button' && [0, 1, 2].includes(m.button) && typeof m.down === 'boolean') {
                send(c.agent, { type: 'release' });
                if (m.down) send(ws, { type: 'error', message: '点击位置无效，请刷新控制页面后重试' });
                return;
              }
              if (m.type === 'move' && (m.x == null || m.y == null)) return;
              return ws.close(1008, 'Invalid input');
            }
            if (m.type === 'paste' && !c.device?.nativePaste) return send(ws, { type: 'error', message: '当前 被控端不支持系统剪贴板粘贴，请更新客户端' });
            if (m.type === 'wake') {
              if (!c.agent || !c.device?.wake) return send(ws, { type: 'wakeResult', ok: false, message: '当前设备不支持唤醒，请更新被控端' });
              const now = elapsed();
              if (ws.lastWake != null && now - ws.lastWake < 2000) return send(ws, { type: 'wakeResult', ok: false, message: '请稍后再试' });
              ws.lastWake = now;
            }
            if (c.agent) { c.stats.inputs++; send(c.agent, m); }
          } catch { ws.close(1008, 'Invalid JSON'); }
        });
        ws.on('close', () => {
          if (c.viewer !== ws) return;
          c.viewer = null;
          send(c.agent, { type: 'release' }); send(c.agent, { type: 'viewing', active: false });
          console.log('[ToLink] controller disconnected: ' + c.id);
        });
      }
    });
  });
  const timer = setInterval(() => {
    const now = Date.now();
    for (const [id, value] of sessions) if (value.expiry <= now) sessions.delete(id);
    for (const [ip, a] of attempts) if (a.until < now) attempts.delete(ip);
    for (const c of channels.values()) {
      const v = c.viewer;
      if (v && (sessions.get(v.sessionId)?.expiry || 0) <= now) v.close(4001, 'Session expired');
      else if (v && v.framePending && elapsed() - v.framePending > 15000) resetVideo(c, 'JPEG ack timeout');
    }
    for (const ws of wss.clients) {
      if (ws.role === 'agent') {
        if (elapsed() - ws.lastActivity > 30_000) { console.log('[ToLink] agent heartbeat timeout: ' + ws.label); ws.terminate(); }
        continue;
      }
      if (ws.readyState !== WebSocket.OPEN) continue;
      if (elapsed() - ws.lastActivity > 45000) { console.log('[ToLink] ping timeout: ' + ws.label); ws.terminate(); continue; }
      ws.ping();
    }
  }, config.heartbeatMs || 10_000);
  timer.unref();
  return { server, status: allStatus, close: async () => {
    clearInterval(timer);
    for (const ws of wss.clients) ws.terminate();
    wss.close();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  } };
}
if (require.main === module) {
  const config = JSON.parse(fs.readFileSync(process.env.TOLINK_CONFIG || path.join(__dirname, '..', 'private', 'relay.json'), 'utf8'));
  const relay = createRelay(config);
  relay.server.listen(config.port || 19000, config.host || '0.0.0.0', () => console.log('[ToLink] relay listening on ' + (config.port || 19000)));
  for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, async () => { await relay.close(); process.exit(0); });
}
module.exports = { createRelay, validInput, validCursor };

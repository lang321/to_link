'use strict';
// REQ-REMOTE-20261105-01: browser owns one device control session; secrets stay in HttpOnly cookies.
const $ = id => document.getElementById(id);
let socket = null, deviceOnline = false, controlling = false, retry = null, intentional = false;
let frames = 0, bytes = 0, lastMetric = performance.now(), lastFrame = 0, lastMove = 0, selectedId = '';
const canvas = $('screen'), ctx = canvas.getContext('2d');
const video = $('video'), Media = window.MediaSource || window.ManagedMediaSource;
const mimeTypes = { h265: 'video/mp4; codecs="hvc1.1.6.L120.B0"', h264: 'video/mp4; codecs="avc1.42E028"' };
for (const option of $('codec').options) if (option.value !== 'jpeg') option.disabled = !Media || !Media.isTypeSupported(mimeTypes[option.value]);
$('codec').value = [...$('codec').options].find(o => !o.disabled).value;
const pageId = Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2,'0')).join('');
let connectionEpoch = 0, waitingForVideo = false;
let format = 'jpeg', actualCodec = 'JPEG', idle = false, controlRtt = 0;
let remoteCursor = null, pointerInside = false;
function updateCursor() {
  const marker = $('remoteCursor');
  marker.hidden = !remoteCursor?.visible || format !== 'mp4' || canvas.hidden || (controlling && pointerInside);
  canvas.style.cursor = controlling ? (remoteCursor?.shape || 'default') : 'default';
  if (marker.hidden) return;
  const rect = ToLinkGeometry.fittedRect(canvas.getBoundingClientRect(), canvas.width, canvas.height);
  if (!rect) { marker.hidden = true; return; }
  const parent = $('desktop').getBoundingClientRect();
  marker.style.left = (rect.left - parent.left - $('desktop').clientLeft + rect.width * remoteCursor.x) + 'px';
  marker.style.top = (rect.top - parent.top - $('desktop').clientTop + rect.height * remoteCursor.y) + 'px';
  marker.textContent = remoteCursor.shape === 'text' ? 'Ⅰ' : '↖';
}
window.addEventListener('resize', updateCursor);
document.addEventListener('fullscreenchange', updateCursor);
canvas.addEventListener('pointerenter', () => { pointerInside = true; updateCursor(); });
canvas.addEventListener('pointerleave', () => { pointerInside = false; updateCursor(); });
let mediaSource = null, sourceBuffer = null, mediaURL = null, queue = [], pendingAppend = 0, mediaVersion = 0;
function stopVideo() {
  mediaVersion++; queue = []; pendingAppend = 0; sourceBuffer = null; mediaSource = null;
  video.pause(); video.removeAttribute('src'); video.load(); video.hidden = true;
  if (mediaURL) URL.revokeObjectURL(mediaURL); mediaURL = null;
  canvas.classList.remove('videoOverlay'); format = 'jpeg'; $('remoteCursor').hidden = true;
}
let decoderFaultSent = false;
function decoderFault() {
  if (!decoderFaultSent && socket?.readyState === WebSocket.OPEN) {
    decoderFaultSent = true; send({type:'videoFault'});
  }
}
function startVideo(m) {
  decoderFaultSent = false;
  stopVideo(); format = 'mp4'; actualCodec = m.codec.toUpperCase(); idle = false;
  if (!Media || !Media.isTypeSupported(m.mime)) { decoderFault(); return; }
  const version = mediaVersion, ms = mediaSource = new Media();
  video.onerror = () => { if (version === mediaVersion && video.error) decoderFault(); };
  video.muted = true; video.disableRemotePlayback = true; video.hidden = false; canvas.hidden = true;
  // MSE may deliver metadata before it exposes the decoded dimensions.
  const syncSize = () => {
    if (version !== mediaVersion) return;
    const width = video.videoWidth || m.width, height = video.videoHeight || m.height;
    if (![width, height].every(n => Number.isInteger(n) && n > 0 && n <= 32768)) return;
    if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
    canvas.classList.add('videoOverlay'); canvas.hidden = false; $('placeholder').hidden = true; updateCursor();
  };
  video.onresize = syncSize;
  video.onloadedmetadata = () => {
    if (version !== mediaVersion) return;
    syncSize();
    video.play().catch(() => showMessage('请点击画面开始播放'));
  };
  ms.addEventListener('sourceopen', () => {
    if (version !== mediaVersion) return;
    try {
      console.info('[ToLink-video] source opened: ' + m.codec);
      const sb = sourceBuffer = ms.addSourceBuffer(m.mime);
      sb.addEventListener('updateend', () => {
        if (version !== mediaVersion) return;
        if (pendingAppend) { send({ type: 'ack', bytes: pendingAppend }); pendingAppend = 0; }
        // Static capture stops advancing the media timeline; resume at its newest frame on changes.
        if (video.buffered.length) {
          const end = video.buffered.end(video.buffered.length - 1), start = video.buffered.start(0);
          if (video.currentTime < start || end - video.currentTime > .25) video.currentTime = Math.max(start, end - .07);
          if (video.paused) video.play().catch(() => {});
        }
        pumpVideo();
      });
      sb.addEventListener('error', () => { if (version === mediaVersion) { decoderFault(); } });
      pumpVideo();
    } catch { decoderFault(); }
  });
  mediaURL = URL.createObjectURL(ms); video.src = mediaURL;
}
function pumpVideo() {
  if (!sourceBuffer || sourceBuffer.updating || !mediaSource || mediaSource.readyState !== 'open') return;
  try {
    if (queue.length) { const chunk = queue.shift(); pendingAppend = chunk.byteLength; sourceBuffer.appendBuffer(chunk); }
    // MSE removal can extend to the next random-access frame. Keep two full GOPs
    // behind playback so trimming never discards the currently decoded GOP.
    else if (video.currentTime > 10 && sourceBuffer.buffered.length && sourceBuffer.buffered.start(0) < video.currentTime - 8) sourceBuffer.remove(0, video.currentTime - 6);
  } catch { decoderFault(); }
}
function send(value) { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(value)); }
let nativePaste = false, filePaste = false, streamingFilePaste = false, uploadJob = null;
const textInput = ToLinkTextInput.createTextInput({ enabled: () => controlling && deviceOnline && socket?.readyState === WebSocket.OPEN, supportsPaste: () => nativePaste, send, message: showMessage });
function release() { textInput.release(); }
canvas.addEventListener('paste', event => {
  if (!controlling) return;
  if (Array.from(event.clipboardData?.items || []).some(item => item.webkitGetAsEntry?.()?.isDirectory)) {
    event.preventDefault(); showMessage('请选择普通文件，暂不支持文件夹'); return;
  }
  const files = Array.from(event.clipboardData?.files || []);
  if (!files.length && Array.from(event.clipboardData?.types || []).includes('Files')) {
    event.preventDefault(); showMessage('请点击“粘贴文件”选择文件'); return;
  }
  if (!files.length) return textInput.paste(event);
  event.preventDefault(); transferFiles(files);
});
$('chooseFiles').onclick = () => $('filePicker').click();
$('filePicker').onchange = event => {
  const files = Array.from(event.target.files); event.target.value = '';
  if (files.length) transferFiles(files);
};
function transferSize(bytes) {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KiB';
  return (bytes / 1048576).toFixed(1) + ' MiB';
}
function renderTransfer(job, state, message) {
  const panel = $('fileTransfer');
  // A cancelled file read may finish after another batch starts. Never repaint the newer batch.
  if (panel.dataset.batchId !== job.id) return;
  if (job.native) {
    panel.hidden = true;
    if (['done', 'error', 'cancelled'].includes(state)) showMessage(message);
    return;
  }
  const finished = state === 'finishing' || state === 'done';
  const percent = job.total ? Math.floor(job.received * 100 / job.total) : (finished ? 100 : 0);
  panel.hidden = false; panel.dataset.state = state;
  $('transferTitle').textContent = message;
  $('transferFile').textContent = job.index + ' / ' + job.count + ' · ' + job.fileName;
  $('transferFile').title = job.fileName;
  $('transferProgress').value = percent;
  $('transferPercent').textContent = percent + '%';
  $('transferBytes').textContent = transferSize(job.received) + ' / ' + transferSize(job.total);
  $('dismissTransfer').hidden = !['done', 'error', 'cancelled'].includes(state);
}
$('dismissTransfer').onclick = () => { $('fileTransfer').hidden = true; };
function cancelUpload(message = '文件传输已中断', state = 'cancelled') {
  const job = uploadJob;
  if (!job) return;
  uploadJob = null;
  try { if (job.socket.readyState === WebSocket.OPEN) job.socket.send(JSON.stringify({type:'filesCancel',id:job.id})); } catch { /* The connection may close during cancellation. */ }
  renderTransfer(job, state, message);
  job.fail?.(new Error(message));
}
async function transferFiles(files) {
  if (!controlling || !deviceOnline || !filePaste) return showMessage('请连接支持文件粘贴的客户端并启用控制');
  if (uploadJob) return showMessage('请等待当前文件传输完成');
  const total=files.reduce((n,f)=>n+f.size,0);
  if (files.length>32 || total>104857600 || files.some(f=>f.name.length>100)) return showMessage('每批最多 32 个文件、100 MiB，文件名最多 100 个字符');
  const id=Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,'0')).join('');
  const job=uploadJob={id,socket,total,native:streamingFilePaste,received:0,count:files.length,index:1,fileName:files[0].name};
  const prefix=new TextEncoder().encode(id);
  $('fileTransfer').dataset.batchId=id;
  renderTransfer(job,'preparing','正在准备传输');
  if(job.native)showMessage('正在启动 Windows 文件复制…');
  // Acknowledge each disk write before reading the next chunk. Memory usage is independent of file size.
  function exchange(payload, expectedReceived) {
    return new Promise((resolve,reject)=>{
      if(uploadJob!==job || job.socket.readyState!==WebSocket.OPEN) return reject(new Error('连接已中断'));
      const timer=setTimeout(()=>job.fail(new Error('文件接收超时')),30000);
      const clear=()=>{clearTimeout(timer);job.fail=job.ack=null;};
      job.fail=e=>{clear();reject(e);};
      job.ack=m=>{clear();if(m.ok && m.received===expectedReceived)resolve(m);else reject(new Error(m.message||'远端接收回执异常'));};
      try {job.socket.send(payload);} catch(e){job.fail(e);}
    });
  }
  $('chooseFiles').disabled=true; release();
  try {
    const begin=await exchange(JSON.stringify({type:'filesBegin',id,files:files.map(f=>({name:f.name,size:f.size})),streaming:job.native}),0);
    if(job.native)showMessage(begin.pasted ? 'Windows 正在边接收边复制，可在系统复制窗口取消' : '请在远端目标文件夹按 Ctrl+V 开始复制');
    for (const [index,f] of files.entries()) {
      job.index=index+1;job.fileName=f.name;
      renderTransfer(job,'sending','正在传输文件');
      for(let start=0;start<f.size;start+=65536) {
        const chunk=new Uint8Array(await f.slice(start,start+65536).arrayBuffer());
        const frame=new Uint8Array(prefix.length+chunk.length); frame.set(prefix);frame.set(chunk,prefix.length);
        const ack=await exchange(frame,job.received+chunk.length);
        job.received=ack.received;
        renderTransfer(job,'sending','正在传输文件');
      }
    }
    renderTransfer(job,'finishing','传输完成，正在准备粘贴…');
    const result=await exchange(JSON.stringify({type:'filesFinish',id}),total);
    if(uploadJob!==job)return;
    const message=job.native && result.pasted ? '数据已传输，复制结果请查看 Windows 窗口' : result.pasted ? '传输完成，已发送粘贴指令' : '已写入远端剪贴板，请在目标文件夹粘贴';
    renderTransfer(job,'done',message);showMessage(message);
  } catch(e) { if(uploadJob===job){cancelUpload(e.message,'error');showMessage(e.message);} }
  finally { if(uploadJob===job)uploadJob=null; $('chooseFiles').disabled=!controlling || !filePaste || !!uploadJob; }
}
function setControl(active) {
  if (!active) cancelUpload();
  release(); controlling = active && deviceOnline && socket?.readyState === WebSocket.OPEN;
  $('control').textContent = controlling ? '停止控制' : '启用控制';
  $('control').classList.toggle('active', controlling);
  canvas.classList.toggle('controlling', controlling); $('chooseFiles').disabled = !controlling || !filePaste || !!uploadJob;
  updateCursor();
}
function showMessage(message) { $('message').textContent = message; }
async function refresh() {
  const response = await fetch('/api/status');
  if (!response.ok) { disconnect(); $('login').hidden = false; $('console').hidden = true; return false; }
  const data = await response.json();
  $('login').hidden = true; $('console').hidden = false;
  const d = data.devices[0]; selectedId = d.id;
  $('deviceName').textContent = d.id + ' · ' + d.name;
  $('remoteId').value = d.id;
  return true;
}
function disconnect() {
  connectionEpoch++;
  waitingForVideo = false;
  intentional = true; clearTimeout(retry); setControl(false); clearWindowFill(); syncDisplayMode();
  if (socket) { socket.onclose = null; socket.close(1000, 'Page disconnect'); socket = null; }
  stopVideo(); remoteCursor = null; idle = false; deviceOnline = false; $('control').disabled = true; $('wake').disabled = true; $('disconnect').disabled = true; $('connect').disabled = false; $('codec').disabled = false;
  canvas.hidden = true; $('placeholder').hidden = false;
  $('connection').textContent = '未连接'; $('connection').classList.remove('online'); showMessage('已断开');
}
function connect(id) {
  disconnect(); intentional = false; selectedId = id;
  if (!id) return showMessage('暂无可连接设备');
  $('connect').disabled = true; $('disconnect').disabled = false; $('codec').disabled = true;
  showMessage('正在连接中转服务器…');
  const epoch = connectionEpoch;
  const ws = socket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws/viewer?device=${encodeURIComponent(id)}&recover=1&page=${pageId}&codec=${$('codec').value}`);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => { send({ type: 'heartbeat' }); showMessage('已连接，等待 远端画面…'); };
  ws.onmessage = async event => {
    if (socket !== ws) return;
    if (event.data instanceof ArrayBuffer) {
      if (format === 'mp4') { queue.push(event.data); frames++; bytes += event.data.byteLength; lastFrame = performance.now(); pumpVideo(); return; }
      const imageVersion = mediaVersion;
      const url = URL.createObjectURL(new Blob([event.data], { type: 'image/jpeg' })), img = new Image();
      img.onload = () => {
        URL.revokeObjectURL(url);
        if (socket !== ws || !deviceOnline || imageVersion !== mediaVersion) return;
        if (canvas.width !== img.width || canvas.height !== img.height) { canvas.width = img.width; canvas.height = img.height; }
        ctx.drawImage(img, 0, 0); canvas.hidden = false; $('placeholder').hidden = true;
        frames++; bytes += event.data.byteLength; lastFrame = performance.now(); send({ type: 'ack' });
      };
      img.onerror = () => { URL.revokeObjectURL(url); send({ type: 'ack' }); showMessage('画面解码失败，等待下一帧'); };
      img.src = url; return;
    }
    const m = JSON.parse(event.data);
    if (m.type === 'status') {
      filePaste = m.device?.filePaste === true; streamingFilePaste = m.device?.streamingFilePaste === true; nativePaste = m.device?.nativePaste === true;
      deviceOnline = m.online && !!m.device; $('control').disabled = !deviceOnline; $('wake').disabled = !deviceOnline || !m.device?.wake;
      $('connection').textContent = deviceOnline ? '设备在线' : '等待设备'; $('connection').classList.toggle('online', deviceOnline);
      showMessage(deviceOnline ? `${m.device.platform === 'macos' ? 'macOS · ' : ''}${m.device.name} · ${m.device.width} × ${m.device.height} · 已连接` : '设备离线，正在等待重新连接…');
      if (!deviceOnline) { setControl(false); stopVideo(); canvas.hidden = true; $('placeholder').hidden = false; }
    } else if (m.type === 'videoReset') {
      release(); stopVideo(); canvas.hidden = true; waitingForVideo = true;
      if (m.codec) $('codec').value = m.codec;
      showMessage('正在重新同步画面…');
      if (!document.hidden) send({ type: 'videoReady' });
    } else if (m.type === 'stream') { if (waitingForVideo) showMessage('画面已重新同步'); waitingForVideo = false; if (m.format === 'mp4') startVideo(m); else { stopVideo(); actualCodec = 'JPEG'; } }
    else if (m.type === 'filesAck') { if(uploadJob?.id===m.id) uploadJob.ack?.(m); }
    else if (m.type === 'idle') idle = m.idle;
    else if (m.type === 'cursor') { remoteCursor = m; updateCursor(); }
    else if (m.type === 'probeAck') controlRtt = Date.now() - m.nonce;
    else if (m.type === 'wakeResult') showMessage(m.message || (m.ok ? '已发送唤醒，请在登录界面输入系统密码' : '唤醒失败'));
    else if (m.type === 'error') showMessage(m.message);
  };
  ws.onclose = async e => {
    if (socket !== ws) return;
    setControl(false); stopVideo(); socket = null; deviceOnline = false; $('control').disabled = true; $('wake').disabled = true; canvas.hidden = true; $('placeholder').hidden = false;
    $('connection').textContent = '连接中断'; $('connection').classList.remove('online');
    if (intentional) return;
    if (e.code === 4001) { disconnect(); await refresh(); showMessage('会话已结束，请重新连接'); return; }
    try {
      const response = await fetch('/api/status', { headers: { 'X-ToLink-Page': pageId } });
      if (epoch !== connectionEpoch) return;
      if (response.status === 401 || response.status === 403) { await refresh(); return; }
      if (response.ok) {
        const state = await response.json();
        if (epoch !== connectionEpoch) return;
        if (state.devices.find(d => d.id === id)?.controlling && !state.ownPage) {
          disconnect(); showMessage('该设备正被其他浏览器控制，请稍后重试'); return;
        }
      }
    } catch { /* Retry after temporary network or relay failures. */ }
    if (intentional || epoch !== connectionEpoch) return;
    showMessage('连接中断，3 秒后自动重连…'); retry = setTimeout(() => connect(id), 3000);
  };
}
$('loginForm').onsubmit = async e => {
  e.preventDefault(); $('loginError').textContent = '';
  try {
    const response = await fetch('/api/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ deviceId: $('remoteId').value, password: $('password').value }) });
    if (!response.ok) throw new Error((await response.json()).error);
    $('password').value = ''; if (await refresh()) connect(selectedId);
  } catch (err) { $('loginError').textContent = err.message; }
};
$('refresh').onclick = () => refresh().catch(() => showMessage('服务器不可达'));
$('connect').onclick = () => connect(selectedId);
$('disconnect').onclick = disconnect;
$('wake').onclick = () => { release(); send({ type: 'wake' }); showMessage('正在唤醒屏幕…'); };
$('control').onclick = () => { setControl(!controlling); if (controlling) canvas.focus(); };
$('logout').onclick = async () => { disconnect(); await fetch('/api/logout', { method: 'POST' }); await refresh(); };
// REQ-REMOTE-20261105-04: window fill preserves browser chrome; native fullscreen is separate.
function syncDisplayMode() {
  const expanded = $('desktop').classList.contains('expanded');
  $('exitFullscreen').hidden = !document.fullscreenElement && !expanded;
  $('exitFullscreen').textContent = document.fullscreenElement ? '退出全屏' : '退出撑满';
  $('fillWindow').classList.toggle('active', expanded);
  $('fillWindow').setAttribute('aria-pressed', String(expanded));
  updateCursor();
}
function clearWindowFill() {
  $('desktop').classList.remove('expanded'); document.body.classList.remove('expandedViewport');
}
async function toggleWindowFill() {
  release();
  const enable = !$('desktop').classList.contains('expanded');
  if (document.fullscreenElement) await document.exitFullscreen();
  $('desktop').classList.toggle('expanded', enable); document.body.classList.toggle('expandedViewport', enable);
  syncDisplayMode();
}
async function toggleFullscreen() {
  release();
  if (document.fullscreenElement) await document.exitFullscreen();
  else {
    clearWindowFill();
    try { await $('desktop').requestFullscreen(); }
    catch { $('desktop').classList.add('expanded'); document.body.classList.add('expandedViewport'); }
  }
  syncDisplayMode();
}
$('fillWindow').onclick = toggleWindowFill;
$('fullscreen').onclick = toggleFullscreen;
$('exitFullscreen').onclick = async () => {
  release(); clearWindowFill();
  if (document.fullscreenElement) await document.exitFullscreen();
  syncDisplayMode();
};
document.addEventListener('fullscreenchange', syncDisplayMode);
let displayEscape = false;
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && $('desktop').classList.contains('expanded')) {
    e.preventDefault(); e.stopPropagation(); displayEscape = true; release(); clearWindowFill(); syncDisplayMode();
  }
}, true);
document.addEventListener('keyup', e => {
  if (e.key === 'Escape' && displayEscape) { e.preventDefault(); e.stopPropagation(); displayEscape = false; }
}, true);
function pointerPoint(event) {
  return ToLinkGeometry.remotePoint(canvas.getBoundingClientRect(), canvas.width, canvas.height, event.clientX, event.clientY);
}
canvas.onpointerdown = e => {
  if (!controlling || ![0, 1, 2].includes(e.button)) return;
  const p = pointerPoint(e);
  if (!p?.inside) return;
  pointerInside = true; updateCursor(); e.preventDefault(); canvas.focus(); canvas.setPointerCapture(e.pointerId);
  send({ type: 'button', button: e.button, down: true, x: p.x, y: p.y });
};
canvas.onpointerup = e => {
  if (!controlling || ![0, 1, 2].includes(e.button)) return;
  e.preventDefault();
  const p = pointerPoint(e);
  // During a resize or decoder reset a captured pointer can outlive its visible frame.
  if (p) send({ type: 'button', button: e.button, down: false, x: p.x, y: p.y });
  else release();
  if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
};
canvas.onpointermove = e => {
  const p = pointerPoint(e);
  if (!p) return;
  if (pointerInside !== p.inside) { pointerInside = p.inside; updateCursor(); }
  if (!controlling || performance.now() - lastMove < 25 || (!p.inside && !e.buttons)) return;
  lastMove = performance.now(); send({ type: 'move', x: p.x, y: p.y });
};
canvas.onpointercancel = release;
canvas.oncontextmenu = e => e.preventDefault();
canvas.addEventListener('wheel', e => { if (!controlling) return; e.preventDefault(); send({ type: 'wheel', delta: e.deltaY > 0 ? -120 : 120 }); }, { passive: false });
const keyMap = { Backspace:8,Tab:9,Enter:13,ShiftLeft:160,ShiftRight:161,ControlLeft:162,ControlRight:163,AltLeft:164,AltRight:165,Pause:19,CapsLock:20,Escape:27,Space:32,PageUp:33,PageDown:34,End:35,Home:36,ArrowLeft:37,ArrowUp:38,ArrowRight:39,ArrowDown:40,Insert:45,Delete:46,MetaLeft:91,MetaRight:92,ContextMenu:93,NumLock:144,ScrollLock:145,Semicolon:186,Equal:187,Comma:188,Minus:189,Period:190,Slash:191,Backquote:192,BracketLeft:219,Backslash:220,BracketRight:221,Quote:222,NumpadMultiply:106,NumpadAdd:107,NumpadSubtract:109,NumpadDecimal:110,NumpadDivide:111,NumpadEnter:13 };
function virtualKey(code) { if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3); if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5); if (/^Numpad[0-9]$/.test(code)) return 96 + Number(code.slice(6)); if (/^F([1-9]|1[0-2])$/.test(code)) return 111 + Number(code.slice(1)); return keyMap[code]; }
function keyboard(e, down) { textInput.key(e, down, virtualKey(e.code)); }
canvas.onkeydown = e => keyboard(e, true); canvas.onkeyup = e => keyboard(e, false);
canvas.onblur = release; window.addEventListener('blur', release);
document.addEventListener('visibilitychange', () => { if (document.hidden) release(); else { send({ type: 'heartbeat' }); if (waitingForVideo) send({ type: 'videoReady' }); } });
window.addEventListener('beforeunload', () => { release(); socket?.close(1000, 'Page unload'); });
setInterval(() => {
  send({ type: 'heartbeat' });
  if (deviceOnline) send({ type: 'probe', nonce: Date.now() });
  const now = performance.now(), seconds = (now - lastMetric) / 1000;
  $('metrics').textContent = `${actualCodec} · ${idle ? '画面静止' : (frames / seconds).toFixed(1) + ' FPS'} · ${(bytes * 8 / seconds / 1e6).toFixed(3)} Mbps${controlRtt ? ' · 控制往返 ' + controlRtt + ' ms' : ''}`;
  frames = 0; bytes = 0; lastMetric = now;
}, 2000);
refresh().catch(() => { $('loginError').textContent = '服务器暂不可达'; });

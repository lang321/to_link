'use strict';
const http = require('node:http');
const fs = require('node:fs');
const c = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const host = c.host === '0.0.0.0' ? '127.0.0.1' : c.host === '::' ? '::1' : c.host;
const req = http.get({ host, port: c.port, path: '/health', timeout: 1500 }, res => {
  let text = ''; res.on('data', d => { text += d; if (text.length > 4096) req.destroy(); });
  res.on('end', () => { try { const data = JSON.parse(text); process.exit(res.statusCode === 200 && data.ok === true && data.service === 'tolink-relay' ? 0 : 1); } catch { process.exit(1); } });
});
req.on('timeout', () => req.destroy()); req.on('error', () => process.exit(1));

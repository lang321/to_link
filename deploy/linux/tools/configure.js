'use strict';
// REQ-REMOTE-20261105-05: preserve secrets and state; only apply explicitly supplied overrides.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const net = require('node:net');
const [existing, output, stateDir, ip, portArg, bindArg, publicArg] = process.argv.slice(2);
const present = fs.existsSync(existing);
const original = present ? fs.readFileSync(existing, 'utf8') : null;
const c = present ? JSON.parse(original) : { host: '0.0.0.0', port: 19000, enrollmentToken: crypto.randomBytes(32).toString('hex'), stateFile: path.join(stateDir, 'devices.json'), maxDevices: 1000, origins: [], secureCookie: false };
if (portArg) { if (!/^\d+$/.test(portArg)) throw Error('端口必须为整数'); c.port = Number(portArg); }
if (bindArg) c.host = bindArg;
if (!Number.isInteger(c.port) || c.port < 1 || c.port > 65535) throw Error('端口范围为 1～65535');
if (!net.isIP(c.host) && c.host !== 'localhost') throw Error('监听地址必须为 IP 或 localhost');
if (typeof c.enrollmentToken !== 'string' || c.enrollmentToken.length < 32) throw Error('注册密钥至少需要 32 个字符');
if (c.stateFile !== path.join(stateDir, 'devices.json')) throw Error('为避免覆盖自定义存储，请先将配置的 stateFile 迁移到 ' + path.join(stateDir, 'devices.json'));
if (!Array.isArray(c.origins)) throw Error('origins 必须为数组');
function origin(value) {
  const u = new URL(value);
  if (!['http:', 'https:'].includes(u.protocol) || u.username || u.password || u.pathname !== '/' || u.search || u.hash) throw Error('访问地址必须是 HTTP/HTTPS 根地址');
  return u.origin;
}
c.origins.forEach(origin);
if (publicArg) {
  const value = origin(publicArg);
  c.secureCookie = value.startsWith('https:');
  c.origins = [...new Set([...c.origins.filter(o => o.startsWith(c.secureCookie ? 'https:' : 'http:')), value])];
} else if (!present) c.origins = [`http://${ip}:${c.port}`, `http://localhost:${c.port}`, `http://127.0.0.1:${c.port}`];
else if (portArg && !c.secureCookie) c.origins = [...new Set([...c.origins, `http://${ip}:${c.port}`, `http://localhost:${c.port}`, `http://127.0.0.1:${c.port}`])];
if (!c.origins.length) throw Error('请指定 --public-url 配置浏览器访问地址');
fs.writeFileSync(output, present && !portArg && !bindArg && !publicArg ? original : JSON.stringify(c, null, 2) + '\n', { mode: 0o640 });

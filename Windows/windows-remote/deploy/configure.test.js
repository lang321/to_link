const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { execFileSync } = require('node:child_process');
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tolink-install-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const config = path.join(root, 'relay.json'), output = path.join(root, 'next.json'), state = path.join(root, 'data');
  const run = (port = '', bind = '', url = '') => execFileSync(process.execPath, [path.join(__dirname, 'tools/configure.js'), config, output, state, '192.0.2.10', port, bind, url], { stdio: 'pipe' });
  return { config, output, state, run };
}
test('fresh install creates an independent secret and automatic origins', t => {
  const f = fixture(t); f.run(); const c = JSON.parse(fs.readFileSync(f.output));
  assert.match(c.enrollmentToken, /^[a-f0-9]{64}$/); assert.equal(c.port, 19000);
  assert.equal(c.stateFile, path.join(f.state, 'devices.json')); assert.ok(c.origins.includes('http://192.0.2.10:19000'));
});
test('upgrade preserves configuration bytes, key and state location', t => {
  const f = fixture(t); f.run(); const original = fs.readFileSync(f.output);
  fs.writeFileSync(f.config, original); f.run(); assert.deepEqual(fs.readFileSync(f.output), original);
  f.run('19999', '127.0.0.1', 'https://remote.example.com'); const changed = JSON.parse(fs.readFileSync(f.output));
  assert.equal(changed.enrollmentToken, JSON.parse(original).enrollmentToken);
  assert.equal(changed.stateFile, JSON.parse(original).stateFile);
  assert.equal(changed.secureCookie, true); assert.deepEqual(changed.origins, ['https://remote.example.com']);
});
test('rejects invalid endpoints and unrelated state paths before activation', t => {
  const f = fixture(t);
  for (const url of ['https://user:pass@example.com', 'https://example.com/path', 'https://example.com?x=1', 'file:///tmp']) assert.throws(() => f.run('', '', url));
  assert.throws(() => f.run('65536')); assert.throws(() => f.run('abc')); assert.throws(() => f.run('', 'invalid'));
  f.run(); const c = JSON.parse(fs.readFileSync(f.output)); c.stateFile = '/unrelated/devices.json'; fs.writeFileSync(f.config, JSON.stringify(c)); assert.throws(() => f.run());
});

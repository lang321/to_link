const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createTextInput } = require('../public/text-input');
function fixture(active = true) {
  const sent = [], messages = [];
  const input = createTextInput({ enabled: () => active, supportsPaste: () => true, send: m => sent.push(m), message: m => messages.push(m) });
  return { input, sent, messages };
}
function event(code, fields = {}) { return { code, preventDefault() { this.prevented = true; }, ...fields }; }
test('Cmd+V reads plain text once without sending Win+V or preventing paste', () => {
  const { input, sent } = fixture();
  input.key(event('MetaLeft', { metaKey: true }), true, 91);
  const v = event('KeyV', { metaKey: true }); input.key(v, true, 86);
  assert.equal(v.prevented, undefined);
  input.paste(event('', { clipboardData: { getData: type => { assert.equal(type, 'text/plain'); return '中文\nline 2 😀'; } } }));
  input.key(event('KeyV', { metaKey: true }), false, 86); input.key(event('MetaLeft'), false, 91);
  assert.equal(sent.some(m => m.type === 'key'), false);
  assert.deepEqual(sent.filter(m => m.type !== 'release'), [{ type: 'paste', text: '中文\nline 2 😀' }]);
});
test('Ctrl+V preserves browser gesture while Ctrl+A remains a remote chord', () => {
  const { input, sent } = fixture();
  input.key(event('ControlLeft', { ctrlKey: true }), true, 162);
  const v = event('KeyV', { ctrlKey: true }); input.key(v, true, 86); assert.equal(v.prevented, undefined);
  input.release(); sent.length = 0;
  input.key(event('ControlLeft', { ctrlKey: true }), true, 162);
  input.key(event('KeyA', { ctrlKey: true }), true, 65);
  input.key(event('KeyA', { ctrlKey: true }), false, 65);
  input.key(event('ControlLeft'), false, 162);
  assert.deepEqual(sent.map(m => [m.vk, m.down]), [[162,true],[65,true],[65,false],[162,false]]);
});
test('read-only, empty and over-limit clipboard never send partial content', () => {
  const off = fixture(false); off.input.paste(event('', { clipboardData: { getData: () => { throw Error('Must not read'); } } })); assert.equal(off.sent.length, 0);
  const { input, sent, messages } = fixture(); const paste = value => input.paste(event('', { clipboardData: { getData: () => value } })); paste(''); paste('x'.repeat(1001)); assert.equal(sent.length, 0); assert.equal(messages.length, 2);
  paste('x'.repeat(1000)); assert.equal(sent.at(-1).text.length, 1000);
});

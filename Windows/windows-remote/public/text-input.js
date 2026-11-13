'use strict';
// REQ-REMOTE-20261105-02: clipboard data is read only during an explicit paste gesture.
(function (root) {
  function createTextInput({ enabled, supportsPaste, send, message, limit = 1000 }) {
    const pending = new Map(), forwarded = new Set();
    const modifier = code => /^(Control|Meta)(Left|Right)$/.test(code);
    function release() { pending.clear(); forwarded.clear(); send({ type: 'release' }); }
    function transmit(value) {
      if (!enabled()) return false;
      if (!value) { message('剪贴板中没有可粘贴的文本'); return false; }
      if (value.length > limit) { message(`单次最多 ${limit} 个字符，请分段粘贴；内容未发送`); return false; }
      if (value.includes('\0')) { message('剪贴板文本包含空字符，无法粘贴'); return false; }
      release(); send({ type: 'paste', text: value }); return true;
    }
    function paste(event) {
      if (!enabled()) return;
      event.preventDefault();
      if (!supportsPaste()) { message('请更新 被控端以启用系统剪贴板粘贴'); return; }
      transmit(event.clipboardData?.getData('text/plain') || '');
    }
    function key(event, down, vk) {
      if (!enabled() || event.isComposing || !vk) return;
      const browserPaste = (event.code === 'KeyV' && (event.ctrlKey || event.metaKey) && !event.altKey)
        || (event.code === 'Insert' && event.shiftKey && !event.ctrlKey && !event.metaKey);
      if (browserPaste) { if (down) release(); return; } // Preserve the browser's native paste event.
      if (modifier(event.code)) {
        // Defer modifiers until a non-paste chord is known; Cmd+V must not open Windows Start.
        if (down) { if (!forwarded.has(event.code)) pending.set(event.code, vk); }
        else {
          const tapped = pending.delete(event.code);
          if (forwarded.delete(event.code)) send({ type: 'key', vk, down: false });
          else if (tapped) { send({ type: 'key', vk, down: true }); send({ type: 'key', vk, down: false }); }
        }
        return;
      }
      event.preventDefault();
      if (down) { for (const [code, value] of pending) { send({ type: 'key', vk: value, down: true }); forwarded.add(code); } pending.clear(); }
      send({ type: 'key', vk, down });
    }
    return { release, paste, key };
  }
  if (typeof module !== 'undefined') module.exports = { createTextInput };
  else root.ToLinkTextInput = { createTextInput };
})(typeof window === 'undefined' ? null : window);

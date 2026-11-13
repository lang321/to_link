'use strict';
// REQ-REMOTE-20261105-01: pointer mapping uses the fitted image, not its letterbox container.
(function (root) {
  function fittedRect(rect, sourceWidth, sourceHeight) {
    if (![rect.width, rect.height, sourceWidth, sourceHeight].every(n => Number.isFinite(n) && n > 0)) return null;
    const scale = Math.min(rect.width / sourceWidth, rect.height / sourceHeight);
    const width = sourceWidth * scale, height = sourceHeight * scale;
    const left = rect.left + (rect.width - width) / 2, top = rect.top + (rect.height - height) / 2;
    return { left, top, width, height };
  }
  function remotePoint(rect, sourceWidth, sourceHeight, clientX, clientY) {
    const fitted = fittedRect(rect, sourceWidth, sourceHeight);
    if (!fitted || !Number.isFinite(clientX) || !Number.isFinite(clientY)) return null;
    const { left, top, width, height } = fitted;
    const x = (clientX - left) / width, y = (clientY - top) / height;
    return { x: Math.max(0, Math.min(1, x)), y: Math.max(0, Math.min(1, y)), inside: x >= 0 && x <= 1 && y >= 0 && y <= 1 };
  }
  if (typeof module !== 'undefined') module.exports = { remotePoint, fittedRect };
  else root.ToLinkGeometry = { remotePoint, fittedRect };
})(typeof window === 'undefined' ? null : window);

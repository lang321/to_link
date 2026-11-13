'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { remotePoint, fittedRect } = require('../public/geometry');
test('fullscreen upscaling preserves center and corners', () => {
  const rect = { left: 0, top: 0, width: 2560, height: 1440 };
  assert.deepEqual(remotePoint(rect, 1600, 900, 1280, 720), { x: .5, y: .5, inside: true });
  assert.deepEqual(remotePoint(rect, 1600, 900, 2560, 1440), { x: 1, y: 1, inside: true });
});
test('normal view ignores horizontal black bars', () => {
  const rect = { left: 40, top: 100, width: 1280, height: 600 };
  const p = remotePoint(rect, 1600, 900, 680, 400);
  assert.equal(p.x, .5); assert.equal(p.y, .5);
  assert.equal(remotePoint(rect, 1600, 900, 50, 400).inside, false);
  assert.equal(remotePoint(rect, 1600, 900, 146.66666666666663, 100).x, 0);
});
test('portrait viewport excludes vertical bars and clamps drag release', () => {
  const rect = { left: 0, top: 0, width: 1080, height: 1920 };
  assert.equal(remotePoint(rect, 1600, 900, 540, 0).inside, false);
  assert.deepEqual(remotePoint(rect, 1600, 900, 540, 960), { x: .5, y: .5, inside: true });
  assert.equal(remotePoint(rect, 1600, 900, 540, 2000).y, 1);
});
test('cursor overlay and pointer input use the same fitted image coordinates', () => {
  const rect = { left: 80, top: 35, width: 1726, height: 886 };
  const image = fittedRect(rect, 1600, 900);
  const point = remotePoint(rect, 1600, 900, image.left + image.width * .2, image.top + image.height * .8);
  assert.ok(Math.abs(point.x - .2) < 1e-12); assert.ok(Math.abs(point.y - .8) < 1e-12);
});
test('input waits for measurable frame and viewport dimensions', () => {
  const rect = { left: 0, top: 0, width: 800, height: 600 };
  for (const [w, h] of [[0,0],[0,1080],[1670,0]]) assert.equal(remotePoint(rect,w,h,10,10),null);
  assert.equal(remotePoint({ ...rect, width:0 },1670,1080,10,10),null);
  assert.equal(remotePoint(rect,1670,1080,NaN,10),null);
});

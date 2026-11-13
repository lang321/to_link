'use strict';
// REQ-REMOTE-20261105-01: per-device password hashes; scrypt runs outside the event loop.
const crypto = require('node:crypto');
const { promisify } = require('node:util');
const derive = promisify(crypto.scrypt);
const validPassword = value => typeof value === 'string' && value.length >= 8 && value.length <= 128;
async function hashPassword(password) {
  if (!validPassword(password)) throw new Error('密码需为 8～128 个字符');
  const salt = crypto.randomBytes(16).toString('hex');
  const hash = (await derive(password, salt, 32)).toString('hex');
  return { salt, hash };
}
async function verifyPassword(password, credential) {
  if (typeof password !== 'string' || password.length > 128) return false;
  const hash = await derive(password, credential.salt, 32);
  const expected = Buffer.from(credential.hash, 'hex');
  return expected.length === hash.length && crypto.timingSafeEqual(hash, expected);
}
module.exports = { hashPassword, verifyPassword, validPassword };

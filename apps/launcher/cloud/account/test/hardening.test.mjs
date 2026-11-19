/**
 * Hardening of the account service's own inputs (README.md › Keys): a stored password hash this code can't read is a wrong
 * password, not a server error; a key id seen for the first time must open a stored item key before its check value is kept.
 *
 * How it could fail: a corrupt or foreign hash answers 500, which tells an attacker the account exists; a new secret under an
 * old key id is accepted and then makes a second set of accounts.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { hex, random } from '../src/crypto.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call, resetClock } from './harness.mjs'

const EMAIL = 'hard@example.com'
const PASSWORD = 'correct horse battery'
const DEVICE = '0123456789abcdef0123456789abcdef'

afterEach(resetClock)

const unthrottled = () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  return env
}

test('a stored hash this code can\'t read is a wrong password: 401, the same as any other, and never a 500', async () => {
  const env = unthrottled()
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  env.DB.db.prepare('UPDATE accounts SET password_hash = ?').run('$argon2id$v=19$garbage')
  const answer = await call(env, 'POST', '/v1/session', { ip: '198.51.100.1', body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  assert.equal(answer.status, 401)
  assert.equal(answer.body.error, 'wrong-credentials')
})

test('a key id seen for the first time must open a stored item key: a wrong secret under a stored id is refused', async () => {
  const env = unthrottled()
  const good = env.ACCOUNT_SECRETS
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  // The check row is gone (say, a restored database): the first request must prove the secret before keeping a new check.
  env.DB.db.prepare('DELETE FROM keyring').run()
  env.ACCOUNT_SECRETS = `k1:${hex(random(32))}`
  const refused = await call(env, 'POST', '/v1/session', { ip: '198.51.100.2', body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  assert.equal(refused.status, 500)
  assert.equal(refused.body.error, 'keyring-mismatch')
  assert.equal(env.DB.rows('keyring').length, 0, 'no check value was kept for the wrong secret')
  env.ACCOUNT_SECRETS = good
  assert.equal((await call(env, 'POST', '/v1/session', { ip: '198.51.100.3', body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).status, 200)
})

test('a new key id with no account sealed under it is kept on first sight, as before', async () => {
  const env = unthrottled()
  const old = env.ACCOUNT_SECRETS
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  env.ACCOUNT_SECRETS = `k2:${hex(random(32))},${old}`
  assert.equal((await call(env, 'POST', '/v1/session', { ip: '198.51.100.4', body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).status, 200)
  assert.deepEqual(env.DB.rows('keyring').map(row => row.key_id).sort(), ['k1', 'k2'])
  assert.ok(limits.accountItems > 0)
})

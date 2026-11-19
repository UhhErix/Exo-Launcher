/**
 * The keyring's check values (README.md › Keys): each key id in ACCOUNT_SECRETS has its check value stored the first time
 * it is seen (an HMAC of a constant under that key). A configured id whose secret gives another value is refused with a
 * server error, before anything is read or made, so a bad rotation can't split one account in two or read every row as a
 * wrong password.
 *
 * How it could fail: a wrong secret under a known id makes a second account for every address (the digests change) and
 * wrong codes for the old ones; the check is skipped, or it answers as a wrong password.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { hex, random } from '../src/crypto.js'
import { account, call, resetClock } from './harness.mjs'

const EMAIL = 'check@example.com'
const PASSWORD = 'correct horse battery'
const DEVICE = '0123456789abcdef0123456789abcdef'

afterEach(resetClock)

const secret = id => `${id}:${hex(random(32))}`

test('a key id whose secret differs from the stored check value is refused as a server error, and nothing is made under it', async () => {
  const env = account()
  const good = env.ACCOUNT_SECRETS
  assert.equal((await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).status, 201)
  env.ACCOUNT_SECRETS = secret('k1')
  const made = await call(env, 'POST', '/v1/account', { body: { email: 'other@example.com', password: PASSWORD, device: DEVICE } })
  assert.equal(made.status, 500)
  assert.equal(made.body.error, 'keyring-mismatch')
  assert.equal(env.DB.rows('accounts').length, 1, 'no second account')
  const signedIn = await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  assert.equal(signedIn.body.error, 'keyring-mismatch', 'a sign-in is not answered as a wrong password either')
  env.ACCOUNT_SECRETS = good
  assert.equal((await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).status, 200)
})

test('a new key id is stored on first sight, and a later secret for it that differs is refused', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const added = secret('k2')
  env.ACCOUNT_SECRETS = `${added},${old}`
  assert.equal((await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).status, 200)
  assert.equal(env.DB.rows('keyring').map(row => row.key_id).sort().join(','), 'k1,k2')
  env.ACCOUNT_SECRETS = `${secret('k2')},${old}`
  assert.equal((await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).body.error, 'keyring-mismatch')
})

test('a keyring that is right is checked, and answered the same on every request', async () => {
  const env = account()
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const stored = env.DB.rows('keyring')
  for (let i = 0; i < 3; i++) assert.equal((await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).status, 200)
  assert.deepEqual(env.DB.rows('keyring'), stored, 'the check values are stored once, not rewritten')
})

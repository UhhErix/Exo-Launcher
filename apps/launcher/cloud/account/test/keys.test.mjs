/**
 * The keyring (README.md › Keys): ACCOUNT_SECRETS lists up to four keys, the first current. A key added on top of an old
 * one keeps every account working, and a row moves to the current key when its account signs in. Dropping a key that a
 * row still names breaks that row, and the Worker says so as a server error, not as a wrong password.
 *
 * How keys could fail: a keyring with a short, duplicate or too many keys is taken; a rotation locks accounts out or
 * loses their items; a row made under an old key stays on it forever; the item key or address digest is readable
 * without its key.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { afterEach, test } from 'node:test'
import { hex, random } from '../src/crypto.js'
import { keysNamedBy, namedRetired } from '../src/rotation.js'
import { account, call, resetClock } from './harness.mjs'

const EMAIL = 'keys@example.com'
const PASSWORD = 'correct horse battery'
const NEW = 'a brand new passphrase'
const DEVICE = '0123456789abcdef0123456789abcdef'
const OTHER = 'fedcba9876543210fedcba9876543210'

afterEach(resetClock)

const secret = id => `${id}:${hex(random(32))}`
const signIn = (env, device = DEVICE, password = PASSWORD) => call(env, 'POST', '/v1/session', { body: { email: EMAIL, password, device } })

test('the keyring: 1 to 4 keys, each "id:hex" of 32 bytes or more, ids unique; anything else is refused as not configured', async () => {
  const env = account()
  const ok = [`k1:${hex(random(32))}`, `k2:${hex(random(40))}`]
  env.ACCOUNT_SECRETS = ok.join(',')
  assert.equal((await signIn(env)).status, 401)
  for (const bad of [
    `k1:${hex(random(16))}`,
    `k1:${hex(random(32))},k1:${hex(random(32))}`,
    `K1:${hex(random(32))}`,
    `k1:${hex(random(32))},k2:${hex(random(32))},k3:${hex(random(32))},k4:${hex(random(32))},k5:${hex(random(32))}`,
    `k1:${hex(random(32))}x`,
    '',
  ]) {
    env.ACCOUNT_SECRETS = bad
    assert.equal((await signIn(env)).body.error, 'not-configured', bad.slice(0, 12))
  }
})

test('rotation: a key added on top keeps every account, its items and its recovery code; the row moves to the new key at sign-in', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const phone = made.body.token
  const put = await call(env, 'PUT', '/v1/sync', { token: phone, body: { items: [{ app: 'cinema', type: 'settings', id: 'main', value: '{"subs":"on"}', base: 0 }] } })
  assert.equal(put.status, 200)

  const fresh = secret('k2')
  env.ACCOUNT_SECRETS = `${fresh},${old}`
  assert.equal(env.DB.rows('accounts')[0].email_mac.slice(0, 3), 'k1:')

  const back = await signIn(env, OTHER)
  assert.equal(back.status, 200)
  const row = env.DB.rows('accounts')[0]
  assert.equal(row.email_mac.slice(0, 3), 'k2:')
  assert.equal(row.item_key[0], 2)
  assert.equal(String.fromCharCode(...row.item_key.slice(1, 3)), 'k2')
  assert.match(row.recovery_mac, /^k1:/)

  const pulled = await call(env, 'GET', '/v1/sync?after=0', { token: back.body.token })
  assert.equal(pulled.body.items[0].value, '{"subs":"on"}')

  const reset = await call(env, 'POST', '/v1/recovery/reset', { body: { email: EMAIL, code: made.body.recoveryCode, password: NEW, device: DEVICE } })
  assert.equal(reset.status, 200)
  assert.match(env.DB.rows('accounts')[0].recovery_mac, /^k2:/)
  assert.equal((await signIn(env, DEVICE, NEW)).status, 200)
})

test('after rotation a new account is made under the new key, and the old one still reads its own rows', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  env.ACCOUNT_SECRETS = `${secret('k2')},${old}`
  const made = await call(env, 'POST', '/v1/account', { body: { email: 'new@example.com', password: PASSWORD, device: DEVICE } })
  assert.equal(made.status, 201)
  const row = env.DB.rows('accounts').find(r => r.email_mac.startsWith('k2:'))
  assert.ok(row)
  assert.equal((await call(env, 'POST', '/v1/session', { body: { email: 'new@example.com', password: PASSWORD, device: OTHER } })).status, 200)
})

test('dropping a key that rows still name: the rows answer as wrong, and a session that reads items gets a server error, not the item', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  await call(env, 'PUT', '/v1/sync', { token: made.body.token, body: { items: [{ app: 'cinema', type: 'settings', id: 'main', value: '{"subs":"on"}', base: 0 }] } })
  env.ACCOUNT_SECRETS = secret('k2')
  assert.equal((await signIn(env)).body.error, 'wrong-credentials')
  const pulled = await call(env, 'GET', '/v1/sync?after=0', { token: made.body.token })
  assert.equal(pulled.status, 500)
  assert.ok(!JSON.stringify(pulled.body).includes('subs'))
  env.ACCOUNT_SECRETS = old
  assert.equal((await call(env, 'GET', '/v1/sync?after=0', { token: made.body.token })).body.items[0].value, '{"subs":"on"}')
})
test('rotation refuses to retire a key that a recovery code still uses; sign-in moves the rest, a new code moves the last', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  env.ACCOUNT_SECRETS = `${secret('k2')},${old}`
  const back = await signIn(env, DEVICE)
  assert.equal(back.status, 200)
  const rows = env.DB.rows('accounts')
  assert.equal(rows[0].email_mac.slice(0, 3), 'k2:')
  assert.match(rows[0].recovery_mac, /^k1:/)
  assert.deepEqual(namedRetired(rows, ['k1']), [['recovery_mac', 'k1']])
  const fresh = await call(env, 'POST', '/v1/recovery', { token: back.body.token, body: { password: PASSWORD } })
  assert.equal(fresh.status, 200)
  assert.deepEqual(namedRetired(env.DB.rows('accounts'), ['k1']), [])
})

test('the retirement check reads the item key id too, and the current key never retires', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const row = env.DB.rows('accounts')[0]
  assert.deepEqual(keysNamedBy(row).map(([column, id]) => [column, id]), [['email_mac', 'k1'], ['recovery_mac', 'k1'], ['item_key', 'k1']])
  const hexRow = { ...row, item_key: hex(Uint8Array.from(row.item_key)) }
  assert.deepEqual(namedRetired([hexRow], ['k1']).map(([column]) => column), ['email_mac', 'recovery_mac', 'item_key'])
  assert.deepEqual(namedRetired([row], ['k9']), [])
  assert.equal(made.status, 201)
})

test('scripts/retire-key.mjs: exit 1 while a row names the key, exit 0 once none does, and refuses the current key', async () => {
  const env = account()
  const old = env.ACCOUNT_SECRETS
  const rowsOf = () => JSON.stringify(env.DB.rows('accounts').map(row => ({ ...row, item_key: hex(Uint8Array.from(row.item_key)) })))
  const run = (...args) => spawnSync(process.execPath, [fileURLToPath(new URL('../scripts/retire-key.mjs', import.meta.url)), ...args], { input: rowsOf(), encoding: 'utf8' })
  assert.equal(run('k2', 'k1').status, 0, 'no account yet names k1')
    await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
    env.ACCOUNT_SECRETS = `${secret('k2')},${old}`
    await signIn(env, DEVICE)
    const refused = run('k2', 'k1')
    assert.equal(refused.status, 1)
    assert.match(refused.stderr, /recovery_mac/)
    assert.doesNotMatch(refused.stderr, /[0-9a-f]{32}/, 'no account id in the output')
    assert.equal(run('k1', 'k1').status, 1, 'the current key never retires')
    await call(env, 'POST', '/v1/recovery', { token: (await signIn(env, DEVICE)).body.token, body: { password: PASSWORD } })
    assert.equal(run('k2', 'k1').status, 0)
})


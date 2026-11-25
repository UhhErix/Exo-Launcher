/**
 * The account Worker's tests (README.md): the real worker.js over the D1 stand-in and the rate limit of test/harness.mjs.
 * Time moves only through later(). Nothing reaches the network.
 *
 * How the account service could fail: an address, password, recovery code or item key stored in clear; a password
 * hashed under other parameters or another function; sign-in that answers differently, or does different work, for an
 * unknown address; one client address locking a real account for everyone (10 wrong tries from one address lock only
 * that pair; 100 an hour across addresses only delay the wrong answers; nothing locks but the pair); a lock outliving 15 minutes; a session
 * outlives its device's removal, the password change that should end it, or the account; the old password or recovery
 * code still works after a change; a recovery reset loses the account's data; a rate limit missing or shared across
 * addresses; the removed features (cron, outbound requests, logs, providers) come back.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { afterEach, test } from 'node:test'
import { argon2id } from '@noble/hashes/argon2.js'
import { dummyHash, passwordRight } from '../src/accounts.js'
import { hex, random, sha256 } from '../src/crypto.js'
import { COMMON_PASSWORDS } from '../src/common-passwords.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call, later, resetClock } from './harness.mjs'

const EMAIL = 'Owner@Example.com '
const PASSWORD = 'correct horse battery'
const NEW = 'a brand new passphrase'
const DEVICE = '0123456789abcdef0123456789abcdef'
const OTHER = 'fedcba9876543210fedcba9876543210'
const WRONG = 'wrong password here'
const OWNER_PC = '198.51.100.7'
const THEIR_PC = '198.51.100.8'
const TOKEN = /^[A-Za-z0-9_-]{43}$/
const CODE = /^[0-9A-Z]{4}(-[0-9A-Z]{4}){5}$/
const PHC = /^\$argon2id\$v=19\$m=19456,t=2,p=1\$[A-Za-z0-9+/]{22}\$[A-Za-z0-9+/]{43}$/
/** A stored hash's parameters: the segments between the algorithm and the salt. */
const params = hash => hash.split('$').slice(1, 4).join('$')

// The concurrency tests below send up to 12 tries at once, past the request cap (busy.test.mjs tests that cap).
const hashesInFlight = limits.hashesInFlight
afterEach(() => {
  resetClock()
  limits.hashesInFlight = hashesInFlight
})

/** An env whose public-route limit doesn't get in the way of the lockout tests (the limit has its own test). */
const unthrottled = () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  limits.hashesInFlight = 100
  return env
}

const signUp = (env, extra = {}) => call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE, ...extra } })
const signIn = (env, email = EMAIL, password = PASSWORD, device = DEVICE, ip) => call(env, 'POST', '/v1/session', { ip, body: { email, password, device } })

test('sign-up: the address only as a keyed digest, the password as Argon2id at the OWASP parameters, a recovery code shown once', async () => {
  const env = account()
  const made = await signUp(env)
  assert.equal(made.status, 201)
  assert.match(made.body.token, TOKEN)
  assert.match(made.body.account.id, /^[0-9a-f]{32}$/)
  assert.match(made.body.recoveryCode, CODE)
  const [row] = env.DB.rows('accounts')
  assert.match(row.password_hash, PHC)
  assert.equal(await passwordRight({ password_hash: row.password_hash }, PASSWORD), true)
  assert.equal(await passwordRight({ password_hash: row.password_hash }, WRONG), false)
  assert.match(row.email_mac, /^k1:[0-9a-f]{64}$/)
  assert.notEqual(row.email_mac.slice(3), hex(await sha256('owner@example.com')))
  assert.ok(!JSON.stringify(env.DB.rows('accounts')).includes('owner'))
  assert.notEqual(row.recovery_mac, hex(await sha256(made.body.recoveryCode.replaceAll('-', ''))))
  assert.match(row.recovery_mac, /^k1:[0-9a-f]{64}$/)
  // The item key names its key id ("k1", one length byte), then a 32-byte key sealed: nonce (12), ciphertext (43), tag (16).
  assert.equal(row.item_key[0], 2)
  assert.equal(row.item_key.length, 1 + 2 + 12 + 43 + 16)
})

test('sign-up refuses a bad address, a short password, a device that is not one, and an address that has an account', async () => {
  const env = account()
  assert.equal((await signUp(env, { email: 'nope' })).body.error, 'bad-email')
  assert.equal((await signUp(env, { password: 'short' })).body.error, 'bad-password')
  assert.equal((await signUp(env, { device: 'XYZ' })).body.error, 'bad-request')
  assert.equal((await signUp(env)).status, 201)
  const taken = await signUp(env, { email: 'owner@example.COM' })
  assert.equal(taken.status, 409)
  assert.equal(taken.body.error, 'email-taken')
})

test('sign-in: the right password makes a session; a wrong password and an unknown address get the same answer', async () => {
  const env = account()
  const made = await signUp(env)
  const right = await signIn(env, 'owner@example.com', PASSWORD, OTHER)
  assert.equal(right.status, 200)
  assert.match(right.body.token, TOKEN)
  assert.equal(right.body.account.id, made.body.account.id)
  const wrong = await signIn(env, EMAIL, WRONG)
  const nobody = await signIn(env, 'nobody@example.com', WRONG)
  assert.equal(wrong.status, 401)
  assert.deepEqual(wrong.body, { error: 'wrong-credentials' })
  assert.equal(nobody.status, wrong.status)
  assert.deepEqual(nobody.body, wrong.body)
})

test('sign-in does the same work whether or not the address has an account: an unknown address is checked against a dummy hash with the same parameters', async () => {
  const env = account()
  await signUp(env)
  const [row] = env.DB.rows('accounts')
  assert.equal(params(await dummyHash()), params(row.password_hash))
  assert.equal((await signIn(env, 'nobody@example.com', WRONG)).body.error, 'wrong-credentials')
  assert.equal((await signIn(env, EMAIL, WRONG)).body.error, 'wrong-credentials')
})

test('a hash made under other parameters is rehashed at sign-in, and the password still signs in afterwards', async () => {
  const env = unthrottled()
  await signUp(env)
  const b64 = bytes => Buffer.from(bytes).toString('base64').replace(/=+$/, '')
  const salt = new Uint8Array(16).fill(3)
  const old = `$argon2id$v=19$m=4096,t=1,p=1$${b64(salt)}$${b64(argon2id(new TextEncoder().encode(PASSWORD), salt, { t: 1, m: 4096, p: 1, dkLen: 32 }))}`
  env.DB.db.prepare('UPDATE accounts SET password_hash = ?').run(old)
  assert.equal((await signIn(env, EMAIL, PASSWORD)).status, 200)
  const [row] = env.DB.rows('accounts')
  assert.notEqual(row.password_hash, old)
  assert.equal(params(row.password_hash), params(await dummyHash()))
  assert.equal(await passwordRight({ password_hash: row.password_hash }, PASSWORD), true)
  assert.equal((await signIn(env, EMAIL, PASSWORD, OTHER)).status, 200)
  assert.equal((await signIn(env, EMAIL, WRONG, OTHER)).status, 401)
})

test('a hash from the reference Argon2id implementation (hash-wasm, the same parameters) is accepted: the encoding is the standard one', async () => {
  const reference = '$argon2id$v=19$m=19456,t=2,p=1$BwcHBwcHBwcHBwcHBwcHBw$WLRGUa13/B3MXznFse33FId4O++DaYL6ulYUMEhpRqE'
  assert.equal(await passwordRight({ password_hash: reference }, 'correct horse battery'), true)
  assert.equal(await passwordRight({ password_hash: reference }, WRONG), false)
})

test('lockout: 10 wrong tries from one client address lock that pair for 15 minutes, even for the right password', async () => {
  const env = unthrottled()
  await signUp(env)
  for (let i = 0; i < 9; i++) assert.equal((await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)).status, 401)
  const tenth = await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal(tenth.status, 429)
  assert.equal(tenth.body.error, 'locked')
  const retry = Number(tenth.headers.get('Retry-After'))
  assert.ok(retry > 890 && retry <= 900, `Retry-After ${retry}`)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 429)
  later(14 * 60)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 429)
  later(60 + 1)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 200)
})

test('lockout: the same wrong tries from another client address do not lock the owner out of their own address', async () => {
  const env = unthrottled()
  await signUp(env)
  for (let i = 0; i < 10; i++) await signIn(env, EMAIL, WRONG, DEVICE, THEIR_PC)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 200)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, THEIR_PC)).status, 429)
})

test('lockout: an address with no account locks per pair too, so the answer is the same as for a real one', async () => {
  const env = unthrottled()
  await signUp(env)
  for (let i = 0; i < 10; i++) await signIn(env, 'nobody@example.com', WRONG, DEVICE, OWNER_PC)
  assert.equal((await signIn(env, 'nobody@example.com', WRONG, DEVICE, OWNER_PC)).body.error, 'locked')
  assert.equal((await signIn(env, 'nobody@example.com', WRONG, DEVICE, THEIR_PC)).body.error, 'wrong-credentials')
})

test('lockout: a right password clears its pair\'s count, and the count restarts after 15 minutes', async () => {
  const env = unthrottled()
  await signUp(env)
  for (let i = 0; i < 9; i++) await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 200)
  for (let i = 0; i < 9; i++) assert.equal((await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)).status, 401)
  later(15 * 60 + 1)
  for (let i = 0; i < 9; i++) assert.equal((await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)).status, 401)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 200)
})

test('sessions: one per PC, the caller marked current, a removed PC ends its sessions, sign-out ends this one', async () => {
  const env = account()
  const mine = (await signUp(env)).body.token
  const laptop = (await signIn(env, EMAIL, PASSWORD, OTHER)).body.token
  const listed = await call(env, 'GET', '/v1/sessions', { token: mine })
  assert.equal(listed.body.sessions.length, 2)
  assert.equal(listed.body.sessions.find(s => s.current).device, DEVICE)
  const again = (await signIn(env, EMAIL, PASSWORD, DEVICE)).body.token
  assert.equal((await call(env, 'GET', '/v1/account', { token: mine })).body.error, 'signed-out')
  assert.equal((await call(env, 'GET', '/v1/sessions', { token: again })).body.sessions.length, 2)
  assert.equal((await call(env, 'DELETE', `/v1/sessions/${OTHER}`, { token: again })).status, 204)
  assert.equal((await call(env, 'GET', '/v1/account', { token: laptop })).body.error, 'signed-out')
  assert.equal((await call(env, 'POST', '/v1/signout', { token: again })).status, 204)
  assert.equal((await call(env, 'GET', '/v1/account', { token: again })).body.error, 'signed-out')
})

test('sessions: two apps on the same PC share one device id without signing each other out', async () => {
  const env = account()
  const launcher = (await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE, app: 'launcher' } })).body.token
  const cinema = (await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE, app: 'cinema' } })).body.token
  assert.equal((await call(env, 'GET', '/v1/account', { token: launcher })).status, 200)
  assert.equal((await call(env, 'GET', '/v1/account', { token: cinema })).status, 200)
  const launcherAgain = (await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE, app: 'launcher' } })).body.token
  assert.equal((await call(env, 'GET', '/v1/account', { token: launcher })).body.error, 'signed-out')
  assert.equal((await call(env, 'GET', '/v1/account', { token: cinema })).status, 200)
  const listed = await call(env, 'GET', '/v1/sessions', { token: launcherAgain })
  assert.equal(listed.body.sessions.length, 2)
  assert.equal((await call(env, 'POST', '/v1/signout', { token: cinema })).status, 204)
  assert.equal((await call(env, 'GET', '/v1/account', { token: launcherAgain })).status, 200)
  assert.equal(env.DB.rows('devices').length, 1)
  assert.equal((await call(env, 'DELETE', `/v1/sessions/${DEVICE}`, { token: launcherAgain })).status, 204)
  assert.equal((await call(env, 'GET', '/v1/account', { token: launcherAgain })).body.error, 'signed-out')
  assert.equal(env.DB.rows('devices').length, 0)
})

test('password change: the current password is needed, other PCs are signed out, this one stays, the old password stops working', async () => {
  const env = account()
  const mine = (await signUp(env)).body.token
  const laptop = (await signIn(env, EMAIL, PASSWORD, OTHER)).body.token
  const refused = await call(env, 'PUT', '/v1/password', { token: mine, body: { current: WRONG, password: NEW } })
  assert.equal(refused.status, 401)
  assert.equal(refused.body.error, 'wrong-password')
  assert.equal((await call(env, 'PUT', '/v1/password', { token: mine, body: { current: PASSWORD, password: 'short' } })).body.error, 'bad-password')
  assert.equal((await call(env, 'PUT', '/v1/password', { token: mine, body: { current: PASSWORD, password: NEW } })).status, 200)
  assert.equal((await call(env, 'GET', '/v1/account', { token: laptop })).body.error, 'signed-out')
  assert.equal((await call(env, 'GET', '/v1/account', { token: mine })).status, 200)
  assert.equal((await signIn(env, EMAIL, PASSWORD)).body.error, 'wrong-credentials')
  assert.equal((await signIn(env, EMAIL, NEW)).status, 200)
})

test('recovery: the code sets a new password, ends every session and gives a new code; the old code and password stop working', async () => {
  const env = account()
  const made = await signUp(env)
  const laptop = (await signIn(env, EMAIL, PASSWORD, OTHER)).body.token
  const reset = (code, password = NEW) => call(env, 'POST', '/v1/recovery/reset', { body: { email: EMAIL, code, password, device: DEVICE } })
  assert.equal((await reset('B2C3D4E5F6G7H8J9K2M3N4P5')).body.error, 'wrong-code')
  assert.equal((await reset('nope')).body.error, 'bad-code')
  const old = made.body.recoveryCode
  const done = await reset(old.toLowerCase().replaceAll('-', ' '))
  assert.equal(done.status, 200)
  assert.match(done.body.recoveryCode, CODE)
  assert.notEqual(done.body.recoveryCode, old)
  assert.equal((await call(env, 'GET', '/v1/account', { token: made.body.token })).body.error, 'signed-out')
  assert.equal((await call(env, 'GET', '/v1/account', { token: laptop })).body.error, 'signed-out')
  assert.equal((await signIn(env, EMAIL, PASSWORD)).body.error, 'wrong-credentials')
  assert.equal((await signIn(env, EMAIL, NEW)).status, 200)
  assert.equal((await reset(old)).body.error, 'wrong-code')
})

test('recovery code: made again with the password, and the old one stops working', async () => {
  const env = account()
  const made = await signUp(env)
  const mine = made.body.token
  assert.equal((await call(env, 'POST', '/v1/recovery', { token: mine, body: { password: WRONG } })).body.error, 'wrong-password')
  const fresh = await call(env, 'POST', '/v1/recovery', { token: mine, body: { password: PASSWORD } })
  assert.equal(fresh.status, 200)
  assert.match(fresh.body.recoveryCode, CODE)
  const old = await call(env, 'POST', '/v1/recovery/reset', { body: { email: EMAIL, code: made.body.recoveryCode, password: NEW, device: DEVICE } })
  assert.equal(old.body.error, 'wrong-code')
  const fine = await call(env, 'POST', '/v1/recovery/reset', { body: { email: EMAIL, code: fresh.body.recoveryCode, password: NEW, device: DEVICE } })
  assert.equal(fine.status, 200)
})

test('deleting the account needs its password; with it the account, its sessions and its lock counts go, and the address can sign up again', async () => {
  const env = account()
  const mine = (await signUp(env)).body.token
  const laptop = (await signIn(env, EMAIL, PASSWORD, OTHER)).body.token
  await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  const refused = await call(env, 'DELETE', '/v1/account', { token: mine, body: { password: WRONG } })
  assert.equal(refused.body.error, 'wrong-password')
  assert.equal(env.DB.rows('accounts').length, 1)
  assert.equal((await call(env, 'DELETE', '/v1/account', { token: mine, body: { password: PASSWORD } })).status, 204)
  assert.equal(env.DB.rows('accounts').length, 0)
  assert.equal(env.DB.rows('sessions').length, 0)
  assert.equal(env.DB.rows('sign_in_failures').length, 0)
  assert.equal((await call(env, 'GET', '/v1/account', { token: laptop })).body.error, 'signed-out')
  assert.equal((await signIn(env, EMAIL, PASSWORD)).body.error, 'wrong-credentials')
  assert.equal((await signUp(env)).status, 201)
})

test('the public routes allow 10 a minute from one address, then slow-down; another address is not held back', async () => {
  const env = account()
  const attempt = (ip, email) => call(env, 'POST', '/v1/session', { ip, body: { email, password: WRONG, device: DEVICE } })
  for (let i = 0; i < 10; i++) assert.equal((await attempt('203.0.113.9', `n${i}@example.com`)).body.error, 'wrong-credentials')
  const refused = await attempt('203.0.113.9', 'z@example.com')
  assert.equal(refused.status, 429)
  assert.equal(refused.body.error, 'slow-down')
  assert.equal((await attempt('203.0.113.10', 'z@example.com')).body.error, 'wrong-credentials')
})

test('sign-in failures: rows whose count has ended are pruned, 100 on each try, so the table stops growing', async () => {
  const env = unthrottled()
  await signUp(env)
  const ended = Math.floor(Date.now() / 1000) - 2 * 60 * 60
  const insert = env.DB.db.prepare('INSERT INTO sign_in_failures (scope, failures, window_start, locked_until) VALUES (?, 1, ?, 0)')
  for (let i = 0; i < 250; i++) insert.run(`p|gone${i}|x`, ended)
  const left = () => env.DB.rows('sign_in_failures').filter(r => r.scope.startsWith('p|gone')).length
  await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal(left(), 150)
  await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal(left(), 50)
  await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal(left(), 0)
})

test('sign-in failures: an unlocked pair is kept for 15 minutes from its first wrong try, then pruned; a lock is kept until it ends', async () => {
  const env = unthrottled()
  await signUp(env)
  const pairs = () => env.DB.rows('sign_in_failures').filter(r => r.scope.startsWith('p|')).length
  await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal(pairs(), 1)
  later(14 * 60)
  await signIn(env, EMAIL, WRONG, DEVICE, THEIR_PC)
  assert.equal(pairs(), 2)
  later(2 * 60)
  await signIn(env, EMAIL, WRONG, DEVICE, THEIR_PC)
  assert.equal(pairs(), 1, 'the owner pair, unlocked and past its 15 minutes, is gone')
  for (let i = 0; i < 9; i++) await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)
  assert.equal((await signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)).body.error, 'locked')
  later(14 * 60)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).body.error, 'locked', 'a lock is kept until it ends')
  later(2 * 60)
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 200)
})

test('tries are reserved before they are checked: 12 wrong tries at once from one pair, 10 are admitted and the other 2 wait for the lock', async () => {
  const env = unthrottled()
  await signUp(env)
  const answers = await Promise.all(Array.from({ length: 12 }, () => signIn(env, EMAIL, WRONG, DEVICE, OWNER_PC)))
  // The tenth wrong try is the one that locks the pair, so it answers locked too (as in the lockout test above).
  assert.equal(answers.filter(a => a.status === 401).length, 9)
  const refused = answers.filter(a => a.status === 429)
  assert.equal(refused.length, 3)
  assert.ok(refused.every(a => a.body.error === 'locked' && Number(a.headers.get('Retry-After')) >= 1))
  assert.equal((await signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)).status, 429)
})

test('a right try in flight holds its place too: 10 tries at once with the right password among them all pass, and a count of 0 is left', async () => {
  const env = unthrottled()
  await signUp(env)
  const answers = await Promise.all(Array.from({ length: 10 }, () => signIn(env, EMAIL, PASSWORD, DEVICE, OWNER_PC)))
  assert.ok(answers.every(a => a.status === 200))
  assert.equal(env.DB.rows('sign_in_failures').filter(r => r.scope.startsWith('p|')).every(r => r.pending === 0 && r.failures === 0), true)
})

test('per-account caps: the sessions beyond 50 end, the least recently used first', async () => {
  const env = account()
  const made = await signUp(env)
  const { newSession } = await import('../src/accounts.js')
  for (let i = 0; i < 51; i++) await newSession(env.DB, made.body.account.id, hex(random(16)))
  const sessions = env.DB.rows('sessions').filter(s => s.account === made.body.account.id)
  assert.equal(sessions.length, limits.accountSessions)
  assert.equal(sessions.filter(s => s.device === DEVICE).length, 0, 'the first PC is the oldest and ends')
})

test('per-account caps: a PUT that would take the account past its item count or its stored bytes is refused as account-full', async () => {
  const env = account()
  const mine = await signUp(env)
  const token = mine.body.token
  const accountId = mine.body.account.id
  const row = env.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, ?, 0, 0, ?)')
  for (let i = 0; i < limits.accountItems - 1; i++) row.run(accountId, 'launcher', 'settings', `full-${i}`, i + 1, null)
  assert.equal((await call(env, 'PUT', '/v1/sync', { token, body: { items: [{ app: 'launcher', type: 'settings', id: 'last', value: '{}', base: 0 }] } })).status, 200)
  const over = await call(env, 'PUT', '/v1/sync', { token, body: { items: [{ app: 'launcher', type: 'settings', id: 'one-more', value: '{}', base: 0 }] } })
  assert.equal(over.status, 413)
  assert.equal(over.body.error, 'account-full')
  // The bytes cap, on a fresh account: one item of the cap's size in the database leaves no room for another byte.
  const other = account()
  const again = await signUp(other)
  other.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, 1, 0, 0, ?)')
    .run(again.body.account.id, 'launcher', 'settings', 'big', new Uint8Array(limits.accountBytes))
  const bytes = await call(other, 'PUT', '/v1/sync', { token: again.body.token, body: { items: [{ app: 'launcher', type: 'settings', id: 'tiny', value: '{}', base: 0 }] } })
  assert.equal(bytes.body.error, 'account-full')
})

test('common passwords are refused at sign-up, a change and a reset, whatever their case; a passphrase that is not on the list is not', async () => {
  const env = unthrottled()
  const common = [...COMMON_PASSWORDS][0]
  assert.ok(common.length >= 12)
  assert.equal((await signUp(env, { password: common })).body.error, 'common-password')
  assert.equal((await signUp(env, { password: common.toUpperCase() })).body.error, 'common-password')
  const made = await signUp(env)
  assert.equal(made.status, 201)
  assert.equal((await call(env, 'PUT', '/v1/password', { token: made.body.token, body: { current: PASSWORD, password: common } })).body.error, 'common-password')
  const reset = await call(env, 'POST', '/v1/recovery/reset', { body: { email: EMAIL, code: made.body.recoveryCode, password: common, device: DEVICE } })
  assert.equal(reset.body.error, 'common-password')
  assert.equal((await call(env, 'PUT', '/v1/password', { token: made.body.token, body: { current: PASSWORD, password: NEW } })).status, 200)
})

test('the password routes take the edge limit too: change, delete and a new recovery code count with sign-up and sign-in, from one address', async () => {
  const env = account()
  const ip = '203.0.113.50'
  for (let i = 0; i < 10; i++) await call(env, 'PUT', '/v1/password', { ip, body: {} })
  assert.equal((await call(env, 'PUT', '/v1/password', { ip, body: {} })).body.error, 'slow-down')
  assert.equal((await call(env, 'DELETE', '/v1/account', { ip, body: {} })).body.error, 'slow-down')
  assert.equal((await call(env, 'POST', '/v1/recovery', { ip, body: {} })).body.error, 'slow-down')
  assert.equal((await call(env, 'PUT', '/v1/password', { ip: '203.0.113.51', body: {} })).body.error, 'signed-out')
})

test('wrangler.toml names what the code reads: the secret ACCOUNT_SECRETS, the D1 binding DB, the rate limit SESSION_TRIES', () => {
  const toml = readFileSync(fileURLToPath(new URL('../wrangler.toml', import.meta.url)), 'utf8')
  assert.match(toml, /wrangler secret put ACCOUNT_SECRETS\b/)
  assert.doesNotMatch(toml, /never changed/)
  assert.match(toml, /binding = "DB"/)
  assert.match(toml, /name = "SESSION_TRIES"/)
})

test('a sign-up that loses a race to another for its address answers email-taken (409), not a server error', async () => {
  // The other sign-up lands between this one's check and its insert: the check is made to see no account, as it would have.
  const env = account()
  await signUp(env)
  const prepare = env.DB.prepare.bind(env.DB)
  env.DB.prepare = sql => (sql.includes('WHERE email_mac IN') ? { bind: () => ({ first: async () => null }) } : prepare(sql))
  const raced = await signUp(env, { device: OTHER })
  env.DB.prepare = prepare
  assert.equal(raced.status, 409)
  assert.equal(raced.body.error, 'email-taken')
  assert.equal(env.DB.rows('accounts').length, 1)
})

test('every answer carries HSTS for the host (max-age a year, no subdomains, no preload) and nosniff, errors included', async () => {
  const env = account()
  const made = await signUp(env)
  const answers = [
    made,
    await call(env, 'GET', '/v1/account', { token: made.body.token }),
    await call(env, 'POST', '/v1/signout', { token: made.body.token }),
    await call(env, 'GET', '/v1/nothing'),
    await call(env, 'GET', '/v1/session'),
    await call(env, 'POST', '/v1/session', { headers: { 'Content-Type': 'text/plain' }, body: '{}' }),
    await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: WRONG, device: DEVICE } }),
  ]
  for (const answer of answers) {
    assert.equal(answer.headers.get('Strict-Transport-Security'), 'max-age=31536000')
    assert.equal(answer.headers.get('X-Content-Type-Options'), 'nosniff')
  }
  assert.ok(answers.some(answer => answer.status === 204), 'a 204 is one of them')
})

test('routes: unknown paths 404, another method 405, a non-JSON body 415, a bad JSON body 400, no CORS, answers never cached', async () => {
  const env = account()
  assert.equal((await call(env, 'GET', '/v1/nothing')).status, 404)
  assert.equal((await call(env, 'GET', '/v1/session')).body.error, 'method-not-allowed')
  const form = await call(env, 'POST', '/v1/session', { headers: { 'Content-Type': 'text/plain' }, body: '{}' })
  assert.equal(form.body.error, 'json-only')
  assert.equal((await call(env, 'POST', '/v1/session', { body: '{' })).body.error, 'bad-json')
  const answer = await signUp(env)
  assert.equal(answer.headers.get('Cache-Control'), 'no-store')
  assert.equal(answer.headers.get('Access-Control-Allow-Origin'), null)
})

test('the Worker answers 503 when ACCOUNT_SECRETS is missing or not a keyring', async () => {
  const env = account()
  env.ACCOUNT_SECRETS = 'k1:abcd'
  assert.equal((await signIn(env)).status, 503)
})

test('nothing the spec removed is left: no cron, no outbound request, no logs, no provider or email config', async () => {
  const { readFileSync, readdirSync } = await import('node:fs')
  const { fileURLToPath } = await import('node:url')
  const src = fileURLToPath(new URL('../src/', import.meta.url))
  // The password list is data (its words are passwords, some of them provider names): it is left out of the word scan.
  const code = readdirSync(src).filter(name => name !== 'common-passwords.js').map(name => readFileSync(src + name, 'utf8')).join('\n')
  assert.ok(!/console\./.test(code), 'the Worker logs nothing')
  assert.ok(!/\benv\.(policy|wait)\b/.test(code), 'no test hook (env.policy, env.wait) in production code')
  assert.ok(!/pbkdf2/i.test(code), 'no PBKDF2 in the Worker: WebCrypto refuses more than 100,000 rounds there, and the hash is Argon2id')
  assert.ok(!/(?<!async )\bfetch\(/.test(code), 'the Worker makes no outbound request (its own async fetch handler aside)')
  assert.ok(!/scheduled|GOOGLE|APPLE|DEV_KEY|RESEND|send_email|email_codes|identities|ACCOUNT_SECRET\b/i.test(code), 'no provider, email, cron or single-key secret left in the code')
  const toml = readFileSync(fileURLToPath(new URL('../wrangler.toml', import.meta.url)), 'utf8')
  assert.ok(!/\[triggers\]|crons|send_email|GOOGLE|APPLE|DEV_KEY/.test(toml), 'wrangler.toml declares no cron, email or dev sign-in')
  // The dev copy is the only [env.dev] block: it has no vars (so no dev-only setting), and the live config above it has no env keys.
  const at = toml.indexOf('[env.dev]')
  assert.ok(at > 0, 'the dev environment is declared')
  assert.ok(!/\[env\.dev\.vars\]|\bvars\s*=/.test(toml), 'no vars on any environment: no dev-only setting anywhere in the config')
  assert.ok(!/\benv\./.test(toml.slice(0, at)), 'live (top-level) config has no environment keys')
  assert.deepEqual([...new Set([...toml.matchAll(/^\[env\.([a-z]+)/gm)].map(m => m[1]))], ['dev'], 'only the dev environment exists')
})

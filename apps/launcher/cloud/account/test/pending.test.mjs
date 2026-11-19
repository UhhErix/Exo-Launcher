/**
 * The pending slot of a try (README.md › Limits): a try takes one place on its pair while it is checked, and gives it back
 * when it is answered, whatever the answer and whatever fails on the way. A slot that never comes back (a D1 error between
 * the reservation and the answer) is treated as none once it is 5 minutes old.
 *
 * How it could fail: a D1 error leaves a pair one try short for good, so that the owner is locked out after 9 tries; a
 * stale slot blocks the pair forever; a stale slot is kept in the table.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { digestOf } from '../src/accounts.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call, resetClock } from './harness.mjs'

const EMAIL = 'pending@example.com'
const PASSWORD = 'correct horse battery'
const WRONG = 'wrong password here'
const DEVICE = '0123456789abcdef0123456789abcdef'
const OWNER_PC = '198.51.100.7'

afterEach(resetClock)

const unthrottled = () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  return env
}
const signIn = (env, password, ip = OWNER_PC) => call(env, 'POST', '/v1/session', { ip, body: { email: EMAIL, password, device: DEVICE } })

/** The next statement whose SQL contains marker fails with a D1 error, once. */
function failOnce(env, marker) {
  const prepare = env.DB.prepare.bind(env.DB)
  const down = async () => {
    throw new Error('D1 unavailable')
  }
  let armed = true
  env.DB.prepare = sql => {
    if (armed && sql.includes(marker)) {
      armed = false
      return { bind: () => ({ first: down, run: down, all: down }) }
    }
    return prepare(sql)
  }
}

const pendingOf = (env, scope) => env.DB.rows('sign_in_failures').find(row => row.scope === scope)?.pending

test('a D1 error while a wrong try is answered gives its slot back: the pair is not one try short afterwards', async () => {
  const env = unthrottled()
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const pair = `p|${env.DB.rows('accounts')[0].email_mac}|${await digestOf(env, 'ip', OWNER_PC)}`
  failOnce(env, 'pending - 1,')
  assert.equal((await signIn(env, WRONG)).status, 500)
  assert.equal(pendingOf(env, pair), 0)
  for (let i = 0; i < 9; i++) assert.equal((await signIn(env, WRONG)).status, 401, `try ${i + 1}`)
  assert.equal((await signIn(env, WRONG)).body.error, 'locked', 'the tenth wrong try locks, as it should')
})

test('a D1 error while a right try is answered gives its slot back too', async () => {
  const env = unthrottled()
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const pair = `p|${env.DB.rows('accounts')[0].email_mac}|${await digestOf(env, 'ip', OWNER_PC)}`
  failOnce(env, 'failures = 0 WHERE scope')
  assert.equal((await signIn(env, PASSWORD)).status, 500)
  assert.equal(pendingOf(env, pair), 0)
  assert.equal((await signIn(env, PASSWORD)).status, 200)
})

test('a slot older than 5 minutes is a try that never answered: it is cleared, and does not block the pair', async () => {
  const env = unthrottled()
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const pair = `p|${env.DB.rows('accounts')[0].email_mac}|${await digestOf(env, 'ip', OWNER_PC)}`
  const now = Math.floor(Date.now() / 1000)
  env.DB.db.prepare('INSERT INTO sign_in_failures (scope, failures, window_start, locked_until, pending, pending_at) VALUES (?, 0, ?, 0, ?, ?)')
    .run(pair, now, limits.lockFailures, now - limits.pendingSeconds - 1)
  assert.equal((await signIn(env, PASSWORD)).status, 200, 'ten stuck slots would have locked the pair')
  assert.equal(pendingOf(env, pair), 0)
})

test('a slot that is still young blocks the pair as before', async () => {
  const env = unthrottled()
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const pair = `p|${env.DB.rows('accounts')[0].email_mac}|${await digestOf(env, 'ip', OWNER_PC)}`
  const now = Math.floor(Date.now() / 1000)
  env.DB.db.prepare('INSERT INTO sign_in_failures (scope, failures, window_start, locked_until, pending, pending_at) VALUES (?, 0, ?, 0, ?, ?)')
    .run(pair, now, limits.lockFailures, now)
  assert.equal((await signIn(env, PASSWORD)).body.error, 'locked')
})

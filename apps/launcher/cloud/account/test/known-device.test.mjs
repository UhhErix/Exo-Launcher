/**
 * Devices the account knows (README.md › Limits). A device proves it is the account's own with the token it was given at its
 * last sign-in, sign-up, password change or reset. While the address is under its wrong-try count (ceiling.test.mjs), a
 * known device doesn't wait, and an unknown one does. Nothing is refused: the right password always gets through, and the
 * token is what a device proves, not its device id (ids are listed to every signed-in PC). A token ends when its device is
 * revoked, signs out, changes or resets the password, or goes unseen for idleSeconds. The previous token counts once more.
 *
 * How it could fail: a device id alone skips the wait; a revoked, signed-out, changed or reset device keeps skipping it; a
 * token of another device or account skips it; the lookup runs only for real addresses, so its time says who has an account;
 * a lost answer leaves a device unable to prove itself; a device with no session left stays known.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, beforeEach, test } from 'node:test'
import { timing } from '../src/accounts.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call, later, resetClock } from './harness.mjs'

const EMAIL = 'known@example.com'
const PASSWORD = 'correct horse battery'
const NEW = 'a brand new passphrase'
const WRONG = 'wrong password here'
const OWNER = '0123456789abcdef0123456789abcdef'
const LAPTOP = 'fedcba9876543210fedcba9876543210'
const STRANGER = '11111111111111111111111111111111'

const saved = { ...limits }
const realPause = timing.pause
let delays
beforeEach(() => {
  delays = []
  timing.pause = async ms => { delays.push(ms) }
})
afterEach(() => {
  resetClock()
  Object.assign(limits, saved)
  timing.pause = realPause
})

const unthrottled = () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  limits.addressFailures = 0
  return env
}
const signIn = (env, ip, password, device, deviceToken) => call(env, 'POST', '/v1/session', { ip, body: { email: EMAIL, password, device, deviceToken } })

/** The owner signed up on OWNER and a laptop signed in on LAPTOP; the address has a wrong try so every wait is one that counts. */
async function setup() {
  const env = unthrottled()
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: OWNER } })
  const laptop = await signIn(env, '198.51.100.9', PASSWORD, LAPTOP)
  assert.equal(laptop.status, 200)
  // Three wrong tries in the hour: from now on a device the account doesn't know waits when it signs in.
  for (const ip of ['203.0.113.1', '203.0.113.2', '203.0.113.3']) await signIn(env, ip, WRONG, STRANGER)
  delays.length = 0
  return { env, ownerToken: made.body.token, owner: made.body.deviceToken, ownerSession: made.body.token, laptopToken: laptop.body.token, laptop: laptop.body.deviceToken, code: made.body.recoveryCode }
}

test('a known device with its token signs in with its right password without waiting, and its wrong tries are checked as usual', async () => {
  const { env, owner } = await setup()
  const right = await signIn(env, '198.51.100.20', PASSWORD, OWNER, owner)
  assert.equal(right.status, 200)
  const wrong = await signIn(env, '198.51.100.21', WRONG, OWNER, right.body.deviceToken)
  assert.equal(wrong.body.error, 'wrong-credentials')
  assert.deepEqual(delays, [], 'known: no wait')
})

test('a device id without its token is not known: its right password goes through after a wait', async () => {
  const { env } = await setup()
  const answer = await signIn(env, '198.51.100.30', PASSWORD, OWNER)
  assert.equal(answer.status, 200, 'not refused')
  assert.equal(delays.length, 1)
  assert.ok(delays[0] > 0)
})

test('a token of another device, or one made up, is not known either', async () => {
  const { env, laptop, owner } = await setup()
  assert.equal((await signIn(env, '198.51.100.40', PASSWORD, OWNER, laptop)).status, 200)
  assert.equal((await signIn(env, '198.51.100.41', PASSWORD, LAPTOP, owner)).status, 200)
  assert.equal((await signIn(env, '198.51.100.42', PASSWORD, OWNER, 'A'.repeat(43))).status, 200)
  assert.equal((await signIn(env, '198.51.100.43', PASSWORD, STRANGER, owner)).status, 200)
  assert.equal(delays.length, 4)
})

test('a revoked device loses its token: its next sign-in waits', async () => {
  const { env, ownerToken, owner, laptop } = await setup()
  assert.equal((await call(env, 'DELETE', `/v1/sessions/${LAPTOP}`, { token: ownerToken })).status, 204)
  assert.equal((await signIn(env, '198.51.100.10', PASSWORD, LAPTOP, laptop)).status, 200)
  assert.equal(delays.length, 1, 'the revoked laptop waits')
  assert.equal((await signIn(env, '198.51.100.11', PASSWORD, OWNER, owner)).status, 200)
  assert.equal(delays.length, 1, 'the owner doesn\'t')
})

test('a sign-out ends this device\'s token: its next sign-in waits', async () => {
  const { env, laptopToken, laptop } = await setup()
  assert.equal((await call(env, 'POST', '/v1/signout', { token: laptopToken })).status, 204)
  assert.equal((await signIn(env, '198.51.100.12', PASSWORD, LAPTOP, laptop)).status, 200)
  assert.equal(delays.length, 1)
})

test('a password change ends every other device\'s token and replaces this device\'s', async () => {
  const { env, ownerToken, owner, laptop } = await setup()
  const changed = await call(env, 'PUT', '/v1/password', { token: ownerToken, body: { current: PASSWORD, password: NEW, deviceToken: owner } })
  assert.equal(changed.status, 200)
  assert.match(changed.body.deviceToken, /^[A-Za-z0-9_-]{43}$/)
  delays.length = 0
  assert.equal((await signIn(env, '198.51.100.50', NEW, LAPTOP, laptop)).status, 200)
  assert.equal(delays.length, 1, 'the laptop waits')
  // The previous token of this device counts once, as after any sign-in: it is not a wait.
  assert.equal((await signIn(env, '198.51.100.51', NEW, OWNER, owner)).status, 200)
  assert.equal(delays.length, 1, 'the old token is accepted once')
  assert.equal((await signIn(env, '198.51.100.52', NEW, OWNER, changed.body.deviceToken)).status, 200)
  assert.equal(delays.length, 1, 'the fresh token is known')
})

test('a recovery reset ends every device\'s token, and the device that reset gets a fresh one', async () => {
  const { env, owner, laptop, code } = await setup()
  const done = await call(env, 'POST', '/v1/recovery/reset', { ip: '198.51.100.60', body: { email: EMAIL, code, password: NEW, device: OWNER, deviceToken: owner } })
  assert.equal(done.status, 200)
  assert.match(done.body.deviceToken, /^[A-Za-z0-9_-]{43}$/)
  delays.length = 0
  assert.equal((await signIn(env, '198.51.100.61', NEW, LAPTOP, laptop)).status, 200)
  assert.equal(delays.length, 1)
  assert.equal((await signIn(env, '198.51.100.62', NEW, OWNER, done.body.deviceToken)).status, 200)
  assert.equal(delays.length, 1)
})

test('the device lookup runs for an address with no account too: the same work either way', async () => {
  const env = unthrottled()
  await signUp(env)
  const prepare = env.DB.prepare.bind(env.DB)
  let lookups = 0
  env.DB.prepare = sql => {
    if (sql.includes('FROM devices')) lookups++
    return prepare(sql)
  }
  await signIn(env, '198.51.100.80', WRONG, OWNER)
  const unknown = lookups
  lookups = 0
  await signIn(env, '198.51.100.81', WRONG, OWNER)
  assert.equal(unknown, 1)
  assert.equal(lookups, 1)
})

test('a wrong password doesn\'t spend the previous token: the right one still gets the grace', async () => {
  const { env, owner } = await setup()
  const first = await signIn(env, '198.51.100.90', PASSWORD, OWNER, owner)
  assert.equal(first.status, 200)
  delays.length = 0
  // The previous token with a wrong password: it is not known for this try (it waits), and it is not spent.
  assert.equal((await signIn(env, '198.51.100.91', WRONG, OWNER, owner)).body.error, 'wrong-credentials')
  assert.equal(delays.length, 1)
  delays.length = 0
  assert.equal((await signIn(env, '198.51.100.92', PASSWORD, OWNER, owner)).status, 200)
  assert.equal(delays.length, 0, 'the grace is still there for the right password')
})

test('a previous token counts once more, in case its answer was lost; a second use waits', async () => {
  const { env, owner } = await setup()
  const first = await signIn(env, '198.51.100.70', PASSWORD, OWNER, owner)
  assert.equal(first.status, 200)
  delays.length = 0
  // The answer to the first use was lost, so the same token is sent again: it counts once more, and its answer gives a newer one.
  const again = await signIn(env, '198.51.100.71', PASSWORD, OWNER, owner)
  assert.equal(again.status, 200)
  assert.equal(delays.length, 0, 'the previous token, once')
  // The same old token used a second time is no longer known: it waits.
  const second = await signIn(env, '198.51.100.72', PASSWORD, OWNER, owner)
  assert.equal(second.status, 200)
  assert.equal(delays.length, 1, 'the second use waits')
  // The token from the latest answer is known.
  delays.length = 0
  assert.equal((await signIn(env, '198.51.100.73', PASSWORD, OWNER, second.body.deviceToken)).status, 200)
  assert.equal(delays.length, 0, 'the newest token is known')
})

test('a token is known for idleSeconds after its device was last seen, and no longer', async () => {
  const { env, owner } = await setup()
  later(limits.idleSeconds + 1)
  // The address count of the hour is gone with the clock: three wrong tries again, so a wait is due.
  for (const ip of ['203.0.113.4', '203.0.113.5', '203.0.113.6']) await signIn(env, ip, WRONG, STRANGER)
  delays.length = 0
  assert.equal((await signIn(env, '198.51.100.74', PASSWORD, OWNER, owner)).status, 200)
  assert.equal(delays.length, 1, 'the token is no longer known')
})

test('GET /v1/sessions lists the devices with their last-seen time, next to the sessions', async () => {
  const { env, ownerToken } = await setup()
  const listed = await call(env, 'GET', '/v1/sessions', { token: ownerToken })
  assert.deepEqual(listed.body.devices.map(d => d.device).sort(), [LAPTOP, OWNER].sort())
  assert.ok(listed.body.devices.every(d => Number.isInteger(d.lastSeen) && d.lastSeen > 0))
})

test('a device whose last session goes (idle, evicted by a newer sign-in) is no longer known', async () => {
  const { env, laptop } = await setup()
  later(limits.idleSeconds + 1)
  for (const ip of ['203.0.113.7', '203.0.113.8', '203.0.113.9']) await signIn(env, ip, WRONG, STRANGER)
  await signIn(env, '198.51.100.75', PASSWORD, OWNER)
  delays.length = 0
  assert.deepEqual(env.DB.rows('devices').map(row => row.device), [OWNER], 'the laptop\'s row went with its last session')
  assert.equal((await signIn(env, '198.51.100.76', PASSWORD, LAPTOP, laptop)).status, 200)
  assert.equal(delays.length, 1)
})

async function signUp(env) {
  return call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: OWNER } })
}

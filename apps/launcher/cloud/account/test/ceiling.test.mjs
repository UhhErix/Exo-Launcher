/**
 * The address-wide count (README.md › Limits): every wrong try against an address, across client addresses and devices, is
 * counted in its hour. Past addressFailures each wrong try waits a second more, at most addressMaxDelaySeconds, outside the
 * hash slot. Nothing is refused for it: the address never locks (only the pair does), and the right password always gets
 * through. A known device (a valid token) doesn't wait.
 *
 * How it could fail: the address locks, so an attacker can keep the owner out; the delay is not applied, so a botnet guesses
 * at full speed; a known device waits; the count doesn't restart after its window.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, beforeEach, test } from 'node:test'
import { timing } from '../src/accounts.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call, later, resetClock } from './harness.mjs'

const EMAIL = 'ceiling@example.com'
const PASSWORD = 'correct horse battery'
const WRONG = 'wrong password here'
const DEVICE = '0123456789abcdef0123456789abcdef'
const OTHER = 'fedcba9876543210fedcba9876543210'

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
  return env
}
const signUp = env => call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
const signIn = (env, ip, password = WRONG, device = OTHER, deviceToken) => call(env, 'POST', '/v1/session', { ip, body: { email: EMAIL, password, device, deviceToken } })

test('past addressFailures each wrong try waits a second more, up to addressMaxDelaySeconds; none is refused', async () => {
  const env = unthrottled()
  limits.addressFailures = 0
  await signUp(env)
  const answers = []
  for (let i = 1; i <= 12; i++) answers.push((await signIn(env, `203.0.113.${i}`)).status)
  assert.deepEqual(answers, Array(12).fill(401))
  assert.deepEqual(delays, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 10].map(s => s * 1000))
})

test('the address never locks: the right password from a new client gets through, after its wait', async () => {
  const env = unthrottled()
  limits.addressFailures = 0
  await signUp(env)
  for (let i = 1; i <= 3; i++) await signIn(env, `203.0.113.${i}`)
  delays.length = 0
  const right = await signIn(env, '198.51.100.5', PASSWORD)
  assert.equal(right.status, 200)
  assert.deepEqual(delays, [3000], 'three tries in the hour: the right password waits for them')
})

test('a known device (a valid token) doesn\'t wait, however many wrong tries the address has had', async () => {
  const env = unthrottled()
  limits.addressFailures = 0
  const made = await signUp(env)
  for (let i = 1; i <= 5; i++) await signIn(env, `203.0.113.${i}`)
  delays.length = 0
  const right = await signIn(env, '198.51.100.6', PASSWORD, DEVICE, made.body.deviceToken)
  assert.equal(right.status, 200)
  // A sign-in issues a new token: the wrong try uses the newest one (the previous one would only get its grace).
  assert.equal((await signIn(env, '198.51.100.7', WRONG, DEVICE, right.body.deviceToken)).body.error, 'wrong-credentials')
  assert.deepEqual(delays, [])
})

test('the count restarts with the hour: after the window a wrong try waits one second again', async () => {
  const env = unthrottled()
  limits.addressFailures = 0
  await signUp(env)
  for (let i = 1; i <= 3; i++) await signIn(env, `203.0.113.${i}`)
  assert.deepEqual(delays, [1000, 2000, 3000])
  later(60 * 60 + 1)
  await signIn(env, '203.0.113.9')
  assert.equal(delays.at(-1), 1000)
})

test('below addressFailures nothing waits at all', async () => {
  const env = unthrottled()
  limits.addressFailures = 100
  await signUp(env)
  for (let i = 1; i <= 20; i++) assert.equal((await signIn(env, `203.0.113.${i}`)).status, 401)
  assert.deepEqual(delays, [])
})

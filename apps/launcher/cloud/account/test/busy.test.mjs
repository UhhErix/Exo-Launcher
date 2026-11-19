/**
 * The hash slots (README.md › Keys, Limits): an isolate runs at most limits.hashesInFlight Argon2 hashes at once. A slot
 * is taken only around the hash itself, after the throttle, the session and the body are read, so a client that stalls
 * its body holds no slot. A request that finds every slot taken waits up to limits.slotWaitMs for one, then is answered
 * 503 busy with Retry-After: 1. A body that stops arriving is answered 408 once limits.bodyReadMs passes.
 *
 * How it could fail: a stalled body holds a slot, and four of them refuse every sign-in; more than hashesInFlight hashes
 * run at once; a waiter is starved or a slot is lost on release; a stalled body waits for ever.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { withHashSlot } from '../src/accounts.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call } from './harness.mjs'

const EMAIL = 'busy@example.com'
const PASSWORD = 'correct horse battery'
const WRONG = 'wrong password here'
const DEVICE = '0123456789abcdef0123456789abcdef'

const saved = { ...limits }
afterEach(() => Object.assign(limits, saved))

const gate = () => {
  let open
  const promise = new Promise(resolve => { open = resolve })
  return { promise, open }
}

test('a slot is held by at most hashesInFlight hashes at once, whatever the number that ask', async () => {
  let running = 0
  let most = 0
  const hash = async () => {
    running++
    most = Math.max(most, running)
    await new Promise(resolve => setImmediate(resolve))
    running--
    return 'done'
  }
  const answers = await Promise.all(Array.from({ length: 10 }, () => withHashSlot(hash)))
  assert.deepEqual(answers, Array(10).fill('done'), 'the ones that wait are served, not refused')
  assert.equal(most, limits.hashesInFlight)
})

test('four slots taken: a fifth waits slotWaitMs for one, then is refused busy with Retry-After: 1', async () => {
  limits.slotWaitMs = 50
  const hold = gate()
  const holders = Array.from({ length: limits.hashesInFlight }, () => withHashSlot(() => hold.promise))
  const started = Date.now()
  const refused = await withHashSlot(() => 'ran').then(() => null, error => error)
  assert.equal(refused?.status, 503)
  assert.equal(refused?.code, 'busy')
  assert.equal(refused?.headers['Retry-After'], '1')
  assert.ok(Date.now() - started >= 40, 'it waited before it was refused')
  hold.open()
  await Promise.all(holders)
})

test('a slot given back goes to the waiter: the waiter runs, and the count never goes past the cap', async () => {
  limits.slotWaitMs = 2000
  const hold = gate()
  const holders = Array.from({ length: limits.hashesInFlight }, () => withHashSlot(() => hold.promise))
  const waiter = withHashSlot(() => 'waited')
  hold.open()
  assert.equal(await waiter, 'waited')
  await Promise.all(holders)
})

test('a request whose body stalls holds no slot: four of them don\'t refuse a fifth sign-in', async () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  limits.bodyReadMs = 2000
  await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const worker = (await import('../src/worker.js')).default
  const stalled = Array.from({ length: limits.hashesInFlight }, (_, i) => {
    const body = new ReadableStream({ start() {} })
    const request = new Request('https://accounts.example/v1/session', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'CF-Connecting-IP': `10.7.0.${i + 1}` },
      body,
      duplex: 'half',
    })
    return worker.fetch(request, env)
  })
  const signedIn = await call(env, 'POST', '/v1/session', { ip: '198.51.100.5', body: { email: EMAIL, password: WRONG, device: DEVICE } })
  assert.equal(signedIn.status, 401, 'a wrong password is answered as usual, not 503 busy')
  const answers = await Promise.all(stalled)
  assert.ok(answers.every(answer => answer.status === 408), 'each stalled body is answered 408 once its deadline passes')
})

test('a body that stops arriving is answered 408 slow-body after limits.bodyReadMs', async () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  limits.bodyReadMs = 100
  const worker = (await import('../src/worker.js')).default
  const request = new Request('https://accounts.example/v1/session', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'CF-Connecting-IP': '10.8.0.1' },
    body: new ReadableStream({ start() {} }),
    duplex: 'half',
  })
  const answer = await worker.fetch(request, env)
  assert.equal(answer.status, 408)
  assert.equal((await answer.json()).error, 'slow-body')
})

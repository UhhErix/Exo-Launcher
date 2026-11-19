/**
 * The client address the Worker keys its limits by (src/http.js address): an IPv4 address as sent, an IPv6 address as its
 * /56, since one site or household holds a whole /56. The pair lock and the edge rate limit both use it.
 *
 * How it could fail: two IPv6 addresses of one /56 count apart, so one host gets a /56 of guesses; two /56s share a key;
 * an IPv4-mapped address collapses every IPv4 client into one key.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { address, body } from '../src/http.js'
import { limits } from '../src/limits.js'
import { RateLimit, account, call } from './harness.mjs'

const from = ip => address(new Request('https://accounts.example/', { headers: ip === undefined ? {} : { 'CF-Connecting-IP': ip } }))

test('an IPv6 address is keyed by its /56, written in lowercase without leading zeros', () => {
  assert.equal(from('2001:db8:1:2:ffff:ffff:ffff:ffff'), '2001:db8:1:0::/56')
  assert.equal(from('2001:DB8:0001:0002::1'), '2001:db8:1:0::/56')
  assert.equal(from('2001:db8:1:2::'), '2001:db8:1:0::/56')
  assert.equal(from('2001:db8::'), '2001:db8:0:0::/56')
  assert.equal(from('::1'), '0:0:0:0::/56')
  assert.equal(from('fe80::1%eth0'), 'fe80::1%eth0', 'not parsed as an address: its raw value is the key')
})

test('an IPv4 address is keyed as sent, and so is anything that is not an address', () => {
  assert.equal(from('198.51.100.7'), '198.51.100.7')
  assert.equal(from('::ffff:198.51.100.7'), '::ffff:198.51.100.7', 'an IPv4-mapped address is its own key, not a shared /56')
  assert.equal(from('nonsense'), 'nonsense')
  assert.equal(from(undefined), '')
})

const DEVICE = '0123456789abcdef0123456789abcdef'
const PASSWORD = 'correct horse battery'
const WRONG = 'wrong password here'

test('lockout: wrong tries from two hosts of one /56 count together; another /56 is not held back', async () => {
  const env = account()
  env.SESSION_TRIES = new RateLimit({ limit: 10_000, period: 60 })
  await call(env, 'POST', '/v1/account', { body: { email: 'v6@example.com', password: PASSWORD, device: DEVICE } })
  const signIn = (ip, password = WRONG) => call(env, 'POST', '/v1/session', { ip, body: { email: 'v6@example.com', password, device: DEVICE } })
  for (let i = 0; i < 5; i++) await signIn(`2001:db8:1:2::${i + 1}`)
  for (let i = 0; i < 5; i++) await signIn(`2001:db8:1:2:aaaa::${i + 1}`)
  assert.equal((await signIn('2001:db8:1:2:cccc::9', PASSWORD)).body.error, 'locked', 'the tenth wrong try of the /56 locks the /56 for the right password too')
  assert.equal((await signIn('2001:db8:9:9::1', PASSWORD)).status, 200, 'another /56 signs in')
})

test('edge rate limit: the 11th password request from one /56 waits, whichever host it comes from', async () => {
  const env = account()
  // A new address each time, so the pair lock (10 wrong tries of one pair) stays out of this test.
  const probe = (ip, email) => call(env, 'POST', '/v1/session', { ip, body: { email, password: WRONG, device: DEVICE } })
  for (let i = 0; i < 10; i++) assert.equal((await probe(`2001:db8:4:4::${i + 1}`, `n${i}@example.com`)).body.error, 'wrong-credentials')
  assert.equal((await probe('2001:db8:4:4:ffff::ffff', 'late@example.com')).body.error, 'slow-down')
  assert.equal((await probe('2001:db8:5:5::1', 'other@example.com')).body.error, 'wrong-credentials')
})

test('the body deadline is idle time: a body whose chunks keep arriving is read whole, however long it takes', async () => {
  const saved = limits.bodyReadMs
  limits.bodyReadMs = 150
  try {
    const pieces = ['{"a":"', 'b', 'c', 'd', 'e', 'f"}']
    const stream = new ReadableStream({
      async start(controller) {
        for (const piece of pieces) {
          await new Promise(resolve => setTimeout(resolve, 60))
          controller.enqueue(new TextEncoder().encode(piece))
        }
        controller.close()
      },
    })
    const request = new Request('https://accounts.example/', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: stream, duplex: 'half' })
    assert.deepEqual(await body(request), { a: 'bcdef' })
  } finally {
    limits.bodyReadMs = saved
  }
})


test('the whole body is capped at bodyMaxMs, however steadily its chunks arrive', async () => {
  const savedIdle = limits.bodyReadMs
  const savedMax = limits.bodyMaxMs
  limits.bodyReadMs = 150
  limits.bodyMaxMs = 250
  try {
    const pieces = ['{"a":"', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j"}']
    const stream = new ReadableStream({
      async start(controller) {
        for (const piece of pieces) {
          await new Promise(resolve => setTimeout(resolve, 60))
          controller.enqueue(new TextEncoder().encode(piece))
        }
        controller.close()
      },
    })
    const request = new Request('https://accounts.example/', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: stream, duplex: 'half' })
    await assert.rejects(body(request), error => error.status === 408 && error.code === 'slow-body')
  } finally {
    limits.bodyReadMs = savedIdle
    limits.bodyMaxMs = savedMax
  }
})

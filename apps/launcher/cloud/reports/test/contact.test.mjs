/**
 * The contact form's endpoint (POST /v1/contact): the real worker.js and contact.js, with fake R2, rate limiter and
 * send_email bindings.
 *
 * How it could fail: a message is stored with a field it doesn't name, or without its message, or over 4000 characters;
 * an address that isn't one, or one with a line break in it (a header injection), is stored or sent; a browser from any
 * origin but https://getexo.app can post, or the CORS answer names another origin; a bot that fills the honeypot is
 * stored or told it failed; the sender's address reaches R2, the email or a log (only the rate limiter's key may hold it);
 * a message is echoed back in the answer; a missing email binding loses the message; a failed email loses the message
 * (it is stored first, so the answer is still 202); an email is sent to anyone but the owner's destination.
 *
 *   npm test   (node --test test; no packages)
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import worker from '../src/worker.js'
import { limiters } from './limiters.mjs'
import { ContactError, contactLimits, parseContact } from '../src/contact.js'

const ORIGIN = 'https://getexo.app'
const IP = '203.0.113.77'

const sample = { email: 'player@example.com', app: 'cinema', message: 'The subtitles drift after the second episode.' }

/** Fake bindings: R2's put, a rate limiter that allows or refuses, and a send_email binding that records or throws. */
function fakeEnv({ allow = true, mailFails = false, mail = true, to = 'owner@example.test' } = {}) {
  const stored = []
  const limited = []
  const sent = []
  const env = {
    REPORTS: {
      async put(key, body, options) {
        stored.push({ key, body, options })
      },
    },
    ...limiters().bindings,
    CONTACT_PER_IP: {
      async limit(options) {
        limited.push(options)
        return { success: allow }
      },
    },
  }
  if (mail) {
    // The runtime's EmailMessage (cloudflare:email) is injected here, since the test runs outside Workers.
    env.EmailMessage = class {
      constructor(from, to, raw) {
        Object.assign(this, { from, to, raw })
      }
    }
    env.CONTACT_MAIL = {
      async send(message) {
        if (mailFails) throw new Error('mail is down')
        sent.push(message)
      },
    }
  }
  if (to) env.CONTACT_TO = to
  return { env, stored, limited, sent }
}

function post(body, headers = {}) {
  return new Request('https://reports.getexo.app/v1/contact', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Origin: ORIGIN, 'CF-Connecting-IP': IP, ...headers },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  })
}

test('a valid message is stored once, as JSON, under contact/<UTC date>/<id>.json', async () => {
  const { env, stored } = fakeEnv()
  const response = await worker.fetch(post(sample), env)
  assert.equal(response.status, 202)
  assert.equal(await response.text(), '')
  assert.equal(stored.length, 1)
  const today = new Date().toISOString().slice(0, 10)
  assert.match(stored[0].key, new RegExp(`^contact/${today}/[0-9a-f-]{36}\\.json$`))
  assert.equal(stored[0].options.httpMetadata.contentType, 'application/json')
  assert.deepEqual(JSON.parse(stored[0].body), sample)
})

test('the email is optional: without it the message is stored with no address', async () => {
  const { env, stored } = fakeEnv()
  const { email, ...rest } = sample
  assert.equal((await worker.fetch(post(rest), env)).status, 202)
  assert.deepEqual(JSON.parse(stored[0].body), { app: 'cinema', email: null, message: sample.message })
  assert.equal((await worker.fetch(post({ ...sample, email: '' }), fakeEnv().env)).status, 202)
})

test('the stored message holds exactly the fields the form sends', () => {
  assert.deepEqual(parseContact(sample), { spam: false, ...sample })
  assert.throws(() => parseContact({ ...sample, name: 'Erix' }), /Unknown field/)
  assert.throws(() => parseContact({ ...sample, ip: '203.0.113.77' }), /Unknown field/)
})

test('a message is required and at most 4000 characters', async () => {
  const { env, stored } = fakeEnv()
  const empty = await worker.fetch(post({ ...sample, message: '   ' }), env)
  assert.equal(empty.status, 400)
  assert.deepEqual(await empty.json(), { field: 'message', problem: 'Write a message.' })
  const { message, ...none } = sample
  assert.throws(() => parseContact(none), ContactError)
  assert.equal(contactLimits.messageChars, 4000)
  assert.doesNotThrow(() => parseContact({ ...sample, message: 'x'.repeat(4000) }))
  const long = await worker.fetch(post({ ...sample, message: 'x'.repeat(4001) }), env)
  assert.equal(long.status, 400)
  assert.deepEqual(await long.json(), { field: 'message', problem: 'Keep the message to 4000 characters.' })
  assert.equal(stored.length, 0)
})

test('the app is one of launcher, cinema, browser or other', () => {
  for (const app of ['launcher', 'cinema', 'browser', 'other']) assert.equal(parseContact({ ...sample, app }).app, app)
  assert.throws(() => parseContact({ ...sample, app: 'steam' }), (error) => error instanceof ContactError && error.field === 'app')
  const { app, ...none } = sample
  assert.throws(() => parseContact(none), /app/)
})

test('an address that is not one, or has a line break or space in it, is refused', () => {
  for (const email of ['not-an-address', 'a@b', 'a b@example.com', 'a@example.com\r\nBcc: x@y.z', 'a@example.com>', 'x'.repeat(250) + '@example.com']) {
    assert.throws(() => parseContact({ ...sample, email }), (error) => error instanceof ContactError && error.field === 'email', email)
  }
  assert.equal(parseContact({ ...sample, email: 'first.last+tag@sub.example.co.uk' }).email, 'first.last+tag@sub.example.co.uk')
})

test('control characters in the message are refused; line breaks and tabs are kept', () => {
  assert.throws(() => parseContact({ ...sample, message: 'bell\u0007' }), (error) => error.field === 'message')
  assert.throws(() => parseContact({ ...sample, message: 'nul\u0000' }), (error) => error.field === 'message')
  assert.equal(parseContact({ ...sample, message: 'line one\nline two\tend' }).message, 'line one\nline two\tend')
})

test('a bot that fills the honeypot gets an ordinary 202 and nothing is stored or sent', async () => {
  const { env, stored, limited, sent } = fakeEnv()
  const response = await worker.fetch(post({ ...sample, website: 'https://spam.example' }), env)
  assert.equal(response.status, 202)
  assert.equal(stored.length, 0)
  assert.equal(sent.length, 0)
  assert.equal(limited.length, 0, 'the honeypot is answered before the rate limit is spent')
  // An empty honeypot is what a person's browser sends: it is ignored, and the message is kept.
  assert.equal((await worker.fetch(post({ ...sample, website: '' }), env)).status, 202)
  assert.equal(stored.length, 1)
})

test('a rate-limited IP gets 429 and nothing is stored or sent; the limiter is keyed by the IP only', async () => {
  const refused = fakeEnv({ allow: false })
  const response = await worker.fetch(post(sample), refused.env)
  assert.equal(response.status, 429)
  assert.equal(refused.stored.length, 0)
  assert.equal(refused.sent.length, 0)
  assert.deepEqual(refused.limited, [{ key: IP }])

  const allowed = fakeEnv()
  await worker.fetch(post(sample), allowed.env)
  assert.deepEqual(allowed.limited, [{ key: IP }])
  assert.deepEqual(Object.keys(allowed.limited[0]), ['key'])
})

test('a request from any origin but https://getexo.app is refused with 403 and nothing is stored', async () => {
  for (const origin of ['https://evil.example', 'http://getexo.app', 'https://getexo.app.evil.example', 'null', 'https://www.getexo.app']) {
    const { env, stored } = fakeEnv()
    const response = await worker.fetch(post(sample, { Origin: origin }), env)
    assert.equal(response.status, 403, origin)
    assert.equal(stored.length, 0, origin)
  }
  const none = fakeEnv()
  const noOrigin = new Request('https://reports.getexo.app/v1/contact', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(sample) })
  assert.equal((await worker.fetch(noOrigin, none.env)).status, 403)
  assert.equal(none.stored.length, 0)
})

test('the preflight answers only getexo.app and allows POST with JSON', async () => {
  const { env } = fakeEnv()
  const preflight = (origin) => worker.fetch(new Request('https://reports.getexo.app/v1/contact', { method: 'OPTIONS', headers: { Origin: origin, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'content-type' } }), env)
  const ok = await preflight(ORIGIN)
  assert.equal(ok.status, 204)
  assert.equal(ok.headers.get('Access-Control-Allow-Origin'), ORIGIN)
  assert.match(ok.headers.get('Access-Control-Allow-Methods'), /POST/)
  assert.match(ok.headers.get('Access-Control-Allow-Headers'), /content-type/i)
  assert.equal(ok.headers.get('Vary'), 'Origin')
  assert.equal((await preflight('https://evil.example')).status, 403)
  assert.equal(await (await worker.fetch(post(sample), env)).headers.get('Access-Control-Allow-Origin'), ORIGIN)
})

test('the answer to a refused message never repeats the message or the address', async () => {
  const { env } = fakeEnv()
  const secret = 'my private note about a Steam key 12345'
  const response = await worker.fetch(post({ ...sample, email: 'bad address', message: secret }), env)
  assert.equal(response.status, 400)
  const text = await response.text()
  assert.ok(!text.includes(secret) && !text.includes('bad address'))
})

test('the message is stored before the email goes out; the email goes to the owner only, with the reply address and no user text in the subject', async () => {
  const { env, stored, sent } = fakeEnv()
  const response = await worker.fetch(post(sample), env)
  assert.equal(response.status, 202)
  assert.equal(stored.length, 1)
  assert.equal(sent.length, 1)
  const mail = sent[0]
  assert.equal(mail.to, 'owner@example.test')
  assert.match(mail.from, /@getexo\.app$/)
  const raw = mail.raw
  assert.ok(raw.includes(`\r\nReply-To: ${sample.email}\r\n`))
  assert.ok(raw.includes('Subject: Exo contact: Cinema'))
  assert.ok(!/^Subject:.*subtitles/m.test(raw), 'the message is not in the subject')
})

test('a missing email binding or destination stores the message and sends nothing', async () => {
  const noBinding = fakeEnv({ mail: false })
  assert.equal((await worker.fetch(post(sample), noBinding.env)).status, 202)
  assert.equal(noBinding.stored.length, 1)
  const noDestination = fakeEnv({ to: null })
  assert.equal((await worker.fetch(post(sample), noDestination.env)).status, 202)
  assert.equal(noDestination.stored.length, 1)
  assert.equal(noDestination.sent.length, 0)
})

test('a failed email still answers 202: the message was stored', async () => {
  const { env, stored } = fakeEnv({ mailFails: true })
  assert.equal((await worker.fetch(post(sample), env)).status, 202)
  assert.equal(stored.length, 1)
})

test('the sender\'s address reaches neither R2 nor the email; it is only the limiter key', async () => {
  const { env, stored, sent } = fakeEnv()
  const headers = { 'X-Forwarded-For': IP, Cookie: 'session=abc123', 'X-Client-Id': 'install-42' }
  const { email, ...anon } = sample
  assert.equal((await worker.fetch(post(anon, headers), env)).status, 202)
  const everything = JSON.stringify([stored, sent, stored[0].key, stored[0].options])
  for (const secret of [IP, 'abc123', 'install-42']) assert.ok(!everything.includes(secret), `${secret} leaked`)
})

test('only POST to /v1/contact; a wrong content type, a body over the cap, or invalid JSON stores nothing', async () => {
  const { env, stored } = fakeEnv()
  assert.equal((await worker.fetch(new Request('https://reports.getexo.app/v1/contact', { headers: { Origin: ORIGIN } }), env)).status, 405)
  const text = new Request('https://reports.getexo.app/v1/contact', { method: 'POST', headers: { 'Content-Type': 'text/plain', Origin: ORIGIN }, body: JSON.stringify(sample) })
  assert.equal((await worker.fetch(text, env)).status, 415)
  assert.equal((await worker.fetch(post('{"app":'), env)).status, 400)
  assert.equal((await worker.fetch(post(JSON.stringify(sample) + ' '.repeat(16 * 1024)), env)).status, 413)
  const declared = await worker.fetch(post(JSON.stringify(sample), { 'Content-Length': String(16 * 1024 + 1) }), env)
  assert.equal(declared.status, 413)
  assert.equal(stored.length, 0)
})

test('the existing crash-report endpoint is unchanged by the contact route', async () => {
  const { env } = fakeEnv()
  const response = await worker.fetch(new Request('https://reports.getexo.app/v1/report', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: '{}' }), env)
  assert.equal(response.status, 415)
  assert.equal(response.headers.get('Access-Control-Allow-Origin'), null)
})

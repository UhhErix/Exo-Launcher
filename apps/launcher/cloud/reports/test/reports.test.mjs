/**
 * The reports service's tests: the real worker.js and validate.js, with fake R2 and rate limiter bindings.
 *
 * How the service could fail: a report with a field it doesn't name, or a missing or wrongly typed one, is stored; an app,
 * version, Windows build or error type outside its shape is stored; an excerpt over its size or with control characters
 * is stored; a report that still holds a path, a URL, an email, an id or a token is stored (it must be refused with 422);
 * a body over its cap is read whole; a GET or a request without JSON is accepted; the rate limit is ignored, or one count
 * is shared by every install so many players online at once starve each other, or one sender making up install IDs
 * passes it; the sender's address (CF-Connecting-IP) or a client id reaches R2, or the address reaches any limiter key but
 * the network's; a report is stored under a
 * name that isn't its UTC date (the 30-day rule would expire the wrong ones); invalid UTF-8 crashes the request.
 *
 *   npm test   (node --test test; no packages)
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import worker, { networkOf } from '../src/worker.js'
import { parseReport, ReportError } from '../src/validate.js'
import { limits } from '../src/limits.js'
import { configuredLimiters, configuredLimits, limiters } from './limiters.mjs'

// A report as the Launcher builds it from a realistic log (see ExoLauncher.Tests/CrashReportTests.cs for the same samples).
const sample = {
  app: 'launcher',
  version: '3.24.1',
  osBuild: '10.0.26300.1',
  errorType: 'SteamGameException',
  excerpt: [
    '[2026-11-05T19:42:10.123Z] WARN Launch: Steam did not start the game in time (%USERPROFILE%\\Games\\Elden Ring\\eldenring.exe)',
    '[2026-11-05T19:42:10.456Z] ERROR Library: Could not read the manifest at <path>\\appmanifest_1245620.acf | SteamGameException: The request was refused.',
    '   at ExoLauncher.Services.Library.Launch(String appId) in <source>:line 88',
    '[2026-11-05T19:42:11.001Z] CRASH <user>: unexpected end of stream',
  ].join('\n'),
}

/** Fake bindings: R2's put, and rate limiters that allow or refuse (or count as wrangler.toml says). Each records its calls. */
function fakeEnv({ allow = true, limiter = limiters({ allow }) } = {}) {
  const stored = []
  return {
    stored,
    limited: limiter.calls,
    env: {
      REPORTS: {
        async put(key, body, options) {
          stored.push({ key, body, options })
        },
      },
      ...limiter.bindings,
    },
  }
}

function post(body, headers = {}) {
  return new Request('https://reports.getexo.app/v1/report', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  })
}

test('a valid report is stored once, as JSON, under the UTC date', async () => {
  const { env, stored } = fakeEnv()
  const response = await worker.fetch(post(sample), env)
  assert.equal(response.status, 202)
  assert.equal(stored.length, 1)
  const today = new Date().toISOString().slice(0, 10)
  assert.match(stored[0].key, new RegExp(`^reports/${today}/[0-9a-f-]{36}\\.json$`))
  assert.equal(stored[0].options.httpMetadata.contentType, 'application/json')
  assert.deepEqual(JSON.parse(stored[0].body), sample)
})

test('the stored report holds exactly the five fields and nothing a client could add', async () => {
  const { env, stored } = fakeEnv()
  await worker.fetch(post(sample), env)
  assert.deepEqual(Object.keys(JSON.parse(stored[0].body)).sort(), ['app', 'errorType', 'excerpt', 'osBuild', 'version'])
})

test('an unknown field refuses the report, even a harmless one', async () => {
  const { env, stored } = fakeEnv()
  const response = await worker.fetch(post({ ...sample, userName: 'Erix' }), env)
  assert.equal(response.status, 400)
  assert.equal(stored.length, 0)
})

test('a missing field or a non-text field refuses the report', () => {
  const { app, ...missing } = sample
  assert.throws(() => parseReport(missing), ReportError)
  assert.throws(() => parseReport({ ...sample, version: 3 }), /must be text/)
  assert.throws(() => parseReport([sample]), /Not a report/)
  assert.throws(() => parseReport(null), /Not a report/)
})

test('app, version, Windows build and error type keep their shape', () => {
  assert.throws(() => parseReport({ ...sample, app: 'steam' }), /Unknown app/)
  assert.throws(() => parseReport({ ...sample, version: '3.24' }), /Bad version/)
  assert.throws(() => parseReport({ ...sample, version: '3.24.1-beta' }), /Bad version/)
  assert.throws(() => parseReport({ ...sample, osBuild: 'Windows 11' }), /Bad Windows build/)
  assert.throws(() => parseReport({ ...sample, errorType: '1Error' }), /Bad error type/)
  assert.throws(() => parseReport({ ...sample, errorType: 'Error (x)' }), /Bad error type/)
  assert.throws(() => parseReport({ ...sample, errorType: 'A'.repeat(81) }), /Bad error type/)
  for (const app of ['launcher', 'cinema', 'browser']) assert.equal(parseReport({ ...sample, app }).app, app)
})

test('an excerpt over its size or with control characters refuses the report', () => {
  assert.throws(() => parseReport({ ...sample, excerpt: 'x'.repeat(limits.excerptChars + 1) }), /too long/)
  assert.doesNotThrow(() => parseReport({ ...sample, excerpt: 'x'.repeat(limits.excerptChars) }))
  assert.throws(() => parseReport({ ...sample, excerpt: 'line\u0000' }), /control characters/)
  assert.throws(() => parseReport({ ...sample, excerpt: 'bell\u0007' }), /control characters/)
  assert.doesNotThrow(() => parseReport({ ...sample, excerpt: 'tab\tand\nnewline' }))
})

test('anything that still looks like personal data is refused with 422 and never stored', async () => {
  const leaks = {
    'a Windows path': 'Could not open C:\\Users\\Erix\\Games\\Riot.exe',
    'a UNC path': 'Read from \\\\DESKTOP-K3J9\\share\\file.txt',
    'a home folder': 'Read /home/erix/.config/exo.json',
    'a URL with a token': 'Requested https://api.example.com/v1?token=abc',
    'a plain URL': 'Fetched http://127.0.0.1:9364/achievements',
    'an email address': 'Signed in as erix@example.com',
    'a long number (a Steam id)': 'Steam id 76561198012345678 owns this',
    'a token': 'Got eyJhbG' + 'ciOiJI' + 'UzI1Ni' + 'J9.eyJ' + 'zdWIiO' + 'iIxMjM' + '0NTY3O' + 'DkwIn0' + '.abcde' + 'fghijk' + 'lmnopq' + 'rstuvw' + 'x',
  }
  for (const [what, excerpt] of Object.entries(leaks)) {
    const { env, stored } = fakeEnv()
    const response = await worker.fetch(post({ ...sample, excerpt }), env)
    assert.equal(response.status, 422, `${what} should be refused`)
    assert.equal(stored.length, 0, `${what} must not be stored`)
  }
})

test('a short id, a version number and a time are not personal data', () => {
  const ok = 'Build 3.24.1 took 2400 ms at 19:42:10 for game 1245620 (12 files, 1048576 bytes)'
  assert.doesNotThrow(() => parseReport({ ...sample, excerpt: ok }))
})

test('the body is capped in bytes, read no further than the cap, and a declared size over it is refused early', async () => {
  const { env, stored } = fakeEnv()
  // The longest excerpt of two-byte letters still fits the cap (8192 × 2 bytes plus the rest).
  const longest = { ...sample, excerpt: 'é'.repeat(limits.excerptChars) }
  assert.ok(new TextEncoder().encode(JSON.stringify(longest)).length <= limits.bodyBytes)
  assert.equal((await worker.fetch(post(longest), env)).status, 202)
  // A body with no declared length over the cap is cut off while it is read.
  const padded = JSON.stringify(sample) + ' '.repeat(limits.bodyBytes)
  assert.equal((await worker.fetch(post(padded), env)).status, 413)
  // A declared length over the cap is refused before the body is read.
  const declared = await worker.fetch(post(JSON.stringify(sample), { 'Content-Length': String(limits.bodyBytes + 1) }), env)
  assert.equal(declared.status, 413)
  assert.equal(stored.length, 1)
})

test('only POST to /v1/report; other paths and methods are refused', async () => {
  const { env } = fakeEnv()
  assert.equal((await worker.fetch(new Request('https://reports.getexo.app/v1/report'), env)).status, 405)
  assert.equal((await worker.fetch(new Request('https://reports.getexo.app/v1/other', { method: 'POST' }), env)).status, 404)
  assert.equal((await worker.fetch(new Request('https://reports.getexo.app/', { method: 'POST' }), env)).status, 404)
})

test('a request without a JSON content type, or with invalid JSON or UTF-8, is refused and stores nothing', async () => {
  const { env, stored } = fakeEnv()
  const text = new Request('https://reports.getexo.app/v1/report', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: JSON.stringify(sample) })
  assert.equal((await worker.fetch(text, env)).status, 415)
  assert.equal((await worker.fetch(post('{not json'), env)).status, 400)
  const bytes = new Request('https://reports.getexo.app/v1/report', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: new Uint8Array([0xff, 0xfe, 0x7b]) })
  assert.equal((await worker.fetch(bytes, env)).status, 400)
  assert.equal(stored.length, 0)
})

/** A D1 stand-in that takes the crash index's writes, for reports that carry an install ID. */
const acceptingD1 = () => ({
  prepare() {
    const s = { bind: () => s, run: async () => ({ meta: { changes: 1 } }), first: async () => null, all: async () => ({ results: [] }) }
    return s
  },
  async batch() { return [] },
})

/** Install IDs as the apps make them (8-4-4-4-12 hex), one per number. */
const installOf = (n) => `${n.toString(16).padStart(8, '0')}-6a2d-4f7e-9c1b-2d8e4a7f0b13`

test('the rate limit is asked per install and per sender network, and a refusal by either stores nothing', async () => {
  const id = installOf(1)
  const allowed = fakeEnv()
  allowed.env.TELEMETRY = acceptingD1()
  assert.equal((await worker.fetch(post({ ...sample, installId: id }, { 'CF-Connecting-IP': '198.51.100.7' }), allowed.env)).status, 202)
  assert.deepEqual(allowed.limited, [
    { binding: 'REPORTS_PER_NETWORK', key: 'report:198.51.100.7' },
    { binding: 'REPORTS_PER_INSTALL', key: `report:${id}` },
    { binding: 'REPORTS_CEILING', key: 'report' },
  ])

  for (const refusing of ['REPORTS_PER_NETWORK', 'REPORTS_PER_INSTALL', 'REPORTS_CEILING']) {
    const refused = fakeEnv({ limiter: limiters({ allow: (binding) => binding !== refusing }) })
    refused.env.TELEMETRY = acceptingD1()
    assert.equal((await worker.fetch(post({ ...sample, installId: id }), refused.env)).status, 429, refusing)
    assert.equal(refused.stored.length, 0, refusing)
  }
})

test('a report from an old client without an install ID meets the network limit alone', async () => {
  const { env, limited } = fakeEnv()
  assert.equal((await worker.fetch(post(sample, { 'CF-Connecting-IP': '198.51.100.7' }), env)).status, 202)
  assert.deepEqual(limited, [{ binding: 'REPORTS_PER_NETWORK', key: 'report:198.51.100.7' }, { binding: 'REPORTS_CEILING', key: 'report' }])
})

/** 10,000 installs each sending one report in the same minute, a few hundred behind one carrier NAT. Returns how many were stored. */
async function tenThousandInstalls(limiter) {
  const { env, stored } = fakeEnv({ limiter })
  env.TELEMETRY = acceptingD1()
  const perNetwork = configuredLimits().REPORTS_PER_NETWORK
  for (let n = 0; n < 10_000; n++) {
    const ip = n < perNetwork ? '100.64.0.1' : `198.18.${n >> 8}.${n & 255}`
    await worker.fetch(post({ ...sample, installId: installOf(n) }, { 'CF-Connecting-IP': ip }), env)
  }
  return stored.length
}

test('10,000 installs reporting in the same minute are all stored: no install shares a count with another', async () => {
  assert.equal(await tenThousandInstalls(configuredLimiters()), 10_000)
})

test('the same 10,000 installs behind one shared count per route, as the old per-app limit was, are starved', async () => {
  // The old binding: REPORTS_PER_APP, 600 a minute, one key for the whole app. Every limiter here answers from that one count.
  let shared = 0
  const old = limiters({ allow: (binding) => binding !== 'REPORTS_PER_INSTALL' || ++shared <= 600 })
  const stored = await tenThousandInstalls(old)
  assert.equal(stored, 600)
  assert.ok(stored < 10_000, 'a shared count is what the test above guards against')
})

test('senders spread over many networks meet the cost ceiling, and a request its own count refused never spends it', async () => {
  const limiter = configuredLimiters()
  const { env, stored } = fakeEnv({ limiter })
  env.TELEMETRY = acceptingD1()
  const { REPORTS_CEILING: ceiling, REPORTS_PER_NETWORK: perNetwork } = configuredLimits()
  // One install flooding from one address: refused by its own count, which does not spend the ceiling.
  for (let i = 0; i < 50; i++) await worker.fetch(post({ ...sample, installId: installOf(0) }, { 'CF-Connecting-IP': '203.0.113.1' }), env)
  const spentBySpam = limiter.calls.filter((call) => call.binding === 'REPORTS_CEILING').length
  assert.equal(spentBySpam, configuredLimits().REPORTS_PER_INSTALL)
  // Many networks, each under its own count: the ceiling stops the total.
  let accepted = stored.length
  for (let n = 1; accepted < ceiling + 100; n++) {
    const response = await worker.fetch(post({ ...sample, installId: installOf(n) }, { 'CF-Connecting-IP': `198.19.${(n / perNetwork) >> 8 & 255}.${(n / perNetwork) & 255 | 0}` }), env)
    if (response.status === 202) accepted++
    else { assert.equal(response.status, 429); break }
  }
  assert.equal(stored.length, ceiling)
})
test('one install past its count is refused while another install on the same network still reports', async () => {
  const { env } = fakeEnv({ limiter: configuredLimiters() })
  env.TELEMETRY = acceptingD1()
  const perInstall = configuredLimits().REPORTS_PER_INSTALL
  const headers = { 'CF-Connecting-IP': '198.51.100.7' }
  for (let i = 0; i < perInstall; i++) assert.equal((await worker.fetch(post({ ...sample, installId: installOf(1) }, headers), env)).status, 202)
  assert.equal((await worker.fetch(post({ ...sample, installId: installOf(1) }, headers), env)).status, 429)
  assert.equal((await worker.fetch(post({ ...sample, installId: installOf(2) }, headers), env)).status, 202)
})

test('one sender making up install IDs is stopped by the network count, and other networks still report', async () => {
  const { env, stored } = fakeEnv({ limiter: configuredLimiters() })
  env.TELEMETRY = acceptingD1()
  const perNetwork = configuredLimits().REPORTS_PER_NETWORK
  let refused = 0
  for (let n = 0; n < perNetwork * 3; n++) {
    const response = await worker.fetch(post({ ...sample, installId: installOf(n) }, { 'CF-Connecting-IP': '2001:db8:1:2:aaaa::1' }), env)
    if (response.status === 429) refused++
  }
  assert.equal(stored.length, perNetwork)
  assert.equal(refused, perNetwork * 2)
  // Another address in the same IPv6 /64 is the same sender; another /64 is not.
  assert.equal((await worker.fetch(post({ ...sample, installId: installOf(1) }, { 'CF-Connecting-IP': '2001:db8:1:2:ffff::9' }), env)).status, 429)
  assert.equal((await worker.fetch(post({ ...sample, installId: installOf(1) }, { 'CF-Connecting-IP': '2001:db8:1:3::9' }), env)).status, 202)
})

test('the sender network key: IPv4 whole, IPv6 by its /64 however it is written, IPv4-mapped as IPv4', () => {
  assert.equal(networkOf('203.0.113.9'), '203.0.113.9')
  assert.equal(networkOf('2001:db8:1:2:aaaa::1'), '2001:db8:1:2::/64')
  assert.equal(networkOf('2001:0DB8:0001:0002:0:0:0:1'), '2001:db8:1:2::/64')
  assert.equal(networkOf('2001:db8::1'), '2001:db8:0:0::/64')
  assert.equal(networkOf('::1'), '0:0:0:0::/64')
  assert.equal(networkOf('::ffff:203.0.113.9'), '203.0.113.9')
  assert.equal(networkOf(null), 'unknown')
})

test('no IP address, client id or cookie reaches R2, and the address reaches only the network limiter as its key', async () => {
  const { env, stored, limited } = fakeEnv()
  const headers = { 'CF-Connecting-IP': '203.0.113.9', 'X-Forwarded-For': '203.0.113.9', 'Cookie': 'session=abc123', 'X-Client-Id': 'install-42' }
  const response = await worker.fetch(post(sample, headers), env)
  assert.equal(response.status, 202)
  const kept = JSON.stringify([stored, stored[0].key, stored[0].options])
  for (const secret of ['203.0.113.9', 'abc123', 'install-42']) assert.ok(!kept.includes(secret), `${secret} leaked into R2`)
  const keys = JSON.stringify(limited)
  for (const secret of ['abc123', 'install-42']) assert.ok(!keys.includes(secret), `${secret} leaked into a limiter key`)
  for (const call of limited) {
    assert.deepEqual(Object.keys(call), ['binding', 'key'])
    if (call.key.includes('203.0.113.9')) assert.equal(call.binding, 'REPORTS_PER_NETWORK')
  }
})

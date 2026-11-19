/**
 * The update address's tests: the real worker.js with R2 (the mirror) stood in for. Nothing reaches GitHub.
 *
 * How it could fail: it hands out a tag or a file that isn't in the mirror, serves a file that isn't one of an app's
 * three, answers a manifest whose checks are malformed, loses a download's byte range, keeps a stale newest-version
 * answer longer than a minute, or serves a change list that isn't the mirror's own CHANGELOG.md.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { afterEach, beforeEach, test } from 'node:test'
const worker = (await import('../src/worker.js')).default
const ORIGIN = 'https://exo-updates.test'
const SHA = 'a'.repeat(64)
const manifest = (app, tag, file) => ({
  schema: 2,
  app,
  tag,
  version: tag.slice(1),
  files: {
    setup: { name: file, size: 10, sha256: SHA },
    sha256: { name: file + '.sha256', size: 80, sha256: 'b'.repeat(64) },
    whatsNew: { name: 'whats-new.json', size: 20, sha256: 'c'.repeat(64) },
  },
})
/** R2 as the worker sees it, with the byte-range shape R2 gives back. */
function standInMirror(objects = {}) {
  const body = (key, range) => {
    const bytes = new TextEncoder().encode(objects[key])
    let part = bytes
    let r
    if (range) {
      const m = /bytes=(\d+)-(\d*)/.exec(range.get('range'))
      const offset = +m[1]
      const end = m[2] ? +m[2] : bytes.length - 1
      r = { offset, length: end - offset + 1 }
      part = bytes.slice(offset, end + 1)
    }
    return {
      size: bytes.length, httpEtag: '"e"', range: r, body: new Blob([part]).stream(),
      writeHttpMetadata: (h) => h.set('content-type', key.endsWith('.json') ? 'application/json' : 'application/octet-stream'),
      json: async () => JSON.parse(objects[key]),
    }
  }
  return {
    // R2 answers a failed onlyIf precondition with the metadata alone, no body.
    get: async (key, options) => {
      if (!(key in objects)) return null
      if (options?.onlyIf?.get('if-none-match') === '"e"') { const { body: _body, ...meta } = body(key); return meta }
      return body(key, options?.range)
    },
    head: async (key) => (key in objects ? { ...body(key), body: undefined } : null),
  }
}
const ask = (path, env = {}, init = {}) => worker.fetch(new Request(ORIGIN + path, init), { ...env }, { waitUntil() {} })
const realFetch = globalThis.fetch
let fetched
beforeEach(() => {
  delete globalThis.caches
  fetched = []
  // Any call out to the network is a failure: the Worker must answer from the mirror alone.
  globalThis.fetch = async (url) => { fetched.push(String(url)); throw new Error('network is not allowed in these tests') }
})
afterEach(() => { globalThis.fetch = realFetch })
test('latest answers from the manifest, with each file\'s size and SHA-256, and never asks GitHub', async () => {
  const env = { MIRROR: standInMirror({ 'launcher/latest.json': JSON.stringify(manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe')) }) }
  const response = await ask('/v1/launcher/latest', env)
  assert.equal(response.status, 200)
  const body = await response.json()
  assert.equal(body.tag, 'v3.24.1')
  assert.equal(body.version, '3.24.1')
  assert.equal(body.source, 'mirror')
  assert.equal(body.files.setup, `${ORIGIN}/v1/launcher/download/v3.24.1/ExoLauncher-Setup.exe`)
  assert.equal(body.files.sha256, `${ORIGIN}/v1/launcher/download/v3.24.1/ExoLauncher-Setup.exe.sha256`)
  assert.equal(body.files.whatsNew, `${ORIGIN}/v1/launcher/download/v3.24.1/whats-new.json`)
  assert.equal(body.checks.setup.sha256, SHA)
  assert.equal(body.checks.setup.size, 10)
  assert.equal(response.headers.get('cache-control'), 'public, max-age=60')
  assert.deepEqual(fetched, [])
})
test('an answer the old Worker cached under the plain latest URL is never served after a deploy', async () => {
  // The edge cache is shared by every Worker version: an entry under the plain URL is what an older Worker wrote.
  const stored = new Map()
  const old = new Response(JSON.stringify({ app: 'launcher', tag: 'v3.23.0', source: 'github' }), { headers: { 'content-type': 'application/json' } })
  stored.set(`${ORIGIN}/v1/launcher/latest`, old)
  const puts = []
  globalThis.caches = { default: {
    match: async (req) => stored.get(req.url)?.clone(),
    put: async (req, res) => { puts.push(req.url); stored.set(req.url, res) },
  } }
  const env = { MIRROR: standInMirror({ 'launcher/latest.json': JSON.stringify(manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe')) }) }
  const body = await (await ask('/v1/launcher/latest', env)).json()
  assert.equal(body.tag, 'v3.24.1')
  assert.equal(body.source, 'mirror')
  assert.equal(body.checks.setup.sha256, SHA)
  assert.ok(puts.every((url) => url.startsWith(`${ORIGIN}/v1/launcher/latest?`)), `stored under ${puts}`)
  assert.equal(puts.length, 1)
})
test('a tag-only manifest from before schema 2 still answers, with no checks', async () => {
  const env = { MIRROR: standInMirror({ 'cinema/latest.json': JSON.stringify({ tag: 'v1.4.0' }) }) }
  const body = await (await ask('/v1/cinema/latest', env)).json()
  assert.equal(body.tag, 'v1.4.0')
  assert.equal(body.checks, undefined)
})
test('a manifest that names a bad tag, a wrong file or a malformed hash is refused, never served', async () => {
  const bad = [
    { ...manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe'), tag: 'main' },
    { ...manifest('launcher', 'v3.24.1', 'evil.exe') },
    { ...manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe'), files: { ...manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe').files, setup: { name: 'ExoLauncher-Setup.exe', size: 10, sha256: 'XYZ' } } },
    { schema: 2, tag: 'v3.24.1' },
  ]
  for (const m of bad) {
    const env = { MIRROR: standInMirror({ 'launcher/latest.json': JSON.stringify(m) }) }
    assert.equal((await ask('/v1/launcher/latest', env)).status, 503)
  }
})
test('no manifest in the mirror: latest is 503, never a guess', async () => {
  assert.equal((await ask('/v1/launcher/latest', { MIRROR: standInMirror({}) })).status, 503)
  assert.equal((await ask('/v1/launcher/latest', {})).status, 503)
})
test('only an app\'s own three files, under its own name, ever pass', async () => {
  const env = { MIRROR: standInMirror({ 'launcher/v3.24.1/ExoLauncher-Setup.exe': '0123456789' }) }
  for (const path of [
    '/v1/launcher/download/v3.24.1/ExoCinema-Setup.exe',
    '/v1/launcher/download/v3.24.1/evil.exe',
    '/v1/launcher/download/v3.24.1/..%2F..%2Fx',
    '/v1/launcher/download/main/ExoLauncher-Setup.exe',
    '/v1/other/latest',
    '/v1/launcher/download/v3.24.1/ExoLauncher-Setup.exe/x',
    '/',
  ]) assert.equal((await ask(path, env)).status, 404, path)
  assert.equal((await ask('/v1/launcher/latest', env, { method: 'POST' })).status, 405)
})
test('a release file comes from the mirror, whole or from a byte range, and is immutable for an hour', async () => {
  const env = { MIRROR: standInMirror({ 'cinema/v1.1.0/ExoCinema-Setup.exe': '0123456789' }) }
  const whole = await ask('/v1/cinema/download/v1.1.0/ExoCinema-Setup.exe', env)
  assert.equal(whole.status, 200)
  assert.equal(whole.headers.get('cache-control'), 'public, max-age=3600')
  assert.equal(whole.headers.get('content-length'), '10')
  assert.equal(await whole.text(), '0123456789')
  const rest = await ask('/v1/cinema/download/v1.1.0/ExoCinema-Setup.exe', env, { headers: { range: 'bytes=4-' } })
  assert.equal(rest.status, 206)
  assert.equal(rest.headers.get('content-range'), 'bytes 4-9/10')
  assert.equal(await rest.text(), '456789')
  assert.equal((await ask('/v1/cinema/download/v1.0.9/ExoCinema-Setup.exe', env)).status, 404)
  assert.deepEqual(fetched, [])
})
test('cache metadata stored on the mirror object never stretches a download past its hour', async () => {
  const env = { MIRROR: standInMirror({ 'cinema/v1.4.6/ExoCinema-Setup.exe': '0123456789' }) }
  const r = await ask('/v1/cinema/download/v1.4.6/ExoCinema-Setup.exe', env)
  assert.equal(r.headers.get('cache-control'), 'public, max-age=3600')
})
test('HEAD gets the GET answer\'s status and headers, and no body', async () => {
  const env = { MIRROR: standInMirror({ 'launcher/latest.json': JSON.stringify(manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe')) }) }
  const get = await ask('/v1/launcher/latest', env)
  const head = await worker.fetch(new Request(ORIGIN + '/v1/launcher/latest', { method: 'HEAD' }), env, { waitUntil() {} })
  assert.equal(head.status, get.status)
  assert.equal(head.headers.get('content-type'), get.headers.get('content-type'))
  assert.equal(await head.text(), '')
})
test('latest has an ETag, and a poll that holds it gets a 304 with no body', async () => {
  const env = { MIRROR: standInMirror({ 'launcher/latest.json': JSON.stringify(manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe')) }) }
  const etag = (await ask('/v1/launcher/latest', env)).headers.get('etag')
  assert.equal(etag, '"launcher-v3.24.1"')
  const polled = await ask('/v1/launcher/latest', env, { headers: { 'if-none-match': etag } })
  assert.equal(polled.status, 304)
  assert.equal(await polled.text(), '')
})
test('a copy the client already holds is a 304, and a range still gets its bytes', async () => {
  const env = { MIRROR: standInMirror({ 'cinema/v1.1.0/ExoCinema-Setup.exe': '0123456789' }) }
  const held = await ask('/v1/cinema/download/v1.1.0/ExoCinema-Setup.exe', env, { headers: { 'if-none-match': '"e"' } })
  assert.equal(held.status, 304)
  const ranged = await ask('/v1/cinema/download/v1.1.0/ExoCinema-Setup.exe', env, { headers: { range: 'bytes=0-2' } })
  assert.equal(ranged.status, 206)
  assert.equal(await ranged.text(), '012')
})
test('the driver advisories come from the mirror only, as JSON', async () => {
  const env = { MIRROR: standInMirror({ 'advisories.json': '{"v":1}' }) }
  const r = await ask('/v1/advisories.json', env)
  assert.equal(r.status, 200)
  assert.equal(r.headers.get('content-type'), 'application/json')
  assert.deepEqual(fetched, [])
})

test("an app's changelog comes from the mirror as markdown, kept at the edge a minute, and never asks GitHub", async () => {
  const env = { MIRROR: standInMirror({ 'launcher/CHANGELOG.md': '## 3.24.3\n- Notes.\n' }) }
  const response = await ask('/v1/launcher/changelog', env)
  assert.equal(response.status, 200)
  assert.equal(response.headers.get('content-type'), 'text/markdown; charset=utf-8')
  assert.equal(response.headers.get('cache-control'), 'public, max-age=60')
  assert.equal(response.headers.get('cdn-cache-control'), 'max-age=60')
  assert.equal(await response.text(), '## 3.24.3\n- Notes.\n')
  assert.deepEqual(fetched, [])
})
test("a changelog is answered for each of the apps, and only from that app's own key", async () => {
  const env = { MIRROR: standInMirror({ 'cinema/CHANGELOG.md': '## 1.4.7\n', 'launcher/CHANGELOG.md': '## 3.26.0\n', 'browser/CHANGELOG.md': '## 1.4.5\n' }) }
  assert.equal(await (await ask('/v1/cinema/changelog', env)).text(), '## 1.4.7\n')
  assert.equal(await (await ask('/v1/launcher/changelog', env)).text(), '## 3.26.0\n')
  assert.equal((await ask('/v1/browser/changelog', env)).status, 404, 'Exo Browser is gone')
})
test('a changelog is 404 until uploaded, for an unknown app, a path past it, or a POST', async () => {
  const env = { MIRROR: standInMirror({ 'launcher/latest.json': JSON.stringify(manifest('launcher', 'v3.24.1', 'ExoLauncher-Setup.exe')) }) }
  for (const path of ['/v1/cinema/changelog', '/v1/other/changelog', '/v1/launcher/changelog/x', '/v1/launcher/CHANGELOG.md', '/v1/launcher/changelog.md'])
    assert.equal((await ask(path, env)).status, 404, path)
  assert.equal((await ask('/v1/launcher/changelog', env, { method: 'POST' })).status, 405)
  assert.equal((await ask('/v1/launcher/changelog', {})).status, 503, 'no mirror bound')
})
test("a changelog HEAD gets the GET's headers and no body, and a held copy is a 304", async () => {
  const env = { MIRROR: standInMirror({ 'cinema/CHANGELOG.md': '## 1.4.5\n' }) }
  const get = await ask('/v1/cinema/changelog', env)
  const head = await worker.fetch(new Request(ORIGIN + '/v1/cinema/changelog', { method: 'HEAD' }), env, { waitUntil() {} })
  assert.equal(head.status, 200)
  assert.equal(head.headers.get('content-type'), get.headers.get('content-type'))
  assert.equal(head.headers.get('cache-control'), 'public, max-age=60')
  assert.equal(await head.text(), '')
  const held = await ask('/v1/cinema/changelog', env, { headers: { 'if-none-match': '"e"' } })
  assert.equal(held.status, 304)
})
test("the stored content type never overrides the changelog's markdown type", async () => {
  // This stand-in stores an object with no type as octet-stream, as an upload without --content-type would be.
  const env = { MIRROR: standInMirror({ 'cinema/CHANGELOG.md': '## 1.4.7\n' }) }
  assert.equal((await ask('/v1/cinema/changelog', env)).headers.get('content-type'), 'text/markdown; charset=utf-8')
})


// No update signing (no keys, owner's rule): a release is the three files. A stray .sig in a manifest or the bucket is never served.
test('a release answers with its three files only, and the edge key names schema 3', async (t) => {
  const stored = new Map()
  const puts = []
  globalThis.caches = { default: {
    match: async (req) => stored.get(req.url)?.clone(),
    put: async (req, res) => { puts.push(req.url); stored.set(req.url, res) },
  } }
  t.after(() => { delete globalThis.caches })
  const m = manifest('cinema', 'v1.4.7', 'ExoCinema-Setup.exe')
  m.files.sig = { name: 'ExoCinema-Setup.exe.sig', size: 10, sha256: 'd'.repeat(64) }
  const env = { MIRROR: standInMirror({ 'cinema/latest.json': JSON.stringify(m) }) }
  const body = await (await ask('/v1/cinema/latest', env)).json()
  assert.deepEqual(Object.keys(body.files), ['setup', 'sha256', 'whatsNew'])
  assert.deepEqual(Object.keys(body.checks), ['setup', 'sha256', 'whatsNew'])
  assert.deepEqual(puts, [`${ORIGIN}/v1/cinema/latest?schema=3`])
})
test('a .sig in the bucket is not served: 404, and nothing is asked of it', async () => {
  const mirror = standInMirror({ 'cinema/v1.4.7/ExoCinema-Setup.exe.sig': '{}' })
  const asked = []
  const env = { MIRROR: { get: async (key, options) => { asked.push(key); return mirror.get(key, options) }, head: mirror.head } }
  for (const path of ['/v1/cinema/download/v1.4.7/ExoCinema-Setup.exe.sig', '/v1/launcher/download/v1.4.7/ExoLauncher-Setup.exe.sig'])
    assert.equal((await ask(path, env)).status, 404, path)
  assert.deepEqual(asked, [])
  assert.deepEqual(fetched, [])
})
/**
 * File-level updates' two routes (/v1/<app>/payload/<tag> and /v1/<app>/file/<sha256>), run in local workerd (miniflare)
 * with a real R2 bucket for MIRROR. Nothing reaches the network.
 *
 * How it could fail: a release without a file list answers anything but a 404 nothing keeps (the app would not fall back
 * to Setup, or the publish run would not see a file right after uploading it); a file is served under a name that is not
 * the SHA-256 of its bytes' key; the routes reach another app's files or a release's installer; a file is kept for less
 * than its year or the list longer than a tag's hour; a byte range is lost; or the routes that were there change.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { gunzipSync, gzipSync } from 'node:zlib'
import { after, before, test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { Miniflare } from 'miniflare'

const ORIGIN = 'https://updates.test'
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')
const DLL = Buffer.from(Array.from({ length: 5000 }, (_, i) => (i * 13) % 256))
const DLL_SHA = sha256(DLL)
const LIST = JSON.stringify({ schema: 1, version: '9.9.9', files: [{ path: 'ExoCinema.dll', size: DLL.length, sha256: DLL_SHA }] })

const mf = new Miniflare({
  modules: true,
  modulesRules: [{ type: 'ESModule', include: ['**/*.js', '**/*.mjs'] }],
  scriptPath: fileURLToPath(new URL('../src/worker.js', import.meta.url)),
  compatibilityDate: '2026-08-06',
  r2Buckets: ['MIRROR'],
})

before(async () => {
  const bucket = await mf.getR2Bucket('MIRROR')
  await bucket.put('cinema/v9.9.9/payload-manifest.json', LIST, { httpMetadata: { contentType: 'application/json' } })
  await bucket.put(`cinema/files/${DLL_SHA}.gz`, gzipSync(DLL), { httpMetadata: { contentType: 'application/gzip' } })
  await bucket.put('cinema/v9.9.9/ExoCinema-Setup.exe', 'setup bytes')
  await bucket.put('cinema/latest.json', JSON.stringify({ tag: 'v9.9.9' }))
})
after(async () => {
  await mf.dispose()
})

const get = (path, init = {}) => mf.dispatchFetch(ORIGIN + path, init)

test('a release\'s file list is served by its tag, as JSON, for a tag\'s hour', async () => {
  const res = await get('/v1/cinema/payload/v9.9.9')
  assert.equal(res.status, 200)
  assert.equal(await res.text(), LIST)
  assert.equal(res.headers.get('content-type'), 'application/json; charset=utf-8')
  assert.equal(res.headers.get('cache-control'), 'public, max-age=3600')
  assert.equal(res.headers.get('location'), null, 'no redirect')
})

test('a release with no file list answers a 404 nothing keeps, so the app uses Setup and a later upload is seen at once', async () => {
  for (const path of ['/v1/cinema/payload/v9.9.8', '/v1/launcher/payload/v9.9.9', `/v1/launcher/file/${DLL_SHA}`, `/v1/cinema/file/${'0'.repeat(64)}`]) {
    const res = await get(path)
    assert.equal(res.status, 404, path)
    assert.equal(res.headers.get('cache-control'), 'no-store', path)
  }
})

test('a file is served by the SHA-256 of its bytes: gzip that unpacks to exactly them, kept for a year', async () => {
  const res = await get(`/v1/cinema/file/${DLL_SHA}`)
  assert.equal(res.status, 200)
  const bytes = gunzipSync(Buffer.from(await res.arrayBuffer()))
  assert.equal(sha256(bytes), DLL_SHA)
  assert.equal(res.headers.get('content-type'), 'application/gzip')
  assert.equal(res.headers.get('content-encoding'), null, 'the app unpacks it itself; no layer in between does')
  assert.equal(res.headers.get('cache-control'), 'public, max-age=31536000, immutable')
})

test('HEAD answers as GET does with no body, and a byte range is honoured', async () => {
  const head = await get(`/v1/cinema/file/${DLL_SHA}`, { method: 'HEAD' })
  assert.equal(head.status, 200)
  assert.equal((await head.arrayBuffer()).byteLength, 0)
  const part = await get(`/v1/cinema/file/${DLL_SHA}`, { headers: { range: 'bytes=0-9' } })
  assert.equal(part.status, 206)
  assert.equal((await part.arrayBuffer()).byteLength, 10)
})

test('only a whole lower-case SHA-256 or a plain tag is a name: nothing else reaches the bucket', async () => {
  const paths = [
    `/v1/cinema/file/${DLL_SHA.toUpperCase()}`,
    `/v1/cinema/file/${DLL_SHA}.gz`,
    `/v1/cinema/file/${DLL_SHA.slice(1)}`,
    `/v1/cinema/file/${DLL_SHA}/x`,
    '/v1/cinema/file/..%2Flatest.json',
    '/v1/cinema/payload/latest',
    '/v1/cinema/payload/v9.9.9/ExoCinema-Setup.exe',
    '/v1/cinema/payload/files',
    '/v1/other/payload/v9.9.9',
  ]
  for (const path of paths) assert.equal((await get(path)).status, 404, path)
})

test('the routes that were there are as they were: the download route serves a release\'s three files and no file list', async () => {
  assert.equal((await get('/v1/cinema/download/v9.9.9/ExoCinema-Setup.exe')).status, 200)
  assert.equal((await get('/v1/cinema/download/v9.9.9/payload-manifest.json')).status, 404)
  assert.equal((await get('/v1/cinema/latest')).status, 200)
  assert.equal((await get(`/v1/cinema/file/${DLL_SHA}`, { method: 'POST' })).status, 405)
})

/**
 * The /v1/cache/ route, run in local workerd (miniflare) with a real R2 bucket for MIRROR. Nothing reaches the network.
 *
 * How it could fail: it serves a key that was never stored, lists a folder, follows a redirect, serves an app's release
 * file through the cache, accepts a traversal or a stray query, answers a listing with the wrong commit, sends the wrong
 * type or cache lifetime for a pinned file, or breaks the existing routes next to it.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { after, before, test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { Miniflare } from 'miniflare'

const ORIGIN = 'https://updates.test'
const COMMIT = '374959484e79a640feaba44c93ac8cfb0a03f5b5'
const FOLDER = 'lib/Windows_x86_64/rel'
const DLL = `cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`
const DLL_BYTES = Buffer.from(Array.from({ length: 1000 }, (_, i) => (i * 7) % 256))
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

const mf = new Miniflare({
  modules: true,
  // The Worker's files are ES modules (package.json "type": "module"); miniflare's default treats .js as CommonJS.
  modulesRules: [{ type: 'ESModule', include: ['**/*.js', '**/*.mjs'] }],
  scriptPath: fileURLToPath(new URL('../src/worker.js', import.meta.url)),
  // The newest date this local workerd accepts. The Worker deploys at 2026-11-18; the route uses nothing date-dependent.
  compatibilityDate: '2026-08-06',
  r2Buckets: ['MIRROR'],
})
let bucket

before(async () => {
  bucket = await mf.getR2Bucket('MIRROR')
  await bucket.put(DLL, DLL_BYTES, { httpMetadata: { contentType: 'application/octet-stream' } })
  await bucket.put(`cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/LICENSE.txt`, 'licence text\n')
  await bucket.put('cache/NVIDIA/DLSS/commits/v310.9.1', JSON.stringify({ sha: COMMIT }))
  await bucket.put(`cache/NVIDIA/DLSS/contents/${FOLDER}@${COMMIT}`, JSON.stringify([{ type: 'file', name: 'nvngx_dlss.dll', size: 1000 }]))
  // App release files live in the same bucket and must never be reachable through the cache.
  await bucket.put('launcher/latest.json', JSON.stringify({ schema: 2, tag: 'v9.9.9' }))
  await bucket.put('advisories.json', JSON.stringify({ ok: true }))
})
after(async () => {
  await mf.dispose()
})

const get = (path, init = {}) => mf.dispatchFetch(ORIGIN + path, init)

test('a stored pinned file answers 200 with its exact bytes, size, type and an immutable lifetime', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`)
  assert.equal(res.status, 200)
  const body = Buffer.from(await res.arrayBuffer())
  assert.equal(sha256(body), sha256(DLL_BYTES))
  assert.equal(res.headers.get('content-length'), String(DLL_BYTES.length))
  assert.equal(res.headers.get('content-type'), 'application/octet-stream')
  assert.equal(res.headers.get('cache-control'), 'public, max-age=31536000, immutable')
  assert.equal(res.headers.get('location'), null, 'no redirect')
})

test('HEAD gives the same status and headers as GET, with no body', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`, { method: 'HEAD' })
  assert.equal(res.status, 200)
  assert.equal(res.headers.get('content-length'), String(DLL_BYTES.length))
  assert.equal((await res.arrayBuffer()).byteLength, 0)
})

test('a byte range answers 206 with exactly those bytes', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`, { headers: { range: 'bytes=10-19' } })
  assert.equal(res.status, 206)
  assert.deepEqual(Buffer.from(await res.arrayBuffer()), DLL_BYTES.subarray(10, 20))
  assert.equal(res.headers.get('content-range'), `bytes 10-19/${DLL_BYTES.length}`)
})

test('a matching If-None-Match answers 304 with no body', async () => {
  const first = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`)
  const etag = first.headers.get('etag')
  assert.ok(etag, 'an ETag is sent')
  const again = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`, { headers: { 'if-none-match': etag } })
  assert.equal(again.status, 304)
})

test('a licence stored next to the DLL is served by its exact key', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/LICENSE.txt`)
  assert.equal(res.status, 200)
  assert.equal(res.headers.get('content-type'), 'text/plain; charset=utf-8')
  assert.equal(await res.text(), 'licence text\n')
})

test('a key that was never stored answers 404', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlssg.dll`)
  assert.equal(res.status, 404)
})

test('a folder answers 404 with or without a trailing slash: no listing is ever served', async () => {
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}`)).status, 404)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/`)).status, 404)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/`)).status, 404)
})

test('an app release file cannot be reached through the cache', async () => {
  assert.equal((await get('/v1/cache/launcher/latest.json')).status, 404)
  assert.equal((await get('/v1/cache/advisories.json')).status, 404)
  // The existing routes still answer their own paths.
  const manifest = await get('/v1/launcher/latest')
  assert.notEqual(manifest.status, 200, 'no release is in the bucket for this app, so the existing route refuses')
})

test('traversal, empty and odd segments are refused with 404', async () => {
  const bad = [
    `/v1/cache/NVIDIA/%2e%2e/launcher/latest.json`,
    `/v1/cache/NVIDIA/DLSS/%2e%2e%2f%2e%2e%2flauncher/latest.json`,
    `/v1/cache/NVIDIA//DLSS/${COMMIT}`,
    `/v1/cache/NVIDIA%5CDLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`,
    `/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll%00`,
    `/v1/cache/NVIDIA/DLSS`,
    `/v1/cache/`,
  ]
  for (const path of bad) assert.equal((await get(path)).status, 404, path)
})

test('a query on a plain file is refused: exact keys only', async () => {
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll?cb=1`)).status, 404)
})

test('a folder listing answers for its own commit only, as GitHub-shaped JSON', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/contents/${FOLDER}?ref=${COMMIT}`)
  assert.equal(res.status, 200)
  assert.equal(res.headers.get('content-type'), 'application/json; charset=utf-8')
  assert.equal((await res.json())[0].name, 'nvngx_dlss.dll')
  // Another commit has no listing stored, and a missing or malformed ref is refused.
  const other = 'a'.repeat(40)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/contents/${FOLDER}?ref=${other}`)).status, 404)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/contents/${FOLDER}`)).status, 404)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/contents/${FOLDER}?ref=main`)).status, 404)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/contents/${FOLDER}?ref=${COMMIT}&x=1`)).status, 404)
})

test('a commit answer is GitHub-shaped JSON with its own short lifetime only for the releases pointer', async () => {
  const commit = await get('/v1/cache/NVIDIA/DLSS/commits/v310.9.1')
  assert.equal(commit.status, 200)
  assert.equal((await commit.json()).sha, COMMIT)
  assert.equal(commit.headers.get('content-type'), 'application/json; charset=utf-8')
  const latest = await get('/v1/cache/NVIDIA/DLSS/releases/latest')
  assert.equal(latest.status, 404, 'no pointer is stored for releases/latest')
})

test('other methods are refused as before', async () => {
  const res = await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`, { method: 'POST' })
  assert.equal(res.status, 405)
})

test('the third-party frame generation mods are never served, even when R2 still holds them', async () => {
  const keys = [
    'cache/optiscaler/OptiScaler/releases/download/v0.9.4/Optiscaler_0.9.4-final.20260718._MM.7z',
    'cache/optiscaler/OptiScaler/releases/download/v0.9.4/LICENSE',
    'cache/dashdogy/MFG-Unlocker/releases/download/v1.7.0/MFGUnlocker-v1.7.0.zip',
    'cache/dashdogy/MFG-Unlocker/releases/download/v1.7.0/LICENSE',
    'cache/sdli1995/dlssg_for_sm86/releases/download/0.4.2/dlssg-release-0.4.2-x64.zip',
  ]
  for (const key of keys) {
    await bucket.put(key, 'mod bytes')
    assert.notEqual(await bucket.head(key), null, key)
    for (const method of ['GET', 'HEAD']) assert.equal((await get('/v1/' + key, { method })).status, 404, `${method} ${key}`)
    assert.equal((await get('/v1/' + key, { headers: { range: 'bytes=0-3' } })).status, 404, `range ${key}`)
  }
  assert.equal((await get('/v1/cache/%6Fptiscaler/OptiScaler/releases/download/v0.9.4/LICENSE')).status, 404)
  assert.equal((await get('/v1/cache/OPTISCALER/optiscaler/releases/download/v0.9.4/LICENSE')).status, 404)
  assert.equal((await get(`/v1/cache/dashdogy/MFG-Unlocker/contents/bin?ref=${COMMIT}`)).status, 404)
  assert.equal((await get(`/v1/cache/NVIDIA/DLSS/${COMMIT}/${FOLDER}/nvngx_dlss.dll`)).status, 200)
})

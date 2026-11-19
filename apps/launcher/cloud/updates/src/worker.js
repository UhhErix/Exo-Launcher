// Exo's update address (../README.md): each Exo app asks here for its newest version and downloads through here, so no
// app names a host that can change under it. Everything is served from the mirror, R2 bucket `exo-releases` (binding MIRROR):
//   <app>/latest.json            the manifest for the newest release (schema 2, see README.md)
//   <app>/<tag>/<file>           the release files, immutable under their tag
//   advisories.json              Exo Launcher's driver advisories, signed by ../advisories.mjs
//   <app>/CHANGELOG.md           each app's change list, for getexo.app's Changelog pages (/v1/<app>/changelog)
//   <app>/<tag>/payload-manifest.json   every file of the installed app at that release: path, size, SHA-256 (/v1/<app>/payload/<tag>)
//   <app>/files/<sha256>.gz      one app file, gzipped, named by the SHA-256 of its bytes (/v1/<app>/file/<sha256>), never replaced
//   cache/<github path>          Exo's copy of the GitHub paths the Launcher pins (/v1/cache/<github path>), read-only, by exact key
// GitHub is not asked for anything. Releases reach the mirror through publish-release.ps1, which then asks POST /v1/<app>/announce
// for the release card (announce.js). The cache is filled by cache-mirror.mjs.
import { announce } from './announce.js'
import { r2Store, runWatch } from './watch.js'
const APPS = {
  launcher: { setup: 'ExoLauncher-Setup.exe' },
  cinema: { setup: 'ExoCinema-Setup.exe' },
}
const TAG = /^v\d{1,4}\.\d{1,4}\.\d{1,4}$/
const SHA256 = /^[0-9a-f]{64}$/
const LATEST = /^\/v1\/(launcher|cinema)\/latest$/
const DOWNLOAD = /^\/v1\/(launcher|cinema)\/download\/(v\d{1,4}\.\d{1,4}\.\d{1,4})\/([A-Za-z0-9.-]{1,64})$/
const CHANGELOG = /^\/v1\/(launcher|cinema)\/changelog$/
// File-level updates (misc/updates/file-level-updates.md): an app reads the new release's file list, then fetches only the
// files it doesn't already have, each by the SHA-256 of its bytes. A release without a list answers 404 and the app uses Setup.
const PAYLOAD = /^\/v1\/(launcher|cinema)\/payload\/(v\d{1,4}\.\d{1,4}\.\d{1,4})$/
const FILE = /^\/v1\/(launcher|cinema)\/file\/([0-9a-f]{64})$/
// publish-release.ps1 asks once a release is verified live; announce.js posts the card, once per tag (the only POST route).
const ANNOUNCE = /^\/v1\/(launcher|cinema)\/announce$/
const ANNOUNCE_BYTES = 1024
const ANNOUNCE_STATUS = { 'not-live': 409, 'wrong-version': 409, refused: 409, failed: 502 }
// The newest-version answer is kept at the edge for a minute: a release reaches everyone within about a minute.
const FRESH_SECONDS = 60
// The edge cache is shared by every Worker version: bump this whenever the latest response shape changes, so a deploy never serves an older version's cached answer.
const CACHE_SCHEMA = 3
// Release files are immutable under their tag: an hour at the edge, whatever the R2 object's own metadata says.
const IMMUTABLE_SECONDS = 3600
// A change list changes with each release, so it is kept at the edge a minute, as the newest-version answer is.
const CHANGELOG_SECONDS = 60
const CHANGELOG_TYPE = 'text/markdown; charset=utf-8'
/** Exo's cache: a GitHub path is served from R2 under `cache/`, by exact key. Pinned content (a commit, a tag's file) is immutable. */
const CACHE_PREFIX = '/v1/cache/'
const CACHE_SEGMENT = /^[A-Za-z0-9._-]{1,200}$/
const CACHE_REF = /^[0-9a-f]{40}$/
const CACHE_IMMUTABLE_SECONDS = 31536000
// A releases/latest pointer moves when a vendor ships, so it is kept at the edge only briefly.
const CACHE_POINTER_SECONDS = 300
/** The keys that move: a vendor's releases/latest, and the DLSS Swapper catalog the cache watch rewrites. */
const MUTABLE = new RegExp('/releases/latest$|^cache/exo/dlss-swapper/catalog[.]json$')
/** The driver advisories' path and its R2 object. */
const ADVISORIES = '/v1/advisories.json'
const ADVISORIES_KEY = 'advisories.json'
/** The three files a release has: the installer, its SHA-256 and the change list. Nothing else is ever served. */
const filesOf = (app) => ({ setup: APPS[app].setup, sha256: APPS[app].setup + '.sha256', whatsNew: 'whats-new.json' })
export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url)
    let match
    if (request.method === 'POST' && (match = ANNOUNCE.exec(url.pathname))) return announceRoute(match[1], request, env)
    if (request.method !== 'GET' && request.method !== 'HEAD') return new Response('GET only', { status: 405, headers: { allow: 'GET, HEAD' } })
    let response
    if ((match = LATEST.exec(url.pathname))) response = await latest(match[1], url.origin, request, env, ctx)
    else if ((match = DOWNLOAD.exec(url.pathname))) response = await download(match[1], match[2], match[3], request, env)
    else if ((match = CHANGELOG.exec(url.pathname))) response = await changelog(match[1], request, env)
    else if ((match = PAYLOAD.exec(url.pathname))) response = await payload(match[1], match[2], request, env)
    else if ((match = FILE.exec(url.pathname))) response = await file(match[1], match[2], request, env)
    else if (url.pathname === ADVISORIES) response = await advisories(request, env)
    else if (url.pathname.startsWith(CACHE_PREFIX)) response = await cached(url, request, env)
    else response = new Response('Not found', { status: 404 })
    // A HEAD gets the same status and headers as its GET, and no body.
    return request.method === 'HEAD' ? new Response(null, response) : response
  },
  /** The daily cache watch (watch.js): finds each vendor's newest SDK and DLSS Swapper's newer archives, and stores them once. */
  async scheduled(controller, env, ctx) {
    if (!env.MIRROR) return
    ctx.waitUntil(runWatch({ store: r2Store(env.MIRROR), fetch: (...args) => fetch(...args), log: (line) => console.log(line) }))
  },
}
/**
 * POST /v1/<app>/announce, body {"tag":"vX.Y.Z","announce":true|false}. It posts the release card only for a tag the mirror
 * already serves as newest, once; announce:false (publish-release -NoAnnounce) is a no-op. A missing
 * webhook is logged and answered 200, so a release never fails over the card.
 */
async function announceRoute(app, request, env) {
  if (!env.MIRROR) return reply({ status: 'unavailable' }, 503)
  const text = await request.text()
  if (text.length > ANNOUNCE_BYTES) return reply({ status: 'too-large' }, 413)
  let body
  try {
    body = JSON.parse(text)
  } catch {
    return reply({ status: 'bad-request' }, 400)
  }
  if (typeof body?.tag !== 'string' || !TAG.test(body.tag)) return reply({ status: 'bad-request' }, 400)
  try {
    const result = await announce({ app, tag: body.tag, announce: body.announce !== false, env, mirror: env.MIRROR })
    return reply({ status: result.status }, ANNOUNCE_STATUS[result.status] ?? 200)
  } catch {
    return reply({ status: 'failed' }, 502)
  }
}
function reply(body, status) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', 'cache-control': 'no-store' } })
}
async function latest(app, origin, request, env, ctx) {
  const cache = globalThis.caches?.default
  const key = new Request(`${origin}/v1/${app}/latest?schema=${CACHE_SCHEMA}`)
  const kept = await cache?.match(key)
  if (kept) return revalidated(request, kept)
  const manifest = await readManifest(app, env)
  if (!manifest) return new Response('No release found', { status: 503, headers: { 'cache-control': 'no-store' } })
  const names = filesOf(app)
  const files = Object.fromEntries(Object.keys(names).map((part) => [part, `${origin}/v1/${app}/download/${manifest.tag}/${names[part]}`]))
  // Each file's size and SHA-256, from the manifest, so an app can check what it downloaded without asking again.
  const checks = manifest.files ? Object.fromEntries(Object.keys(names).map((part) => [part, manifest.files[part]])) : undefined
  const body = { app, tag: manifest.tag, version: manifest.tag.slice(1), source: 'mirror', files, ...(checks ? { checks } : {}) }
  // The ETag names the answer (app, tag), so a poll that already has it gets a 304 with no body.
  // CDN-Cache-Control pins Cloudflare's own edge copy too: a query string (?cb=) once bypassed the worker's cache.
  const response = new Response(JSON.stringify(body), {
    headers: {
      'content-type': 'application/json',
      etag: `"${app}-${manifest.tag}"`,
      'cache-control': `public, max-age=${FRESH_SECONDS}`,
      'cdn-cache-control': `max-age=${FRESH_SECONDS}`,
    },
  })
  if (cache) ctx.waitUntil(cache.put(key, response.clone()))
  return revalidated(request, response)
}
/** The same answer as a 304 when the client already holds it (If-None-Match names its ETag). */
function revalidated(request, response) {
  const etag = response.headers.get('etag')
  const asked = request.headers.get('if-none-match')
  if (response.status !== 200 || !etag || !asked) return response
  const named = asked.trim() === '*' || asked.split(',').map((tag) => tag.trim()).includes(etag)
  return named ? new Response(null, { status: 304, headers: response.headers }) : response
}
/**
 * The app's manifest from the mirror, or null. Accepts schema 2 (tag plus per-file size and SHA-256) and the older
 * tag-only manifest, which still names the tag and gets no checks.
 */
async function readManifest(app, env) {
  try {
    const manifest = await (await env.MIRROR?.get(`${app}/latest.json`))?.json()
    if (!manifest || typeof manifest.tag !== 'string' || !TAG.test(manifest.tag)) return null
    if (manifest.schema === 2) {
      const names = filesOf(app)
      for (const part of Object.keys(names)) {
        const file = manifest.files?.[part]
        if (file?.name !== names[part] || !Number.isInteger(file.size) || !SHA256.test(file.sha256 ?? '')) return null
      }
    }
    return manifest
  } catch {
    return null
  }
}
async function download(app, tag, name, request, env) {
  if (!Object.values(filesOf(app)).includes(name)) return new Response('Not found', { status: 404 })
  return fromMirror(`${app}/${tag}/${name}`, request, env)
}
/** An app's CHANGELOG.md from the mirror, as markdown; 404 until publish-release.ps1 has uploaded one. */
function changelog(app, request, env) {
  return fromMirror(`${app}/CHANGELOG.md`, request, env, { seconds: CHANGELOG_SECONDS, type: CHANGELOG_TYPE })
}
/** A release's file list, immutable under its tag as its other files are. Never kept when it is missing: it may arrive a moment later. */
async function payload(app, tag, request, env) {
  return found(await fromMirror(`${app}/${tag}/payload-manifest.json`, request, env, { type: 'application/json; charset=utf-8' }))
}
/** One app file by the SHA-256 of its bytes, gzipped. The name says what the bytes are, so it is kept for a year. */
async function file(app, sha256, request, env) {
  return found(await fromMirror(`${app}/files/${sha256}.gz`, request, env, { seconds: CACHE_IMMUTABLE_SECONDS, immutable: true, type: 'application/gzip' }))
}
/** A 404 that nothing keeps: the publish run asks for a file before it uploads it, and must see it right after. */
function found(response) {
  return response.status === 404 ? new Response('Not found', { status: 404, headers: { 'cache-control': 'no-store' } }) : response
}
/** The driver advisories, kept at the edge an hour (the app reads them at most once a day). */
async function advisories(request, env) {
  const copy = await fromMirror(ADVISORIES_KEY, request, env)
  if (copy.status !== 200) return copy
  const headers = new Headers(copy.headers)
  headers.set('content-type', 'application/json')
  return new Response(copy.body, { headers })
}
/**
 * Exo's cache for a GitHub path (/v1/cache/<owner>/<repo>/...), read-only: the R2 object under `cache/` with the same
 * path, and nothing else. Two shapes are not a plain path: a folder listing (contents/<folder>?ref=<40-hex commit>)
 * maps to the object `contents/<folder>@<commit>`, and any other query is refused. Every segment is a plain name, so no
 * path can reach an app's release files, and a folder with no object of its own answers 404 (no listing exists).
 */
async function cached(url, request, env) {
  const key = cacheKey(url)
  if (!key) return new Response('Not found', { status: 404 })
  const pointer = MUTABLE.test(key)
  return fromMirror(key, request, env, {
    seconds: pointer ? CACHE_POINTER_SECONDS : CACHE_IMMUTABLE_SECONDS,
    immutable: !pointer,
    type: contentTypeOf(key),
  })
}
/** The R2 key for a cache request, or null when the request is not one the cache serves. */
function cacheKey(url) {
  const parts = url.pathname.slice(CACHE_PREFIX.length).split('/')
  const names = parts.map((part) => {
    try {
      return decodeURIComponent(part)
    } catch {
      return null
    }
  })
  if (names.length < 3 || names.some((name) => name === null || !CACHE_SEGMENT.test(name) || name === '.' || name === '..')) return null
  if (!url.search) return `cache/${names.join('/')}`
  // A listing is served only for one commit: contents/<folder>?ref=<sha> and no other query.
  const query = [...new URLSearchParams(url.search)]
  const ref = query.length === 1 && query[0][0] === 'ref' ? query[0][1] : ''
  if (names[2] !== 'contents' || names.length < 4 || !CACHE_REF.test(ref)) return null
  return `cache/${names[0]}/${names[1]}/contents/${names.slice(3).join('/')}@${ref}`
}
/** The type a cache object is served as, by what it is: a listing or a metadata JSON, a licence text, or a binary. */
function contentTypeOf(key) {
  if (/\/(contents|commits)\/|\/releases\/latest$|\.json$/.test(key)) return 'application/json; charset=utf-8'
  if (key.endsWith('.md')) return 'text/markdown; charset=utf-8'
  if (key.endsWith('.txt')) return 'text/plain; charset=utf-8'
  return 'application/octet-stream'
}
/**
 * The mirror's copy, with byte ranges so an interrupted download picks up where it stopped, and a 304 when the client
 * already has this exact copy (R2 answers the condition itself, so the body is never read).
 */
async function fromMirror(key, request, env, { seconds = IMMUTABLE_SECONDS, type, immutable = false } = {}) {
  if (!env.MIRROR) return new Response('Not available', { status: 503, headers: { 'cache-control': 'no-store' } })
  const heading = request.method === 'HEAD'
  const ranged = !heading && request.headers.has('range')
  const object = heading
    ? await env.MIRROR.head(key)
    : await env.MIRROR.get(key, ranged ? { range: request.headers } : { onlyIf: request.headers })
  if (!object) return new Response('Not found', { status: 404 })
  const headers = new Headers({ etag: object.httpEtag, 'accept-ranges': 'bytes' })
  object.writeHttpMetadata(headers)
  if (type) headers.set('content-type', type)
  if (!headers.has('content-type')) headers.set('content-type', 'application/octet-stream')
  // Set after writeHttpMetadata: cache metadata stored on the object itself can never stretch a release file's
  // cache past its hour (the files never change under their tag).
  headers.set('cache-control', `public, max-age=${seconds}${immutable ? ', immutable' : ''}`)
  headers.set('cdn-cache-control', `max-age=${seconds}`)
  if (!heading && !object.body) return new Response(null, { status: 304, headers })
  const range = object.range
  if (ranged && range) {
    const start = range.offset ?? (range.suffix !== undefined ? object.size - range.suffix : 0)
    const length = range.length ?? (range.suffix ?? object.size - start)
    headers.set('content-range', `bytes ${start}-${start + length - 1}/${object.size}`)
    headers.set('content-length', String(length))
    return new Response(object.body, { status: 206, headers })
  }
  headers.set('content-length', String(object.size))
  return new Response(heading ? null : object.body, { headers })
}

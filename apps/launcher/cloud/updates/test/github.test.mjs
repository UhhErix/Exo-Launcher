/**
 * Exo Launcher's releases read from GitHub (src/github.js through src/worker.js), with GitHub, R2 and the edge cache stood in for.
 *
 * How it could fail: it offers a release whose .sha256 GitHub's own digest contradicts; it asks GitHub on every poll (and
 * runs into the unauthenticated rate limit); it moves the offered version backwards; it replaces a tag's files that R2
 * already holds; it stores, or serves from R2, an installer whose bytes don't match its SHA-256; it loses a byte range;
 * it fails when GitHub is down instead of answering from R2; it names the file-level update's files when the release
 * doesn't carry both, or with a pack hash GitHub contradicts; it posts the release card twice or never.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { afterEach, beforeEach, test } from 'node:test'
import { CHECK_SECONDS, STATE_KEY, compactRelease, fill, parseSha256File } from '../src/github.js'

const worker = (await import('../src/worker.js')).default
const ORIGIN = 'https://updates.test'
const REPO = 'UhhErix/Exo-Launcher'
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex')
const enc = (text) => new TextEncoder().encode(text)
const DL = (tag, name) => `https://github.com/${REPO}/releases/download/${tag}/${name}`

// ---- R2 -------------------------------------------------------------------------------------------------------------
/** R2 as the Worker sees it. strict: put's sha256 option is enforced, as R2 does; lax stands in for an R2 that missed it. */
function r2({ strict = true } = {}) {
  const objects = new Map()
  const metas = new Map()
  const writes = []
  const etagOf = (bytes) => sha(bytes).slice(0, 32)
  const object = (key, bytes, range) => {
    let part = bytes
    let r
    if (range) {
      const m = /^bytes=(\d*)-(\d*)$/.exec(range.get('range'))
      if (m[1] === '') {
        const suffix = +m[2]
        r = { suffix }
        part = bytes.slice(bytes.length - suffix)
      } else {
        const offset = +m[1]
        if (offset >= bytes.length) throw new RangeError('range not satisfiable')
        const end = m[2] ? Math.min(+m[2], bytes.length - 1) : bytes.length - 1
        r = { offset, length: end - offset + 1 }
        part = bytes.slice(offset, end + 1)
      }
    }
    return {
      key, size: bytes.length, etag: etagOf(bytes), httpEtag: `"${etagOf(bytes)}"`, range: r, customMetadata: metas.get(key) ?? {},
      checksums: { sha256: Buffer.from(sha(bytes), 'hex') },
      body: new Blob([part]).stream(),
      writeHttpMetadata: (h) => h.set('content-type', key.endsWith('.json') ? 'application/json' : 'application/octet-stream'),
      text: async () => new TextDecoder().decode(bytes),
      json: async () => JSON.parse(new TextDecoder().decode(bytes)),
      arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength),
    }
  }
  return {
    objects, writes, metas,
    set: (key, value) => objects.set(key, typeof value === 'string' ? enc(value) : value),
    text: (key) => (objects.has(key) ? new TextDecoder().decode(objects.get(key)) : null),
    get: async (key, options) => {
      if (!objects.has(key)) return null
      const o = object(key, objects.get(key), options?.range)
      if (options?.onlyIf?.get?.('if-none-match') === o.httpEtag) { const { body: _b, ...meta } = o; return meta }
      return o
    },
    head: async (key) => (objects.has(key) ? { ...object(key, objects.get(key)), body: undefined } : null),
    put: async (key, value, options = {}) => {
      if (options.onlyIf?.etagDoesNotMatch === '*' && objects.has(key)) return null
      if (options.onlyIf?.etagMatches !== undefined && (!objects.has(key) || etagOf(objects.get(key)) !== options.onlyIf.etagMatches)) return null
      let bytes
      if (value instanceof ReadableStream) bytes = new Uint8Array(await new Response(value).arrayBuffer())
      else bytes = typeof value === 'string' ? enc(value) : new Uint8Array(value)
      if (strict && options.sha256 && sha(bytes) !== options.sha256) throw new Error('put: The SHA-256 checksum you specified did not match')
      objects.set(key, bytes)
      metas.set(key, options.customMetadata ?? {})
      writes.push(key)
      return object(key, bytes)
    },
    delete: async (key) => { objects.delete(key); metas.delete(key) },
    list: async ({ prefix }) => ({ objects: [...objects.keys()].filter((k) => k.startsWith(prefix)).map((key) => ({ key })) }),
  }
}

// ---- GitHub ---------------------------------------------------------------------------------------------------------
/** A release as GitHub's API answers it, with the files it serves at its download URLs. */
function release(tag, { installer = enc('installer ' + tag), shaFile, notesAsset, digests = true, delta, extra = {} } = {}) {
  const version = tag.slice(1)
  const setupName = `ExoLauncher-${version}-Setup.exe`
  const files = {
    [setupName]: installer,
    [setupName + '.sha256']: enc(shaFile ?? `${sha(installer)}  ${setupName}\n`),
    ...(notesAsset ? { 'whats-new.json': enc(notesAsset) } : {}),
    ...(delta ?? {}),
    ...extra,
  }
  const json = {
    tag_name: tag, draft: false, prerelease: false,
    assets: Object.entries(files).map(([name, bytes]) => ({
      name, size: bytes.length, state: 'uploaded', browser_download_url: DL(tag, name),
      digest: digests === true || (Array.isArray(digests) && digests.includes(name)) ? `sha256:${sha(bytes)}` : null,
    })),
  }
  return { tag, json, files }
}

const notesFor = (version, n = 2) => ({ version, items: Array.from({ length: n }, (_, i) => ({ icon: 'Sparkle', title: `Item ${i + 1}`, detail: 'Does a thing.' })) })

/** GitHub: the API, raw.githubusercontent.com and the download host. Every request is recorded. */
function github({ latest, tags = {}, trees = {} }) {
  const state = { latest, down: false, asked: [], etag: '"rel-1"' }
  const all = () => [state.latest, ...Object.values(tags)].filter(Boolean)
  state.fetch = async (input, init = {}) => {
    const url = String(input)
    const headers = new Headers(init.headers ?? {})
    state.asked.push({ url, range: headers.get('range'), inm: headers.get('if-none-match') })
    if (state.down) throw new TypeError('fetch failed')
    if (url === `https://api.github.com/repos/${REPO}/releases/latest`) {
      if (!state.latest) return new Response('{}', { status: 404 })
      if (headers.get('if-none-match') === state.etag) return new Response(null, { status: 304 })
      return Response.json(state.latest.json, { headers: { etag: state.etag } })
    }
    const byTag = /\/releases\/tags\/(v[\d.]+)$/.exec(url)
    if (byTag) {
      const r = all().find((x) => x.tag === byTag[1])
      return r ? Response.json(r.json) : new Response('{}', { status: 404 })
    }
    const raw = new RegExp(`^https://raw\\.githubusercontent\\.com/${REPO}/(v[\\d.]+)/apps/launcher/(.+)$`).exec(url)
    if (raw) {
      const tree = trees[raw[1]]
      return tree?.[raw[2]] !== undefined ? new Response(tree[raw[2]]) : new Response('404', { status: 404 })
    }
    for (const r of all()) {
      for (const [name, bytes] of Object.entries(r.files)) {
        if (url !== DL(r.tag, name)) continue
        const range = headers.get('range')
        if (range) {
          const m = /^bytes=(\d+)-(\d*)$/.exec(range)
          const start = +m[1]
          const end = m[2] ? +m[2] : bytes.length - 1
          return new Response(bytes.slice(start, end + 1), { status: 206, headers: { 'content-range': `bytes ${start}-${end}/${bytes.length}`, 'content-length': String(end - start + 1) } })
        }
        return new Response(bytes, { headers: { 'content-length': String(bytes.length) } })
      }
    }
    return new Response('not found', { status: 404 })
  }
  return state
}

/** The tag's committed tree: VERSION, whatsNew.json and CHANGELOG.md. */
const tree = (version, { notes = notesFor(version, 3), changelog = `# Changelog\n\n## ${version} — November 20, 2026\n- A change.\n` } = {}) => ({
  VERSION: version,
  'ui/src/lib/whatsNew.json': JSON.stringify([notes, notesFor('0.0.1')]),
  'CHANGELOG.md': changelog,
})

/** The mirror's manifest for an older release, as publish-release.ps1 wrote it. */
function seed(mirror, tag, installer = enc('installer ' + tag)) {
  const shaFile = enc(`${sha(installer)}  ExoLauncher-Setup.exe`)
  const notes = enc(JSON.stringify(notesFor(tag.slice(1), 1)))
  mirror.set(`launcher/${tag}/ExoLauncher-Setup.exe`, installer)
  mirror.set(`launcher/${tag}/ExoLauncher-Setup.exe.sha256`, shaFile)
  mirror.set(`launcher/${tag}/whats-new.json`, notes)
  mirror.set('launcher/latest.json', JSON.stringify({
    schema: 2, app: 'launcher', tag, version: tag.slice(1),
    files: {
      setup: { name: 'ExoLauncher-Setup.exe', size: installer.length, sha256: sha(installer) },
      sha256: { name: 'ExoLauncher-Setup.exe.sha256', size: shaFile.length, sha256: sha(shaFile) },
      whatsNew: { name: 'whats-new.json', size: notes.length, sha256: sha(notes) },
    },
  }))
}

// ---- the harness ----------------------------------------------------------------------------------------------------
let clock
let gh
const realFetch = globalThis.fetch
const realNow = Date.now
/** The edge cache, honouring max-age as Cloudflare's does. A new call is a new edge with nothing kept. */
function edge() {
  const stored = new Map()
  const keyOf = (req) => (typeof req === 'string' ? req : req.url)
  globalThis.caches = { default: {
    match: async (req) => {
      const kept = stored.get(keyOf(req))
      return kept && Date.now() < kept.expires ? kept.response.clone() : undefined
    },
    put: async (req, res) => {
      const age = Number(/max-age=(\d+)/.exec(res.headers.get('cache-control') ?? '')?.[1] ?? 0)
      stored.set(keyOf(req), { response: res, expires: Date.now() + age * 1000 })
    },
  } }
  return stored
}
function context() {
  const pending = []
  return { pending, waitUntil: (p) => pending.push(p), settle: async () => { while (pending.length) await pending.shift() } }
}
async function ask(path, env, init = {}) {
  const ctx = context()
  const response = await worker.fetch(new Request(ORIGIN + path, init), env, ctx)
  const bytes = init.method === 'HEAD' ? null : new Uint8Array(await response.arrayBuffer())
  await ctx.settle()
  return { response, bytes, json: () => JSON.parse(new TextDecoder().decode(bytes)) }
}
const apiAsks = () => gh.asked.filter((a) => new URL(a.url).hostname === 'api.github.com').length
beforeEach(() => {
  clock = 1_800_000_000_000
  Date.now = () => clock
  edge()
})
afterEach(() => {
  globalThis.fetch = realFetch
  Date.now = realNow
  delete globalThis.caches
})
function use(state) {
  gh = state
  globalThis.fetch = state.fetch
}

// ---- the manifest ---------------------------------------------------------------------------------------------------
test('a newer GitHub release becomes the manifest: the .sha256 checked against GitHub\'s digest, What\'s New and the changelog from the tag', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const rel = release('v3.27.0')
  use(github({ latest: rel, trees: { 'v3.27.0': tree('3.27.0') } }))
  const { response, json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(response.status, 200)
  const body = json()
  assert.equal(body.tag, 'v3.27.0')
  assert.equal(body.version, '3.27.0')
  assert.equal(body.source, 'mirror')
  assert.equal(body.files.setup, `${ORIGIN}/v1/launcher/download/v3.27.0/ExoLauncher-Setup.exe`)
  assert.deepEqual(Object.keys(body.files), ['setup', 'sha256', 'whatsNew'])
  const installer = rel.files['ExoLauncher-3.27.0-Setup.exe']
  assert.deepEqual(body.checks.setup, { name: 'ExoLauncher-Setup.exe', size: installer.length, sha256: sha(installer) })
  // The .sha256 the apps read names the file they ask for, as Build-Release writes it.
  assert.equal(mirror.text('launcher/v3.27.0/ExoLauncher-Setup.exe.sha256'), `${sha(installer)}  ExoLauncher-Setup.exe`)
  assert.equal(body.checks.sha256.sha256, sha(mirror.objects.get('launcher/v3.27.0/ExoLauncher-Setup.exe.sha256')))
  // What's New: the tag's entry (three items), not anything else.
  const notes = JSON.parse(mirror.text('launcher/v3.27.0/whats-new.json'))
  assert.equal(notes.version, '3.27.0')
  assert.equal(notes.items.length, 3)
  assert.match(mirror.text('launcher/CHANGELOG.md'), /## 3\.27\.0/)
  assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.27.0')
  // Nothing large was asked of GitHub to answer: no installer bytes in the poll's path.
  assert.ok(!gh.asked.some((a) => a.url.endsWith('.exe') && a.range === null && gh.asked.indexOf(a) < 4))
})

test('the release\'s whats-new.json asset is preferred over the tag\'s entry', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const asset = JSON.stringify(notesFor('3.27.0', 5))
  use(github({ latest: release('v3.27.0', { notesAsset: asset }), trees: { 'v3.27.0': tree('3.27.0') } }))
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(json().tag, 'v3.27.0')
  assert.equal(mirror.text('launcher/v3.27.0/whats-new.json'), asset)
})

test('a release GitHub\'s own record contradicts, or a tag without its notes, VERSION or changelog entry, is not offered', async (t) => {
  const cases = {
    'the .sha256 names another hash': () => ({ latest: release('v3.27.0', { shaFile: `${'f'.repeat(64)}  ExoLauncher-3.27.0-Setup.exe` }), trees: { 'v3.27.0': tree('3.27.0') } }),
    'the .sha256 names another file': () => ({ latest: release('v3.27.0', { shaFile: `${sha(enc('installer v3.27.0'))}  Other.exe` }), trees: { 'v3.27.0': tree('3.27.0') } }),
    'VERSION is another version': () => ({ latest: release('v3.27.0'), trees: { 'v3.27.0': { ...tree('3.27.0'), VERSION: '3.26.9' } } }),
    'no What\'s New entry': () => ({ latest: release('v3.27.0'), trees: { 'v3.27.0': { ...tree('3.27.0'), 'ui/src/lib/whatsNew.json': '[]' } } }),
    'no changelog entry': () => ({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0', { changelog: '## 3.26.0 — x\n' }) } }),
    'GitHub recorded no SHA-256 for the installer': () => ({ latest: release('v3.27.0', { digests: ['ExoLauncher-3.27.0-Setup.exe.sha256'] }), trees: { 'v3.27.0': tree('3.27.0') } }),
    'no installer attached': () => ({ latest: { ...release('v3.27.0'), json: { ...release('v3.27.0').json, assets: [] } }, trees: { 'v3.27.0': tree('3.27.0') } }),
  }
  for (const [name, setup] of Object.entries(cases)) {
    await t.test(name, async () => {
      edge()
      const mirror = r2()
      seed(mirror, 'v3.26.0')
      use(github(setup()))
      const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
      assert.equal(json().tag, 'v3.26.0')
      assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.26.0')
    })
  }
})

test('the offered version never moves backwards: an older GitHub release leaves the mirror\'s newer one', async () => {
  const mirror = r2()
  seed(mirror, 'v3.28.0')
  use(github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(json().tag, 'v3.28.0')
  assert.ok(!mirror.objects.has('launcher/v3.27.0/whats-new.json'))
})

test('the same tag as the mirror\'s is answered from the mirror unchanged (3.26.0: the 18-item notes already there stay)', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const before = mirror.text('launcher/latest.json')
  use(github({ latest: release('v3.26.0'), trees: { 'v3.26.0': tree('3.26.0') } }))
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(json().tag, 'v3.26.0')
  assert.equal(mirror.text('launcher/latest.json'), before)
  assert.equal(JSON.parse(mirror.text('launcher/v3.26.0/whats-new.json')).items.length, 1)
  assert.ok(!gh.asked.some((a) => new URL(a.url).hostname === 'raw.githubusercontent.com'))
})

test('never replace: a tag whose .sha256 the mirror already holds with another hash is refused', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  mirror.set('launcher/v3.27.0/ExoLauncher-Setup.exe.sha256', `${'e'.repeat(64)}  ExoLauncher-Setup.exe`)
  use(github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(json().tag, 'v3.26.0')
  assert.equal(mirror.text('launcher/v3.27.0/ExoLauncher-Setup.exe.sha256'), `${'e'.repeat(64)}  ExoLauncher-Setup.exe`)
})

// ---- the cache ------------------------------------------------------------------------------------------------------
test('GitHub is asked at most once per five minutes: the edge keeps the answer, R2 keeps the time for every other edge', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: release('v3.26.0') }))
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(apiAsks(), 1)
  // The answer's own 60 s edge copy aside (another key), the GitHub check is kept: a minute later, nothing is asked.
  clock += 61_000
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(apiAsks(), 1, 'the same edge keeps the check')
  edge()
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  // A fresh edge (nothing cached) reads the check time from R2 and does not ask GitHub either.
  assert.equal(apiAsks(), 1)
  assert.equal(JSON.parse(mirror.text(STATE_KEY)).release.tag, 'v3.26.0')
})

test('past five minutes the edge answers with what it has and asks again in the background, conditionally (a 304 costs nothing)', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: release('v3.26.0') }))
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  clock += (CHECK_SECONDS + 1) * 1000
  // The 60 s answer copy has expired at this edge, and its GitHub check is stale: it answers, and asks in the background.
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(json().tag, 'v3.26.0')
  const last = gh.asked.filter((a) => new URL(a.url).hostname === 'api.github.com').at(-1)
  assert.equal(apiAsks(), 2)
  assert.equal(last.inm, '"rel-1"')
})

test('a new release reaches the manifest once the check is due again, and the 60-second answer copy has passed', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const state = github({ latest: release('v3.26.0'), trees: { 'v3.27.0': tree('3.27.0') } })
  use(state)
  assert.equal((await ask('/v1/launcher/latest', { MIRROR: mirror })).json().tag, 'v3.26.0')
  state.latest = release('v3.27.0')
  state.etag = '"rel-2"'
  clock += 120_000
  edge()
  assert.equal((await ask('/v1/launcher/latest', { MIRROR: mirror })).json().tag, 'v3.26.0', 'within the five minutes, no new ask')
  clock += CHECK_SECONDS * 1000
  edge()
  assert.equal((await ask('/v1/launcher/latest', { MIRROR: mirror })).json().tag, 'v3.27.0')
})

// ---- GitHub down ----------------------------------------------------------------------------------------------------
test('GitHub down: the last manifest and the installer come from R2', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const state = github({ latest: release('v3.27.0') })
  state.down = true
  use(state)
  const latest = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(latest.response.status, 200)
  assert.equal(latest.json().tag, 'v3.26.0')
  const setup = await ask('/v1/launcher/download/v3.26.0/ExoLauncher-Setup.exe', { MIRROR: mirror })
  assert.equal(setup.response.status, 200)
  assert.equal(sha(setup.bytes), sha(enc('installer v3.26.0')))
  assert.ok(setup.response.headers.get('etag'))
  // A tag R2 doesn't hold, with GitHub down: a 404 nothing keeps, never a guess.
  const missing = await ask('/v1/launcher/download/v3.27.0/ExoLauncher-Setup.exe', { MIRROR: mirror })
  assert.equal(missing.response.status, 404)
  assert.equal(missing.response.headers.get('cache-control'), 'no-store')
})

// ---- the installer: pass-through, the copy into R2, its SHA-256 -----------------------------------------------------
test('an installer not yet in R2 is passed through from GitHub while R2 gets its copy; the next request is R2\'s', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const rel = release('v3.27.0', { installer: enc('x'.repeat(5000)) })
  use(github({ latest: rel, trees: { 'v3.27.0': tree('3.27.0') } }))
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  mirror.delete('launcher/v3.27.0/ExoLauncher-Setup.exe') // the promotion's own background copy, undone: the first download does it
  const first = await ask('/v1/launcher/download/v3.27.0/ExoLauncher-Setup.exe', { MIRROR: mirror })
  assert.equal(first.response.status, 200)
  assert.equal(sha(first.bytes), sha(enc('x'.repeat(5000))))
  assert.equal(first.response.headers.get('content-length'), '5000')
  assert.equal(first.response.headers.get('etag'), null, 'no ETag: a client never resumes against a pass-through')
  assert.equal(first.response.headers.get('cache-control'), 'no-store')
  assert.equal(sha(mirror.objects.get('launcher/v3.27.0/ExoLauncher-Setup.exe')), sha(enc('x'.repeat(5000))))
  assert.ok(!mirror.objects.has('_fill/launcher/v3.27.0/ExoLauncher-Setup.exe'), 'the fill lock is released')
  const before = gh.asked.length
  const second = await ask('/v1/launcher/download/v3.27.0/ExoLauncher-Setup.exe', { MIRROR: mirror })
  assert.equal(second.response.status, 200)
  assert.ok(second.response.headers.get('etag'))
  assert.equal(gh.asked.length, before, 'served from R2 without asking GitHub')
  // The .sha256 and whats-new.json are R2's too.
  const shaFile = await ask('/v1/launcher/download/v3.27.0/ExoLauncher-Setup.exe.sha256', { MIRROR: mirror })
  assert.equal(parseSha256File(new TextDecoder().decode(shaFile.bytes), ['ExoLauncher-Setup.exe']), sha(enc('x'.repeat(5000))))
})

test('the promotion copies the installer into R2 in the background, before anyone downloads it', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(sha(mirror.objects.get('launcher/v3.27.0/ExoLauncher-Setup.exe')), sha(enc('installer v3.27.0')))
})

test('a copy whose bytes are not the .sha256\'s hash is never kept: R2 refuses it, and one R2 kept anyway is deleted', async () => {
  const good = enc('the real installer')
  const bad = enc('a tampered installer')
  const asset = { url: DL('v3.27.0', 'ExoLauncher-3.27.0-Setup.exe'), size: bad.length }
  const expected = { size: bad.length, sha256: sha(good) }
  const serve = async () => new Response(bad, { headers: { 'content-length': String(bad.length) } })
  globalThis.fetch = serve
  const strict = r2()
  assert.equal(await fill({ MIRROR: strict }, 'launcher/v3.27.0/ExoLauncher-Setup.exe', asset, expected), 'refused')
  assert.ok(!strict.objects.has('launcher/v3.27.0/ExoLauncher-Setup.exe'))
  const lax = r2({ strict: false })
  assert.equal(await fill({ MIRROR: lax }, 'launcher/v3.27.0/ExoLauncher-Setup.exe', asset, expected), 'mismatch')
  assert.ok(!lax.objects.has('launcher/v3.27.0/ExoLauncher-Setup.exe'), 'deleted, so it is never served')
  assert.ok(!lax.objects.has('_fill/launcher/v3.27.0/ExoLauncher-Setup.exe'))
  // A body of another length than GitHub recorded is not even uploaded.
  assert.equal(await fill({ MIRROR: lax }, 'k', { ...asset, size: 3 }, { size: 3, sha256: sha(good) }), 'wrong-size')
  assert.ok(!lax.writes.includes('k'))
})

test('never replace: an installer R2 holds is never copied over, and a fill in progress is not started twice', async () => {
  const mirror = r2()
  mirror.set('launcher/v3.27.0/ExoLauncher-Setup.exe', enc('already here'))
  let asked = 0
  globalThis.fetch = async () => { asked++; return new Response('other') }
  const asset = { url: 'https://github.com/x', size: 5 }
  assert.equal(await fill({ MIRROR: mirror }, 'launcher/v3.27.0/ExoLauncher-Setup.exe', asset, { size: 5, sha256: sha(enc('other')) }), 'present')
  assert.equal(mirror.text('launcher/v3.27.0/ExoLauncher-Setup.exe'), 'already here')
  mirror.set('_fill/launcher/v3.27.0/files.pack', JSON.stringify({ at: clock - 1000 }))
  assert.equal(await fill({ MIRROR: mirror }, 'launcher/v3.27.0/files.pack', asset, { size: 5, sha256: sha(enc('other')) }), 'busy')
  assert.equal(asked, 0)
  // A lock left by a run that died is taken over after ten minutes.
  clock += 11 * 60_000
  assert.equal(await fill({ MIRROR: mirror }, 'launcher/v3.27.0/files.pack', asset, { size: 5, sha256: sha(enc('other')) }), 'stored')
})

// ---- byte ranges ----------------------------------------------------------------------------------------------------
function deltaRelease(tag, { packHashIn = 'asset', packBytes = enc('0123456789abcdefghij'), listExtra = {} } = {}) {
  const header = { version: tag.slice(1), pack: packHashIn === 'header' ? { size: packBytes.length, sha256: sha(packBytes) } : undefined, files: [{ path: 'ExoLauncher.exe', size: 9, sha256: 'a'.repeat(64), offset: 0, length: 10 }], ...listExtra }
  return release(tag, {
    delta: {
      'files.json': enc(JSON.stringify(header)),
      'files.pack': packBytes,
      ...(packHashIn === 'asset' ? { 'files.pack.sha256': enc(`${sha(packBytes)}  files.pack`) } : {}),
    },
  })
}

test('the pack from R2 answers one byte range with 206 and Content-Range; a list of ranges gets the whole file; past the end is 416', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: deltaRelease('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.ok(mirror.objects.has('launcher/v3.27.0/files.pack'), 'the promotion copied the pack into R2')
  const before = gh.asked.length
  const part = await ask('/v1/launcher/download/v3.27.0/files.pack', { MIRROR: mirror }, { headers: { range: 'bytes=10-14' } })
  assert.equal(part.response.status, 206)
  assert.equal(new TextDecoder().decode(part.bytes), 'abcde')
  assert.equal(part.response.headers.get('content-range'), 'bytes 10-14/20')
  assert.equal(part.response.headers.get('content-length'), '5')
  assert.equal(gh.asked.length, before, 'ranges come from R2')
  const tail = await ask('/v1/launcher/download/v3.27.0/files.pack', { MIRROR: mirror }, { headers: { range: 'bytes=-3' } })
  assert.equal(tail.response.status, 206)
  assert.equal(tail.response.headers.get('content-range'), 'bytes 17-19/20')
  const many = await ask('/v1/launcher/download/v3.27.0/files.pack', { MIRROR: mirror }, { headers: { range: 'bytes=0-1,5-6' } })
  assert.equal(many.response.status, 200)
  assert.equal(many.bytes.length, 20)
  const past = await ask('/v1/launcher/download/v3.27.0/files.pack', { MIRROR: mirror }, { headers: { range: 'bytes=99-' } })
  assert.equal(past.response.status, 416)
  assert.equal(past.response.headers.get('content-range'), 'bytes */20')
})

test('a range of a pack R2 doesn\'t hold yet is streamed from GitHub\'s asset, and R2 gets the whole pack', async () => {
  const mirror = r2()
  seed(mirror, 'v3.27.0') // the mirror already names the tag (say, promoted by an older Worker), but holds no pack
  const rel = deltaRelease('v3.27.0')
  use(github({ latest: rel, trees: { 'v3.27.0': tree('3.27.0') } }))
  const part = await ask('/v1/launcher/download/v3.27.0/files.pack', { MIRROR: mirror }, { headers: { range: 'bytes=2-4' } })
  assert.equal(part.response.status, 206)
  assert.equal(new TextDecoder().decode(part.bytes), '234')
  assert.equal(part.response.headers.get('content-range'), 'bytes 2-4/20')
  assert.ok(gh.asked.some((a) => a.url === DL('v3.27.0', 'files.pack') && a.range === 'bytes=2-4'))
  assert.equal(mirror.text('launcher/v3.27.0/files.pack'), '0123456789abcdefghij')
  const head = await ask('/v1/launcher/download/v3.27.0/files.json', { MIRROR: mirror }, { method: 'HEAD' })
  assert.equal(head.response.status, 200)
})

// ---- the file-level update in the manifest --------------------------------------------------------------------------
test('a release with files.json and files.pack names both in the manifest, with the pack\'s SHA-256 from files.pack.sha256', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const rel = deltaRelease('v3.27.0')
  use(github({ latest: rel, trees: { 'v3.27.0': tree('3.27.0') } }))
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  const body = json()
  assert.equal(body.files.filesJson, `${ORIGIN}/v1/launcher/download/v3.27.0/files.json`)
  assert.equal(body.files.filesPack, `${ORIGIN}/v1/launcher/download/v3.27.0/files.pack`)
  assert.deepEqual(body.checks.filesPack, { name: 'files.pack', size: 20, sha256: sha(rel.files['files.pack']) })
  assert.equal(body.checks.filesJson.sha256, sha(rel.files['files.json']))
  // The old client reads at most 4096 bytes of this answer.
  assert.ok(JSON.stringify(body).length < 2048)
  const list = await ask('/v1/launcher/download/v3.27.0/files.json', { MIRROR: mirror })
  assert.equal(list.response.status, 200)
  assert.equal(list.response.headers.get('content-type'), 'application/json; charset=utf-8')
  assert.equal(sha(list.bytes), sha(rel.files['files.json']))
})

test('the pack\'s SHA-256 may come from files.json\'s header instead', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: deltaRelease('v3.27.0', { packHashIn: 'header' }), trees: { 'v3.27.0': tree('3.27.0') } }))
  const body = (await ask('/v1/launcher/latest', { MIRROR: mirror })).json()
  assert.equal(body.checks.filesPack.sha256, sha(enc('0123456789abcdefghij')))
})

test('a pack hash GitHub contradicts, or a files.json for another version, refuses the release; a release without the pair has no delta', async (t) => {
  await t.test('pack hash', async () => {
    edge()
    const mirror = r2()
    seed(mirror, 'v3.26.0')
    const rel = deltaRelease('v3.27.0')
    rel.files['files.pack.sha256'] = enc(`${'0'.repeat(64)}  files.pack`)
    rel.json.assets.find((a) => a.name === 'files.pack.sha256').digest = `sha256:${sha(rel.files['files.pack.sha256'])}`
    rel.json.assets.find((a) => a.name === 'files.pack.sha256').size = rel.files['files.pack.sha256'].length
    use(github({ latest: rel, trees: { 'v3.27.0': tree('3.27.0') } }))
    assert.equal((await ask('/v1/launcher/latest', { MIRROR: mirror })).json().tag, 'v3.26.0')
  })
  await t.test('files.json version', async () => {
    edge()
    const mirror = r2()
    seed(mirror, 'v3.26.0')
    use(github({ latest: deltaRelease('v3.27.0', { listExtra: { version: '3.26.0' } }), trees: { 'v3.27.0': tree('3.27.0') } }))
    assert.equal((await ask('/v1/launcher/latest', { MIRROR: mirror })).json().tag, 'v3.26.0')
  })
  await t.test('only files.json', async () => {
    edge()
    const mirror = r2()
    seed(mirror, 'v3.26.0')
    use(github({ latest: release('v3.27.0', { extra: { 'files.json': enc('{}') } }), trees: { 'v3.27.0': tree('3.27.0') } }))
    const body = (await ask('/v1/launcher/latest', { MIRROR: mirror })).json()
    assert.equal(body.tag, 'v3.27.0')
    assert.equal(body.files.filesJson, undefined)
    assert.equal(body.checks.filesPack, undefined)
  })
})

// ---- the release card -----------------------------------------------------------------------------------------------
test('the first sight of a new release posts its card once; later polls and the announce workflow post nothing more', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const state = github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } })
  const posted = []
  globalThis.fetch = async (url, init) => {
    if (new URL(String(url)).hostname === 'discord.com') {
      posted.push({ url: String(url), method: init?.method, body: init?.body })
      return Response.json({ id: '123456789012345678' })
    }
    return state.fetch(url, init)
  }
  gh = state
  const env = { MIRROR: mirror, DISCORD_WEBHOOK_LAUNCHER: 'https://discord.com/api/webhooks/1/abc' }
  await ask('/v1/launcher/latest', env)
  edge()
  clock += 61_000
  await ask('/v1/launcher/latest', env)
  const announce = await ask('/v1/launcher/announce', env, { method: 'POST', body: JSON.stringify({ tag: 'v3.27.0' }) })
  assert.equal(announce.json().status, 'already')
  const cards = posted.filter((p) => p.method === 'POST')
  assert.equal(cards.length, 1)
  assert.match(JSON.parse(cards[0].body).embeds[0].title, /Exo Launcher 3\.27\.0/)
})

test('the announce route promotes a release GitHub has just published, so the workflow\'s first ask finds it live', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  const response = await ask('/v1/launcher/announce', { MIRROR: mirror }, { method: 'POST', body: JSON.stringify({ tag: 'v3.27.0' }) })
  assert.equal(response.json().status, 'no-webhook')
  assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.27.0')
})

// ---- the cron -------------------------------------------------------------------------------------------------------
test('the cron promotes a new release and copies its installer and pack into R2', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: deltaRelease('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  const ctx = context()
  // The cache watch shares the tick; its own requests (vendor repos) get GitHub's 404 here and are not this test's concern.
  await worker.scheduled({}, { MIRROR: mirror }, ctx)
  await ctx.settle()
  assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.27.0')
  assert.equal(sha(mirror.objects.get('launcher/v3.27.0/ExoLauncher-Setup.exe')), sha(enc('installer v3.27.0')))
  assert.equal(mirror.text('launcher/v3.27.0/files.pack'), '0123456789abcdefghij')
})

// ---- what is kept of a release --------------------------------------------------------------------------------------
test('drafts, pre-releases and tags that are not vX.Y.Z are never a release; assets off the release\'s own URL are dropped', () => {
  const base = release('v3.27.0').json
  assert.equal(compactRelease({ ...base, draft: true }), null)
  assert.equal(compactRelease({ ...base, prerelease: true }), null)
  assert.equal(compactRelease({ ...base, tag_name: 'v3.27.0-beta' }), null)
  const moved = compactRelease({ ...base, assets: base.assets.map((a) => ({ ...a, browser_download_url: 'https://example.com/' + a.name })) })
  assert.deepEqual(moved.assets, {})
})

// ---- races and retries ----------------------------------------------------------------------------------------------
test('two promotions at once never move the pointer backwards, and the change list stays the newest one\'s', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  // Another request promotes v3.28.0 between this one's read of the pointer and its write.
  const put = mirror.put
  let raced = false
  mirror.put = async (key, value, options) => {
    if (key === 'launcher/latest.json' && !raced) {
      raced = true
      const newer = JSON.parse(mirror.text('launcher/latest.json'))
      Object.assign(newer, { tag: 'v3.28.0', version: '3.28.0' })
      await put(key, JSON.stringify(newer))
      await put('launcher/CHANGELOG.md', '## 3.28.0 — x\n', { customMetadata: { tag: 'v3.28.0' } })
    }
    return put(key, value, options)
  }
  const { json } = await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(json().tag, 'v3.28.0')
  assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.28.0')
  assert.equal(mirror.text('launcher/CHANGELOG.md'), '## 3.28.0 — x\n')
})

test('an older change list is never written over a newer one\'s', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  mirror.set('launcher/CHANGELOG.md', '## 3.28.0 — x\n')
  mirror.metas.set('launcher/CHANGELOG.md', { tag: 'v3.28.0' })
  use(github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } }))
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.27.0')
  assert.equal(mirror.text('launcher/CHANGELOG.md'), '## 3.28.0 — x\n')
})

test('many cold edges at once ask GitHub once: the others answer with the last release while one holds the lease', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  use(github({ latest: release('v3.26.0') }))
  const answers = await Promise.all(Array.from({ length: 8 }, async () => {
    const ctx = context()
    const response = await worker.fetch(new Request(ORIGIN + '/v1/launcher/latest'), { MIRROR: mirror }, ctx)
    await ctx.settle()
    return (await response.json()).tag
  }))
  assert.deepEqual([...new Set(answers)], ['v3.26.0'])
  assert.equal(apiAsks(), 1)
  assert.ok(!mirror.objects.has('_github/launcher.lease'), 'the lease is released')
  // A lease left by a request that died is taken over after 30 seconds.
  clock += (CHECK_SECONDS + 1) * 1000
  mirror.set('_github/launcher.lease', JSON.stringify({ at: clock }))
  edge()
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(apiAsks(), 1, 'a fresh lease: not asked')
  clock += 31_000
  edge()
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  assert.equal(apiAsks(), 2)
})

test('the announce route asks GitHub if it was not asked in the last 30 seconds, so the workflow need not wait five minutes', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const state = github({ latest: release('v3.26.0'), trees: { 'v3.27.0': tree('3.27.0') } })
  use(state)
  await ask('/v1/launcher/latest', { MIRROR: mirror })
  state.latest = release('v3.27.0')
  state.etag = '"rel-2"'
  clock += 40_000
  const response = await ask('/v1/launcher/announce', { MIRROR: mirror }, { method: 'POST', body: JSON.stringify({ tag: 'v3.27.0' }) })
  assert.equal(response.json().status, 'no-webhook')
  assert.equal(JSON.parse(mirror.text('launcher/latest.json')).tag, 'v3.27.0')
})

test('a card Discord refused is asked for again on the next cron tick, once; a release put in by hand gets none from the cron', async () => {
  const mirror = r2()
  seed(mirror, 'v3.26.0')
  const state = github({ latest: release('v3.27.0'), trees: { 'v3.27.0': tree('3.27.0') } })
  gh = state
  let discordUp = false
  const posted = []
  globalThis.fetch = async (url, init) => {
    if (new URL(String(url)).hostname === 'discord.com') {
      if (!discordUp) return new Response('bad gateway', { status: 502 })
      if (init?.method === 'POST') posted.push(String(url))
      return Response.json({ id: '123456789012345678' })
    }
    return state.fetch(url, init)
  }
  const env = { MIRROR: mirror, DISCORD_WEBHOOK_LAUNCHER: 'https://discord.com/api/webhooks/1/abc' }
  await ask('/v1/launcher/latest', env)
  assert.equal(posted.length, 0)
  assert.ok(mirror.objects.has('_github/announce/v3.27.0'), 'the card is still owed')
  discordUp = true
  const tick = async () => { const ctx = context(); await worker.scheduled({}, env, ctx); await ctx.settle() }
  await tick()
  assert.equal(posted.length, 1)
  assert.ok(!mirror.objects.has('_github/announce/v3.27.0'))
  await tick()
  assert.equal(posted.length, 1, 'once')
  // v3.26.0 style: in the mirror by hand, no marker, no claim: the cron leaves its card alone.
  const hand = r2()
  seed(hand, 'v3.29.0')
  state.latest = release('v3.26.0')
  edge()
  const ctx = context()
  await worker.scheduled({}, { ...env, MIRROR: hand }, ctx)
  await ctx.settle()
  assert.equal(posted.length, 1)
})

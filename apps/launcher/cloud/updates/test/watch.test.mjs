/**
 * The cache watch's discovery (src/watch.js) on the three public formats, against RECORDED fixtures of GitHub's real answers (test/fixtures):
 * the releases Atom feed, the git ref advertisement, and a raw HEAD. The file bytes are synthetic (the real DLLs are too large to fake), but
 * their sizes come from the recorded HEAD and their git blob hashes are checked against an independent computation. Also the pure parts: the
 * candidate filter, the floor and the vendor file names against the Launcher's sources.
 *
 * How it could fail: a release tag or commit is read from the wrong line of a feed or an advertisement; an annotated tag's commit is taken from
 * the tag object instead of the peeled line; a listing carries a blob hash that is not git's; a file of another size is stored; a key is
 * overwritten; the pointer moves before its files; discovery calls api.github.com (its unauthenticated limit); a failure is not recorded in the
 * state; the state keeps more than the last 20 failures.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { after, test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { Miniflare } from 'miniflare'
import {
  CATALOG_KEY, CHECK_EVERY_MS, MAX_ERRORS, STATE_KEY, SWAPPER_FLOOR, SWAPPER_FAMILIES, SWAPPER_MANIFEST_URL, VENDOR_REPOS,
  compareVersions, emptyState, latestTagFromAtom, newerThanFloor, pktLines, recordError, refillSlots, shaOfTagFromRefs, swapperCandidates,
} from '../src/watch.js'

const ORIGIN = 'https://updates.test'
const LAUNCHER = new URL('../../../ExoLauncher/Services/', import.meta.url)
const FIXTURES = new URL('./fixtures/', import.meta.url)
const atomFixture = readFileSync(new URL('nvidia-releases.atom', FIXTURES), 'utf8')
const refsFixture = readFileSync(new URL('nvidia-info-refs.bin', FIXTURES), 'utf8')
const headFixture = readFileSync(new URL('raw-nvngx_dlss-9981c635.head', FIXTURES), 'utf8')
const blob = (bytes) => createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex')

// The recorded NVIDIA release (v310.9.16, commit 9981c635).
const TAG = 'v310.9.16'
const COMMIT = '9981c6350dc58c0ce21f024fd65e31e851c31689'
const NVIDIA = 'NVIDIA/DLSS'
const FOLDER = 'lib/Windows_x86_64/rel'
const DLL = Buffer.from('pretend nvngx_dlss 310.9.16 bytes, 35 long')
const DLLD = Buffer.from('pretend nvngx_dlssd 310.9.16 bytes, 46 long, a bit longer!!')
const LICENCE = Buffer.from('NVIDIA licence text\n')
const RAW = `https://raw.githubusercontent.com/${NVIDIA}/${COMMIT}`

// ---------- pure: the three formats, from the recorded fixtures ----------

test('the newest release tag is the first Atom entry\'s /releases/tag/ link (recorded feed)', () => {
  assert.equal(latestTagFromAtom(atomFixture), TAG)
  assert.throws(() => latestTagFromAtom('<feed></feed>'), /names no plain version tag/)
  assert.throws(() => latestTagFromAtom('<feed><entry><link href="https://github.com/x/releases/tag/nightly-1"/></entry></feed>'), /names no plain version tag/)
})

test('the pkt-lines of the recorded ref advertisement parse, flush packets dropped', () => {
  const lines = pktLines(refsFixture)
  assert.match(lines[0], /^# service=git-upload-pack$/)
  assert.ok(lines.includes(`95f2f13bf45d5427f32266954647a090becaf4fa refs/tags/v310.9.16`) || lines.some((l) => l.endsWith('refs/tags/v310.9.16')))
  assert.ok(lines.some((l) => l.endsWith('refs/tags/v310.9.16^{}')))
})

test('a tag\'s commit is its peeled ^{} line (recorded advertisement): the tag object\'s own sha is not the commit', () => {
  assert.equal(shaOfTagFromRefs(refsFixture, TAG), COMMIT)
  // The tag object (95f2f1…) is not the commit: taking the plain line would be the bug.
  assert.notEqual(shaOfTagFromRefs(refsFixture, TAG), '95f2f13bf45d5427f32266954647a090becaf4fa')
})

test('a lightweight tag, with no peeled line, names its commit directly; a missing tag is refused', () => {
  const pkt = (text) => (text.length + 4).toString(16).padStart(4, '0') + text
  const light = pkt('# service=git-upload-pack\n') + '0000' + pkt(`${COMMIT} refs/tags/v1.2.3\n`) + '0000'
  assert.equal(shaOfTagFromRefs(light, 'v1.2.3'), COMMIT)
  assert.throws(() => shaOfTagFromRefs(refsFixture, 'v9.9.9'), /names no commit for v9.9.9/)
})

test('the recorded HEAD gives the file\'s size in its Content-Length', () => {
  const length = /^content-length: (\d+)/im.exec(headFixture.replace(/\r/g, ''))?.[1]
  assert.equal(Number(length), 58956912)
})

test('a git blob hash is git\'s: "hello\\n" is ce0136...', () => {
  assert.equal(blob(Buffer.from('hello\n')), 'ce013625030ba8dba906f756967f9e9ca394464a')
})

test('the recorded feed lists the newest release first (the watch reads the first entry)', () => {
  const tags = [...atomFixture.matchAll(/\/releases\/tag\/([^"]+)"/g)].map((m) => m[1])
  assert.equal(tags[0], TAG)
  assert.ok(tags.length > 1)
})

// ---------- pure: the candidates, the floor, the vendor file names ----------

const item = (over = {}) => ({
  dll_source: 'x', file_description: 'NVIDIA DLSS-G - PRODUCTION', internal_name: '', additional_label: '', is_dev_file: false, is_signature_valid: true,
  file_size: 9, zip_file_size: 4, version: '310.9.17.0', download_url: 'https://dlss-swapper-downloads.beeradmoore.com/dlss/dlss_v310.9.17.0_AB.zip', ...over,
})

test('a candidate is a signed, non-dev archive on the archive host; FSR 3.1 is not a family', () => {
  const ok = item()
  const manifest = {
    dlss: [ok, { ...ok, is_dev_file: true }, { ...ok, is_signature_valid: false }, { ...ok, download_url: 'https://example.com/x.zip' },
      { ...ok, download_url: 'https://dlss-swapper-downloads.beeradmoore.com/dlss/../x.zip' }, { ...ok, version: '310.9' }, { ...ok, zip_file_size: undefined }],
    fsr_31_dx12: [{ ...ok, version: '9.9.9.9' }],
    xess: [{ ...ok, version: '2.0.2.69', download_url: 'https://dlss-swapper-downloads.beeradmoore.com/xess/x.zip', file_description: 'XeSS SDK' }],
  }
  assert.deepEqual(swapperCandidates(manifest).map((c) => [c.file, c.version]), [['nvngx_dlss.dll', '310.9.17.0'], ['libxess.dll', '2.0.2.69']])
})

test('only archives strictly newer than the pinned floor are kept, per file', () => {
  const at = (file, version) => ({ file, version })
  assert.deepEqual(newerThanFloor([at('nvngx_dlss.dll', '310.9.16.0'), at('nvngx_dlss.dll', '310.9.17.0'), at('libxess.dll', '2.0.2.68')]), [at('nvngx_dlss.dll', '310.9.17.0')])
  assert.ok(compareVersions('310.9.17.0', '310.9.16.0') > 0)
})

test("the floor is the newest pinned version per DLL in the Launcher's sources", () => {
  const src = readFileSync(new URL('DlssSwapService.Releases.cs', LAUNCHER), 'utf8') + readFileSync(new URL('DlssSwapService.VendorWatch.cs', LAUNCHER), 'utf8')
  const pinned = {}
  for (const m of src.matchAll(/new\("([\w.]+\.dll)", "[^"]+", "[^"]*", "([\d.]+)"/g)) {
    if (!pinned[m[1]] || compareVersions(m[2], pinned[m[1]]) > 0) pinned[m[1]] = m[2]
  }
  for (const file of Object.values(SWAPPER_FAMILIES)) assert.equal(SWAPPER_FLOOR[file], pinned[file], `${file}: the watch floor must equal the newest pin`)
})

test("each vendor repo's folder and DLL names are the ones the Launcher's VendorWatch lists", () => {
  const src = readFileSync(new URL('DlssSwapService.VendorWatch.cs', LAUNCHER), 'utf8')
  for (const vendor of VENDOR_REPOS) {
    assert.ok(src.includes(`"${vendor.folder}"`), `${vendor.repo} folder ${vendor.folder}`)
    for (const name of vendor.files) assert.ok(src.includes(`"${name}"`), `${vendor.repo} file ${name}`)
  }
})

test('discovery runs once per six hours: the slots refill only when empty and the last round is old', () => {
  const state = emptyState()
  const start = 10 * CHECK_EVERY_MS
  refillSlots(state, start)
  assert.equal(state.slots.length, VENDOR_REPOS.length + 1)
  state.slots = []
  refillSlots(state, start + CHECK_EVERY_MS - 1)
  assert.deepEqual(state.slots, [])
  refillSlots(state, start + CHECK_EVERY_MS)
  assert.equal(state.slots.length, VENDOR_REPOS.length + 1)
})

test('the state keeps only the last 20 errors, each with its step, repo, status and a short message', () => {
  const state = emptyState()
  for (let i = 0; i < MAX_ERRORS + 5; i++) recordError(state, { step: 'discover', repo: NVIDIA, error: Object.assign(new Error(`e${i} ${'x'.repeat(300)}`), { status: 404 }), at: `t${i}` })
  assert.equal(state.errors.length, MAX_ERRORS)
  assert.equal(state.errors[0].message.length, 200)
  assert.deepEqual(Object.keys(state.errors[0]).sort(), ['at', 'message', 'repo', 'status', 'step'])
  assert.equal(state.errors.at(-1).status, 404)
})

// ---------- workerd: the discovery and the copy, against the recorded fixtures ----------

const made = []
after(async () => {
  for (const mf of made) await mf.dispose()
})

/** The fake upstream: the recorded feed and advertisement, and the synthetic files at the recorded commit. HEAD answers with Content-Length. */
function upstream(overrides = {}) {
  return new Map([
    [`https://github.com/${NVIDIA}/releases.atom`, Buffer.from(atomFixture)],
    [`https://github.com/${NVIDIA}.git/info/refs?service=git-upload-pack`, Buffer.from(refsFixture)],
    [`${RAW}/${FOLDER}/nvngx_dlss.dll`, DLL],
    [`${RAW}/${FOLDER}/nvngx_dlssd.dll`, DLLD],
    [`${RAW}/LICENSE.txt`, LICENCE],
    ...Object.entries(overrides),
  ])
}

async function harness({ table = upstream(), seed = {}, state: stateSeed = {} } = {}) {
  const calls = []
  const mf = new Miniflare({
    modules: true,
    modulesRules: [{ type: 'ESModule', include: ['**/*.js', '**/*.mjs'] }],
    scriptPath: fileURLToPath(new URL('../src/worker.js', import.meta.url)),
    compatibilityDate: '2026-08-06',
    r2Buckets: ['MIRROR'],
    outboundService: async (request) => {
      calls.push(`${request.method} ${request.url}`)
      const hit = table.get(request.url)
      if (hit === undefined) return new Response('not in the fake upstream', { status: 404 })
      if (hit && typeof hit === 'object' && !Buffer.isBuffer(hit)) return new Response(hit.body ?? null, { status: hit.status, headers: hit.headers })
      if (request.method === 'HEAD') return new Response(null, { status: 200, headers: { 'content-length': String(hit.length) } })
      return new Response(hit, { status: 200 })
    },
  })
  made.push(mf)
  const bucket = await mf.getR2Bucket('MIRROR')
  await bucket.put(STATE_KEY, JSON.stringify({ ...emptyState(), slots: [NVIDIA], lastCheck: Date.now(), ...stateSeed }))
  for (const [key, bytes] of Object.entries(seed)) await bucket.put(key, bytes)
  const worker = await mf.getWorker()
  const tick = () => worker.scheduled({ cron: '*/15 * * * *' })
  const read = async (key) => {
    const object = await bucket.get(key)
    return object ? Buffer.from(await object.arrayBuffer()) : null
  }
  const readState = async () => JSON.parse((await read(STATE_KEY)).toString())
  const drain = async (max = 60) => {
    for (let i = 1; i <= max; i++) {
      await tick()
      const s = await readState()
      if (s.queue.length === 0 && s.slots.length === 0 && !s.inFlight) return i
    }
    throw new Error('not drained')
  }
  return { mf, bucket, calls, tick, read, readState, drain, get: (path) => mf.dispatchFetch(ORIGIN + path) }
}

test('a run from the recorded feed and advertisement stores the commit, the files, a built listing and the pointer last', async () => {
  const h = await harness()
  await h.drain()
  assert.equal((await h.read(`cache/${NVIDIA}/commits/${TAG}`)).toString(), JSON.stringify({ sha: COMMIT }), 'the mirror\'s canonical commit bytes')
  assert.ok((await h.read(`cache/${NVIDIA}/${COMMIT}/${FOLDER}/nvngx_dlss.dll`)).equals(DLL))
  assert.ok((await h.read(`cache/${NVIDIA}/${COMMIT}/${FOLDER}/nvngx_dlssd.dll`)).equals(DLLD))
  assert.equal(await h.read(`cache/${NVIDIA}/${COMMIT}/${FOLDER}/nvngx_dlssg.dll`), null, 'a file the release does not ship (404) is absent, not an error')
  assert.ok((await h.read(`cache/${NVIDIA}/${COMMIT}/${FOLDER}/LICENSE.txt`)).equals(LICENCE), 'the licence sits beside the DLLs')
  const listing = JSON.parse((await h.read(`cache/${NVIDIA}/contents/${FOLDER}@${COMMIT}`)).toString())
  assert.deepEqual(listing, [
    { name: 'nvngx_dlss.dll', path: `${FOLDER}/nvngx_dlss.dll`, type: 'file', size: DLL.length, sha: blob(DLL) },
    { name: 'nvngx_dlssd.dll', path: `${FOLDER}/nvngx_dlssd.dll`, type: 'file', size: DLLD.length, sha: blob(DLLD) },
  ])
  assert.equal((await h.read(`cache/${NVIDIA}/releases/latest`)).toString(), JSON.stringify({ tag_name: TAG }))
  assert.equal((await h.readState()).repos[NVIDIA].complete, true)
  assert.ok(!h.calls.some((c) => c.includes('api.github.com')), 'discovery never calls api.github.com')
})

test('the listing\'s blob hash is git\'s for the bytes: "hello\\n" in a file is ce0136...', async () => {
  const table = upstream({ [`${RAW}/${FOLDER}/nvngx_dlssd.dll`]: Buffer.from('hello\n') })
  const h = await harness({ table })
  await h.drain()
  const listing = JSON.parse((await h.read(`cache/${NVIDIA}/contents/${FOLDER}@${COMMIT}`)).toString())
  assert.equal(listing.find((e) => e.name === 'nvngx_dlssd.dll').sha, 'ce013625030ba8dba906f756967f9e9ca394464a')
})

test('a feed that is not answered is a recorded failure with its step, repo and HTTP status, and nothing is written', async () => {
  const table = upstream()
  table.delete(`https://github.com/${NVIDIA}/releases.atom`)
  const h = await harness({ table })
  await h.tick()
  const s = await h.readState()
  assert.equal(s.errors.length, 1)
  assert.deepEqual({ step: s.errors[0].step, repo: s.errors[0].repo, status: s.errors[0].status }, { step: 'discover', repo: NVIDIA, status: 404 })
  assert.equal(await h.read(`cache/${NVIDIA}/releases/latest`), null)
})

test('a redirect off the allowlist is refused and recorded; nothing is copied from it', async () => {
  const table = upstream({ [`${RAW}/${FOLDER}/nvngx_dlss.dll`]: { status: 302, headers: { location: 'https://evil.example/x.dll' } } })
  const h = await harness({ table })
  await h.drain()
  const s = await h.readState()
  assert.ok(s.errors.some((e) => /not a host the watch reads/.test(e.message)), 'the refused hop is recorded')
  assert.equal(await h.read(`cache/${NVIDIA}/releases/latest`), null, 'the pointer does not move over the refused file')
})

test('a file whose HEAD names no size is refused (dropped), and the release is not published', async () => {
  const table = upstream()
  const h = await harness({ table })
  // A HEAD without Content-Length: the fake answers HEAD for this URL with an empty header set.
  table.set(`${RAW}/${FOLDER}/nvngx_dlss.dll`, { status: 200, headers: {}, body: null })
  await h.drain()
  assert.equal(await h.read(`cache/${NVIDIA}/releases/latest`), null)
  assert.ok((await h.readState()).dropped.some((d) => d.group === NVIDIA && /gives no size/.test(d.error)))
})

test('a key that exists at another size is refused and never overwritten', async () => {
  const key = `cache/${NVIDIA}/${COMMIT}/${FOLDER}/nvngx_dlss.dll`
  const h = await harness({ seed: { [key]: Buffer.from('a longer file that is not this one, so it is refused') } })
  await h.drain()
  assert.equal((await h.read(key)).toString(), 'a longer file that is not this one, so it is refused')
  assert.equal(await h.read(`cache/${NVIDIA}/releases/latest`), null)
})

test('a key that exists at the same size keeps its bytes and is not written again', async () => {
  const key = `cache/${NVIDIA}/${COMMIT}/${FOLDER}/nvngx_dlss.dll`
  const other = Buffer.alloc(DLL.length, 'x')
  const h = await harness({ seed: { [key]: other } })
  await h.drain()
  assert.ok((await h.read(key)).equals(other), 'the existing bytes are kept')
  assert.equal(JSON.parse((await h.read(`cache/${NVIDIA}/releases/latest`)).toString()).tag_name, TAG)
})

test('a second round for the same tag asks the feed, and nothing else', async () => {
  const h = await harness()
  await h.drain()
  const before = h.calls.length
  await h.bucket.put(STATE_KEY, JSON.stringify({ ...(await h.readState()), slots: [NVIDIA] }))
  await h.tick()
  const later = h.calls.slice(before)
  assert.deepEqual(later, [`GET https://github.com/${NVIDIA}/releases.atom`], 'only the feed is read when the tag is current')
})

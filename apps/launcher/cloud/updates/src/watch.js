// The cache watch (README.md, "The cache watch"). It runs on the free plan and never calls api.github.com (its unauthenticated limit is
// shared by every Worker on Cloudflare's egress). Discovery reads three public formats instead:
//   the newest release tag      https://github.com/<repo>/releases.atom
//   the tag's commit            https://github.com/<repo>.git/info/refs?service=git-upload-pack (the peeled ^{} entry)
//   each file                   https://raw.githubusercontent.com/<repo>/<sha>/<path> (size from HEAD; git blob SHA-1 while it streams)
// A cron run does at most one discovery step and copies at most one queued item. Nothing is hashed in JavaScript: bytes stream through a
// native DigestStream, which computes the SHA-256 and, for vendor files, the git blob SHA-1 the Launcher checks.
//   cache/<repo>/commits/<tag>              {"sha": ...} (the mirror's canonical bytes)
//   cache/<repo>/<sha>/<folder>/<file>      each SDK DLL the Launcher uses, and the licence beside it
//   cache/<repo>/contents/<folder>@<sha>    a listing the watch builds: name, path, type, size and git blob sha of each file it stored
//   cache/<repo>/releases/latest            {"tag_name": ...} (a pointer: it moves last)
//   cache/dlss-swapper-downloads.beeradmoore.com/<path>   DLSS Swapper archives newer than the pins
//   cache/exo/dlss-swapper/catalog.json     those archives, each with its zip's SHA-256 (a pointer)
// Every upstream request names a host on ALLOWED_HOSTS and follows redirects by hand. The state (slots, queue, what is stored, rejected
// archives, and the last errors) is _watch/state.json, outside the served prefix.

export const CACHE_ORIGIN = 'https://updates.getexo.app'
export const VENDOR_REPOS = [
  { repo: 'NVIDIA/DLSS', folder: 'lib/Windows_x86_64/rel', files: ['nvngx_dlss.dll', 'nvngx_dlssd.dll', 'nvngx_dlssg.dll'] },
  {
    repo: 'GPUOpen-LibrariesAndSDKs/FidelityFX-SDK',
    folder: 'Kits/FidelityFX/signedbin',
    files: ['amd_fidelityfx_loader_dx12.dll', 'amd_fidelityfx_upscaler_dx12.dll', 'amd_fidelityfx_framegeneration_dx12.dll',
      'amd_fidelityfx_denoiser_dx12.dll', 'amd_fidelityfx_radiancecache_dx12.dll'],
  },
  { repo: 'intel/xess', folder: 'bin', files: ['libxess.dll', 'libxess_dx11.dll', 'libxess_fg.dll', 'libxell.dll'] },
]
/**
 * DLSS Swapper's manifest families and the DLL each holds. FSR 3.1 (fsr_31_*) is not here: its loader versions do not order like
 * its releases, so Exo takes FSR 3.1 from its pins only (DlssSwapService.Catalog.cs, ReleaseOrder).
 */
export const SWAPPER_FAMILIES = {
  dlss: 'nvngx_dlss.dll', dlss_d: 'nvngx_dlssd.dll', dlss_g: 'nvngx_dlssg.dll',
  xess: 'libxess.dll', xess_dx11: 'libxess_dx11.dll', xess_fg: 'libxess_fg.dll', xell: 'libxell.dll',
}
export const SWAPPER_MANIFEST_URL = 'https://beeradmoore.github.io/dlss-swapper/manifest.json'
export const SWAPPER_HOST = 'dlss-swapper-downloads.beeradmoore.com'
/**
 * The newest version the Launcher pins per DLL (DlssSwapService.Releases.cs and the vendor pins). The watch stores only archives newer
 * than these. The Worker cannot read the Launcher, so test/watch.test.mjs fails when this differs from the pins.
 */
export const SWAPPER_FLOOR = {
  'nvngx_dlss.dll': '310.9.16.0', 'nvngx_dlssd.dll': '310.9.16.0', 'nvngx_dlssg.dll': '310.9.16.0',
  'libxess.dll': '2.0.2.68', 'libxess_dx11.dll': '2.0.2.68', 'libxess_fg.dll': '1.3.1.78', 'libxell.dll': '1.3.2.10',
}
/** Builds that are not NVIDIA's own DLSS-G (the SM86 community build the Launcher no longer pins), by any text they carry. */
export const DENIED_MARKERS = /sm86|sdli1995|dlssg_for|not for production/i
export const CATALOG_KEY = 'cache/exo/dlss-swapper/catalog.json'
export const STATE_KEY = '_watch/state.json'
/** Discovery runs at most once per this, when every slot has been done. */
export const CHECK_EVERY_MS = 6 * 60 * 60 * 1000
/** An item that fails this many times (counting runs killed while it was in flight) is dropped. */
export const MAX_ATTEMPTS = 5
/** The largest file or archive the watch stores. */
export const MAX_BYTES = 256 * 1024 * 1024
/** The largest DLSS Swapper manifest read. It is about 0.5 MB today. */
export const MAX_MANIFEST_BYTES = 4 * 1024 * 1024
/** The most archives kept per DLL file, and the most queued items at once. */
export const MAX_ARCHIVES_PER_FILE = 50
export const MAX_QUEUE = 500
/** The most recent failures kept in the state, for reading after a run. */
export const MAX_ERRORS = 20
/** A version whose major part is above this is not a DLSS, FidelityFX or XeSS version Exo knows: it is refused. */
export const MAX_MAJOR = 1000
/** Hosts an upstream request may name (the first host, and each redirect's). Anything else is refused. */
export const ALLOWED_HOSTS = new Set(['github.com', 'raw.githubusercontent.com', 'beeradmoore.github.io', SWAPPER_HOST])
/** A licence is looked for at a vendor repo's root, in this order (the order cache-mirror.mjs uses). */
export const LICENCE_NAMES = ['LICENSE.txt', 'LICENSE', 'LICENSE.md', 'LICENCE.txt', '3rdpartynotice.md']
const MAX_HOPS = 4
const TAG = /^v\d{1,4}\.\d{1,4}\.\d{1,4}$/
const COMMIT = /^[0-9a-f]{40}$/
const VERSION4 = /^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$/
const UA = 'exo-cache-watch'
const encoder = new TextEncoder()
const decoder = new TextDecoder()

/** A refusal: upstream data that is not what it claims, or a key that holds other bytes. What it refers to is dropped, not kept. */
export class Refused extends Error {}

/** An answer that is not 2xx, with its status, so the state can say which step failed and how. */
export class HttpStatusError extends Error {
  constructor(message, status) {
    super(message)
    this.status = status
  }
}

/** The newest release tag from the repo's Atom feed (its entries are newest first): the first entry's /releases/tag/ link. */
export function latestTagFromAtom(text) {
  const entry = /<entry>([\s\S]*?)<\/entry>/.exec(text)?.[1]
  const tag = entry ? /\/releases\/tag\/([^"<\s]+)"/.exec(entry)?.[1] : undefined
  if (!tag || !TAG.test(tag)) throw new Refused('the release feed names no plain version tag')
  return tag
}

/** The data of each pkt-line in a git ref advertisement, in order (flush packets dropped, the trailing newline removed). */
export function pktLines(text) {
  const out = []
  let at = 0
  while (at < text.length) {
    const length = Number.parseInt(text.slice(at, at + 4), 16)
    if (!Number.isInteger(length)) throw new Refused('the ref advertisement is not pkt-line data')
    if (length === 0) {
      at += 4
      continue
    }
    if (length < 4) throw new Refused('the ref advertisement has a short pkt-line')
    out.push(text.slice(at + 4, at + length).replace(/\n$/, ''))
    at += length
  }
  return out
}

/**
 * The commit a tag names, from the git smart-HTTP ref advertisement. An annotated tag's peeled line (refs/tags/<tag>^{}) names the commit;
 * a lightweight tag names it directly.
 */
export function shaOfTagFromRefs(text, tag) {
  const refs = new Map()
  for (const line of pktLines(text)) {
    const match = /^([0-9a-f]{40}) (refs\/[^\0]+)/.exec(line)
    if (match) refs.set(match[2], match[1])
  }
  const sha = refs.get(`refs/tags/${tag}^{}`) ?? refs.get(`refs/tags/${tag}`)
  if (!sha || !COMMIT.test(sha)) throw new Refused(`the ref advertisement names no commit for ${tag}`)
  return sha
}

/** DLSS Swapper's archives for the watched DLLs: signed, not development builds, on the archive host, with sane paths, sizes and versions. */
export function swapperCandidates(manifest) {
  const prefix = `https://${SWAPPER_HOST}/`
  const out = []
  for (const [family, file] of Object.entries(SWAPPER_FAMILIES)) {
    const items = Array.isArray(manifest?.[family]) ? manifest[family] : []
    for (const item of items) {
      if (item?.is_dev_file !== false || item?.is_signature_valid !== true) continue
      if (typeof item.download_url !== 'string' || !item.download_url.startsWith(prefix)) continue
      const path = item.download_url.slice(prefix.length)
      if (!/^[A-Za-z0-9_./-]+$/.test(path) || path.includes('..')) continue
      if (!VERSION4.test(item.version ?? '') || Number(item.version.split('.')[0]) > MAX_MAJOR) continue
      if (!Number.isInteger(item.zip_file_size) || !Number.isInteger(item.file_size)) continue
      if (item.zip_file_size <= 0 || item.file_size <= 0 || item.zip_file_size > MAX_BYTES || item.file_size > MAX_BYTES) continue
      const text = [item.file_description, item.dll_source, item.internal_name, item.additional_label, item.download_url].join(' ')
      if (DENIED_MARKERS.test(text)) continue
      if (family === 'dlss_g' && !/^NVIDIA DLSS-G/.test(item.file_description ?? '')) continue
      out.push({ file, version: item.version, path, zipUrl: item.download_url, zipSize: item.zip_file_size, dllSize: item.file_size })
    }
  }
  return out
}

/** Four-part version compare: negative, zero or positive. */
export function compareVersions(a, b) {
  const left = a.split('.').map(Number)
  const right = b.split('.').map(Number)
  for (let i = 0; i < 4; i++) if (left[i] !== right[i]) return left[i] - right[i]
  return 0
}

/** The candidates strictly newer than the pinned floor for their file. */
export function newerThanFloor(candidates, floor = SWAPPER_FLOOR) {
  return candidates.filter((c) => floor[c.file] !== undefined && compareVersions(c.version, floor[c.file]) > 0)
}

export function emptyState() {
  return { schema: 2, slots: [], lastCheck: 0, repos: {}, swapper: { archives: {}, rejected: {} }, queue: [], dropped: [], errors: [], inFlight: null }
}

/** Refills the discovery slots once every slot is done and the last round is old enough. */
export function refillSlots(state, nowMs) {
  if (state.slots.length === 0 && nowMs - state.lastCheck >= CHECK_EVERY_MS) {
    state.slots = [...VENDOR_REPOS.map((vendor) => vendor.repo), 'dlss-swapper']
    state.lastCheck = nowMs
  }
}

/**
 * The hosts a request may name, and each redirect's host, checked before the request is sent. A redirect is followed by hand so its host is
 * checked too; anything off the allowlist (or not https) is refused.
 */
export async function upstream(fetchImpl, url, init = {}) {
  let current = url
  for (let hop = 0; hop <= MAX_HOPS; hop++) {
    const parsed = new URL(current)
    if (parsed.protocol !== 'https:' || !ALLOWED_HOSTS.has(parsed.host)) throw new Refused(`${current}: not a host the watch reads`)
    const res = await fetchImpl(current, { ...init, redirect: 'manual' })
    if (res.status < 300 || res.status > 399) return res
    const location = res.headers.get('location')
    if (!location) throw new HttpStatusError(`${current} redirected without a location`, res.status)
    current = new URL(location, current).href
  }
  throw new Refused(`${url}: more than ${MAX_HOPS} redirects`)
}

async function getText(fetchImpl, url) {
  const res = await upstream(fetchImpl, url, { headers: { 'user-agent': UA } })
  if (!res.ok) throw new HttpStatusError(`${url} answered ${res.status}`, res.status)
  return res.text()
}

/**
 * One cron invocation: the queue's head and a discovery slot are taken out of the state and that state is written BEFORE any work, so a run
 * killed mid-work leaves its attempt counted; the next run puts the item back, or drops it after MAX_ATTEMPTS. At most one discovery step and
 * one item per run. Returns what it did.
 */
export async function runWatch({ store, fetch: fetchImpl = fetch, now = () => new Date(), log = () => {} }) {
  const state = await readState(store)
  const ctx = { store, state, fetchImpl, now, log }
  const outcomes = []
  if (state.inFlight) {
    const killed = state.inFlight
    state.inFlight = null
    if (killed.attempts >= MAX_ATTEMPTS) outcomes.push(failItem(ctx, killed, 'stopped in flight too many times', log, false))
    else state.queue.unshift(killed)
  }
  refillSlots(state, now().getTime())
  const slot = state.slots.shift()
  const next = state.queue.shift()
  if (next) state.inFlight = { ...next, attempts: next.attempts + 1 }
  await save(ctx)
  if (slot) outcomes.push(await settle('discover', slot, log, () => discover(slot, ctx), undefined, ctx))
  if (next) outcomes.push(await settle('copy', next.group, log, () => runItem(state.inFlight, ctx), state.inFlight, ctx))
  await save(ctx)
  return outcomes
}

async function save({ store, state }) {
  await store.replace(STATE_KEY, encoder.encode(JSON.stringify(state)), 'application/json')
}

/** Records a failure in the state's last errors: which step, which repo, the HTTP status when there was one, and a short message. */
export function recordError(state, { step, repo, error, at }) {
  const entry = { at, step, repo: repo ?? null, status: error?.status ?? null, message: String(error?.message ?? error).slice(0, 200) }
  state.errors = [...(state.errors ?? []), entry].slice(-MAX_ERRORS)
}

/** Runs one step. A refused or repeatedly failing item is dropped (per archive for the Swapper), a transient one goes back to the front. */
async function settle(step, name, log, work, item, ctx) {
  try {
    const result = await work()
    if (ctx && item !== undefined) ctx.state.inFlight = null
    log(`${step} ${name}: ${result.status}`)
    return { step, name, ...result }
  } catch (err) {
    if (!ctx) {
      log(`${step} ${name}: failed: ${err.message}`)
      return { step, name, status: 'failed', error: err.message }
    }
    recordError(ctx.state, { step, repo: name, error: err, at: ctx.now().toISOString() })
    if (item === undefined) {
      // A failed discovery is not retried in this round: the slot is spent, and the next round asks again.
      log(`${step} ${name}: failed: ${err.message}`)
      return { step, name, status: 'failed', error: err.message }
    }
    const current = ctx.state.inFlight
    ctx.state.inFlight = null
    const refusal = err instanceof Refused
    const exhausted = current.attempts >= MAX_ATTEMPTS
    const outcome = refusal || exhausted ? failItem(ctx, current, err.message, log, refusal) : { status: 'retry' }
    if (!(refusal || exhausted)) ctx.state.queue.unshift(current)
    log(`${step} ${name}: ${outcome.status}: ${err.message}`)
    return { step, name, status: outcome.status, error: err.message }
  }
}

/**
 * A failed item. A Swapper archive is rejected on its own only when it was refused (remembered in state.swapper.rejected); a transient
 * failure that ran out of attempts is deferred, and the next discovery round finds it again. A vendor repo's item drops the repo: its files,
 * its listing and its pointer, so no pointer moves over them.
 */
function failItem(ctx, item, error, log, refusal) {
  const at = ctx.now().toISOString()
  if (item.group === 'swapper' && item.kind === 'file') {
    if (!refusal) return { status: 'deferred' }
    ctx.state.swapper.rejected[item.key] = { file: item.archive?.file ?? null, version: item.archive?.version ?? null, error, at }
    return { status: 'rejected' }
  }
  ctx.state.queue = ctx.state.queue.filter((queued) => queued.group !== item.group)
  if (ctx.state.repos[item.group]) ctx.state.repos[item.group] = { ...ctx.state.repos[item.group], complete: false, failed: error }
  ctx.state.dropped = [...ctx.state.dropped, { group: item.group, key: item.key ?? null, error, at }].slice(-10)
  log(`${item.group}: group dropped: ${error}`)
  return { status: 'dropped' }
}

async function discover(slot, ctx) {
  if (slot === 'dlss-swapper') return discoverSwapper(ctx)
  const vendor = VENDOR_REPOS.find((v) => v.repo === slot)
  if (!vendor) throw new Error(`unknown discovery slot ${slot}`)
  return discoverVendor(vendor, ctx)
}

/** A vendor repo's newest release, found from its public Atom feed and ref advertisement. Its items are queued in order; the pointer is last. */
async function discoverVendor({ repo, folder, files }, { state, fetchImpl }) {
  const tag = latestTagFromAtom(await getText(fetchImpl, `https://github.com/${repo}/releases.atom`))
  if (state.repos[repo]?.tag === tag && state.repos[repo].complete) return { status: 'current', tag }
  // A newer tag supersedes whatever this repo still had queued.
  state.queue = state.queue.filter((queued) => queued.group !== repo)
  const sha = shaOfTagFromRefs(await getText(fetchImpl, `https://github.com/${repo}.git/info/refs?service=git-upload-pack`), tag)
  const queue = [{ group: repo, kind: 'put', key: `cache/${repo}/commits/${tag}`, text: JSON.stringify({ sha }), type: 'application/json', attempts: 0 }]
  for (const name of files) {
    queue.push({
      group: repo, kind: 'raw', key: `cache/${repo}/${sha}/${folder}/${name}`, url: `https://raw.githubusercontent.com/${repo}/${sha}/${folder}/${name}`,
      name, repo, folder, optional: true, type: 'application/octet-stream', attempts: 0,
    })
  }
  queue.push({ group: repo, kind: 'licence', repo, sha, folder, attempts: 0 })
  queue.push({ group: repo, kind: 'listing', key: `cache/${repo}/contents/${folder}@${sha}`, repo, folder, names: files, attempts: 0 })
  queue.push({ group: repo, kind: 'put', pointer: true, key: `cache/${repo}/releases/latest`, text: JSON.stringify({ tag_name: tag }),
    type: 'application/json', complete: { repo, tag, sha }, attempts: 0 })
  enqueue(state, queue)
  state.repos[repo] = { tag, sha, complete: false, files: {}, checked: null }
  return { status: 'queued', tag, sha, items: queue.length }
}

/** Appends items, refusing to grow the queue past MAX_QUEUE (the slot is then lost; the next round asks again). */
function enqueue(state, items) {
  if (state.queue.length + items.length > MAX_QUEUE) throw new Refused(`the queue would pass ${MAX_QUEUE} items`)
  state.queue.push(...items)
}

/** DLSS Swapper's newer archives: each zip queued once, within the per-file cap; the catalog is queued again after them. */
async function discoverSwapper(ctx) {
  const { state } = ctx
  const res = await upstream(ctx.fetchImpl, SWAPPER_MANIFEST_URL, { headers: { 'user-agent': UA } })
  if (!res.ok) throw new HttpStatusError(`the DLSS Swapper manifest answered ${res.status}`, res.status)
  const length = res.headers.get('content-length')
  if (length !== null && Number(length) > MAX_MANIFEST_BYTES) throw new Refused(`the DLSS Swapper manifest is ${length} bytes, over ${MAX_MANIFEST_BYTES}`)
  const text = await res.text()
  if (text.length > MAX_MANIFEST_BYTES) throw new Refused(`the DLSS Swapper manifest is over ${MAX_MANIFEST_BYTES} bytes`)
  const candidates = swapperCandidates(JSON.parse(text))
  const newer = newerThanFloor(candidates)
  const queued = new Set(state.queue.map((item) => item.key))
  const perFile = (file) => Object.values(state.swapper.archives).filter((archive) => archive.file === file).length +
    state.queue.filter((item) => item.archive?.file === file).length
  const add = []
  let skippedCap = 0
  let evicted = 0
  for (const c of newer) {
    const key = `cache/${SWAPPER_HOST}/${c.path}`
    if (state.swapper.archives[key] || state.swapper.rejected[key] || queued.has(key)) continue
    if (perFile(c.file) >= MAX_ARCHIVES_PER_FILE) {
      // At the cap, a newer archive takes the place of the oldest stored one for this file. The oldest's R2 object stays; the catalog stops listing it.
      const [oldestKey, oldest] = Object.entries(state.swapper.archives).filter(([, a]) => a.file === c.file)
        .sort(([, a], [, b]) => compareVersions(a.version, b.version))[0] ?? []
      if (!oldest || compareVersions(c.version, oldest.version) <= 0) { skippedCap++; continue }
      delete state.swapper.archives[oldestKey]
      evicted++
    }
    add.push({
      group: 'swapper', kind: 'file', key, url: c.zipUrl, size: c.zipSize, type: 'application/zip', attempts: 0,
      archive: { file: c.file, version: c.version, dllSize: c.dllSize, zipSize: c.zipSize, url: `${CACHE_ORIGIN}/v1/cache/${SWAPPER_HOST}/${c.path}` },
    })
    queued.add(key)
  }
  if (add.length) {
    state.queue = state.queue.filter((item) => item.kind !== 'catalog')
    enqueue(state, [...add, { group: 'swapper', kind: 'catalog', attempts: 0 }])
  }
  return { status: 'checked', candidates: candidates.length, newer: newer.length, queued: add.length, skippedCap, evicted }
}

/** One queued item. Each kind copies or writes exactly one object. */
async function runItem(item, ctx) {
  const { store, state, fetchImpl } = ctx
  if (item.kind === 'put') {
    if (item.pointer) await store.replace(item.key, encoder.encode(item.text), item.type)
    else await createOnce(store, item.key, encoder.encode(item.text), item.type)
  } else if (item.kind === 'raw') {
    const outcome = await storeRaw(item, ctx)
    if (outcome.status === 'absent') return { status: 'absent', key: item.name }
  } else if (item.kind === 'licence') {
    return storeLicence(item, ctx)
  } else if (item.kind === 'listing') {
    return writeListing(item, ctx)
  } else if (item.kind === 'file') {
    const { sha256 } = await storeArchive(item, { store, fetchImpl })
    if (item.archive) state.swapper.archives[item.key] = { ...item.archive, zipSha256: sha256 }
  } else if (item.kind === 'catalog') {
    await writeCatalog(store, state)
  } else {
    throw new Refused(`unknown queue item ${item.kind}`)
  }
  if (item.complete) state.repos[item.complete.repo] = { ...state.repos[item.complete.repo], ...item.complete, complete: true, checked: ctx.now().toISOString() }
  return { status: 'stored', key: item.key ?? item.kind }
}

/** Writes small bytes once: a key that exists must hold exactly these bytes, or the write is refused. */
async function createOnce(store, key, bytes, type) {
  if (await store.create(key, bytes, type)) return
  if (!(await store.sameBytes(key, bytes))) throw new Refused(`${key} already holds other bytes; it is never overwritten`)
}

/**
 * One vendor file: its size from a HEAD (Content-Length), then the bytes streamed once, computing the SHA-256 and the git blob SHA-1. The file
 * is written only if its key is absent; if it is present its size must be the same. A file the release does not ship (404, optional) is absent.
 */
async function storeRaw(item, { store, state, fetchImpl }) {
  const head = await upstream(fetchImpl, item.url, { method: 'HEAD', headers: { 'user-agent': UA } })
  if (head.status === 404 && item.optional) return { status: 'absent' }
  if (!head.ok) throw new HttpStatusError(`${item.url} answered ${head.status}`, head.status)
  const length = head.headers.get('content-length')
  if (length === null || !/^\d+$/.test(length)) throw new Refused(`${item.url} gives no size`)
  const size = Number(length)
  if (size <= 0 || size > MAX_BYTES) throw new Refused(`${item.url}: ${size} bytes is outside what the watch stores`)
  const existing = await store.head(item.key)
  if (existing && existing.size !== size) throw new Refused(`${item.key} is ${existing.size} bytes, upstream gives ${size}; never overwritten`)
  const res = await upstream(fetchImpl, item.url, { headers: { 'user-agent': UA } })
  if (!res.ok) throw new HttpStatusError(`${item.url} answered ${res.status}`, res.status)
  if (!res.body) throw new Error(`${item.url} has no body`)
  const { created, blobSha1 } = await store.stream(item.key, res.body, size, { write: !existing, type: item.type, blob: true })
  if (!existing && !created) throw new Refused(`${item.key} appeared while it was copied; never overwritten`)
  state.repos[item.repo].files[item.name] = { size, sha: blobSha1 }
  return { status: 'stored' }
}

/** A repo's licence, from its root: the first of LICENCE_NAMES that exists, stored beside the DLLs. Absent when none exists. */
async function storeLicence(item, { store, state, fetchImpl }) {
  for (const name of LICENCE_NAMES) {
    const url = `https://raw.githubusercontent.com/${item.repo}/${item.sha}/${name}`
    const head = await upstream(fetchImpl, url, { method: 'HEAD', headers: { 'user-agent': UA } })
    if (head.status === 404) continue
    if (!head.ok) throw new HttpStatusError(`${url} answered ${head.status}`, head.status)
    const size = Number(head.headers.get('content-length'))
    if (!Number.isInteger(size) || size <= 0 || size > MAX_BYTES) throw new Refused(`${url} gives no usable size`)
    const key = `cache/${item.repo}/${item.sha}/${item.folder}/${name}`
    const existing = await store.head(key)
    if (existing && existing.size !== size) throw new Refused(`${key} is ${existing.size} bytes, upstream gives ${size}; never overwritten`)
    const res = await upstream(fetchImpl, url, { headers: { 'user-agent': UA } })
    if (!res.ok) throw new HttpStatusError(`${url} answered ${res.status}`, res.status)
    await store.stream(key, res.body, size, { write: !existing, type: 'text/plain', blob: false })
    return { status: 'stored', key }
  }
  return { status: 'absent' }
}

/**
 * The listing the Launcher reads (the GitHub contents shape, from what the watch stored): each file's name, path, size and git blob SHA-1.
 * A listing already at the key must agree with those files on size and blob SHA-1 (the mirror wrote it from the GitHub API), or the write is refused.
 */
async function writeListing(item, { store, state }) {
  const files = state.repos[item.repo]?.files ?? {}
  const entries = item.names.filter((name) => files[name]).map((name) => ({ name, path: `${item.folder}/${name}`, type: 'file', size: files[name].size, sha: files[name].sha }))
  if (entries.length === 0) throw new Refused(`${item.repo}: no SDK file of the release was found`)
  const existing = await store.read(item.key)
  if (existing) {
    const listed = JSON.parse(decoder.decode(existing))
    for (const entry of entries) {
      const match = listed.find((candidate) => candidate.name === entry.name)
      if (!match || match.size !== entry.size || match.sha !== entry.sha) throw new Refused(`${item.key} already holds a listing that disagrees with the files`)
    }
    return { status: 'present', key: item.key }
  }
  await createOnce(store, item.key, encoder.encode(JSON.stringify(entries)), 'application/json')
  return { status: 'stored', key: item.key }
}

/** One DLSS Swapper archive: its size known from the manifest; written once, its SHA-256 recorded. */
async function storeArchive(item, { store, fetchImpl }) {
  const existing = await store.head(item.key)
  if (existing) {
    if (existing.size !== item.size) throw new Refused(`${item.key} is ${existing.size} bytes, its listing gives ${item.size}; never overwritten`)
    return { sha256: null }
  }
  const res = await upstream(fetchImpl, item.url, { headers: { 'user-agent': UA } })
  if (!res.ok) throw new HttpStatusError(`${item.url} answered ${res.status}`, res.status)
  const length = res.headers.get('content-length')
  if (length !== null && Number(length) !== item.size) throw new Refused(`${item.url} is ${length} bytes, its listing gives ${item.size}`)
  if (!res.body) throw new Error(`${item.url} has no body`)
  const { created, sha256 } = await store.stream(item.key, res.body, item.size, { write: true, type: item.type, blob: false })
  if (!created) throw new Refused(`${item.key} appeared while it was copied; never overwritten`)
  return { sha256 }
}

/** The catalog: every stored archive (newest first per file, at most the cap per file), with its zip's SHA-256 where it was streamed. */
async function writeCatalog(store, state) {
  const archives = Object.values(state.swapper.archives).sort((a, b) => a.file.localeCompare(b.file) || compareVersions(b.version, a.version))
  const kept = []
  const perFile = {}
  for (const a of archives) {
    perFile[a.file] = (perFile[a.file] ?? 0) + 1
    if (perFile[a.file] <= MAX_ARCHIVES_PER_FILE) kept.push(a)
  }
  const catalog = {
    schema: 2,
    archives: kept.map((a) => ({
      file: a.file, version: a.version, dllSize: a.dllSize,
      zip: { url: a.url, size: a.zipSize, ...(a.zipSha256 ? { sha256: a.zipSha256 } : {}) },
    })),
  }
  await store.replace(CATALOG_KEY, encoder.encode(JSON.stringify(catalog)), 'application/json')
}

async function readState(store) {
  const raw = await store.read(STATE_KEY)
  if (!raw) return emptyState()
  const state = JSON.parse(decoder.decode(raw))
  if (state?.schema !== 2) throw new Error('the watch state is not schema 2')
  return { ...emptyState(), ...state, swapper: { archives: {}, rejected: {}, ...state.swapper } }
}

/** The Worker's R2 binding as the store runWatch writes through. */
export function r2Store(bucket) {
  return {
    async read(key) {
      const object = await bucket.get(key)
      return object ? new Uint8Array(await object.arrayBuffer()) : null
    },
    async head(key) {
      const object = await bucket.head(key)
      return object ? { size: object.size } : null
    },
    // Create-only: R2 returns null, and writes nothing, when the key exists.
    async create(key, bytes, type) {
      const object = await bucket.put(key, bytes, { onlyIf: { etagDoesNotMatch: '*' }, httpMetadata: { contentType: type } })
      return object !== null
    },
    /**
     * A body streamed once. Natively: the SHA-256 of the bytes, and with <c>blob</c> the git blob SHA-1 ("blob <size>\0" then the bytes).
     * With <c>write</c> the bytes also go to R2 (create-only, with the length fixed by the listing). A body whose length is not <c>size</c> is
     * a refusal, counted as it streams, whatever the stream reports.
     */
    async stream(key, body, size, { write, type, blob }) {
      const sha = new crypto.DigestStream('SHA-256')
      const shaWriter = sha.getWriter()
      let git = null
      let gitWriter = null
      if (blob) {
        git = new crypto.DigestStream('SHA-1')
        gitWriter = git.getWriter()
        await gitWriter.write(encoder.encode(`blob ${size}\0`))
      }
      let seen = 0
      let mismatch = null
      const tap = new TransformStream({
        async transform(chunk, controller) {
          seen += chunk.byteLength
          if (seen > size) {
            mismatch = `more than the ${size} bytes the listing gives`
            throw new Error(mismatch)
          }
          await shaWriter.write(chunk)
          if (gitWriter) await gitWriter.write(chunk)
          controller.enqueue(chunk)
        },
        async flush() {
          if (seen !== size) {
            mismatch = `${seen} bytes, the listing gives ${size}`
            throw new Error(mismatch)
          }
          await shaWriter.close()
          if (gitWriter) await gitWriter.close()
        },
      })
      const piped = body.pipeThrough(tap)
      let created = false
      try {
        if (write) {
          const object = await bucket.put(key, piped.pipeThrough(new FixedLengthStream(size)), {
            onlyIf: { etagDoesNotMatch: '*' }, httpMetadata: { contentType: type },
          })
          created = object !== null
        } else {
          await piped.pipeTo(new WritableStream({}))
        }
      } catch (err) {
        if (mismatch) throw new Refused(`${key}: ${mismatch}`)
        throw err
      }
      return { created, sha256: hex(await sha.digest), blobSha1: git ? hex(await git.digest) : null }
    },
    async sameBytes(key, bytes) {
      const object = await bucket.get(key)
      if (!object || object.size !== bytes.byteLength) return false
      const reader = object.body.getReader()
      let at = 0
      for (;;) {
        const { done, value } = await reader.read()
        if (done) break
        for (let i = 0; i < value.length; i++) {
          if (value[i] !== bytes[at + i]) {
            await reader.cancel()
            return false
          }
        }
        at += value.length
      }
      return at === bytes.byteLength
    },
    async replace(key, bytes, type) {
      await bucket.put(key, bytes, { httpMetadata: { contentType: type } })
    },
  }
}

export const hex = (buffer) => [...new Uint8Array(buffer)].map((b) => b.toString(16).padStart(2, '0')).join('')

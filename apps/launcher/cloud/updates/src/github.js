// Exo Launcher's releases, read from GitHub by the Worker itself (../README.md, "Where a launcher release comes from").
// Players' PCs never ask GitHub: they ask updates.getexo.app, and this module is the only part of Exo that reads
// github.com for them. Everything it learns ends up in R2 bucket exo-releases, under the same keys publish-release.ps1
// writes, so the mirror keeps answering when GitHub doesn't.
//
//   latest      GitHub's newest stable release, asked at most once every CHECK_SECONDS across all of Cloudflare (the
//               check time is kept in R2, the answer at each edge), with a conditional request that GitHub doesn't count.
//   materialize A release's small files (.sha256, whats-new.json, files.json) written to R2 once, never replaced, after
//               the .sha256 agrees with GitHub's own record of the installer (size and SHA-256 digest).
//   promote     launcher/latest.json and launcher/CHANGELOG.md, written when GitHub names a newer tag than the mirror does.
//   fill        A large file (the installer, files.pack) copied from GitHub into R2, with R2 checking its SHA-256.
//   stream      A large file not yet in R2, passed through from GitHub to the client, byte ranges included.
//
// GitHub is asked without a token: the repository is public, and nothing here is secret.
export const REPO = 'UhhErix/Exo-Launcher'
const API = 'https://api.github.com'
const RAW = 'https://raw.githubusercontent.com'
const TAG = /^v\d{1,4}\.\d{1,4}\.\d{1,4}$/
const SHA256 = /^[0-9a-f]{64}$/
const AGENT = { 'user-agent': 'exo-updates (+https://getexo.app)' }
const HEADERS = { ...AGENT, 'x-github-api-version': '2022-11-28' }
/** GitHub is asked about the newest release at most this often, all edges together. */
export const CHECK_SECONDS = 300
/** The check's state: when GitHub was last asked, its ETag, and the release it named. */
export const STATE_KEY = '_github/launcher.json'
/** One edge at a time asks GitHub: the others answer with the last release meanwhile. A lease older than this is taken over. */
const LEASE_KEY = '_github/launcher.lease'
const LEASE_SECONDS = 30
/** Each edge keeps the check's answer this long before it reads STATE_KEY again; past it, the old answer still serves once. */
const EDGE_KEY = 'https://exo-updates.internal/github/launcher?v=1'
const RELEASE_KEY = (tag) => `https://exo-updates.internal/github/launcher/${tag}?v=1`
/** One copy at a time per file: a fill in progress holds _fill/<key> for at most this long. */
const FILL_LOCK_SECONDS = 600
/** GitHub's answers are small; anything larger is refused rather than read. */
const LIMITS = { release: 1 << 20, sha256: 4096, whatsNew: 64 << 10, whatsNewSource: 2 << 20, changelog: 8 << 20, version: 64, filesJson: 16 << 20 }
/** The largest file this copies: an installer, or a release's files.pack. */
export const MAX_FILE_BYTES = 1 << 30
/** How long one request to GitHub may take before the mirror's own copy answers instead. */
const ASK_MS = 8000

/** The names a release's files have in the mirror (and at /v1/launcher/download/<tag>/<name>). */
export const NAMES = {
  setup: 'ExoLauncher-Setup.exe',
  sha256: 'ExoLauncher-Setup.exe.sha256',
  whatsNew: 'whats-new.json',
  filesJson: 'files.json',
  filesPack: 'files.pack',
}
/** The files a client downloads whole or by range, copied into R2 by fill; the others are small and written by materialize. */
export const LARGE = new Set(['setup', 'filesPack'])

const now = () => Date.now()
const hex = (buffer) => [...new Uint8Array(buffer)].map((b) => b.toString(16).padStart(2, '0')).join('')
const digestOf = (asset) => (/^sha256:([0-9a-f]{64})$/.exec(asset?.digest ?? '') ?? [])[1] ?? null

/** A version's order, for "is this tag newer": v3.26.0 -> [3, 26, 0]. */
export function newer(a, b) {
  const x = a.slice(1).split('.').map(Number)
  const y = b.slice(1).split('.').map(Number)
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] > y[i]
  return false
}

async function sha256Of(bytes) {
  return hex(await crypto.subtle.digest('SHA-256', bytes))
}

/** A request to GitHub with a time limit; null when it fails or takes too long. Sends GitHub API headers only to api.github.com. */
async function ask(url, init = {}) {
  const signal = typeof AbortSignal.timeout === 'function' ? AbortSignal.timeout(ASK_MS) : undefined
  const baseHeaders = url.startsWith(API) ? HEADERS : AGENT
  try {
    return await fetch(url, { ...init, headers: { ...baseHeaders, ...(init.headers ?? {}) }, signal })
  } catch {
    return null
  }
}

/** A response's bytes, or null when it is not 200 or longer than limit. */
async function bytesOf(response, limit) {
  if (!response || response.status !== 200) return null
  const declared = Number(response.headers.get('content-length'))
  if (declared > limit) return null
  const bytes = new Uint8Array(await response.arrayBuffer())
  return bytes.byteLength > limit ? null : bytes
}

const text = (bytes) => new TextDecoder('utf-8', { fatal: true }).decode(bytes)

/**
 * What the Worker keeps of a GitHub release: its tag and the assets it knows by name. Null for a draft, a pre-release, a
 * tag that isn't vX.Y.Z, or anything that isn't a release.
 */
export function compactRelease(json) {
  if (!json || json.draft !== false || json.prerelease !== false || typeof json.tag_name !== 'string' || !TAG.test(json.tag_name)) return null
  const assets = {}
  for (const asset of Array.isArray(json.assets) ? json.assets : []) {
    if (asset?.state !== 'uploaded' || typeof asset.name !== 'string' || !Number.isSafeInteger(asset.size)) continue
    if (typeof asset.browser_download_url !== 'string' || !asset.browser_download_url.startsWith(`https://github.com/${REPO}/releases/download/`)) continue
    assets[asset.name] = { size: asset.size, sha256: digestOf(asset), url: asset.browser_download_url }
  }
  return { tag: json.tag_name, assets }
}

/** The release's GitHub asset for each mirror name, or null for a name the release doesn't carry. */
export function assetsOf(release) {
  const version = release.tag.slice(1)
  const pick = (...names) => names.map((name) => (release.assets[name] ? { name, ...release.assets[name] } : null)).find(Boolean) ?? null
  // The versioned installer name is what Build-Release attaches; the plain one is accepted too. Its .sha256 sits beside it.
  const setup = pick(`ExoLauncher-${version}-Setup.exe`, NAMES.setup)
  return {
    setup,
    sha256: setup ? pick(setup.name + '.sha256') : null,
    whatsNew: pick(NAMES.whatsNew),
    filesJson: pick(NAMES.filesJson),
    filesPack: pick(NAMES.filesPack),
    packSha256: pick(NAMES.filesPack + '.sha256'),
  }
}

/**
 * GitHub's newest stable release, compacted, or the last one known when GitHub can't be asked. Each edge keeps the answer
 * CHECK_SECONDS and then serves it once more while it asks again in the background (stale-while-revalidate); R2 holds the
 * time GitHub was last asked, so all edges together ask about once every CHECK_SECONDS.
 */
export async function latestRelease(env, ctx, { maxAge = CHECK_SECONDS } = {}) {
  const cache = globalThis.caches?.default
  if (cache) {
    const kept = await cache.match(EDGE_KEY).then((r) => r?.json()).catch(() => null)
    if (kept && typeof kept.at === 'number') {
      if (now() - kept.at < maxAge * 1000) return kept.release ?? null
      if (maxAge === CHECK_SECONDS) {
        ctx?.waitUntil?.(checkGitHub(env, ctx, maxAge).catch(() => {}))
        return kept.release ?? null
      }
    }
  }
  return checkGitHub(env, ctx, maxAge)
}

/**
 * Takes the lease on asking GitHub (R2 conditional writes, so only one request holds it), or false when another request
 * holds a fresh one.
 */
async function takeLease(mirror) {
  const body = JSON.stringify({ at: now() })
  if (await mirror.put(LEASE_KEY, body, { onlyIf: { etagDoesNotMatch: '*' } }).catch(() => null)) return true
  const held = await mirror.get(LEASE_KEY).catch(() => null)
  if (!held) return false
  const at = await held.json().then((lease) => lease?.at).catch(() => null)
  if (typeof at === 'number' && now() - at < LEASE_SECONDS * 1000) return false
  return Boolean(await mirror.put(LEASE_KEY, body, { onlyIf: { etagMatches: held.etag } }).catch(() => null))
}

async function checkGitHub(env, ctx, maxAge) {
  const state = await readJson(env.MIRROR, STATE_KEY)
  let release = state?.release ?? null
  const due = !(typeof state?.checkedAt === 'number' && now() - state.checkedAt < maxAge * 1000)
  if (due && !(await takeLease(env.MIRROR))) return release // another request is asking; the next one reads its answer
  if (due) {
    try {
      // Read again under the lease: a request that held it a moment ago may have just asked.
      const fresh = await readJson(env.MIRROR, STATE_KEY)
      release = typeof fresh?.checkedAt === 'number' && now() - fresh.checkedAt < maxAge * 1000 ? fresh.release ?? null : await askGitHub(env, fresh)
    } finally {
      await env.MIRROR.delete(LEASE_KEY).catch(() => {})
    }
  }
  const cache = globalThis.caches?.default
  if (cache) {
    const put = cache.put(EDGE_KEY, new Response(JSON.stringify({ at: now(), release }), { headers: { 'cache-control': 'max-age=86400' } }))
    ctx?.waitUntil ? ctx.waitUntil(put.catch(() => {})) : await put.catch(() => {})
  }
  return release
}

/** One conditional ask for the newest release; the state it leaves in R2 names the release (the old one when the ask failed). */
async function askGitHub(env, state) {
  let release = state?.release ?? null
  const response = await ask(`${API}/repos/${REPO}/releases/latest`, {
    headers: { accept: 'application/vnd.github+json', ...(state?.etag ? { 'if-none-match': state.etag } : {}) },
  })
  let etag = state?.etag ?? null
  if (response?.status === 200) {
    const bytes = await bytesOf(response, LIMITS.release).catch(() => null)
    let parsed = null
    try {
      parsed = bytes ? compactRelease(JSON.parse(text(bytes))) : null
    } catch {}
    if (parsed) {
      release = parsed
      etag = response.headers.get('etag')
    }
  }
  // A failed ask still counts as a check: GitHub is not asked again for CHECK_SECONDS, and the last release stands.
  await env.MIRROR.put(STATE_KEY, JSON.stringify({ checkedAt: now(), etag, release, ok: response?.status === 200 || response?.status === 304 }))
    .catch(() => {})
  return release
}

/** A tag's release from GitHub (any stable tag, not only the newest), kept at the edge CHECK_SECONDS unless `fresh` bypasses the read cache. */
export async function releaseForTag(tag, env, ctx, { fresh = false } = {}) {
  if (!fresh) {
    const latest = await latestRelease(env, ctx)
    if (latest?.tag === tag) return latest
  }
  const cache = globalThis.caches?.default
  if (!fresh) {
    const kept = await cache?.match(RELEASE_KEY(tag)).then((r) => r?.json()).catch(() => null)
    if (kept && now() - kept.at < CHECK_SECONDS * 1000) return kept.release ?? null
  }
  const response = await ask(`${API}/repos/${REPO}/releases/tags/${tag}`, { headers: { accept: 'application/vnd.github+json' } })
  let release = null
  try {
    const bytes = await bytesOf(response, LIMITS.release)
    release = bytes ? compactRelease(JSON.parse(text(bytes))) : null
  } catch {}
  if (release?.tag !== tag) release = null
  if (cache && response && (response.status === 200 || response.status === 404)) {
    const put = cache.put(RELEASE_KEY(tag), new Response(JSON.stringify({ at: now(), release }), { headers: { 'cache-control': 'max-age=86400' } }))
    ctx?.waitUntil ? ctx.waitUntil(put.catch(() => {})) : await put.catch(() => {})
  }
  return release
}

/** A "<hex> [*name]" .sha256 file's hash when it names one of the names (or none), else null. */
export function parseSha256File(content, names) {
  const tokens = content.trim().split(/\s+/)
  if (tokens.length < 1 || tokens.length > 2 || !tokens[0]) return null
  if (tokens.length === 2 && !names.includes(tokens[1].replace(/^\*/, ''))) return null
  const hash = tokens[0].toLowerCase().replace(/^sha256:/, '')
  return SHA256.test(hash) ? hash : null
}

/** A GitHub asset's bytes, held to its recorded size and, when GitHub recorded one, its SHA-256. */
async function assetBytesDetail(asset, limit) {
  if (!asset || asset.size > limit) return { bytes: null, reason: asset ? `size=${asset.size}>${limit}` : 'missing' }
  const response = await ask(asset.url)
  if (!response) return { bytes: null, reason: 'net' }
  if (response.status !== 200) {
    await response.body?.cancel().catch(() => {})
    return { bytes: null, reason: `status=${response.status}` }
  }
  const bytes = await bytesOf(response, limit)
  if (!bytes || bytes.byteLength !== asset.size) return { bytes: null, reason: `len=${bytes?.byteLength ?? 'null'}/${asset.size}` }
  if (asset.sha256 && (await sha256Of(bytes)) !== asset.sha256) return { bytes: null, reason: 'digest-mismatch' }
  return { bytes, reason: null }
}

async function assetBytes(asset, limit) {
  return (await assetBytesDetail(asset, limit)).bytes
}

/** A file from the tag's committed tree, through raw.githubusercontent.com. */
async function tagFile(tag, path, limit) {
  return bytesOf(await ask(`${RAW}/${REPO}/${tag}/apps/launcher/${path}`), limit)
}

/**
 * Writes key once: when R2 already holds it, its bytes are kept and returned (never replaced), else these bytes are.
 * Null when R2 can't be written or read.
 */
async function putOnce(mirror, key, bytes, contentType) {
  try {
    const written = await mirror.put(key, bytes, { onlyIf: { etagDoesNotMatch: '*' }, httpMetadata: { contentType } })
    if (written) return bytes
    const kept = await mirror.get(key)
    return kept ? new Uint8Array(await kept.arrayBuffer()) : null
  } catch {
    return null
  }
}

async function check(name, bytes) {
  return { name, size: bytes.byteLength, sha256: await sha256Of(bytes) }
}

/**
 * The release's manifest (schema 2, the shape publish-release.ps1 writes), with its small files in R2 under
 * launcher/<tag>/. Returns { manifest } or { error } (a reason for the log). Checks, in order:
 *   - the installer and its .sha256 are attached; the .sha256 names the installer's SHA-256, and GitHub's own digest and
 *     size for the installer agree with it (GitHub records a SHA-256 for every upload; a release without one is refused);
 *   - the tag's apps/launcher/VERSION is the tag's version;
 *   - What's New: the release's whats-new.json asset (held to GitHub's digest), else the version's entry in the tag's
 *     ui/src/lib/whatsNew.json;
 *   - when the release has files.json and files.pack (file-level updates): files.json held to GitHub's digest, and the
 *     pack's SHA-256 from files.pack.sha256, else files.json's own "pack" header, agreeing with GitHub's digest.
 * A tag whose files R2 already holds keeps them: a .sha256 there that names another hash refuses the release.
 */
export async function materialize(release, env) {
  const mirror = env.MIRROR
  const tag = release.tag
  const version = tag.slice(1)
  const assets = assetsOf(release)
  if (!assets.setup || !assets.sha256) return { error: `${tag} has no installer and .sha256 attached` }
  if (assets.setup.size <= 0 || assets.setup.size > MAX_FILE_BYTES) return { error: `${tag}'s installer size is out of range` }
  // GitHub's own SHA-256 of the upload is the check that doesn't come from the release's files: no digest, no release.
  if (!assets.setup.sha256) return { error: `GitHub recorded no SHA-256 for ${tag}'s installer` }
  const shaResult = await assetBytesDetail(assets.sha256, LIMITS.sha256)
  const shaBytes = shaResult.bytes
  const hash = shaBytes ? parseSha256File(text(shaBytes), [assets.setup.name, NAMES.setup]) : null
  if (!hash) return { error: `${tag}'s .sha256 could not be read (${shaResult.reason ?? 'bad-parse'}) or does not name the installer` }
  if (assets.setup.sha256 !== hash) return { error: `${tag}'s .sha256 is not GitHub's digest of the installer` }

  // What the apps read is "<hash>  ExoLauncher-Setup.exe", as Build-Release writes it: the name the app asks for.
  const prefix = `launcher/${tag}/`
  const kept = await mirror.get(prefix + NAMES.sha256).then((o) => (o ? o.text() : null)).catch(() => null)
  if (kept !== null && parseSha256File(kept, [NAMES.setup]) !== hash) return { error: `the mirror already holds another build of ${tag}` }

  const versionBytes = await tagFile(tag, 'VERSION', LIMITS.version)
  if (!versionBytes || text(versionBytes).trim() !== version) return { error: `${tag}'s apps/launcher/VERSION is not ${version}` }

  let notes = null
  if (assets.whatsNew) {
    const bytes = await assetBytes(assets.whatsNew, LIMITS.whatsNew)
    try {
      if (bytes && JSON.parse(text(bytes))?.version === version) notes = bytes
    } catch {}
    if (!notes) return { error: `${tag}'s whats-new.json asset is unreadable or for another version` }
  } else {
    const bytes = await tagFile(tag, 'ui/src/lib/whatsNew.json', LIMITS.whatsNewSource)
    let entry = null
    try {
      entry = bytes ? JSON.parse(text(bytes)).find((item) => item?.version === version) : null
    } catch {}
    if (!entry) return { error: `ui/src/lib/whatsNew.json at ${tag} has no ${version} entry` }
    notes = new TextEncoder().encode(JSON.stringify(entry, null, 2))
  }

  let delta = null
  if (assets.filesJson && assets.filesPack) {
    delta = await deltaFiles(assets, tag)
    if (delta.error) return delta
  }

  const sha256Bytes = await putOnce(mirror, prefix + NAMES.sha256, new TextEncoder().encode(`${hash}  ${NAMES.setup}`), 'text/plain; charset=utf-8')
  const notesBytes = await putOnce(mirror, prefix + NAMES.whatsNew, notes, 'application/json; charset=utf-8')
  if (!sha256Bytes || !notesBytes) return { error: `${tag}'s files could not be written to the mirror` }
  const files = {
    setup: { name: NAMES.setup, size: assets.setup.size, sha256: hash },
    sha256: await check(NAMES.sha256, sha256Bytes),
    whatsNew: await check(NAMES.whatsNew, notesBytes),
  }
  if (delta) {
    const listBytes = await putOnce(mirror, prefix + NAMES.filesJson, delta.list, 'application/json; charset=utf-8')
    if (!listBytes) return { error: `${tag}'s files.json could not be written to the mirror` }
    files.filesJson = await check(NAMES.filesJson, listBytes)
    files.filesPack = { name: NAMES.filesPack, size: assets.filesPack.size, sha256: delta.packSha256 }
  }
  return { manifest: { schema: 2, app: 'launcher', tag, version, files } }
}

/** Backfills files.json + files.pack for an already-promoted release whose setup digest already matches the mirror. */
async function materializeDelta(release, current, env) {
  const tag = release.tag
  const assets = assetsOf(release)
  if (!assets.filesJson || !assets.filesPack) return { error: `${tag} has no delta assets` }
  if (!assets.setup?.sha256 || assets.setup.sha256 !== current.files?.setup?.sha256) {
    return { error: `${tag}'s installer digest differs from mirror` }
  }
  const delta = await deltaFiles(assets, tag)
  if (delta.error) return delta
  const listBytes = await putOnce(env.MIRROR, `launcher/${tag}/${NAMES.filesJson}`, delta.list, 'application/json; charset=utf-8')
  if (!listBytes) return { error: `${tag}'s files.json could not be written to the mirror` }
  return {
    manifest: {
      ...current,
      files: {
        ...current.files,
        filesJson: await check(NAMES.filesJson, listBytes),
        filesPack: { name: NAMES.filesPack, size: assets.filesPack.size, sha256: delta.packSha256 },
      },
    },
  }
}

/** files.json's bytes and the pack's SHA-256, each checked against what GitHub recorded. */
async function deltaFiles(assets, tag) {
  if (!assets.filesJson.sha256 || !assets.filesPack.sha256) return { error: `${tag}'s files.json or files.pack has no GitHub digest` }
  if (assets.filesPack.size <= 0 || assets.filesPack.size > MAX_FILE_BYTES) return { error: `${tag}'s files.pack size is out of range` }
  const list = await assetBytes(assets.filesJson, LIMITS.filesJson)
  let header = null
  try {
    header = list ? JSON.parse(text(list)) : null
  } catch {}
  if (!header || typeof header !== 'object') return { error: `${tag}'s files.json is unreadable` }
  if (header.version !== undefined && header.version !== tag.slice(1)) return { error: `${tag}'s files.json is for another version` }
  let packSha256 = null
  if (assets.packSha256) {
    const bytes = await assetBytes(assets.packSha256, LIMITS.sha256)
    packSha256 = bytes ? parseSha256File(text(bytes), [NAMES.filesPack]) : null
    if (!packSha256) return { error: `${tag}'s files.pack.sha256 does not name files.pack` }
  } else if (typeof header.pack?.sha256 === 'string' && SHA256.test(header.pack.sha256)) {
    packSha256 = header.pack.sha256
  }
  if (!packSha256) return { error: `${tag} gives no SHA-256 for files.pack` }
  if (packSha256 !== assets.filesPack.sha256) return { error: `${tag}'s files.pack is not the pack its SHA-256 names` }
  if (header.pack?.size !== undefined && header.pack.size !== assets.filesPack.size) return { error: `${tag}'s files.pack is not the size files.json gives` }
  return { list, packSha256 }
}

/** A release the Worker promoted and whose card is not posted yet (worker.js announcePending). */
export const PENDING_CARD = (tag) => `_github/announce/${tag}`

/**
 * The newest release for launcher/latest.json: GitHub's, when it is newer than the mirror's and checks out (then it is
 * written, with launcher/CHANGELOG.md); else the mirror's own. Never moves to an older tag: the pointer is written only
 * if it is still the copy just read (an R2 conditional write), and read again when it isn't. Returns
 * { manifest, promoted, release } with manifest null when neither has one.
 */
export async function promote(env, ctx, { current, maxAge = CHECK_SECONDS, hintTag } = {}) {
  let release = await latestRelease(env, ctx, { maxAge })
  if (typeof hintTag === 'string' && TAG.test(hintTag) && (!current || newer(hintTag, current.tag)) && (!release || newer(hintTag, release.tag))) {
    const hinted = await releaseForTag(hintTag, env, ctx, { fresh: true })
    if (hinted) release = hinted
  }
  const sameTagNeedsDelta = Boolean(
    release && current && release.tag === current.tag && current.schema === 2 &&
    !current.files?.filesJson && assetsOf(release).filesJson && assetsOf(release).filesPack
  )
  if (!release || (current && !newer(release.tag, current.tag) && !sameTagNeedsDelta)) return { manifest: current ?? null, promoted: false }
  if (sameTagNeedsDelta) {
    const { manifest: deltaManifest, error: deltaError } = await materializeDelta(release, current, env)
    if (!deltaManifest) {
      console.warn(`GitHub delta backfill skipped: ${deltaError}.`)
      return { manifest: current ?? null, promoted: false }
    }
    const mirror = env.MIRROR
    for (let attempt = 0; attempt < 3; attempt++) {
      const object = await mirror.get('launcher/latest.json')
      const seen = object ? await object.json().catch(() => null) : null
      if (!seen || seen.tag !== release.tag) return { manifest: seen ?? current, promoted: false }
      if (seen.files?.filesJson && seen.files?.filesPack) return { manifest: seen, promoted: false }
      const updated = {
        ...seen,
        files: { ...seen.files, filesJson: deltaManifest.files.filesJson, filesPack: deltaManifest.files.filesPack },
      }
      const written = await mirror.put('launcher/latest.json', JSON.stringify(updated), {
        httpMetadata: { contentType: 'application/json' },
        onlyIf: { etagMatches: object.etag },
      })
      if (written) return { manifest: updated, promoted: false, release }
    }
    return { manifest: current ?? null, promoted: false }
  }
  const { manifest, error } = await materialize(release, env)
  if (!manifest) {
    console.warn(`GitHub release not offered: ${error}.`)
    return { manifest: current ?? null, promoted: false }
  }
  const changelog = await tagFile(release.tag, 'CHANGELOG.md', LIMITS.changelog)
  const version = release.tag.slice(1)
  if (!changelog || !text(changelog).split('\n').some((line) => line.startsWith(`## ${version} `) || line.trim() === `## ${version}`)) {
    console.warn(`GitHub release not offered: CHANGELOG.md at ${release.tag} has no ${version} entry.`)
    return { manifest: current ?? null, promoted: false }
  }
  const mirror = env.MIRROR
  for (let attempt = 0; attempt < 3; attempt++) {
    const object = await mirror.get('launcher/latest.json')
    const seen = object ? await object.json().catch(() => null) : null
    if (seen && typeof seen.tag === 'string' && TAG.test(seen.tag) && !newer(release.tag, seen.tag)) return { manifest: seen, promoted: false }
    const written = await mirror.put('launcher/latest.json', JSON.stringify(manifest), {
      httpMetadata: { contentType: 'application/json' },
      onlyIf: object ? { etagMatches: object.etag } : { etagDoesNotMatch: '*' },
    })
    if (!written) continue // another request wrote the pointer meanwhile: compare with what it wrote
    await mirror.put(PENDING_CARD(release.tag), '{}').catch(() => {})
    await writeChangelog(mirror, release.tag, changelog)
    return { manifest, promoted: true, release }
  }
  return { manifest: current ?? null, promoted: false }
}

/**
 * Repairs a missed launcher/CHANGELOG.md write when promote committed the pointer but all three changelog writes failed.
 * Keeps the conditional write and newer-tag check so recovery never overwrites a newer changelog.
 */
export async function reconcileChangelog(env, tag) {
  const mirror = env?.MIRROR
  if (!mirror || typeof tag !== 'string' || !TAG.test(tag)) return
  const head = await mirror.head('launcher/CHANGELOG.md').catch(() => undefined)
  if (head === undefined) return
  const kept = head?.customMetadata?.tag
  if (typeof kept === 'string' && TAG.test(kept) && !newer(tag, kept)) return
  const changelog = await tagFile(tag, 'CHANGELOG.md', LIMITS.changelog).catch(() => null)
  if (!changelog) return
  const version = tag.slice(1)
  if (!text(changelog).split('\n').some((line) => line.startsWith(`## ${version} `) || line.trim() === `## ${version}`)) return
  await writeChangelog(mirror, tag, changelog)
}

/**
 * launcher/CHANGELOG.md for tag, unless the one there is already for a newer tag (its customMetadata names the tag it is
 * for), with the same conditional write as the pointer, so the change list never ends up older than the release offered.
 */
async function writeChangelog(mirror, tag, bytes) {
  const key = 'launcher/CHANGELOG.md'
  for (let attempt = 0; attempt < 3; attempt++) {
    const head = await mirror.head(key).catch(() => null)
    const kept = head?.customMetadata?.tag
    if (typeof kept === 'string' && TAG.test(kept) && !newer(tag, kept)) return
    const written = await mirror.put(key, bytes, {
      httpMetadata: { contentType: 'text/markdown; charset=utf-8' },
      customMetadata: { tag },
      onlyIf: head ? { etagMatches: head.etag } : { etagDoesNotMatch: '*' },
    }).catch(() => null)
    if (written) return
  }
}

/**
 * Copies a large file (the installer or files.pack) from GitHub into R2 at key, once. R2 checks the SHA-256 as it
 * stores the bytes (put's sha256 option) and the stored object is checked again; a copy that doesn't match is deleted
 * and never served. Never replaces an object that is there. Returns what happened, for tests and the log.
 */
export async function fill(env, key, asset, expected) {
  const mirror = env.MIRROR
  if (await mirror.head(key).catch(() => null)) return 'present'
  const lock = `_fill/${key}`
  const held = await mirror.put(lock, JSON.stringify({ at: now() }), { onlyIf: { etagDoesNotMatch: '*' } }).catch(() => null)
  if (!held) {
    const at = (await readJson(mirror, lock))?.at
    if (typeof at === 'number' && now() - at < FILL_LOCK_SECONDS * 1000) return 'busy'
    await mirror.put(lock, JSON.stringify({ at: now() }))
  }
  try {
    const response = await fetch(asset.url, { headers: HEADERS })
    if (response.status !== 200 || !response.body) return 'unavailable'
    const declared = response.headers.get('content-length')
    if (declared !== null && Number(declared) !== expected.size) {
      await response.body.cancel()
      return 'wrong-size'
    }
    const body = globalThis.FixedLengthStream ? response.body.pipeThrough(new FixedLengthStream(expected.size)) : response.body
    let stored
    try {
      stored = await mirror.put(key, body, {
        sha256: expected.sha256,
        onlyIf: { etagDoesNotMatch: '*' },
        httpMetadata: { contentType: 'application/octet-stream' },
      })
    } catch {
      // R2 refused the bytes (its SHA-256 or length didn't match) or the stream broke: nothing was stored.
      return 'refused'
    }
    if (!stored) return 'present'
    const sha = stored.checksums?.sha256 ? hex(stored.checksums.sha256) : expected.sha256
    if (stored.size !== expected.size || sha !== expected.sha256) {
      await mirror.delete(key)
      return 'mismatch'
    }
    return 'stored'
  } finally {
    await mirror.delete(lock).catch(() => {})
  }
}

/**
 * A large file R2 doesn't hold yet, passed through from GitHub: the whole file, or the one byte range asked for (GitHub's
 * download host answers ranges). No ETag, so a client never resumes against it; the next request is served from R2.
 */
export async function stream(asset, request) {
  const heading = request.method === 'HEAD'
  const range = request.headers.get('range')
  const headers = { 'content-type': 'application/octet-stream', 'cache-control': 'no-store', 'accept-ranges': 'bytes' }
  if (heading) return new Response(null, { headers: { ...headers, 'content-length': String(asset.size) } })
  const single = range && /^bytes=\d*-\d*$/.test(range.trim()) ? range.trim() : null
  const upstream = await fetch(asset.url, { headers: { ...HEADERS, ...(single ? { range: single } : {}) } }).catch(() => null)
  if (!upstream || (upstream.status !== 200 && upstream.status !== 206 && upstream.status !== 416)) {
    await upstream?.body?.cancel().catch(() => {})
    return new Response('Not available', { status: 502, headers: { 'cache-control': 'no-store' } })
  }
  const out = new Headers(headers)
  for (const name of ['content-length', 'content-range']) if (upstream.headers.has(name)) out.set(name, upstream.headers.get(name))
  if (upstream.status === 200) out.set('content-length', String(asset.size))
  return new Response(upstream.status === 416 ? null : upstream.body, { status: upstream.status, headers: out })
}

export async function readJson(mirror, key) {
  try {
    const object = await mirror.get(key)
    return object ? await object.json() : null
  } catch {
    return null
  }
}

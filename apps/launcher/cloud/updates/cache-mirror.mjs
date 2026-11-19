#!/usr/bin/env node
/**
 * Exo's cache mirror (README.md, "Exo's cache"). Reads the exact pins the Launcher compiles in (its URLs, sizes and
 * SHA-256s), downloads each pinned file from its upstream once, checks the size and SHA-256 before anything is uploaded,
 * and writes it to R2 bucket exo-releases under `cache/<github path>`, the key the update Worker serves.
 *
 *   node cache-mirror.mjs --launcher <Launcher checkout> [--rev fix/3.25-off-github] [--dry-run]
 *
 * Also written, from the GitHub API and checked against the pins: the folder listing at each pinned commit
 * (`cache/<repo>/contents/<folder>@<commit>`) and the tag's commit (`cache/<repo>/commits/<tag>`). Each licence file sits
 * next to its DLLs; its first hash is recorded in cache-licences.json and a later change is refused (licences have no
 * pin in the Launcher, so this record is the only check).
 *
 * Re-runnable: an object already in the state manifest (`_mirror/manifest.json`, outside the served `cache/` prefix)
 * with the same hash is skipped. An object with another hash is never overwritten. A DLSS Swapper zip is checked against its
 * archive SHA-256 in the Launcher (ZipSource.Sha256) before its DLL is read.
 *
 * How it could fail: an upstream file that doesn't match its pin reaches R2; an object is overwritten with another hash;
 * a listing that disagrees with the pinned bytes is served; the pin parser misses a pin and nothing says so; a licence
 * changes without notice; a path outside `cache/` is written.
 *   node --test test
 */
import { createHash } from 'node:crypto'
import { execFile, execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

export const WRANGLER = 'wrangler@4.165.0'
export const BUCKET = 'exo-releases'
export const STATE_KEY = '_mirror/manifest.json'
const HERE = dirname(fileURLToPath(import.meta.url))
export const LICENCE_RECORD = join(HERE, 'cache-licences.json')
const PIN_FILES = [
  'ExoLauncher/Services/DlssSwapService.Releases.cs',
  'ExoLauncher/Adapters/AmazonAdapter.cs',
  'ExoLauncher/Adapters/EpicAdapter.cs',
  'ExoLauncher/Adapters/GogAdapter.cs',
  'ExoLauncher/Services/DlssSwapService.FrameGen.cs',
  'ExoLauncher/Services/DiscordClient/Voice/SpeakerModel.cs',
]

// ---------- reading the Launcher's pins ----------

/**
 * The Launcher's constants (repos, commits, paths, and joined ones such as the cache URL built from CacheHost) and the tag
 * each commit constant's comment names. `base` holds names from other files.
 */
export function parseConstants(src, base = new Map()) {
  const values = new Map(base)
  const tags = new Map()
  for (const m of src.matchAll(/const string (\w+) = ([^;]+);([^\n]*)/g)) {
    let value
    try {
      value = evalToken(m[2].trim(), values)
    } catch {
      continue // a constant the mirror does not need (it names something outside these files)
    }
    values.set(m[1], value)
    const tag = /\/\/\s*(v\d[^\s,)]*)/.exec(m[3])
    if (tag) tags.set(m[1], tag[1])
  }
  return { values, tags }
}

/** The arguments of a `new(` call starting at `start` (just after the paren), split at top-level commas. */
function argsAt(src, start) {
  const out = []
  let depth = 0
  let cur = ''
  let inString = false
  for (let i = start; i < src.length; i++) {
    const c = src[i]
    if (inString) {
      cur += c
      if (c === '\\') cur += src[++i]
      else if (c === '"') inString = false
      continue
    }
    if (c === '"') inString = true
    else if (c === '(') depth++
    else if (c === ')') {
      if (depth === 0) {
        out.push(cur.trim())
        return out
      }
      depth--
    } else if (c === ',' && depth === 0) {
      out.push(cur.trim())
      cur = ''
      continue
    }
    cur += c
  }
  throw new Error('unterminated new( in the Launcher source')
}

/** A token is a string literal, an integer, a constant, or constants and literals joined with +. */
function evalToken(token, values) {
  return token
    .split(/\s\+\s/)
    .map((part) => {
      const p = part.trim()
      if (p.startsWith('"')) return JSON.parse(p)
      if (/^\d+$/.test(p)) return p
      if (values.has(p)) return values.get(p)
      throw new Error(`unknown constant in the Launcher source: ${p}`)
    })
    .join('')
}

/** The PinnedFile entries of DlssSwapService.Releases.cs (not the zip-only ones, which have no commit). */
export function parseVendorPins(src, { values, tags }) {
  const pins = []
  const entry = /^ {8}new\(/gm
  let m
  while ((m = entry.exec(src))) {
    const t = argsAt(src, m.index + m[0].length).map((tok) => tok)
    const [fileName, release, , , , repoTok, commitTok, pathTok, sizeTok, shaTok] = t
    const commitName = commitTok
    const commit = evalToken(commitName, values)
    if (commit === '') continue // a zip-only build: DLL Swapper's archive, not a GitHub path
    const repo = evalToken(repoTok, values)
    const repoPath = evalToken(pathTok, values)
    const slash = repoPath.lastIndexOf('/')
    const folder = repoPath.slice(0, slash)
    const name = repoPath.slice(slash + 1)
    if (name !== evalToken(fileName, values)) throw new Error(`pin ${release}: file name ${name} does not match its path`)
    const commitConst = values.has(commitName) ? commitName : null
    pins.push({
      kind: 'dll',
      release: evalToken(release, values),
      repo,
      commit,
      commitConst,
      tag: commitConst ? tags.get(commitConst) ?? null : null,
      folder,
      name,
      size: Number(evalToken(sizeTok, values)),
      sha256: evalToken(shaTok, values),
    })
  }
  return pins
}

/**
 * The GitHubReleaseAsset pins that are one release file each: the three tool adapters (size and SHA-256 named
 * `ExpectedSize:` and `ExpectedSha256:`) and the TitaNet voice model (SpeakerModel.cs, the same five values by position).
 */
export function parseToolPins(src) {
  const re = /GitHubReleaseAsset\s+\w+\s*=\s*new\(\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)",\s*(?:ExpectedSize:\s*)?([\d_]+),\s*(?:ExpectedSha256:\s*)?"([0-9a-f]{64})"/g
  const pins = []
  for (const m of src.matchAll(re)) {
    pins.push({
      kind: 'asset',
      owner: m[1],
      repo: m[2],
      tag: m[3],
      asset: m[4],
      size: Number(m[5].replaceAll('_', '')),
      sha256: m[6],
    })
  }
  return pins
}

/**
 * The frame-gen releases (FrameGen.cs PinnedFrameGenReleases): one release file each, as a GitHub release asset. The
 * owner of each repo comes from the mod table ("<name>", "<owner>", "<repo>", <asset pattern>).
 */
export function parseFrameGenPins(src) {
  const owners = new Map()
  for (const m of src.matchAll(/"([^"]+)", "([^"]+)", "([^"]+)", [A-Za-z0-9]+\(\)/g)) owners.set(m[3], m[2])
  const pins = []
  for (const m of src.matchAll(/\("([^"]+)", new\("([^"]+)", "([^"]+)", (\d+), "([0-9a-f]{64})"\)\)/g)) {
    const repo = m[1]
    if (!owners.has(repo)) throw new Error(`frame-gen pin ${repo} has no owner in the mod table`)
    pins.push({ kind: 'asset', owner: owners.get(repo), repo, tag: m[2], asset: m[3], size: Number(m[4]), sha256: m[5] })
  }
  return pins
}

/** Reads the pins from the Launcher at a revision (committed source, never the working tree). */
export function readPins(launcher, rev, git = gitShow) {
  const read = (path) => git(launcher, rev, path)
  // The cache host, named in VerifiedGitHubReleaseDownloader.cs (the zip pins are built from it at 9d797c20 and later).
  const cacheHost = /CacheHost = "([^"]+)"/.exec(read('ExoLauncher/Services/VerifiedGitHubReleaseDownloader.cs'))?.[1]
  const constants = parseConstants(read(PIN_FILES[0]), new Map(cacheHost ? [['VerifiedGitHubReleaseDownloader.CacheHost', cacheHost]] : []))
  const vendor = parseVendorPins(read(PIN_FILES[0]), constants)
  const tools = [...PIN_FILES.slice(1, 4), PIN_FILES[5]].flatMap((path) => parseToolPins(read(path)))
  const frameGen = parseFrameGenPins(read(PIN_FILES[4]))
  const zips = parseZipPins(read(PIN_FILES[0]), constants)
  return { vendor, tools: [...tools, ...frameGen], zips }
}

/**
 * The DLSS Swapper archive pins (PinnedFile entries with no commit, whose last argument is the zip): the zip's URL and size,
 * and the DLL inside it with its size and SHA-256.
 */
export function parseZipPins(src, { values }) {
  const pins = []
  const entry = /^ {8}new\(/gm
  let m
  while ((m = entry.exec(src))) {
    const t = argsAt(src, m.index + m[0].length)
    const [fileName, , , , , , commitTok, , sizeTok, shaTok, zipTok] = t
    if (evalToken(commitTok, values) !== '' || !zipTok?.startsWith('new(')) continue
    const [urlTok, zipSizeTok, zipShaTok] = argsAt(zipTok, 4)
    pins.push({
      dllName: evalToken(fileName, values),
      dllSize: Number(evalToken(sizeTok, values)),
      dllSha256: evalToken(shaTok, values),
      zipUrl: evalToken(urlTok, values),
      zipSize: Number(zipSizeTok),
      zipSha256: evalToken(zipShaTok, values),
    })
  }
  return pins
}

function gitShow(launcher, rev, path) {
  return execFileSync('git', ['-C', launcher, 'show', `${rev}:${path}`], { encoding: 'utf8', maxBuffer: 64 << 20, windowsHide: true })
}

// ---------- the plan: every object the Worker will serve ----------

/**
 * Every object the mirror writes, from the pins. Keys are the Worker's exact paths under `cache/`.
 * DLLs and tool files are pinned; listings and commits are checked against them; licences are recorded on first sight.
 */
/** Repos whose releases Exo may not redistribute, with the reason. Their pins stay in the Launcher until it drops them. */
export const EXCLUDED_REPOS = new Map([
  ['sdli1995/dlssg_for_sm86', 'no licence file at its release; Exo may not redistribute it (owner decision, 2026-11-09)'],
])

export function planObjects({ vendor, tools: allTools, zips = [] }) {
  const tools = allTools.filter((pin) => !EXCLUDED_REPOS.has(`${pin.owner}/${pin.repo}`))
  // DLSS Swapper archives: stored at the same path under the cache host. Their SHA-256 is recorded on first download (the
  // Launcher pins the DLL inside, not the zip). The NVIDIA DLSS licence goes next to each one, in the same folder.
  // The Launcher names each zip at the cache host (`https://updates.getexo.app/v1/cache/<path>`) or at the archive host itself;
  // either way the path after the host is the same, and the upstream is the archive host.
  const ARCHIVE = 'dlss-swapper-downloads.beeradmoore.com/'
  const zipPlan = [...new Map(zips.map((z) => {
    const rest = z.zipUrl.replace(/^https:\/\/updates\.getexo\.app\/v1\/cache\//, '').replace(/^https:\/\//, '')
    if (!rest.startsWith(ARCHIVE)) throw new Error(`zip ${z.zipUrl} is not on ${ARCHIVE}`)
    const key = `cache/${rest}`
    return [key, { key, upstream: `https://${rest}`, zipSize: z.zipSize, zipSha256: z.zipSha256, dllName: z.dllName, dllSize: z.dllSize, dllSha256: z.dllSha256 }]
  })).values()]
  const nvidia = vendor.find((p) => p.release === 'dlss-310.9.1')
  const dlssLicence = nvidia
    ? {
        recordKey: `cache/${nvidia.repo}/${nvidia.commit}/${nvidia.folder}/LICENSE.txt`,
        upstream: `https://raw.githubusercontent.com/${nvidia.repo}/${nvidia.commit}/LICENSE.txt`,
      }
    : null
  const binaries = []
  for (const pin of tools) {
    binaries.push({
      key: `cache/${pin.owner}/${pin.repo}/releases/download/${pin.tag}/${pin.asset}`,
      upstream: `https://github.com/${pin.owner}/${pin.repo}/releases/download/${pin.tag}/${pin.asset}`,
      size: pin.size,
      sha256: pin.sha256,
      contentType: pin.asset.endsWith('.onnx') ? 'application/octet-stream' : 'application/x-msdownload',
      label: `${pin.repo} ${pin.tag}`,
    })
  }
  for (const pin of vendor) {
    binaries.push({
      key: `cache/${pin.repo}/${pin.commit}/${pin.folder}/${pin.name}`,
      upstream: `https://raw.githubusercontent.com/${pin.repo}/${pin.commit}/${pin.folder}/${pin.name}`,
      size: pin.size,
      sha256: pin.sha256,
      contentType: 'application/octet-stream',
      label: pin.release,
      vendorPin: pin,
    })
  }
  const groups = new Map()
  for (const pin of vendor) {
    const id = `${pin.repo}|${pin.commit}|${pin.folder}`
    if (!groups.has(id)) groups.set(id, { repo: pin.repo, commit: pin.commit, folder: pin.folder, pins: [] })
    groups.get(id).pins.push(pin)
  }
  const listings = [...groups.values()].map((g) => ({
    key: `cache/${g.repo}/contents/${g.folder}@${g.commit}`,
    api: `repos/${g.repo}/contents/${g.folder}?ref=${g.commit}`,
    repo: g.repo,
    commit: g.commit,
    folder: g.folder,
    pins: g.pins,
  }))
  const tagged = new Map()
  for (const pin of vendor) {
    if (!pin.tag) throw new Error(`pin ${pin.release} names no tag in the Launcher source`)
    tagged.set(`${pin.repo}|${pin.tag}`, { repo: pin.repo, tag: pin.tag, commit: pin.commit })
  }
  const commits = [...tagged.values()].map((c) => ({
    key: `cache/${c.repo}/commits/${c.tag}`,
    api: `repos/${c.repo}/commits/${c.tag}`,
    repo: c.repo,
    tag: c.tag,
    commit: c.commit,
  }))
  const licenceGroups = [...new Map(listings.map((l) => [`${l.repo}|${l.commit}|${l.folder}`, l])).values()]
  // Each release file's repo licence, next to the file under its tag's download path (the tools and the frame-gen releases).
  const assetLicences = tools.map((pin) => ({
    dir: `cache/${pin.owner}/${pin.repo}/releases/download/${pin.tag}`,
    owner: pin.owner,
    repo: pin.repo,
    tag: pin.tag,
  }))
  return { binaries, listings, commits, licenceGroups, assetLicences, zips: zipPlan, dlssLicence }
}

// ---------- checks ----------

export function gitBlobSha1(bytes) {
  return createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex')
}

export function sha256Hex(bytes) {
  return createHash('sha256').update(bytes).digest('hex')
}

/** Refuses bytes whose size or SHA-256 is not the pin's. */
export function verifyBytes(bytes, { size, sha256 }, label) {
  if (bytes.length !== size) throw new Error(`${label}: ${bytes.length} bytes, the pin says ${size}`)
  const actual = sha256Hex(bytes)
  if (actual !== sha256) throw new Error(`${label}: SHA-256 ${actual}, the pin says ${sha256}`)
}

/** A folder listing must name every pinned DLL in it, with the pinned size and the git blob hash of the bytes. */
export function checkListing(listing, pins, blobOf) {
  if (!Array.isArray(listing)) throw new Error('listing is not a folder array')
  for (const pin of pins) {
    const entry = listing.find((e) => e.name === pin.name)
    if (!entry) throw new Error(`listing has no ${pin.name}`)
    if (entry.type !== 'file') throw new Error(`listing says ${pin.name} is a ${entry.type}`)
    if (entry.size !== pin.size) throw new Error(`listing size for ${pin.name} is ${entry.size}, the pin says ${pin.size}`)
    const blob = blobOf(pin)
    if (entry.sha !== blob) throw new Error(`listing blob for ${pin.name} is ${entry.sha}, the bytes give ${blob}`)
  }
}

/** Licence file names in the order they are preferred; the third-party notice is the fallback where a repo has no licence file. */
export function pickLicence(rootNames) {
  for (const want of ['LICENSE.txt', 'LICENSE', 'LICENSE.md', 'LICENCE.txt', '3rdpartynotice.md']) {
    if (rootNames.includes(want)) return want
  }
  return null
}

// ---------- the run ----------

/**
 * Publishes every planned object. io: { fetchBytes(url), fetchJson(apiPath), r2Get(key) -> Buffer|null,
 * r2Put(key, bytes, contentType), readRecord(), writeRecord(record) }.
 * Returns a log of what happened; throws on the first refusal (nothing unverified is written).
 */
export async function mirror({ plan, io, dryRun = false, log = () => {} }) {
  const stored = await io.r2Get(STATE_KEY)
  const state = stored ? JSON.parse(stored.toString('utf8')) : { schema: 1, objects: {} }
  const licences = io.readRecord()
  const blobs = new Map()
  const done = []

  const save = async () => {
    if (!dryRun) await io.r2Put(STATE_KEY, Buffer.from(JSON.stringify(state, null, 2)), 'application/json')
  }

  /** Writes one verified object, refusing a different object already at its key. */
  const publish = async (key, bytes, sha256, contentType, record) => {
    const known = state.objects[key]
    if (known) {
      if (known.sha256 !== sha256) throw new Error(`refused ${key}: the state records ${known.sha256}, the pin says ${sha256}`)
      done.push(['present', key, bytes.length, sha256])
      return
    }
    const existing = await io.r2Get(key)
    if (existing) {
      const actual = sha256Hex(existing)
      if (actual !== sha256) throw new Error(`refused ${key}: R2 already holds ${actual}, not the pin ${sha256}`)
      state.objects[key] = { ...record, size: bytes.length, sha256 }
      await save()
      done.push(['adopted', key, bytes.length, sha256])
      return
    }
    if (dryRun) {
      done.push(['would upload', key, bytes.length, sha256])
      return
    }
    await io.r2Put(key, bytes, contentType)
    state.objects[key] = { ...record, size: bytes.length, sha256 }
    await save()
    done.push(['uploaded', key, bytes.length, sha256])
  }

  for (const obj of plan.binaries) {
    if (state.objects[obj.key] && state.objects[obj.key].sha256 === obj.sha256) {
      blobs.set(obj.key, state.objects[obj.key].blobSha1)
      done.push(['present', obj.key, obj.size, obj.sha256])
      log(`present  ${obj.key}`)
      continue
    }
    const bytes = await io.fetchBytes(obj.upstream)
    verifyBytes(bytes, obj, obj.upstream)
    const blobSha1 = obj.vendorPin ? gitBlobSha1(bytes) : undefined
    if (obj.vendorPin) blobs.set(obj.key, blobSha1)
    await publish(obj.key, bytes, obj.sha256, obj.contentType, { upstream: obj.upstream, ...(blobSha1 ? { blobSha1 } : {}) })
    log(`${done.at(-1)[0]}  ${obj.key}`)
  }

  for (const z of plan.zips) {
    const known = state.objects[z.key]
    if (known) {
      done.push(['present', z.key, known.size, known.sha256])
      log(`present  ${z.key}`)
      continue
    }
    const bytes = await io.fetchBytes(z.upstream)
    if (bytes.length !== z.zipSize) throw new Error(`${z.upstream}: ${bytes.length} bytes, the Launcher's pin says ${z.zipSize}`)
    const zipSha = sha256Hex(bytes)
    if (zipSha !== z.zipSha256) throw new Error(`refused ${z.key}: the zip is ${zipSha}, the Launcher's archive pin says ${z.zipSha256}`)
    const inner = await io.inspectZip(bytes, z.dllName)
    if (inner.size !== z.dllSize || inner.sha256 !== z.dllSha256) {
      throw new Error(`refused ${z.key}: ${z.dllName} is ${inner.size} bytes / ${inner.sha256}, the pin says ${z.dllSize} / ${z.dllSha256}`)
    }
    if (!inner.valid || !/(^|,\s*)CN=NVIDIA Corporation(,|$)/.test(inner.signer)) {
      throw new Error(`refused ${z.key}: ${z.dllName} is not validly signed by NVIDIA Corporation (${inner.status}, ${inner.signer})`)
    }
    // The zip matches its archive pin (above). A re-run trusts the record and does not download again; an object already in R2
    // with another hash is refused by publish().
    await publish(z.key, bytes, z.zipSha256, 'application/zip', { upstream: z.upstream })
    log(`${done.at(-1)[0]}  ${z.key}`)
  }

  for (const listing of plan.listings) {
    const listingJson = await io.fetchJson(listing.api)
    const blobOf = (pin) => {
      const key = `cache/${pin.repo}/${pin.commit}/${pin.folder}/${pin.name}`
      if (!blobs.has(key)) throw new Error(`no blob hash for ${key}`)
      return blobs.get(key)
    }
    checkListing(listingJson, listing.pins, blobOf)
    const bytes = Buffer.from(JSON.stringify(listingJson))
    await publish(listing.key, bytes, sha256Hex(bytes), 'application/json; charset=utf-8', { upstream: listing.api })
    log(`${done.at(-1)[0]}  ${listing.key}`)
  }

  for (const commit of plan.commits) {
    const json = await io.fetchJson(commit.api)
    if (json?.sha !== commit.commit) throw new Error(`${commit.repo} ${commit.tag} is ${json?.sha}, the pin's commit is ${commit.commit}`)
    const bytes = Buffer.from(JSON.stringify({ sha: commit.commit }))
    await publish(commit.key, bytes, sha256Hex(bytes), 'application/json; charset=utf-8', { upstream: commit.api })
    log(`${done.at(-1)[0]}  ${commit.key}`)
  }

  /** A repo's licence (or third-party notice) at a ref, written under `dir`; its first hash is recorded and a change refused. */
  const licence = async (dir, ref, repo, label) => {
    const root = await io.fetchJson(`repos/${repo}/contents?ref=${ref}`)
    const name = pickLicence(root.map((e) => e.name))
    if (!name) {
      log(`no licence file at ${repo} ${ref}: none mirrored for ${label}`)
      return
    }
    const key = `${dir}/${name}`
    const upstream = `https://raw.githubusercontent.com/${repo}/${ref}/${name}`
    const bytes = await io.fetchBytes(upstream)
    const sha256 = sha256Hex(bytes)
    const recorded = licences[key]
    if (recorded && (recorded.sha256 !== sha256 || recorded.size !== bytes.length)) {
      throw new Error(`refused ${key}: the licence changed (recorded ${recorded.sha256}, now ${sha256})`)
    }
    if (!recorded) licences[key] = { size: bytes.length, sha256, upstream }
    await publish(key, bytes, sha256, name.endsWith('.md') ? 'text/markdown; charset=utf-8' : 'text/plain; charset=utf-8', { upstream })
    log(`${done.at(-1)[0]}  ${key}`)
  }

  for (const group of plan.licenceGroups) {
    await licence(`cache/${group.repo}/${group.commit}/${group.folder}`, group.commit, group.repo, group.folder)
  }
  for (const asset of plan.assetLicences) {
    await licence(asset.dir, asset.tag, `${asset.owner}/${asset.repo}`, asset.repo)
  }

  // The NVIDIA DLSS licence next to each zip: the same bytes already recorded for the DLSS DLLs, checked against that record.
  if (plan.zips.length && plan.dlssLicence) {
    const { recordKey, upstream } = plan.dlssLicence
    const recorded = licences[recordKey]
    if (!recorded) throw new Error(`no recorded DLSS licence at ${recordKey}; the DLSS DLLs must be mirrored first`)
    const bytes = await io.fetchBytes(upstream)
    if (sha256Hex(bytes) !== recorded.sha256) throw new Error(`refused: the DLSS licence at ${upstream} changed`)
    const folders = [...new Set(plan.zips.map((z) => z.key.slice(0, z.key.lastIndexOf('/'))))]
    for (const folder of folders) {
      await publish(`${folder}/LICENSE.txt`, bytes, recorded.sha256, 'text/plain', { upstream })
      log(`${done.at(-1)[0]}  ${folder}/LICENSE.txt`)
    }
  }

  if (!dryRun) io.writeRecord(licences)
  return { done, state }
}

// ---------- real I/O ----------

function execText(file, args, options = {}) {
  return new Promise((resolve, reject) => {
    execFile(file, args, { maxBuffer: 64 << 20, windowsHide: true, shell: process.platform === 'win32', ...options }, (err, stdout, stderr) => {
      if (err) reject(new Error(`${file} ${args.slice(0, 3).join(' ')} failed: ${String(stderr || err.message).trim().slice(0, 400)}`))
      else resolve(stdout)
    })
  })
}

const GITHUB = 'https://api.github.com/'

export function realIo({ dir, bucket = BUCKET, fetchImpl = fetch }) {
  let n = 0
  const wrangler = (args) => execText('npx', ['--yes', WRANGLER, ...args])
  return {
    async fetchBytes(url) {
      const res = await fetchImpl(url, { redirect: 'follow', headers: { 'user-agent': 'exo-cache-mirror' } })
      if (!res.ok) throw new Error(`${url} answered ${res.status}`)
      return Buffer.from(await res.arrayBuffer())
    },
    async fetchJson(apiPath) {
      const headers = { accept: 'application/vnd.github+json', 'user-agent': 'exo-cache-mirror' }
      if (process.env.GITHUB_TOKEN) headers.authorization = `Bearer ${process.env.GITHUB_TOKEN}`
      const res = await fetchImpl(GITHUB + apiPath, { headers })
      if (!res.ok) throw new Error(`api ${apiPath} answered ${res.status}`)
      return res.json()
    },
    /**
     * A zip's one DLL, read without running anything: listed and extracted by bsdtar (Windows' tar reads zip; GNU tar does not),
     * then its Authenticode signature read with Get-AuthenticodeSignature (a read of the signature, not a load of the file).
     */
    async inspectZip(bytes, dllName) {
      const tar = 'C:/Windows/System32/tar.exe'
      const zipPath = join(dir, `zip-${n++}.zip`)
      writeFileSync(zipPath, bytes)
      const outDir = join(dir, `unzip-${n++}`)
      mkdirSync(outDir)
      const names = execFileSync(tar, ['-tf', zipPath], { encoding: 'utf8' }).split(/\r?\n/).filter(Boolean)
      if (!names.includes(dllName)) throw new Error(`the zip has no ${dllName} at its top (entries: ${names.slice(0, 8).join(', ')})`)
      execFileSync(tar, ['-xf', zipPath, '-C', outDir, dllName])
      const dllPath = join(outDir, dllName)
      const dll = readFileSync(dllPath)
      const sig = JSON.parse(execFileSync('pwsh', [
        '-NoProfile', '-NonInteractive', '-Command',
        `$s = Get-AuthenticodeSignature -LiteralPath '${dllPath}'; [pscustomobject]@{ status = [string]$s.Status; signer = [string]$s.SignerCertificate.Subject } | ConvertTo-Json -Compress`,
      ], { encoding: 'utf8' }))
      return { size: dll.length, sha256: sha256Hex(dll), status: sig.status, valid: sig.status === 'Valid', signer: sig.signer ?? '' }
    },
    async r2Get(key) {
      const file = join(dir, `get-${n++}`)
      try {
        await wrangler(['r2', 'object', 'get', `${bucket}/${key}`, '--remote', '--file', file])
      } catch (err) {
        if (/does not exist|not found|10007|no such key/i.test(err.message)) return null
        throw err
      }
      const bytes = readFileSync(file)
      rmSync(file, { force: true })
      return bytes
    },
    async r2Put(key, bytes, contentType) {
      if (!key.startsWith('cache/') && key !== STATE_KEY) throw new Error(`refused to write ${key}: outside the mirror's keys`)
      const file = join(dir, `put-${n++}`)
      writeFileSync(file, bytes)
      try {
        // The Worker sets each served type itself (contentTypeOf); R2's copy is the bare media type, because the shell joins
        // arguments unquoted on Windows and a "; charset" would split there.
        await wrangler(['r2', 'object', 'put', `${bucket}/${key}`, '--remote', '--file', file, '--content-type', contentType.split(';')[0].trim()])
      } finally {
        rmSync(file, { force: true })
      }
    },
    readRecord() {
      return existsSync(LICENCE_RECORD) ? JSON.parse(readFileSync(LICENCE_RECORD, 'utf8')) : {}
    },
    writeRecord(record) {
      writeFileSync(LICENCE_RECORD, JSON.stringify(record, null, 2) + '\n')
    },
  }
}

// ---------- command line ----------

async function main(argv) {
  const flag = (name, fallback) => {
    const i = argv.indexOf(name)
    return i >= 0 ? argv[i + 1] : fallback
  }
  const launcher = flag('--launcher')
  if (!launcher) throw new Error('usage: node cache-mirror.mjs --launcher <Launcher checkout> [--rev <branch>] [--dry-run]')
  const rev = flag('--rev', 'fix/3.25-off-github')
  const dryRun = argv.includes('--dry-run')
  const head = (await execText('git', ['-C', launcher, 'rev-parse', rev])).trim()
  const pins = readPins(launcher, rev)
  console.log(`pins from ${launcher} at ${rev} (${head}): ${pins.tools.length} tool files, ${pins.vendor.length} vendor DLLs`)
  const plan = planObjects(pins)
  const dir = mkdtempSync(join(tmpdir(), 'exo-cache-mirror-'))
  try {
    const { done } = await mirror({ plan, io: realIo({ dir }), dryRun, log: (line) => console.log(line) })
    console.log(`\n${done.length} objects:`)
    for (const [status, key, size, sha] of done) console.log(`${status.padEnd(13)} ${key} ${size} ${sha}`)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main(process.argv.slice(2)).catch((err) => {
    console.error(`cache-mirror: ${err.message}`)
    process.exit(1)
  })
}

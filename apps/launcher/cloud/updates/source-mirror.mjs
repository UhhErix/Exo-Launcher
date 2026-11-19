#!/usr/bin/env node
/**
 * The corresponding source of each GPL binary Exo ships (Nile, Legendary, gogdl, OptiScaler), served at getexo.app/source
 * (exo-site src/source.js). Reads the tool pins from the Launcher source (cache-mirror.mjs readPins), takes each pin's tag
 * commit and its source archive from GitHub, checks the archive really holds the GPL licence, and writes it to R2 bucket
 * exo-source as source/<name>/<name>-<version>.tar.gz, then the index the page reads (source/index.json).
 *
 * Hashes are recorded the first time an archive is seen (_source/manifest.json in exo-source). Later runs refuse an
 * archive whose bytes changed. GitHub's tag archives are generated, so a change would be refused, never overwritten.
 *
 *   node source-mirror.mjs --launcher <Launcher checkout> [--rev 287b0af6]
 *
 * How it could fail: an archive without the GPL licence is published; an archive changes without notice; the index names a
 * file that is not in the bucket; a tag whose commit is not what the Launcher's binary pins were taken from is published.
 */
import { execFile, execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { gunzipSync } from 'node:zlib'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { readPins } from './cache-mirror.mjs'

export const SOURCE_BUCKET = 'exo-source'
export const MANIFEST_KEY = '_source/manifest.json'
const WRANGLER = 'wrangler@4.158.0'
const GPL3 = ['GNU GENERAL PUBLIC LICENSE', 'Version 3, 29 June 2007']
const TITLES = {
  nile: 'Nile (Amazon Games)',
  legendary: 'Legendary (Epic Games)',
  'heroic-gogdl': 'gogdl (GOG)',
  optiscaler: 'OptiScaler',
}

/** The source archive for a tool pin: its name, version, tag and where GitHub serves it. */
export function sourceFor(pin) {
  const name = pin.repo.toLowerCase()
  if (!TITLES[name]) throw new Error(`no title for ${pin.repo}; add it to TITLES`)
  const version = pin.tag.replace(/^v/, '')
  if (!/^\d{1,4}\.\d{1,4}\.\d{1,4}$/.test(version)) throw new Error(`${pin.repo} tag ${pin.tag} is not a version`)
  const file = `${name}-${version}.tar.gz`
  return {
    name,
    title: TITLES[name],
    version,
    file,
    key: `source/${name}/${file}`,
    owner: pin.owner,
    repo: pin.repo,
    tag: pin.tag,
    upstream: `https://codeload.github.com/${pin.owner}/${pin.repo}/tar.gz/refs/tags/${pin.tag}`,
    commitApi: `https://api.github.com/repos/${pin.owner}/${pin.repo}/commits/${pin.tag}`,
  }
}

/** The archive must be a gzip tar that holds the repo's GPL-3.0 licence text. */
export function checkArchive(bytes, label) {
  let tar
  try {
    tar = gunzipSync(bytes)
  } catch {
    throw new Error(`${label}: not a gzip archive`)
  }
  const text = tar.toString('latin1')
  if (!text.includes('LICENSE')) throw new Error(`${label}: holds no LICENSE file`)
  for (const line of GPL3) {
    if (!text.includes(line)) throw new Error(`${label}: its licence is not GPL-3.0 (missing "${line}")`)
  }
  return true
}

const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

/**
 * The index with `added` add-ons put into `existing`'s: an add-on this run does not list stays as it was (the Exo add-ons
 * that addon-source.mjs publishes are not this script's to drop), a release already listed must have the same file bytes
 * (a changed archive is refused, never replaced), and a new release is added. Either side may be null or empty.
 */
export function mergeAddons(existing, added) {
  const merged = new Map((existing?.addons ?? []).map((addon) => [addon.name, { ...addon, releases: [...addon.releases] }]))
  for (const addon of added) {
    const kept = merged.get(addon.name) ?? { name: addon.name, releases: [] }
    for (const release of addon.releases) {
      const known = kept.releases.find((r) => r.version === release.version)
      if (known && known.sha256 !== release.sha256) {
        throw new Error(`refused ${addon.name} ${release.version}: the index lists ${known.sha256}, this archive is ${release.sha256}`)
      }
      if (!known) kept.releases.push(release)
    }
    merged.set(addon.name, { name: addon.name, title: addon.title, license: addon.license, releases: kept.releases })
  }
  return { addons: [...merged.values()] }
}

/** Runs the plan. io: { fetchBytes(url), fetchJson(url), get(key) -> Buffer|null, put(key, bytes, type) }. */
export async function publishSources({ sources, io, log = () => {} }) {
  const manifest = (await io.get(MANIFEST_KEY))
    ? JSON.parse((await io.get(MANIFEST_KEY)).toString('utf8'))
    : { schema: 1, archives: {} }
  const releases = []
  for (const src of sources) {
    const commit = await io.fetchJson(src.commitApi)
    const bytes = await io.fetchBytes(src.upstream)
    checkArchive(bytes, src.key)
    const digest = sha256(bytes)
    const known = manifest.archives[src.key]
    if (known) {
      if (known.sha256 !== digest) throw new Error(`refused ${src.key}: the archive changed (recorded ${known.sha256}, now ${digest})`)
      log(`present   ${src.key}`)
    } else {
      const existing = await io.get(src.key)
      if (existing && sha256(existing) !== digest) throw new Error(`refused ${src.key}: R2 holds a different archive`)
      if (!existing) await io.put(src.key, bytes, 'application/gzip')
      manifest.archives[src.key] = { commit: commit.sha, upstream: src.upstream, bytes: bytes.length, sha256: digest }
      await io.put(MANIFEST_KEY, Buffer.from(JSON.stringify(manifest, null, 2)), 'application/json')
      log(`uploaded  ${src.key} ${bytes.length} ${digest}`)
    }
    releases.push({ src, bytes: manifest.archives[src.key].bytes, sha256: manifest.archives[src.key].sha256 })
  }
  // The index lists each addon's releases; every entry was checked above, so the page never links an unchecked file.
  const addons = new Map()
  for (const { src, bytes, sha256: digest } of releases) {
    if (!addons.has(src.name)) addons.set(src.name, { name: src.name, title: src.title, license: 'GPL-3.0', releases: [] })
    addons.get(src.name).releases.push({ version: src.version, file: src.file, bytes, sha256: digest })
  }
  const live = await io.get('source/index.json')
  const index = Buffer.from(JSON.stringify(mergeAddons(live ? JSON.parse(live.toString('utf8')) : null, [...addons.values()]), null, 2))
  await io.put('source/index.json', index, 'application/json')
  log(`uploaded  source/index.json ${index.length}`)
  return { index: JSON.parse(index.toString('utf8')), manifest }
}

// ---------- real I/O ----------

function execText(file, args, options = {}) {
  return new Promise((resolve, reject) => {
    execFile(file, args, { maxBuffer: 64 << 20, windowsHide: true, shell: process.platform === 'win32', ...options }, (err, stdout, stderr) => {
      if (err) reject(new Error(`${file} ${args.slice(0, 3).join(' ')} failed: ${String(stderr || err.message).trim().slice(0, 300)}`))
      else resolve(stdout)
    })
  })
}

export function realIo({ dir, fetchImpl = fetch }) {
  let n = 0
  const wrangler = (args) => execText('npx', ['--yes', WRANGLER, ...args])
  return {
    async fetchBytes(url) {
      const res = await fetchImpl(url, { redirect: 'follow', headers: { 'user-agent': 'exo-source-mirror' } })
      if (!res.ok) throw new Error(`${url} answered ${res.status}`)
      return Buffer.from(await res.arrayBuffer())
    },
    async fetchJson(url) {
      const headers = { accept: 'application/vnd.github+json', 'user-agent': 'exo-source-mirror' }
      if (process.env.GITHUB_TOKEN) headers.authorization = `Bearer ${process.env.GITHUB_TOKEN}`
      const res = await fetchImpl(url, { headers })
      if (!res.ok) throw new Error(`${url} answered ${res.status}`)
      return res.json()
    },
    async get(key) {
      const file = join(dir, `get-${n++}`)
      try {
        await wrangler(['r2', 'object', 'get', `${SOURCE_BUCKET}/${key}`, '--remote', '--file', file])
      } catch (err) {
        if (/does not exist|not found|10007|no such key/i.test(err.message)) return null
        throw err
      }
      const bytes = readFileSync(file)
      rmSync(file, { force: true })
      return bytes
    },
    async put(key, bytes, type) {
      if (!key.startsWith('source/') && key !== MANIFEST_KEY) throw new Error(`refused to write ${key} to ${SOURCE_BUCKET}`)
      const file = join(dir, `put-${n++}`)
      writeFileSync(file, bytes)
      try {
        // Bare media type: the shell joins arguments unquoted on Windows, so a "; charset" would split there.
        await wrangler(['r2', 'object', 'put', `${SOURCE_BUCKET}/${key}`, '--remote', '--file', file, '--content-type', type.split(';')[0].trim()])
      } finally {
        rmSync(file, { force: true })
      }
    },
  }
}

async function main(argv) {
  const flag = (name, fallback) => {
    const i = argv.indexOf(name)
    return i >= 0 ? argv[i + 1] : fallback
  }
  const launcher = flag('--launcher')
  if (!launcher) throw new Error('usage: node source-mirror.mjs --launcher <Launcher checkout> [--rev <rev>]')
  const rev = flag('--rev', 'release/3.25.0')
  const { tools } = readPins(launcher, rev)
  // Only the four GPL binaries: the sources Exo must offer. Other pins (MFG Unlocker, the excluded DLSSG SM86) are not GPL sources here.
  const gpl = new Set(['nile', 'legendary', 'heroic-gogdl', 'optiscaler'])
  const sources = tools.filter((pin) => gpl.has(pin.repo.toLowerCase())).map(sourceFor)
  if (sources.length !== 4) throw new Error(`expected 4 GPL pins, found ${sources.length}`)
  console.log(`sources from ${launcher} at ${rev}: ${sources.map((s) => `${s.name} ${s.version}`).join(', ')}`)
  const dir = mkdtempSync(join(tmpdir(), 'exo-source-mirror-'))
  try {
    const { index } = await publishSources({ sources, io: realIo({ dir }), log: (line) => console.log(line) })
    console.log(JSON.stringify(index, null, 2))
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main(process.argv.slice(2)).catch((err) => {
    console.error(`source-mirror: ${err.message}`)
    process.exit(1)
  })
}

#!/usr/bin/env node
/**
 * The corresponding source of the copyleft add-ons Exo ships itself, for getexo.app/source (exo-site src/source.js): Exo Mouse
 * (AGPL-3.0-or-later, `mouse/`), Exo Shaders (GPL-3.0-or-later, `shaders/`: Exo's fork of SCSKiller with its native source),
 * Exo Shield (GPL-3.0, the extension folder shipped in the Launcher) and OBS Studio's game capture hook (GPL-2.0-or-later,
 * unchanged OBS files that Exo Capture runs). source-mirror.mjs does the same for the third-party GPL tools (Nile and the
 * others); this script is for Exo's own and the bundled OBS source. Exo Capture itself is not here: it is under its own
 * unpublished terms (capture/LICENSE).
 *
 * Archives are <name>-<version>.tar.gz with one top folder <name>-<version>/, where <version> is the Exo Launcher release that
 * ships them. They are made from committed trees only (git archive: no node_modules, no dist, no working-tree changes), so
 * the same commits give the same bytes. Each holds its licence file, and EXO-SOURCE.txt names the commit it came from.
 *
 *   node addon-source.mjs build --launcher <apps/launcher of a checkout> --rev <commit>
 *                               [--out artifacts/source]       local only: archives, entries.json, SHA256SUMS
 *   node addon-source.mjs fetch-obs --tag 32.2.2 [--out artifacts/source]
 *                               the one step that reads from the network: OBS Studio's tag archive from its upstream
 *   node addon-source.mjs upload --dir artifacts/source [--yes]
 *                               prints the plan; with --yes puts the archives and the merged index into R2 bucket exo-source
 *
 * How it could fail: an archive without its licence file is published; the same version is published twice with different
 * bytes; the index loses another add-on's entries (the merge keeps them, source-mirror's too); the archive holds a
 * working-tree change that is not committed; a name or version outside the shape exo-site's source.js accepts makes the
 * whole index refused (503), so the shape is checked here first.
 */
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { gunzipSync, gzipSync } from 'node:zlib'
import { mergeAddons, realIo, SOURCE_BUCKET } from './source-mirror.mjs'

const NAME = /^[a-z0-9-]{1,40}$/
const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,4}(?:-[0-9A-Za-z.]{1,20})?$/
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex')

/**
 * The add-ons built from git trees, all folders of the launcher's own tree at one commit. `tree` is a git tree-ish (its path
 * starts with ./, so it is read from `launcher`, apps/launcher in the monorepo); `licenceFile` is the licence in it and
 * `marker` a line of its text.
 */
export function treeAddons({ launcher, rev }) {
  return [
    { name: 'exo-mouse', title: 'Exo Mouse', license: 'AGPL-3.0-or-later', repo: launcher, tree: `${rev}:./mouse`, licenceFile: 'LICENSE', marker: 'GNU AFFERO GENERAL PUBLIC LICENSE' },
    { name: 'exo-shaders', title: 'Exo Shaders', license: 'GPL-3.0-or-later', repo: launcher, tree: `${rev}:./shaders`, licenceFile: 'LICENSE', marker: 'GNU GENERAL PUBLIC LICENSE' },
    { name: 'exo-shield', title: 'Exo Shield', license: 'GPL-3.0', repo: launcher, tree: `${rev}:./ExoLauncher/Assets/Extensions/exo-shield`, licenceFile: 'LICENSE.txt', marker: 'GNU GENERAL PUBLIC LICENSE' },
  ]
}

const git = (repo, args, options = {}) =>
  execFileSync('git', ['-C', repo, '-c', 'core.autocrlf=false', ...args], { maxBuffer: 1 << 30, windowsHide: true, ...options })

/** The commit a rev or sha names, in full. */
export function commitOf(repo, rev) {
  return git(repo, ['rev-parse', '--verify', `${rev}^{commit}`], { encoding: 'utf8' }).trim()
}

/** A gzip with a fixed header (no time, OS byte 3), so the same tar always gives the same file. */
export function gzipFixed(tar) {
  const out = gzipSync(tar, { level: 9 })
  out.fill(0, 4, 8)
  out[9] = 3
  return out
}

/** Whether the tar bytes hold `path` and the licence `marker` text. */
export function checkArchive(gz, { path, marker }, label) {
  let tar
  try {
    tar = gunzipSync(gz).toString('latin1')
  } catch {
    throw new Error(`${label}: not a gzip archive`)
  }
  if (!tar.includes(path)) throw new Error(`${label}: holds no ${path}`)
  if (!tar.includes(marker)) throw new Error(`${label}: its licence file lacks "${marker}"`)
}

/** One add-on's archive from its committed tree: { name, version, file, bytes, sha256, commit, gz }. */
export function buildTreeArchive(def, version, commit) {
  if (!NAME.test(def.name) || !VERSION.test(version)) throw new Error(`${def.name} ${version} is outside the shape source.js accepts`)
  const prefix = `${def.name}-${version}/`
  const note = [
    `${def.title} source, for Exo Launcher ${version}.`,
    `Licence: ${def.license} (${def.licenceFile}).`,
    `Commit: ${commit}`,
    `Tree: ${def.tree}`,
    '',
  ].join('\n')
  // A tree-ish like <commit>:mouse has no time of its own, so git would stamp the build time: the commit's time is used.
  const time = git(def.repo, ['show', '-s', '--format=%cI', commit], { encoding: 'utf8' }).trim()
  const tar = git(def.repo, ['archive', '--format=tar', `--mtime=${time}`, `--prefix=${prefix}`, `--add-virtual-file=${prefix}EXO-SOURCE.txt:${note}`, def.tree])
  const gz = gzipFixed(tar)
  const file = `${def.name}-${version}.tar.gz`
  checkArchive(gz, { path: `${prefix}${def.licenceFile}`, marker: def.marker }, file)
  return { name: def.name, title: def.title, license: def.license, version, file, bytes: gz.length, sha256: sha256(gz), commit, gz }
}

/** The index entries for built archives, in the shape of exo-site's source.js. */
export function entriesOf(archives) {
  return archives.map((a) => ({
    name: a.name,
    title: a.title,
    license: a.license,
    releases: [{ version: a.version, file: a.file, bytes: a.bytes, sha256: a.sha256 }],
  }))
}

/** OBS Studio's own tag archive, for the graphics hook files Exo Capture ships unchanged (capture/third_party/obs/README.md). */
export async function fetchObs(tag, fetchImpl = fetch) {
  const version = tag
  if (!VERSION.test(version)) throw new Error(`OBS tag ${tag} is not a version`)
  const url = `https://codeload.github.com/obsproject/obs-studio/tar.gz/refs/tags/${tag}`
  const res = await fetchImpl(url, { redirect: 'follow', headers: { 'user-agent': 'exo-source-mirror' } })
  if (!res.ok) throw new Error(`${url} answered ${res.status}`)
  const gz = Buffer.from(await res.arrayBuffer())
  const file = `obs-studio-${version}.tar.gz`
  // GitHub's tag archive: the repo's COPYING (GPL-2.0) and the win-capture plugin that holds the hook.
  checkArchive(gz, { path: 'plugins/win-capture/', marker: 'GNU GENERAL PUBLIC LICENSE' }, file)
  checkArchive(gz, { path: 'COPYING', marker: 'Version 2, June 1991' }, file)
  return {
    name: 'obs-studio', title: "OBS Studio (the game capture hook Exo Capture runs)", license: 'GPL-2.0-or-later',
    version, file, bytes: gz.length, sha256: sha256(gz), commit: null, gz, upstream: url,
  }
}

// ---------- the local output ----------

function writeOut(out, archives) {
  mkdirSync(out, { recursive: true })
  for (const a of archives) writeFileSync(join(out, a.file), a.gz)
  const path = join(out, 'entries.json')
  const previous = existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : { addons: [] }
  const entries = mergeAddons(previous, entriesOf(archives))
  writeFileSync(path, JSON.stringify(entries, null, 2) + '\n')
  const sums = entries.addons.flatMap((addon) => addon.releases.map((r) => `${r.sha256}  ${r.file}`))
  writeFileSync(join(out, 'SHA256SUMS'), sums.join('\n') + '\n')
  return entries
}

// ---------- the upload (R2 bucket exo-source) ----------

/** What an upload would do, and (with io) does: each archive, then the merged index last. io: { get(key), put(key, bytes, type) }. */
export async function uploadSources({ dir, io, log = () => {} }) {
  const entries = JSON.parse(readFileSync(join(dir, 'entries.json'), 'utf8'))
  for (const addon of entries.addons) {
    for (const release of addon.releases) {
      const bytes = readFileSync(join(dir, release.file))
      if (sha256(bytes) !== release.sha256 || bytes.length !== release.bytes) throw new Error(`${release.file} is not what entries.json lists`)
      const key = `source/${addon.name}/${release.file}`
      const existing = await io.get(key)
      if (existing && sha256(existing) !== release.sha256) throw new Error(`refused ${key}: the bucket holds a different archive`)
      if (existing) log(`present   ${key}`)
      else {
        await io.put(key, bytes, 'application/gzip')
        log(`uploaded  ${key} ${bytes.length} ${release.sha256}`)
      }
    }
  }
  // The index last, so it only ever names archives that are there; the add-ons already listed (the tools) stay.
  const live = await io.get('source/index.json')
  const index = mergeAddons(live ? JSON.parse(live.toString('utf8')) : null, entries.addons)
  await io.put('source/index.json', Buffer.from(JSON.stringify(index, null, 2)), 'application/json')
  log(`uploaded  source/index.json (${index.addons.map((a) => a.name).join(', ')})`)
  return index
}

async function main(argv) {
  const [command, ...rest] = argv
  const flag = (name, fallback) => {
    const i = rest.indexOf(name)
    return i >= 0 ? rest[i + 1] : fallback
  }
  const need = (name) => {
    const value = flag(name)
    if (!value) throw new Error(`${command} needs ${name}`)
    return value
  }
  if (command === 'build') {
    const launcher = need('--launcher')
    const commit = commitOf(launcher, need('--rev'))
    const version = git(launcher, ['show', `${commit}:./VERSION`], { encoding: 'utf8' }).trim()
    const archives = treeAddons({ launcher, rev: commit }).map((def) => buildTreeArchive(def, version, commit))
    const entries = writeOut(flag('--out', 'artifacts/source'), archives)
    for (const a of archives) console.log(`${a.file}  ${a.bytes} bytes  ${a.sha256}  from ${a.commit}`)
    console.log(JSON.stringify(entries, null, 2))
  } else if (command === 'fetch-obs') {
    const a = await fetchObs(need('--tag'))
    writeOut(flag('--out', 'artifacts/source'), [a])
    console.log(`${a.file}  ${a.bytes} bytes  ${a.sha256}  from ${a.upstream}`)
  } else if (command === 'upload') {
    const dir = need('--dir')
    if (!rest.includes('--yes')) {
      const entries = JSON.parse(readFileSync(join(dir, 'entries.json'), 'utf8'))
      for (const addon of entries.addons) for (const r of addon.releases) console.log(`would put ${SOURCE_BUCKET}/source/${addon.name}/${r.file} (${r.bytes} bytes, ${r.sha256})`)
      console.log(`would put ${SOURCE_BUCKET}/source/index.json (merged with the live index). Nothing uploaded; add --yes to do it.`)
      return
    }
    const tmp = mkdtempSync(join(tmpdir(), 'exo-addon-source-'))
    try {
      await uploadSources({ dir, io: realIo({ dir: tmp }), log: (line) => console.log(line) })
    } finally {
      rmSync(tmp, { recursive: true, force: true })
    }
  } else {
    throw new Error('usage: node addon-source.mjs build|fetch-obs|upload (see the header)')
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  main(process.argv.slice(2)).catch((err) => {
    console.error(`addon-source: ${err.message}`)
    process.exit(1)
  })
}

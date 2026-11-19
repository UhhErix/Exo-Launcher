/**
 * addon-source.mjs and the index merge in source-mirror.mjs. Nothing is uploaded or downloaded: a throwaway git repo and a
 * stand-in bucket.
 *
 * How it could fail: an archive differs between two builds of the same commit; a tree without its licence is published; a
 * changed archive replaces a published one; the index loses the entries another script wrote; an entry has a shape that
 * exo-site's source.js would refuse (and with it the whole page).
 *   node --test test
 */
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'
import { gunzipSync } from 'node:zlib'
import { buildTreeArchive, commitOf, entriesOf, launcherRepo, treeAddons, uploadSources } from '../addon-source.mjs'
import { mergeAddons } from '../source-mirror.mjs'

const git = (repo, ...args) => execFileSync('git', ['-C', repo, ...args], { encoding: 'utf8' })

function repoWith(files) {
  const repo = mkdtempSync(join(tmpdir(), 'exo-addon-source-test-'))
  git(repo, 'init', '-q')
  git(repo, 'config', 'user.email', 'test@example.invalid')
  git(repo, 'config', 'user.name', 'test')
  for (const [path, text] of Object.entries(files)) {
    mkdirSync(join(repo, path, '..'), { recursive: true })
    writeFileSync(join(repo, path), text)
  }
  git(repo, 'add', '-A')
  git(repo, 'commit', '-q', '-m', 'one')
  return repo
}

const def = (repo, commit) => ({
  name: 'exo-mouse', title: 'Exo Mouse', license: 'AGPL-3.0-or-later', repo, tree: `${commit}:mouse`,
  licenceFile: 'LICENSE', marker: 'GNU AFFERO GENERAL PUBLIC LICENSE',
})

test('the same commit gives the same archive, with the licence and the commit named inside', () => {
  const repo = repoWith({ 'mouse/LICENSE': 'GNU AFFERO GENERAL PUBLIC LICENSE\n', 'mouse/src/a.ts': 'export {}\n', 'other/x': 'not in the archive\n' })
  try {
    const commit = commitOf(repo, 'HEAD')
    const a = buildTreeArchive(def(repo, commit), '3.25.0', commit)
    const b = buildTreeArchive(def(repo, commit), '3.25.0', commit)
    assert.equal(a.sha256, b.sha256)
    assert.equal(a.file, 'exo-mouse-3.25.0.tar.gz')
    const tar = gunzipSync(a.gz).toString('latin1')
    assert.ok(tar.includes(`Commit: ${commit}`) && tar.includes('exo-mouse-3.25.0/LICENSE'))
    assert.ok(!tar.includes('other/x'))
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('in a monorepo every add-on comes from the launcher folder, Exo Shaders from its shaders folder', () => {
  const repo = repoWith({
    'apps/launcher/VERSION': '3.26.0\n',
    'apps/launcher/mouse/LICENSE': 'GNU AFFERO GENERAL PUBLIC LICENSE\n',
    'apps/launcher/shaders/LICENSE': 'GNU GENERAL PUBLIC LICENSE\n',
    'apps/launcher/shaders/src/a.cs': 'class A {}\n',
    'apps/launcher/ExoLauncher/Assets/Extensions/exo-shield/LICENSE.txt': 'GNU GENERAL PUBLIC LICENSE\n',
  })
  try {
    const where = launcherRepo(join(repo, 'apps', 'launcher'))
    assert.equal(where.prefix, 'apps/launcher/')
    const commit = commitOf(where.repo, 'HEAD')
    const archives = treeAddons({ ...where, rev: commit }).map((d) => buildTreeArchive(d, '3.26.0', commit))
    assert.deepEqual(archives.map((a) => a.name), ['exo-mouse', 'exo-shaders', 'exo-shield'])
    const shaders = gunzipSync(archives[1].gz).toString('latin1')
    assert.ok(shaders.includes('exo-shaders-3.26.0/src/a.cs') && !shaders.includes('mouse/LICENSE'))
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('a tree without its licence file is refused', () => {
  const repo = repoWith({ 'mouse/src/a.ts': 'export {}\n' })
  try {
    const commit = commitOf(repo, 'HEAD')
    assert.throws(() => buildTreeArchive(def(repo, commit), '3.25.0', commit), /holds no exo-mouse-3.25.0\/LICENSE/)
  } finally {
    rmSync(repo, { recursive: true, force: true })
  }
})

test('a name or version outside what source.js accepts is refused before anything is built', () => {
  assert.throws(() => buildTreeArchive({ ...def('.', 'HEAD'), name: 'Exo Mouse' }, '3.25.0', 'x'), /outside the shape/)
  assert.throws(() => buildTreeArchive(def('.', 'HEAD'), '3.25', 'x'), /outside the shape/)
})

const release = (version, sha) => ({ version, file: `x-${version}.tar.gz`, bytes: 1, sha256: sha })

test('the merge keeps add-ons it was not given, adds releases, and refuses a changed archive', () => {
  const live = { addons: [
    { name: 'nile', title: 'Nile', license: 'GPL-3.0', releases: [release('1.2.0', 'a'.repeat(64))] },
    { name: 'exo-mouse', title: 'Exo Mouse', license: 'AGPL-3.0-or-later', releases: [release('3.24.4', 'b'.repeat(64))] },
  ] }
  const merged = mergeAddons(live, [{ name: 'exo-mouse', title: 'Exo Mouse', license: 'AGPL-3.0-or-later', releases: [release('3.25.0', 'c'.repeat(64))] }])
  assert.deepEqual(merged.addons.map((a) => a.name), ['nile', 'exo-mouse'])
  assert.deepEqual(merged.addons[1].releases.map((r) => r.version), ['3.24.4', '3.25.0'])
  assert.throws(() => mergeAddons(live, [{ name: 'exo-mouse', title: 'x', license: 'y', releases: [release('3.24.4', 'd'.repeat(64))] }]), /refused exo-mouse 3.24.4/)
  assert.deepEqual(mergeAddons(null, entriesOf([{ name: 'a', title: 'A', license: 'L', version: '1.0.0', file: 'a-1.0.0.tar.gz', bytes: 2, sha256: 'e'.repeat(64) }])).addons[0].releases[0].file, 'a-1.0.0.tar.gz')
})

test('upload puts the archives first and the merged index last, and refuses a different archive already there', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'exo-addon-upload-test-'))
  try {
    const bytes = Buffer.from('archive')
    const sha = (await import('node:crypto')).createHash('sha256').update(bytes).digest('hex')
    writeFileSync(join(dir, 'exo-mouse-3.25.0.tar.gz'), bytes)
    writeFileSync(join(dir, 'entries.json'), JSON.stringify({ addons: [{ name: 'exo-mouse', title: 'Exo Mouse', license: 'AGPL-3.0-or-later', releases: [{ version: '3.25.0', file: 'exo-mouse-3.25.0.tar.gz', bytes: bytes.length, sha256: sha }] }] }))
    const store = new Map([['source/index.json', Buffer.from(JSON.stringify({ addons: [{ name: 'nile', title: 'Nile', license: 'GPL-3.0', releases: [release('1.2.0', 'a'.repeat(64))] }] }))]])
    const order = []
    const io = { get: async (key) => store.get(key) ?? null, put: async (key, value) => { order.push(key); store.set(key, value) } }
    const index = await uploadSources({ dir, io })
    assert.deepEqual(order, ['source/exo-mouse/exo-mouse-3.25.0.tar.gz', 'source/index.json'])
    assert.deepEqual(index.addons.map((a) => a.name), ['nile', 'exo-mouse'])
    store.set('source/exo-mouse/exo-mouse-3.25.0.tar.gz', Buffer.from('different'))
    await assert.rejects(uploadSources({ dir, io }), /bucket holds a different archive/)
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

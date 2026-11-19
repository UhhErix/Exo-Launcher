/**
 * publish-release.ps1 -Payload (file-level updates), run for real against stand-ins: R2 is a folder
 * (fixtures/fake-wrangler.mjs) and the update address is the real Worker over that folder (fixtures/local-service.mjs).
 * Ways it could fail:
 * - the file list goes up before its files do, or the newest-version answer before either, so an app starts a
 *   file-level update whose files are not there;
 * - a file the address already serves is uploaded again, so every release uploads the whole app;
 * - a file that is served with other bytes leaves its list published, so apps download it, fail and fall back for nothing;
 * - a problem with the file-level part stops a release whose installer is fine;
 * - a payload folder that is not what its list says is uploaded anyway.
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { gunzipSync } from 'node:zlib'
import { serve } from './fixtures/local-service.mjs'

const script = fileURLToPath(new URL('../publish-release.ps1', import.meta.url))
const wrangler = fileURLToPath(new URL('./fixtures/fake-wrangler.mjs', import.meta.url))
const SETUP = 'ExoCinema-Setup.exe'
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex')

const ONE = {
  'ExoCinema.exe': 'host',
  'ExoCinema.dll': 'app code, version one',
  'libmpv-2.dll': 'm'.repeat(5000),
  'wwwroot/index.html': '<html>one</html>',
  'wwwroot/copy.html': '<html>one</html>', // the same bytes at a second path
}
const TWO = { ...ONE, 'ExoCinema.dll': 'app code, version two!' }

/** A bucket, and a release folder per tag: the three release files, the app folder (payload) and its file list beside them. */
function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'exo-publish-payload-'))
  t.after(() => rmSync(root, { recursive: true, force: true }))
  const bucket = join(root, 'bucket')
  mkdirSync(bucket)
  const release = (tag, files) => {
    const dir = join(root, tag, 'artifacts')
    const payload = join(root, tag, 'payload')
    mkdirSync(dir, { recursive: true })
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(payload, path)), { recursive: true })
      writeFileSync(join(payload, path), text)
    }
    const list = JSON.stringify({ schema: 1, version: tag.slice(1),
      files: Object.entries(files).map(([path, text]) => ({ path, size: Buffer.byteLength(text), sha256: sha(text) })) })
    writeFileSync(join(payload, 'payload-manifest.json'), list)
    writeFileSync(join(dir, 'payload-manifest.json'), list)
    writeFileSync(join(dir, SETUP), `installer ${tag}`)
    writeFileSync(join(dir, `${SETUP}.sha256`), `${sha(`installer ${tag}`)} *${SETUP}\n`)
    writeFileSync(join(dir, 'whats-new.json'), JSON.stringify({ version: tag.slice(1), items: [] }))
    return { tag, dir, payload, list }
  }
  return { root, bucket, log: join(root, 'r2.log'), release }
}

async function service(t, fx, options) {
  const s = await serve(fx.bucket, options)
  t.after(() => s.close())
  return s
}

function publish(fx, base, rel, extra = ['-Publish']) {
  return new Promise((resolve) => {
    const child = spawn('pwsh', ['-NoProfile', '-File', script, '-App', 'cinema', '-Tag', rel.tag, '-Dir', rel.dir, '-Payload', rel.payload,
      '-NoAnnounce', '-Tries', '2', '-RetrySeconds', '0', '-TestRun', ...extra], {
      env: { ...process.env, EXO_TEST_WRANGLER: wrangler, EXO_TEST_UPDATES_BASE: base, FAKE_R2_DIR: fx.bucket, FAKE_R2_LOG: fx.log },
    })
    let out = ''
    child.stdout.on('data', (d) => { out += d })
    child.stderr.on('data', (d) => { out += d })
    child.on('close', (code) => resolve({ code, out }))
  })
}

const puts = (fx) => (existsSync(fx.log) ? readFileSync(fx.log, 'utf8').trim().split('\n') : []).filter((l) => l.startsWith('put ')).map((l) => l.slice(4))
const fileKey = (text) => `cinema/files/${sha(text)}.gz`

test('a release with -Payload: the installer first, then each distinct file by hash, then the file list, latest.json last', { timeout: 180_000 }, async (t) => {
  const fx = fixture(t)
  const s = await service(t, fx)
  const one = fx.release('v9.9.1', ONE)
  const r = await publish(fx, s.url, one)
  assert.equal(r.code, 0, r.out)
  const order = puts(fx)
  const distinct = [...new Set(Object.values(ONE))]
  assert.deepEqual(order.slice(0, 3), [`cinema/v9.9.1/${SETUP}`, `cinema/v9.9.1/${SETUP}.sha256`, 'cinema/v9.9.1/whats-new.json'])
  assert.deepEqual(order.slice(3, 3 + distinct.length).sort(), distinct.map(fileKey).sort(), 'each distinct file once, the twin path not twice')
  assert.deepEqual(order.slice(3 + distinct.length), ['cinema/v9.9.1/payload-manifest.json', 'cinema/latest.json'])
  for (const text of distinct) assert.equal(gunzipSync(readFileSync(join(fx.bucket, ...fileKey(text).split('/')))).toString(), text)
  assert.equal(await (await fetch(`${s.url}/v1/cinema/payload/v9.9.1`)).text(), one.list)
  assert.match(r.out, /File-level update: published/)
})

test('the next release uploads only the file that changed', { timeout: 240_000 }, async (t) => {
  const fx = fixture(t)
  const s = await service(t, fx)
  assert.equal((await publish(fx, s.url, fx.release('v9.9.1', ONE))).code, 0)
  const before = puts(fx).length
  const r = await publish(fx, s.url, fx.release('v9.9.2', TWO))
  assert.equal(r.code, 0, r.out)
  assert.deepEqual(puts(fx).slice(before).filter((key) => key.startsWith('cinema/files/')), [fileKey(TWO['ExoCinema.dll'])])
  assert.match(r.out, /3 already there; 1 to upload/)
})

test('a file served with other bytes: its list is not left published, and the release still goes live through Setup', { timeout: 180_000 }, async (t) => {
  const fx = fixture(t)
  const bad = `/v1/cinema/file/${sha(ONE['ExoCinema.dll'])}`
  const s = await service(t, fx, { tamper: (path) => (path === bad ? Buffer.from('not gzip, not the file') : null) })
  const r = await publish(fx, s.url, fx.release('v9.9.1', ONE))
  assert.equal(r.code, 0, r.out)
  assert.match(r.out, /file-level update not published/)
  assert.match(r.out, /NOT published; this version updates through Setup only/)
  assert.equal(existsSync(join(fx.bucket, 'cinema', 'v9.9.1', 'payload-manifest.json')), false)
  assert.equal((await fetch(`${s.url}/v1/cinema/payload/v9.9.1`)).status, 404)
  assert.equal(JSON.parse(readFileSync(join(fx.bucket, 'cinema', 'latest.json'), 'utf8')).tag, 'v9.9.1')
})

test('a payload folder that is not what its list says stops the run before anything uploads', { timeout: 180_000 }, async (t) => {
  const fx = fixture(t)
  const s = await service(t, fx)
  const altered = fx.release('v9.9.1', ONE)
  writeFileSync(join(altered.payload, 'ExoCinema.dll'), 'app code, version ONE') // same size, other bytes
  let r = await publish(fx, s.url, altered)
  assert.equal(r.code, 1, r.out)
  assert.match(r.out, /1 differ, 0 extra/)
  const extra = fx.release('v9.9.3', ONE)
  writeFileSync(join(extra.payload, 'planted.dll'), 'x')
  r = await publish(fx, s.url, extra)
  assert.equal(r.code, 1, r.out)
  const other = fx.release('v9.9.4', ONE)
  writeFileSync(join(other.dir, 'payload-manifest.json'), other.list.replace('9.9.4', '9.9.5')) // not the build's list
  r = await publish(fx, s.url, other)
  assert.equal(r.code, 1, r.out)
  assert.deepEqual(puts(fx), [])
})

test('the dry run reads what is already there and uploads nothing', { timeout: 180_000 }, async (t) => {
  const fx = fixture(t)
  const s = await service(t, fx)
  const r = await publish(fx, s.url, fx.release('v9.9.1', ONE), [])
  assert.equal(r.code, 0, r.out)
  assert.match(r.out, /4 distinct files; 0 already there; 4 to upload/)
  assert.match(r.out, /WHATIF PASS/)
  assert.deepEqual(puts(fx), [])
})

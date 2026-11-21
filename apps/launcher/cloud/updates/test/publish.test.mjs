/**
 * publish-release.ps1's order and its one-way tags, run for real against stand-ins: R2 is a folder (fixtures/fake-wrangler.mjs)
 * and updates.getexo.app is a local server that answers from that folder as the Worker does. Ways it could fail:
 * - latest.json goes up before the tag's files check out, so apps are offered bytes nobody confirmed;
 * - a failed check after latest.json went up leaves it pointing at the unconfirmed tag instead of the one before;
 * - a re-run with another build replaces a published tag's files (the edge would serve the old bytes for an hour);
 * - a re-run with the same build is refused, so a release that failed its checks can never be finished.
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { createServer } from 'node:http'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('../publish-release.ps1', import.meta.url))
const wrangler = fileURLToPath(new URL('./fixtures/fake-wrangler.mjs', import.meta.url))
const SETUP = 'ExoLauncher-Setup.exe'
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex')

/** A release folder for `tag` whose installer is `bytes`, and an empty bucket (optionally holding an older live release). */
function fixture(t, { tag = 'v9.9.9', bytes = 'installer 9.9.9', live = null } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'exo-publish-'))
  t.after(() => rmSync(root, { recursive: true, force: true }))
  const dir = join(root, 'artifacts')
  const bucket = join(root, 'bucket')
  mkdirSync(dir)
  mkdirSync(bucket)
  writeRelease(dir, tag, bytes)
  if (live) {
    mkdirSync(join(bucket, 'launcher', live.tag), { recursive: true })
    writeRelease(join(bucket, 'launcher', live.tag), live.tag, live.bytes)
    writeFileSync(join(bucket, 'launcher', 'latest.json'), JSON.stringify(manifestOf(join(bucket, 'launcher', live.tag), live.tag)))
  }
  return { root, dir, bucket, log: join(root, 'r2.log') }
}

function writeRelease(dir, tag, bytes) {
  writeFileSync(join(dir, SETUP), bytes)
  writeFileSync(join(dir, `${SETUP}.sha256`), `${sha(bytes)} *${SETUP}\n`)
  writeFileSync(join(dir, 'whats-new.json'), JSON.stringify({ version: tag.slice(1), items: [] }))
}

function manifestOf(dir, tag) {
  const file = (name) => {
    const bytes = readFileSync(join(dir, name))
    return { name, size: bytes.length, sha256: sha(bytes) }
  }
  return { schema: 2, app: 'launcher', tag, version: tag.slice(1),
    files: { setup: file(SETUP), sha256: file(`${SETUP}.sha256`), whatsNew: file('whats-new.json') } }
}

/** The Worker's two read routes over the bucket folder; `tamper` serves other installer bytes, `frozen` an unchanging latest. */
async function standIn(t, bucket, { tamper = false, frozen = null } = {}) {
  const server = createServer((req, res) => {
    const path = new URL(req.url, 'http://x').pathname
    if (path === '/v1/launcher/latest') {
      const file = join(bucket, 'launcher', 'latest.json')
      const text = frozen ?? (existsSync(file) ? readFileSync(file, 'utf8') : null)
      if (!text) { res.writeHead(503).end(); return }
      const m = JSON.parse(text)
      res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ app: 'launcher', tag: m.tag, version: m.version, checks: m.files }))
      return
    }
    const download = /^\/v1\/launcher\/download\/(v[\d.]+)\/([A-Za-z0-9.-]+)$/.exec(path)
    const file = download && join(bucket, 'launcher', download[1], download[2])
    if (!file || !existsSync(file)) { res.writeHead(404).end(); return }
    const bytes = readFileSync(file)
    res.writeHead(200).end(tamper && download[2] === SETUP ? Buffer.concat([bytes, Buffer.from('!')]) : bytes)
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  t.after(() => server.close())
  return `http://127.0.0.1:${server.address().port}`
}

function publish(fx, base, tag = 'v9.9.9', extra = ['-Publish', '-TestRun'], env = {}) {
  return new Promise((resolve) => {
    const child = spawn('pwsh', ['-NoProfile', '-File', script, '-App', 'launcher', '-Tag', tag, '-Dir', fx.dir, '-NoAnnounce',
      '-Tries', '2', '-RetrySeconds', '0', ...extra], {
      env: { ...process.env, EXO_TEST_WRANGLER: wrangler, EXO_TEST_UPDATES_BASE: base, FAKE_R2_DIR: fx.bucket, FAKE_R2_LOG: fx.log, ...env },
    })
    let out = ''
    child.stdout.on('data', (d) => { out += d })
    child.stderr.on('data', (d) => { out += d })
    child.on('close', (code) => resolve({ code, out }))
  })
}

const puts = (fx) => (existsSync(fx.log) ? readFileSync(fx.log, 'utf8').trim().split('\n') : []).filter((l) => l.startsWith('put '))

test('a good release: the tag files go up and check out first, latest.json last', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t)
  const base = await standIn(t, fx.bucket)
  const r = await publish(fx, base)
  assert.equal(r.code, 0, r.out)
  assert.deepEqual(puts(fx), [
    `put launcher/v9.9.9/${SETUP}`, `put launcher/v9.9.9/${SETUP}.sha256`, 'put launcher/v9.9.9/whats-new.json', 'put launcher/latest.json',
  ])
  assert.equal(JSON.parse(readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8')).tag, 'v9.9.9')
})

test('served bytes that do not match: latest.json is never uploaded and the live release stays offered', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t, { live: { tag: 'v9.9.8', bytes: 'installer 9.9.8' } })
  const before = readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8')
  const base = await standIn(t, fx.bucket, { tamper: true })
  const r = await publish(fx, base)
  assert.equal(r.code, 1, r.out)
  assert.match(r.out, /latest\.json was not touched/)
  assert.equal(puts(fx).includes('put launcher/latest.json'), false)
  assert.equal(readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8'), before)
})

test('a newest-version answer that never names the tag: the latest.json from before is put back', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t, { live: { tag: 'v9.9.8', bytes: 'installer 9.9.8' } })
  const before = readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8')
  const base = await standIn(t, fx.bucket, { frozen: before })
  const r = await publish(fx, base)
  assert.equal(r.code, 1, r.out)
  assert.deepEqual(puts(fx).filter((l) => l === 'put launcher/latest.json').length, 2)
  assert.equal(readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8'), before)
})

test('a newer tag that reached the bucket during the run (the address still a minute old) is never pointed back', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t, { live: { tag: 'v9.9.8', bytes: 'installer 9.9.8' } })
  const served = readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8')
  const newer = JSON.stringify({ ...JSON.parse(served), tag: 'v9.9.10', version: '9.9.10' })
  writeFileSync(join(fx.bucket, 'launcher', 'latest.json'), newer)
  const base = await standIn(t, fx.bucket, { frozen: served })
  const r = await publish(fx, base)
  assert.equal(r.code, 1, r.out)
  assert.match(r.out, /already names v9\.9\.10, newer than v9\.9\.9/)
  assert.equal(puts(fx).includes('put launcher/latest.json'), false)
  assert.equal(readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8'), newer)
})

test('a tag that already holds another build is refused before anything uploads', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t, { bytes: 'a second build of 9.9.9' })
  mkdirSync(join(fx.bucket, 'launcher', 'v9.9.9'), { recursive: true })
  writeRelease(join(fx.bucket, 'launcher', 'v9.9.9'), 'v9.9.9', 'the first build of 9.9.9')
  const base = await standIn(t, fx.bucket)
  for (const whatIf of [['-TestRun'], ['-Publish', '-TestRun']]) {
    const r = await publish(fx, base, 'v9.9.9', whatIf)
    assert.equal(r.code, 1, r.out)
    assert.match(r.out, /already holds other files; nothing uploaded/)
  }
  assert.deepEqual(puts(fx), [])
  assert.equal(readFileSync(join(fx.bucket, 'launcher', 'v9.9.9', SETUP), 'utf8'), 'the first build of 9.9.9')
})

test('the same build again (a re-run after a failed check) uploads none of the tag files again and goes live', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t)
  mkdirSync(join(fx.bucket, 'launcher', 'v9.9.9'), { recursive: true })
  writeRelease(join(fx.bucket, 'launcher', 'v9.9.9'), 'v9.9.9', 'installer 9.9.9')
  const base = await standIn(t, fx.bucket)
  const r = await publish(fx, base)
  assert.equal(r.code, 0, r.out)
  assert.deepEqual(puts(fx), ['put launcher/latest.json'])
})

test('the dry run is the default: without -Publish nothing is uploaded', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t)
  const base = await standIn(t, fx.bucket)
  const r = await publish(fx, base, 'v9.9.9', ['-TestRun'])
  assert.equal(r.code, 0, r.out)
  assert.match(r.out, /WHATIF PASS/)
  assert.deepEqual(puts(fx), [])
})

test('a stand-in left in the environment fails a real run instead of passing it', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t)
  const base = await standIn(t, fx.bucket)
  for (const extra of [['-Publish'], []]) {
    const r = await publish(fx, base, 'v9.9.9', extra)
    assert.equal(r.code, 1, r.out)
    assert.match(r.out, /EXO_TEST_WRANGLER or EXO_TEST_UPDATES_BASE is set/)
  }
  assert.equal(existsSync(fx.log), false, 'nothing was asked of R2')
})

test('an R2 answer that is not the missing-key message stops the run; it is never taken for "not there"', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t)
  const base = await standIn(t, fx.bucket)
  const r = await publish(fx, base, 'v9.9.9', ['-Publish', '-TestRun'], { FAKE_R2_GET_ERROR: '1' })
  assert.equal(r.code, 1, r.out)
  assert.match(r.out, /could not read exo-releases\/launcher\/v9\.9\.9/)
  assert.deepEqual(puts(fx), [])
})

test('putting the old latest.json back is tried again before it says to do it by hand', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t, { live: { tag: 'v9.9.8', bytes: 'installer 9.9.8' } })
  const before = readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8')
  const base = await standIn(t, fx.bucket, { frozen: before })
  // The first put of latest.json is the new one; the second (the first try at putting it back) fails.
  const r = await publish(fx, base, 'v9.9.9', ['-Publish', '-TestRun'], { FAKE_R2_FAIL_PUT: 'launcher/latest.json:2' })
  assert.equal(r.code, 1, r.out)
  assert.match(r.out, /try 1 of 3/)
  assert.doesNotMatch(r.out, /by hand/)
  assert.equal(readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8'), before)
})

test('a launcher release with files.json and files.pack uploads them and includes delta checks in latest.json', { timeout: 120_000 }, async (t) => {
  const fx = fixture(t)
  const filesJson = JSON.stringify({ schema: 1, version: '9.9.9', installer: 'a'.repeat(64), files: [] })
  const filesPack = 'delta-pack-bytes'
  writeFileSync(join(fx.dir, 'files.json'), filesJson)
  writeFileSync(join(fx.dir, 'files.pack'), filesPack)
  writeFileSync(join(fx.dir, 'files.pack.sha256'), `${sha(filesPack)} *files.pack\n`)
  const base = await standIn(t, fx.bucket)
  const r = await publish(fx, base)
  assert.equal(r.code, 0, r.out)
  assert.deepEqual(puts(fx), [
    `put launcher/v9.9.9/${SETUP}`,
    `put launcher/v9.9.9/${SETUP}.sha256`,
    'put launcher/v9.9.9/whats-new.json',
    'put launcher/v9.9.9/files.json',
    'put launcher/v9.9.9/files.pack',
    'put launcher/v9.9.9/files.pack.sha256',
    'put launcher/latest.json',
  ])
  const manifest = JSON.parse(readFileSync(join(fx.bucket, 'launcher', 'latest.json'), 'utf8'))
  assert.equal(manifest.files.filesJson.sha256, sha(filesJson))
  assert.equal(manifest.files.filesPack.sha256, sha(filesPack))
})

/**
 * The Worker under workerd, the runtime Cloudflare runs it in: `wrangler dev --local`, with a local D1 that has the
 * migrations applied and a throwaway keyring (ACCOUNT_SECRETS). Nothing remote is touched (--local, and no login or
 * deploy). Two simulated PCs take the whole path: create the account, sync items both ways (with the server's version
 * ordering), export, and delete with the password.
 *
 * Needs the pinned wrangler: run `npm ci` in cloud/account first. A missing wrangler fails the test rather than skipping it.
 *   npm run e2e    (or node --test cloud/account/test/workerd.test.mjs)
 */
import assert from 'node:assert/strict'
import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { once } from 'node:events'
import { after, before, test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { hex, random } from '../src/crypto.js'

const root = fileURLToPath(new URL('..', import.meta.url))
const wrangler = join(root, 'node_modules', 'wrangler', 'bin', 'wrangler.js')
const env = { ...process.env, WRANGLER_SEND_METRICS: 'false', CI: '1' }
const EMAIL = 'two-pcs@example.com'
const PASSWORD = 'correct horse battery'
const LAPTOP = 'fedcba9876543210fedcba9876543210'
const PHONE = '0123456789abcdef0123456789abcdef'
const NOW = Date.now()

const persist = mkdtempSync(join(tmpdir(), 'exo-account-'))
let dev
let base
let calls = 0

const port = () => new Promise(resolve => {
  const probe = createServer().listen(0, '127.0.0.1', () => {
    const { port: free } = probe.address()
    probe.close(() => resolve(free))
  })
})

before(async () => {
  assert.ok(existsSync(wrangler), 'wrangler is not installed: run npm ci in cloud/account')
  const migrate = spawnSync(process.execPath, [wrangler, 'd1', 'migrations', 'apply', 'exo-account', '--local', '--persist-to', persist], { cwd: root, env, encoding: 'utf8' })
  assert.equal(migrate.status, 0, migrate.stdout + migrate.stderr)
  const free = await port()
  base = `http://127.0.0.1:${free}`
  dev = spawn(process.execPath, [wrangler, 'dev', '--local', '--ip', '127.0.0.1', '--port', String(free), '--persist-to', persist, '--var', `ACCOUNT_SECRETS:k1:${hex(random(32))}`], { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] })
  let log = ''
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`wrangler dev did not start:\n${log}`)), 120_000)
    const watch = chunk => {
      log += chunk
      if (log.includes('Ready on')) {
        clearTimeout(timer)
        resolve()
      }
    }
    dev.stdout.on('data', watch)
    dev.stderr.on('data', watch)
    dev.on('exit', code => reject(new Error(`wrangler dev exited ${code}:\n${log}`)))
  })
})

after(async () => {
  if (dev && dev.exitCode === null) {
    const exited = once(dev, 'exit')
    dev.kill('SIGTERM')
    await exited
  }
  // The local D1 file is released when wrangler's own process is gone.
  rmSync(persist, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 })
})

/** A request from its own address (the limit is per address), as JSON. */
async function call(method, path, { body, token } = {}) {
  calls++
  const headers = { 'CF-Connecting-IP': `10.1.0.${calls}` }
  if (token) headers.Authorization = `Bearer ${token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const response = await fetch(base + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  const text = await response.text()
  return { status: response.status, body: text ? JSON.parse(text) : null }
}

/**
 * Every password route, in workerd: each hashes or verifies an Argon2id password, and each once answered 500 on the live
 * Workers runtime. The local runtime refuses WebAssembly compiled at run time, so a WASM hash fails here too; the
 * PBKDF2 round limit (100,000 on Workers) is not enforced here, so the static check in account.test.mjs keeps PBKDF2 out.
 * The time per hash is reported as a diagnostic.
 */
test('every password route answers in workerd: sign-up, change, a new recovery code and a reset with it', async t => {
  const email = 'every-route@example.com'
  const next = 'a brand new passphrase'
  const device = '1111111111111111111111111111aaaa'
  const started = performance.now()
  const made = await call('POST', '/v1/account', { body: { email, password: PASSWORD, device } })
  assert.equal(made.status, 201)
  const hashMs = performance.now() - started
  t.diagnostic(`sign-up (one Argon2id hash) in workerd: ${hashMs.toFixed(0)} ms`)
  assert.equal((await call('PUT', '/v1/password', { token: made.body.token, body: { current: PASSWORD, password: next } })).status, 200)
  const fresh = await call('POST', '/v1/recovery', { token: made.body.token, body: { password: next } })
  assert.equal(fresh.status, 200)
  const reset = await call('POST', '/v1/recovery/reset', { body: { email, code: fresh.body.recoveryCode, password: PASSWORD, device } })
  assert.equal(reset.status, 200)
  // The sign-in on the same device ends the reset's session: the delete uses the newer one.
  const signed = await call('POST', '/v1/session', { body: { email, password: PASSWORD, device } })
  assert.equal(signed.status, 200)
  assert.equal((await call('DELETE', '/v1/account', { token: signed.body.token, body: { password: PASSWORD } })).status, 204)
})

test('two PCs: create the account, sync both ways with the server\'s order, export, delete with the password', async () => {
  const made = await call('POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: PHONE } })
  assert.equal(made.status, 201, JSON.stringify(made.body))
  const phone = made.body.token
  const put = await call('PUT', '/v1/sync', { token: phone, body: { items: [
    { app: 'cinema', type: 'watch-history', id: 'tt0111161', value: '{"position":120}', base: 0, updated: NOW },
    { app: 'launcher', type: 'riot-ledger', id: 'session-1', value: '{"minutes":40}', base: 0, updated: NOW },
  ] } })
  assert.equal(put.status, 200)
  assert.equal(put.body.written.length, 2)

  const signed = await call('POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: LAPTOP } })
  assert.equal(signed.status, 200)
  const laptop = signed.body.token
  const first = await call('GET', '/v1/sync?after=0', { token: laptop })
  assert.deepEqual(first.body.items.map(i => i.id).sort(), ['session-1', 'tt0111161'])
  const seen = first.body.items.find(i => i.id === 'tt0111161')
  assert.equal(seen.value, '{"position":120}')

  // The laptop writes on the version it saw: it lands. The phone's write on the old version now loses, with the value.
  const landed = await call('PUT', '/v1/sync', { token: laptop, body: { items: [
    { app: 'cinema', type: 'watch-history', id: 'tt0111161', value: '{"position":900}', base: seen.version, updated: NOW - 86_400_000 },
  ] } })
  assert.equal(landed.body.written.length, 1)
  const lost = await call('PUT', '/v1/sync', { token: phone, body: { items: [
    { app: 'cinema', type: 'watch-history', id: 'tt0111161', value: '{"position":5}', base: seen.version, updated: NOW + 60_000 },
  ] } })
  assert.deepEqual(lost.body.conflicts.map(c => c.value), ['{"position":900}'])
  const newest = await call('GET', '/v1/sync?after=0', { token: phone })
  assert.equal(newest.body.items.find(i => i.id === 'tt0111161').value, '{"position":900}')
  assert.deepEqual((await call('GET', `/v1/sync?after=${newest.body.cursor}`, { token: phone })).body.items, [])

  const exported = await call('GET', '/v1/export', { token: laptop })
  assert.equal(exported.status, 200)
  assert.equal(exported.body.items.length, 2)
  assert.equal(exported.body.more, false)
  assert.equal(exported.body.account.id, made.body.account.id)

  const sessions = await call('GET', '/v1/sessions', { token: phone })
  assert.equal(sessions.body.sessions.length, 2)

  assert.equal((await call('DELETE', '/v1/account', { token: phone, body: { password: 'wrong password here' } })).body.error, 'wrong-password')
  assert.equal((await call('DELETE', '/v1/account', { token: phone, body: { password: PASSWORD } })).status, 204)
  assert.equal((await call('GET', '/v1/export', { token: laptop })).body.error, 'signed-out')
  assert.equal((await call('POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: PHONE } })).body.error, 'wrong-credentials')
})

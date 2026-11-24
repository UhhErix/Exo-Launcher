import assert from 'node:assert/strict'
import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { once } from 'node:events'
import { after, before, test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { b64url, hex, random, sha256, utf8 } from '../src/crypto.js'

const root = fileURLToPath(new URL('..', import.meta.url))
const localWrangler = join(root, 'node_modules', 'wrangler', 'bin', 'wrangler.js')
const globalWrangler = join(process.env.APPDATA ?? '', 'npm', 'node_modules', 'wrangler', 'bin', 'wrangler.js')
const wrangler = existsSync(globalWrangler) ? globalWrangler : localWrangler
const env = { ...process.env, WRANGLER_SEND_METRICS: 'false', CI: '1' }

const RP_ID = 'accounts.getexo.app'
const ORIGIN = 'https://accounts.getexo.app'
const EMAIL = 'passkey-workerd@example.invalid'
const PASSWORD = 'correct horse battery staple'
const DEVICE = '0123456789abcdef0123456789abcdef'

const persist = mkdtempSync(join(tmpdir(), 'exo-account-passkey-'))
let dev
let base
let calls = 0

const port = () => new Promise(resolve => {
  const probe = createServer().listen(0, '127.0.0.1', () => {
    const { port: free } = probe.address()
    probe.close(() => resolve(free))
  })
})

function cborUint(major, n) {
  const baseTag = major << 5
  if (n < 24) return Uint8Array.of(baseTag | n)
  if (n <= 0xff) return Uint8Array.of(baseTag | 24, n)
  return Uint8Array.of(baseTag | 25, (n >> 8) & 0xff, n & 0xff)
}
const cborInt = n => (n >= 0 ? cborUint(0, n) : cborUint(1, -1 - n))
const cborBytes = b => concatBytes(cborUint(2, b.length), b)
const cborText = s => {
  const b = utf8(s)
  return concatBytes(cborUint(3, b.length), b)
}

function concatBytes(...parts) {
  const total = parts.reduce((a, b) => a + b.length, 0)
  const out = new Uint8Array(total)
  let off = 0
  for (const p of parts) {
    out.set(p, off)
    off += p.length
  }
  return out
}

function encodeCoseP256(rawUncompressed) {
  return concatBytes(
    cborUint(5, 5),
    cborInt(1), cborInt(2),
    cborInt(3), cborInt(-7),
    cborInt(-1), cborInt(1),
    cborInt(-2), cborBytes(rawUncompressed.subarray(1, 33)),
    cborInt(-3), cborBytes(rawUncompressed.subarray(33, 65)),
  )
}

function encodeAttestationObject(authData) {
  return concatBytes(
    cborUint(5, 3),
    cborText('fmt'), cborText('none'),
    cborText('attStmt'), cborUint(5, 0),
    cborText('authData'), cborBytes(authData),
  )
}

async function buildRegistrationPayload(challenge, initialSignCount = 1) {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify'])
  const rawPub = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey))
  const credId = random(32)
  const rpHash = await sha256(utf8(RP_ID))
  const aaguid = new Uint8Array(16)
  const flagsAndCount = Uint8Array.of(0x45, 0, 0, 0, initialSignCount & 0xff)
  const credLen = Uint8Array.of((credId.length >> 8) & 0xff, credId.length & 0xff)
  const authData = concatBytes(rpHash, flagsAndCount, aaguid, credLen, credId, encodeCoseP256(rawPub))
  const clientDataJSON = utf8(JSON.stringify({ type: 'webauthn.create', challenge, origin: ORIGIN, crossOrigin: false }))
  return {
    pair,
    credentialId: b64url(credId),
    clientDataJSON: b64url(clientDataJSON),
    attestationObject: b64url(encodeAttestationObject(authData)),
  }
}

async function buildAssertionPayload(pair, credentialId, challenge, signCount) {
  const rpHash = await sha256(utf8(RP_ID))
  const flagsAndCount = Uint8Array.of(
    0x05,
    (signCount >>> 24) & 0xff,
    (signCount >>> 16) & 0xff,
    (signCount >>> 8) & 0xff,
    signCount & 0xff,
  )
  const authData = concatBytes(rpHash, flagsAndCount)
  const clientDataBytes = utf8(JSON.stringify({ type: 'webauthn.get', challenge, origin: ORIGIN, crossOrigin: false }))
  const signedData = concatBytes(authData, await sha256(clientDataBytes))
  const sig = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, pair.privateKey, signedData))
  return {
    credentialId,
    clientDataJSON: b64url(clientDataBytes),
    authenticatorData: b64url(authData),
    signature: b64url(sig),
  }
}

async function call(method, path, { body, token } = {}) {
  calls++
  const headers = { 'CF-Connecting-IP': `10.9.${Math.floor(calls / 200)}.${(calls % 200) + 1}` }
  if (token) headers.Authorization = `Bearer ${token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const response = await fetch(base + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  const text = await response.text()
  return { status: response.status, body: text ? JSON.parse(text) : null }
}

before(async () => {
  assert.ok(existsSync(wrangler), 'wrangler is not installed')
  const migrate = spawnSync(
    process.execPath,
    [wrangler, 'd1', 'migrations', 'apply', 'exo-account', '--local', '--persist-to', persist],
    { cwd: root, env, encoding: 'utf8' },
  )
  assert.equal(migrate.status, 0, migrate.stdout + migrate.stderr)
  const free = await port()
  base = `http://127.0.0.1:${free}`
  dev = spawn(
    process.execPath,
    [
      wrangler, 'dev', '--local', '--ip', '127.0.0.1', '--port', String(free),
      '--persist-to', persist, '--var', `ACCOUNT_SECRETS:k1:${hex(random(32))}`,
    ],
    { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] },
  )
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
  rmSync(persist, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 })
})

test('workerd end-to-end: register, UV-required ES256 sign-in, regression flag, and recovery fallback', async () => {
  const signup = await call('POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  assert.equal(signup.status, 201)
  const token = signup.body.token
  const recoveryCode = signup.body.recoveryCode

  const regStart = await call('POST', '/v1/passkeys/register/start', { token, body: {} })
  assert.equal(regStart.status, 200)
  assert.equal(regStart.body.userVerification, 'required')
  const regPayload = await buildRegistrationPayload(regStart.body.challenge, 1)
  const regFinish = await call('POST', '/v1/passkeys/register/finish', {
    token,
    body: {
      challenge: regStart.body.challenge,
      clientDataJSON: regPayload.clientDataJSON,
      attestationObject: regPayload.attestationObject,
      label: 'Windows Hello',
    },
  })
  assert.equal(regFinish.status, 201)

  const st = await call('POST', '/v1/passkeys/signin/start', { body: {} })
  assert.equal(st.status, 200)
  assert.equal(st.body.rpId, RP_ID)
  assert.equal(st.body.origin, ORIGIN)
  assert.deepEqual(st.body.allowCredentials, [])
  const assertion = await buildAssertionPayload(regPayload.pair, regPayload.credentialId, st.body.challenge, 2)
  const fin = await call('POST', '/v1/passkeys/signin/finish', {
    body: { challenge: st.body.challenge, device: DEVICE, ...assertion },
  })
  assert.equal(fin.status, 200)

  const stRegress = await call('POST', '/v1/passkeys/signin/start', { body: {} })
  const aRegress = await buildAssertionPayload(regPayload.pair, regPayload.credentialId, stRegress.body.challenge, 2)
  const rej = await call('POST', '/v1/passkeys/signin/finish', {
    body: { challenge: stRegress.body.challenge, device: DEVICE, ...aRegress },
  })
  assert.equal(rej.status, 401)
  assert.equal(rej.body.error, 'passkey-replay')

  const listed = await call('GET', '/v1/passkeys', { token: fin.body.token })
  assert.equal(listed.status, 200)
  assert.equal(listed.body.passkeys[0].flagged, true)

  const reset = await call('POST', '/v1/recovery/reset', {
    body: { email: EMAIL, code: recoveryCode, password: PASSWORD + '!', device: DEVICE },
  })
  assert.equal(reset.status, 200)

  const delAcct = await call('DELETE', '/v1/account', {
    token: reset.body.token,
    body: { password: PASSWORD + '!' },
  })
  assert.equal(delAcct.status, 204)
})

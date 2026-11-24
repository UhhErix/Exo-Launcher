import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { afterEach, test } from 'node:test'
import { b64url, random, sha256, utf8 } from '../src/crypto.js'
import { account, call, later, resetClock } from './harness.mjs'

const RP_ID = 'accounts.getexo.app'
const ORIGIN = 'https://accounts.getexo.app'
const PASSWORD = 'correct horse battery staple'
const DEVICE = '0123456789abcdef0123456789abcdef'

const passkeyAccount = () => ({
  ...account(),
  WEBAUTHN_RP_ID: RP_ID,
  WEBAUTHN_ORIGIN: ORIGIN,
})

afterEach(() => resetClock())

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

async function makeKeyPair() {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify'])
  const rawPub = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey))
  return { pair, rawPub, credId: random(32) }
}

async function buildRegistration(key, challenge, { rpId = RP_ID, origin = ORIGIN, flags = 0x45, signCount = 1 } = {}) {
  const rpHash = await sha256(utf8(rpId))
  const flagsAndCount = Uint8Array.of(
    flags,
    (signCount >>> 24) & 0xff,
    (signCount >>> 16) & 0xff,
    (signCount >>> 8) & 0xff,
    signCount & 0xff,
  )
  const aaguid = new Uint8Array(16)
  const credLen = Uint8Array.of((key.credId.length >> 8) & 0xff, key.credId.length & 0xff)
  const authData = concatBytes(rpHash, flagsAndCount, aaguid, credLen, key.credId, encodeCoseP256(key.rawPub))
  const clientDataJSON = utf8(JSON.stringify({ type: 'webauthn.create', challenge, origin, crossOrigin: false }))
  return {
    credentialId: b64url(key.credId),
    clientDataJSON: b64url(clientDataJSON),
    attestationObject: b64url(encodeAttestationObject(authData)),
  }
}

async function buildAssertion(key, challenge, { rpId = RP_ID, origin = ORIGIN, flags = 0x05, signCount = 2 } = {}) {
  const rpHash = await sha256(utf8(rpId))
  const flagsAndCount = Uint8Array.of(
    flags,
    (signCount >>> 24) & 0xff,
    (signCount >>> 16) & 0xff,
    (signCount >>> 8) & 0xff,
    signCount & 0xff,
  )
  const authData = concatBytes(rpHash, flagsAndCount)
  const clientDataBytes = utf8(JSON.stringify({ type: 'webauthn.get', challenge, origin, crossOrigin: false }))
  const signedData = concatBytes(authData, await sha256(clientDataBytes))
  const sig = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key.pair.privateKey, signedData))
  return {
    credentialId: b64url(key.credId),
    clientDataJSON: b64url(clientDataBytes),
    authenticatorData: b64url(authData),
    signature: b64url(sig),
  }
}

async function signUpAndRegister(env, email = 'passkey@example.com', key = null) {
  const k = key ?? await makeKeyPair()
  const made = await call(env, 'POST', '/v1/account', { body: { email, password: PASSWORD, device: DEVICE } })
  assert.equal(made.status, 201)
  const start = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  assert.equal(start.status, 200)
  assert.equal(start.body.userVerification, 'required')
  const reg = await buildRegistration(k, start.body.challenge)
  const fin = await call(env, 'POST', '/v1/passkeys/register/finish', {
    token: made.body.token,
    body: { challenge: start.body.challenge, clientDataJSON: reg.clientDataJSON, attestationObject: reg.attestationObject, label: 'Hello' },
  })
  assert.equal(fin.status, 201)
  return { key: k, reg, made }
}

test('challenges are single-use, server-stored in D1, and expire after 5 minutes', async () => {
  const env = passkeyAccount()
  const { key } = await signUpAndRegister(env)

  const st1 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  assert.equal(st1.status, 200)
  assert.equal(env.DB.rows('webauthn_challenges').length, 1)

  const a1 = await buildAssertion(key, st1.body.challenge, { signCount: 2 })
  const ok1 = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: st1.body.challenge, device: DEVICE, ...a1 } })
  assert.equal(ok1.status, 200)
  assert.equal(env.DB.rows('webauthn_challenges').length, 0)

  const replay = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: st1.body.challenge, device: DEVICE, ...a1 } })
  assert.equal(replay.status, 401)
  assert.equal(replay.body.error, 'bad-challenge')

  const st2 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  assert.equal(st2.status, 200)
  later(301)
  const a2 = await buildAssertion(key, st2.body.challenge, { signCount: 3 })
  const expired = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: st2.body.challenge, device: DEVICE, ...a2 } })
  assert.equal(expired.status, 401)
  assert.equal(expired.body.error, 'bad-challenge')
})

test('rpId and origin are checked exactly on registration and sign-in', async () => {
  const env = passkeyAccount()
  const made = await call(env, 'POST', '/v1/account', { body: { email: 'rp@example.com', password: PASSWORD, device: DEVICE } })
  const k = await makeKeyPair()

  const r1 = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  const badOrigin = await buildRegistration(k, r1.body.challenge, { origin: 'https://accounts.getexo.app.evil.com' })
  const res1 = await call(env, 'POST', '/v1/passkeys/register/finish', {
    token: made.body.token,
    body: { challenge: r1.body.challenge, ...badOrigin },
  })
  assert.equal(res1.status, 401)
  assert.equal(res1.body.error, 'bad-origin')

  const r2 = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  const badRp = await buildRegistration(k, r2.body.challenge, { rpId: 'evil.example.com' })
  const res2 = await call(env, 'POST', '/v1/passkeys/register/finish', {
    token: made.body.token,
    body: { challenge: r2.body.challenge, ...badRp },
  })
  assert.equal(res2.status, 401)
  assert.equal(res2.body.error, 'bad-rp')

  const r3 = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  const goodReg = await buildRegistration(k, r3.body.challenge)
  assert.equal((await call(env, 'POST', '/v1/passkeys/register/finish', { token: made.body.token, body: { challenge: r3.body.challenge, ...goodReg } })).status, 201)

  const s1 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  const badSignOrigin = await buildAssertion(k, s1.body.challenge, { origin: 'http://accounts.getexo.app', signCount: 2 })
  const sRes1 = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: s1.body.challenge, device: DEVICE, ...badSignOrigin } })
  assert.equal(sRes1.status, 401)
  assert.equal(sRes1.body.error, 'bad-origin')

  const s2 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  const badSignRp = await buildAssertion(k, s2.body.challenge, { rpId: 'getexo.app', signCount: 2 })
  const sRes2 = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: s2.body.challenge, device: DEVICE, ...badSignRp } })
  assert.equal(sRes2.status, 401)
  assert.equal(sRes2.body.error, 'bad-rp')
})

test('userVerification (UV flag) is strictly required on registration and sign-in', async () => {
  const env = passkeyAccount()
  const made = await call(env, 'POST', '/v1/account', { body: { email: 'uv@example.com', password: PASSWORD, device: DEVICE } })
  const k = await makeKeyPair()

  const r1 = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  const noUvReg = await buildRegistration(k, r1.body.challenge, { flags: 0x41 })
  const regRes = await call(env, 'POST', '/v1/passkeys/register/finish', {
    token: made.body.token,
    body: { challenge: r1.body.challenge, ...noUvReg },
  })
  assert.equal(regRes.status, 401)
  assert.equal(regRes.body.error, 'passkey-uv-required')

  const r2 = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  const uvReg = await buildRegistration(k, r2.body.challenge, { flags: 0x45 })
  assert.equal((await call(env, 'POST', '/v1/passkeys/register/finish', { token: made.body.token, body: { challenge: r2.body.challenge, ...uvReg } })).status, 201)

  const s1 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  const noUvAssert = await buildAssertion(k, s1.body.challenge, { flags: 0x01, signCount: 2 })
  const sRes = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: s1.body.challenge, device: DEVICE, ...noUvAssert } })
  assert.equal(sRes.status, 401)
  assert.equal(sRes.body.error, 'passkey-uv-required')
})

test('signCount regression is refused and flagged in D1 and GET /v1/passkeys', async () => {
  const env = passkeyAccount()
  const { key, made } = await signUpAndRegister(env)

  const s1 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  const a1 = await buildAssertion(key, s1.body.challenge, { signCount: 10 })
  const ok = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: s1.body.challenge, device: DEVICE, ...a1 } })
  assert.equal(ok.status, 200)

  const s2 = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })
  const aRegress = await buildAssertion(key, s2.body.challenge, { signCount: 9 })
  const rej = await call(env, 'POST', '/v1/passkeys/signin/finish', { body: { challenge: s2.body.challenge, device: DEVICE, ...aRegress } })
  assert.equal(rej.status, 401)
  assert.equal(rej.body.error, 'passkey-replay')

  const list = await call(env, 'GET', '/v1/passkeys', { token: ok.body.token })
  assert.equal(list.status, 200)
  assert.equal(list.body.passkeys[0].flagged, true)
  assert.equal(made.body.account.id, ok.body.account.id)
})

test('credential IDs are unique per account and globally across accounts', async () => {
  const env = passkeyAccount()
  const sharedKey = await makeKeyPair()
  const { made: acct1 } = await signUpAndRegister(env, 'one@example.com', sharedKey)

  const dupSameStart = await call(env, 'POST', '/v1/passkeys/register/start', { token: acct1.body.token, body: {} })
  const dupSameReg = await buildRegistration(sharedKey, dupSameStart.body.challenge)
  const dupSameRes = await call(env, 'POST', '/v1/passkeys/register/finish', {
    token: acct1.body.token,
    body: { challenge: dupSameStart.body.challenge, ...dupSameReg },
  })
  assert.equal(dupSameRes.status, 409)
  assert.equal(dupSameRes.body.error, 'passkey-exists')

  const acct2 = await call(env, 'POST', '/v1/account', { body: { email: 'two@example.com', password: PASSWORD, device: DEVICE } })
  const dupOtherStart = await call(env, 'POST', '/v1/passkeys/register/start', { token: acct2.body.token, body: {} })
  const dupOtherReg = await buildRegistration(sharedKey, dupOtherStart.body.challenge)
  const dupOtherRes = await call(env, 'POST', '/v1/passkeys/register/finish', {
    token: acct2.body.token,
    body: { challenge: dupOtherStart.body.challenge, ...dupOtherReg },
  })
  assert.equal(dupOtherRes.status, 409)
  assert.equal(dupOtherRes.body.error, 'passkey-exists')
})

test('passkey register and sign-in endpoints are rate-limited (429 slow-down)', async () => {
  const env = passkeyAccount()
  const made = await call(env, 'POST', '/v1/account', { body: { email: 'rl@example.com', password: PASSWORD, device: DEVICE } })

  for (let i = 0; i < 10; i++) {
    const r = await call(env, 'POST', '/v1/passkeys/signin/start', { ip: '198.51.100.10', body: {} })
    assert.equal(r.status, 200)
  }
  const blockedSignIn = await call(env, 'POST', '/v1/passkeys/signin/start', { ip: '198.51.100.10', body: {} })
  assert.equal(blockedSignIn.status, 429)
  assert.equal(blockedSignIn.body.error, 'slow-down')

  for (let i = 0; i < 10; i++) {
    const r = await call(env, 'POST', '/v1/passkeys/register/start', { ip: '198.51.100.20', token: made.body.token, body: {} })
    assert.equal(r.status, 200)
  }
  const blockedReg = await call(env, 'POST', '/v1/passkeys/register/start', { ip: '198.51.100.20', token: made.body.token, body: {} })
  assert.equal(blockedReg.status, 429)
  assert.equal(blockedReg.body.error, 'slow-down')
})

test('registering a passkey requires a fresh session (<=5m) or fresh password re-auth', async () => {
  const env = passkeyAccount()
  const made = await call(env, 'POST', '/v1/account', { body: { email: 'reauth@example.com', password: PASSWORD, device: DEVICE } })

  later(301)
  const stale = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
  assert.equal(stale.status, 403)
  assert.equal(stale.body.error, 'reauth-required')

  const wrongPw = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: { password: 'not the right password' } })
  assert.equal(wrongPw.status, 401)
  assert.equal(wrongPw.body.error, 'wrong-password')

  const freshPw = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: { password: PASSWORD } })
  assert.equal(freshPw.status, 200)
  assert.ok(freshPw.body.challenge)
})

test('removing the last sign-in method is blocked, while password accounts can delete passkeys and recovery codes still work', async () => {
  const env = passkeyAccount()
  const { reg, made } = await signUpAndRegister(env, 'fallback@example.com')

  const delWithPw = await call(env, 'DELETE', `/v1/passkeys/${reg.credentialId}`, { token: made.body.token })
  assert.equal(delWithPw.status, 204)

  const k1 = await makeKeyPair()
  const k2 = await makeKeyPair()
  for (const k of [k1, k2]) {
    const st = await call(env, 'POST', '/v1/passkeys/register/start', { token: made.body.token, body: {} })
    const r = await buildRegistration(k, st.body.challenge)
    assert.equal((await call(env, 'POST', '/v1/passkeys/register/finish', { token: made.body.token, body: { challenge: st.body.challenge, ...r } })).status, 201)
  }

  env.DB.db.prepare("UPDATE accounts SET password_hash = '' WHERE id = ?").run(made.body.account.id)

  const delFirst = await call(env, 'DELETE', `/v1/passkeys/${b64url(k1.credId)}`, { token: made.body.token })
  assert.equal(delFirst.status, 204)

  const delLast = await call(env, 'DELETE', `/v1/passkeys/${b64url(k2.credId)}`, { token: made.body.token })
  assert.equal(delLast.status, 409)
  assert.equal(delLast.body.error, 'last-sign-in-method')

  const reset = await call(env, 'POST', '/v1/recovery/reset', {
    body: { email: 'fallback@example.com', code: made.body.recoveryCode, password: PASSWORD + '!', device: DEVICE },
  })
  assert.equal(reset.status, 200)
  const delAfterReset = await call(env, 'DELETE', `/v1/passkeys/${b64url(k2.credId)}`, { token: reset.body.token })
  assert.equal(delAfterReset.status, 204)
})

test('no auth secrets or PII are logged: zero console calls in Worker src and clean error bodies', async () => {
  const srcDir = new URL('../src/', import.meta.url)
  for (const file of readdirSync(srcDir).filter(f => f.endsWith('.js'))) {
    const content = readFileSync(new URL(file, srcDir), 'utf8')
    assert.doesNotMatch(content, /\bconsole\s*\./, `unexpected console logging in ${file}`)
  }
  const wranglerToml = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')
  assert.match(wranglerToml, /\[observability\]\s*\r?\nenabled = false/)

  const env = passkeyAccount()
  const secretEmail = 'pii-secret@example.com'
  const badRes = await call(env, 'POST', '/v1/passkeys/signin/finish', {
    body: {
      challenge: 'secret_challenge_value_12345678901234567890',
      device: DEVICE,
      credentialId: 'secret_cred_id_12345',
      clientDataJSON: b64url(utf8(secretEmail)),
      authenticatorData: 'aaaa',
      signature: 'bbbb',
    },
  })
  assert.equal(badRes.status, 401)
  const serialized = JSON.stringify(badRes.body)
  assert.equal(serialized, '{"error":"bad-challenge"}')
  assert.ok(!serialized.includes(secretEmail))
  assert.ok(!serialized.includes('secret_cred_id'))
})

test('signin/start never enumerates accounts: known email, unknown email, and no email return identically shaped discoverable challenges', async () => {
  const env = passkeyAccount()
  const { key, made } = await signUpAndRegister(env, 'known@example.com')

  const known = await call(env, 'POST', '/v1/passkeys/signin/start', { body: { email: 'known@example.com' } })
  const unknown = await call(env, 'POST', '/v1/passkeys/signin/start', { body: { email: 'unknown@example.com' } })
  const bare = await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} })

  for (const res of [known, unknown, bare]) {
    assert.equal(res.status, 200)
    assert.deepEqual(Object.keys(res.body).sort(), ['allowCredentials', 'challenge', 'origin', 'rpId', 'timeout', 'userVerification'])
    assert.deepEqual(res.body.allowCredentials, [])
    assert.equal(res.body.rpId, RP_ID)
    assert.equal(res.body.origin, ORIGIN)
    assert.equal(res.body.userVerification, 'required')
    assert.equal(res.body.timeout, 60000)
  }

  const storedChallenges = env.DB.rows('webauthn_challenges')
  assert.equal(storedChallenges.length, 3)
  assert.ok(storedChallenges.every(row => row.account === null))

  const assertion = await buildAssertion(key, bare.body.challenge, { signCount: 2 })
  const fin = await call(env, 'POST', '/v1/passkeys/signin/finish', {
    body: { challenge: bare.body.challenge, device: DEVICE, ...assertion },
  })
  assert.equal(fin.status, 200)
  assert.equal(fin.body.account.id, made.body.account.id)
})

test('passkey routes fail closed with 503 passkeys-unavailable when WEBAUTHN_RP_ID or WEBAUTHN_ORIGIN is missing', async () => {
  const wranglerToml = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')
  assert.match(wranglerToml, /WEBAUTHN_RP_ID\s*=\s*"accounts\.getexo\.app"/)
  assert.match(wranglerToml, /WEBAUTHN_ORIGIN\s*=\s*"https:\/\/accounts\.getexo\.app"/)

  const configs = [
    {},
    { WEBAUTHN_RP_ID: RP_ID },
    { WEBAUTHN_ORIGIN: ORIGIN },
    { WEBAUTHN_RP_ID: RP_ID, WEBAUTHN_ORIGIN: 'http://accounts.getexo.app' },
  ]

  for (const vars of configs) {
    const env = { ...account(), ...vars }
    const made = await call(env, 'POST', '/v1/account', { body: { email: 'cfg@example.com', password: PASSWORD, device: DEVICE } })
    assert.equal(made.status, 201)
    const token = made.body.token

    const routes = [
      await call(env, 'GET', '/v1/passkeys', { token }),
      await call(env, 'POST', '/v1/passkeys/register/start', { token, body: {} }),
      await call(env, 'POST', '/v1/passkeys/register/finish', { token, body: {} }),
      await call(env, 'DELETE', '/v1/passkeys/cred_missing_cfg_1', { token }),
      await call(env, 'POST', '/v1/passkeys/signin/start', { body: {} }),
      await call(env, 'POST', '/v1/passkeys/signin/finish', { body: {} }),
    ]
    for (const res of routes) {
      assert.equal(res.status, 503)
      assert.equal(res.body.error, 'passkeys-unavailable')
    }
  }
})

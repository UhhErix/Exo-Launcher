import { b64url, bytesOf, same, sha256, unb64url, utf8 } from './crypto.js'
import { refuse } from './http.js'

const P256_SPKI_PREFIX = Uint8Array.from([
  0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a,
  0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04,
])

function readCbor(bytes, offset) {
  if (offset >= bytes.length) throw new Error('cbor-eof')
  const head = bytes[offset++]
  const major = head >> 5
  const info = head & 0x1f
  let val = 0
  if (info < 24) {
    val = info
  } else if (info === 24) {
    if (offset + 1 > bytes.length) throw new Error('cbor-eof')
    val = bytes[offset++]
  } else if (info === 25) {
    if (offset + 2 > bytes.length) throw new Error('cbor-eof')
    val = (bytes[offset] << 8) | bytes[offset + 1]
    offset += 2
  } else if (info === 26) {
    if (offset + 4 > bytes.length) throw new Error('cbor-eof')
    val = ((bytes[offset] << 24) >>> 0) + (bytes[offset + 1] << 16) + (bytes[offset + 2] << 8) + bytes[offset + 3]
    offset += 4
  } else {
    throw new Error('cbor-unsupported')
  }
  if (major === 0) return [val, offset]
  if (major === 1) return [-1 - val, offset]
  if (major === 2) {
    if (offset + val > bytes.length) throw new Error('cbor-eof')
    return [bytes.subarray(offset, offset + val), offset + val]
  }
  if (major === 3) {
    if (offset + val > bytes.length) throw new Error('cbor-eof')
    return [new TextDecoder().decode(bytes.subarray(offset, offset + val)), offset + val]
  }
  if (major === 4) {
    const arr = []
    for (let i = 0; i < val; i++) {
      let item
      ;[item, offset] = readCbor(bytes, offset)
      arr.push(item)
    }
    return [arr, offset]
  }
  if (major === 5) {
    const map = new Map()
    for (let i = 0; i < val; i++) {
      let k
      let v
      ;[k, offset] = readCbor(bytes, offset)
      ;[v, offset] = readCbor(bytes, offset)
      map.set(k, v)
    }
    return [map, offset]
  }
  if (major === 7) {
    if (info === 20) return [false, offset]
    if (info === 21) return [true, offset]
    if (info === 22) return [null, offset]
  }
  throw new Error('cbor-unsupported')
}

export function decodeCbor(bytes) {
  const [value] = readCbor(bytes, 0)
  return value
}

function derLen(len) {
  if (len < 0x80) return Uint8Array.of(len)
  if (len <= 0xff) return Uint8Array.of(0x81, len)
  return Uint8Array.of(0x82, (len >> 8) & 0xff, len & 0xff)
}

function derTlv(tag, body) {
  const l = derLen(body.length)
  const out = new Uint8Array(1 + l.length + body.length)
  out[0] = tag
  out.set(l, 1)
  out.set(body, 1 + l.length)
  return out
}

function derUnsignedInt(raw) {
  let start = 0
  while (start < raw.length - 1 && raw[start] === 0) start++
  const trimmed = raw.subarray(start)
  if (trimmed[0] & 0x80) {
    const padded = new Uint8Array(trimmed.length + 1)
    padded.set(trimmed, 1)
    return derTlv(0x02, padded)
  }
  return derTlv(0x02, trimmed)
}

function concat(...parts) {
  const total = parts.reduce((sum, p) => sum + p.length, 0)
  const out = new Uint8Array(total)
  let off = 0
  for (const p of parts) {
    out.set(p, off)
    off += p.length
  }
  return out
}

export function coseToSpki(cose) {
  if (!(cose instanceof Map)) refuse(400, 'bad-passkey')
  const kty = cose.get(1)
  const alg = cose.get(3)
  if (kty === 2 && alg === -7 && cose.get(-1) === 1) {
    const x = cose.get(-2)
    const y = cose.get(-3)
    if (!(x instanceof Uint8Array) || !(y instanceof Uint8Array) || x.length !== 32 || y.length !== 32) {
      refuse(400, 'bad-passkey')
    }
    return { alg: -7, spki: concat(P256_SPKI_PREFIX, x, y) }
  }
  if (kty === 3 && alg === -257) {
    const n = cose.get(-1)
    const e = cose.get(-2)
    if (!(n instanceof Uint8Array) || !(e instanceof Uint8Array) || n.length < 128) refuse(400, 'bad-passkey')
    const rsaPub = derTlv(0x30, concat(derUnsignedInt(n), derUnsignedInt(e)))
    const bitString = derTlv(0x03, concat(Uint8Array.of(0), rsaPub))
    const algId = Uint8Array.from([0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00])
    return { alg: -257, spki: derTlv(0x30, concat(algId, bitString)) }
  }
  refuse(400, 'unsupported-algorithm')
}

function parseAuthData(authData, requireAttested) {
  if (!(authData instanceof Uint8Array) || authData.length < 37) refuse(400, 'bad-passkey')
  const rpIdHash = authData.subarray(0, 32)
  const flags = authData[32]
  const signCount = ((authData[33] << 24) >>> 0) + (authData[34] << 16) + (authData[35] << 8) + authData[36]
  if ((flags & 0x01) === 0) refuse(401, 'passkey-not-present')
  if ((flags & 0x04) === 0) refuse(401, 'passkey-uv-required')
  if (!requireAttested) return { rpIdHash, flags, signCount }
  if ((flags & 0x40) === 0 || authData.length < 55) refuse(400, 'bad-passkey')
  const credLen = (authData[53] << 8) | authData[54]
  if (credLen === 0 || credLen > 1023 || 55 + credLen >= authData.length) refuse(400, 'bad-passkey')
  const credentialId = authData.subarray(55, 55 + credLen)
  const coseBytes = authData.subarray(55 + credLen)
  let cose
  try {
    cose = decodeCbor(coseBytes)
  } catch {
    refuse(400, 'bad-passkey')
  }
  const { alg, spki } = coseToSpki(cose)
  return { rpIdHash, flags, signCount, credentialId, alg, spki }
}

function derEcdsaToP1363(sig) {
  if (sig.length === 64) return sig
  if (sig.length < 8 || sig[0] !== 0x30) refuse(401, 'bad-signature')
  let off = 2
  if (sig[1] & 0x80) off = 2 + (sig[1] & 0x7f)
  if (sig[off++] !== 0x02) refuse(401, 'bad-signature')
  const rLen = sig[off++]
  const rRaw = sig.subarray(off, off + rLen)
  off += rLen
  if (sig[off++] !== 0x02) refuse(401, 'bad-signature')
  const sLen = sig[off++]
  const sRaw = sig.subarray(off, off + sLen)
  const out = new Uint8Array(64)
  const place = (raw, dstOffset) => {
    let start = 0
    while (start < raw.length - 1 && raw[start] === 0) start++
    const trimmed = raw.subarray(start)
    if (trimmed.length > 32) refuse(401, 'bad-signature')
    out.set(trimmed, dstOffset + (32 - trimmed.length))
  }
  place(rRaw, 0)
  place(sRaw, 32)
  return out
}

async function importPasskeyPublicKey(spki, alg) {
  if (alg === -7) {
    return crypto.subtle.importKey('spki', spki, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
  }
  if (alg === -257) {
    return crypto.subtle.importKey('spki', spki, { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['verify'])
  }
  refuse(400, 'unsupported-algorithm')
}

function parseClientData(clientDataB64, expectedType, expectedChallenge, expectedOrigin) {
  const bytes = unb64url(clientDataB64)
  if (!bytes || bytes.length > 4096) refuse(400, 'bad-passkey')
  let parsed
  try {
    parsed = JSON.parse(new TextDecoder().decode(bytes))
  } catch {
    refuse(400, 'bad-passkey')
  }
  if (!parsed || typeof parsed !== 'object') refuse(400, 'bad-passkey')
  if (parsed.type !== expectedType) refuse(401, 'bad-passkey')
  if (!same(parsed.challenge, expectedChallenge)) refuse(401, 'bad-challenge')
  if (!same(parsed.origin, expectedOrigin)) refuse(401, 'bad-origin')
  return bytes
}

export async function verifyRegistration({ rpId, expectedOrigin, expectedChallenge, clientDataJSON, attestationObject }) {
  parseClientData(clientDataJSON, 'webauthn.create', expectedChallenge, expectedOrigin)
  const attBytes = unb64url(attestationObject)
  if (!attBytes || attBytes.length > 16384) refuse(400, 'bad-passkey')
  let att
  try {
    att = decodeCbor(attBytes)
  } catch {
    refuse(400, 'bad-passkey')
  }
  if (!(att instanceof Map)) refuse(400, 'bad-passkey')
  const authData = att.get('authData')
  const parsed = parseAuthData(authData, true)
  const expectedRpHash = await sha256(utf8(rpId))
  if (!same(b64url(parsed.rpIdHash), b64url(expectedRpHash))) refuse(401, 'bad-rp')
  await importPasskeyPublicKey(parsed.spki, parsed.alg)
  return {
    credentialId: b64url(parsed.credentialId),
    publicKeySpki: parsed.spki,
    alg: parsed.alg,
    signCount: parsed.signCount,
  }
}

export async function verifyAssertion({
  rpId,
  expectedOrigin,
  expectedChallenge,
  storedSpki,
  storedAlg,
  storedSignCount,
  clientDataJSON,
  authenticatorData,
  signature,
}) {
  const clientDataBytes = parseClientData(clientDataJSON, 'webauthn.get', expectedChallenge, expectedOrigin)
  const authBytes = unb64url(authenticatorData)
  const sigBytes = unb64url(signature)
  if (!authBytes || !sigBytes || authBytes.length > 4096 || sigBytes.length > 1024) refuse(400, 'bad-passkey')
  const parsed = parseAuthData(authBytes, false)
  const expectedRpHash = await sha256(utf8(rpId))
  if (!same(b64url(parsed.rpIdHash), b64url(expectedRpHash))) refuse(401, 'bad-rp')
  const clientHash = await sha256(clientDataBytes)
  const signedData = concat(authBytes, clientHash)
  const key = await importPasskeyPublicKey(bytesOf(storedSpki), storedAlg)
  const verifyAlg = storedAlg === -7 ? { name: 'ECDSA', hash: 'SHA-256' } : { name: 'RSASSA-PKCS1-v1_5' }
  const normalizedSig = storedAlg === -7 ? derEcdsaToP1363(sigBytes) : sigBytes
  const valid = await crypto.subtle.verify(verifyAlg, key, normalizedSig, signedData)
  if (!valid) refuse(401, 'bad-signature')
  const signCountRegression = (storedSignCount > 0 || parsed.signCount > 0) && parsed.signCount <= storedSignCount
  return { signCount: parsed.signCount, signCountRegression }
}

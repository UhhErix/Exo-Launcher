/**
 * Byte and key helpers on WebCrypto alone: base64url, hex, SHA-256, a constant-time compare, HMAC, HKDF-SHA256 keys,
 * and AES-256-GCM seals kept as nonce || ciphertext.
 */

export const utf8 = text => new TextEncoder().encode(text)
export const random = length => crypto.getRandomValues(new Uint8Array(length))
const NONE = new Uint8Array(0)

export function b64url(bytes) {
  let text = ''
  for (const b of bytes) text += String.fromCharCode(b)
  return btoa(text).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '')
}

/** The bytes, or null for anything that isn't unpadded base64url. */
export function unb64url(text) {
  if (typeof text !== 'string' || !/^[A-Za-z0-9_-]*$/.test(text) || text.length % 4 === 1) return null
  const binary = atob(text.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - (text.length % 4)) % 4))
  return Uint8Array.from(binary, c => c.charCodeAt(0))
}

export const hex = bytes => [...bytes].map(b => b.toString(16).padStart(2, '0')).join('')

/** A BLOB as D1 gives it back: an ArrayBuffer, or an array of numbers from the local stand-in. */
export const bytesOf = stored => (stored instanceof ArrayBuffer ? new Uint8Array(stored) : Uint8Array.from(stored))

export async function sha256(data) {
  return new Uint8Array(await crypto.subtle.digest('SHA-256', typeof data === 'string' ? utf8(data) : data))
}

/** Equal strings, compared in a time that depends only on their length (every value compared here has a fixed one). */
export function same(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false
  let diff = 0
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i)
  return diff === 0
}

async function hkdf(ikm, info, salt, algorithm, usages) {
  const base = await crypto.subtle.importKey('raw', ikm, 'HKDF', false, ['deriveKey'])
  return crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: utf8(info) }, base, algorithm, false, usages)
}

/** An HMAC-SHA256 key for one use (info), derived from ikm. */
export const macKey = (ikm, info) => hkdf(ikm, info, NONE, { name: 'HMAC', hash: 'SHA-256', length: 256 }, ['sign', 'verify'])

/** An AES-256-GCM key for one use (info), derived from ikm and salt. */
export const sealKey = (ikm, info, salt = NONE) => hkdf(ikm, info, salt, { name: 'AES-GCM', length: 256 }, ['encrypt', 'decrypt'])

export async function mac(key, text) {
  return new Uint8Array(await crypto.subtle.sign('HMAC', key, utf8(text)))
}

/** text sealed with a random 12-byte nonce: nonce || ciphertext, the 16-byte tag at its end. */
export async function seal(key, text, aad = NONE) {
  const nonce = random(12)
  const sealed = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad }, key, utf8(text)))
  const out = new Uint8Array(12 + sealed.length)
  out.set(nonce)
  out.set(sealed, 12)
  return out
}

/** What seal() sealed, from the stored bytes. Throws when the tag doesn't check. */
export async function unseal(key, stored, aad = NONE) {
  const bytes = bytesOf(stored)
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: bytes.subarray(0, 12), additionalData: aad }, key, bytes.subarray(12))
  return new TextDecoder().decode(plain)
}

/**
 * The profile's media too big for a box row (account plan item 6): end-to-end encrypted parts in R2, under the box that
 * owns them. The Worker sees a box id, a part's opaque name and its ciphertext: never the file, its hash or its type.
 *
 * PUT, GET or DELETE /v1/media/<box: 32 hex>/<name: 64 hex>, with
 *   x-exo-key   the box's public key, SEC1 uncompressed, base64url (the box id is the first 16 bytes of its SHA-256)
 *   x-exo-time  Unix seconds, within 5 minutes of the Worker's clock
 *   x-exo-sig   P1363 ECDSA P-256 SHA-256, low s, base64url, over
 *               "exo-media/1" || method || box id (16 bytes) || name (ASCII) || time (ASCII) || SHA-256(body)
 * PUT stores the body (at most limits.mediaPart bytes) unless the box already holds limits.mediaBox bytes; GET answers
 * it; DELETE removes it. 401 for a signature that doesn't hold, 404 for none, 413 too big, 507 the box is full, 503 when
 * the Worker has no MEDIA bucket bound.
 */
import { limits } from './limits.js'

export const MEDIA_PATH = /^\/v1\/media\/([0-9a-f]{32})\/([0-9a-f]{64})$/
const LABEL = new TextEncoder().encode('exo-media/1')
const HALF_ORDER = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551n >> 1n

export async function media(request, env, box, name) {
  if (!env.MEDIA) return new Response('No media here', { status: 503 })
  const method = request.method
  if (method !== 'PUT' && method !== 'GET' && method !== 'DELETE') return new Response('No', { status: 405 })
  const length = Number(request.headers.get('Content-Length') ?? 0)
  if (method === 'PUT' && !(length > 0 && length <= limits.mediaPart)) return new Response('Too big', { status: 413 })
  const body = method === 'PUT' ? new Uint8Array(await request.arrayBuffer()) : new Uint8Array(0)
  if (body.length > limits.mediaPart) return new Response('Too big', { status: 413 })
  if (!(await signed(request, method, box, name, body))) return new Response('No', { status: 401 })
  const key = `${box}/${name}`
  if (method === 'GET') {
    const object = await env.MEDIA.get(key)
    return object ? new Response(object.body, { headers: { 'Content-Type': 'application/octet-stream' } }) : new Response('None', { status: 404 })
  }
  if (method === 'DELETE') {
    await env.MEDIA.delete(key)
    return new Response(null, { status: 204 })
  }
  if ((await used(env, box)) + body.length > limits.mediaBox) return new Response('Full', { status: 507 })
  await env.MEDIA.put(key, body)
  return new Response(null, { status: 201 })
}

/** The bytes a box holds now. */
async function used(env, box) {
  let total = 0
  let cursor
  do {
    const page = await env.MEDIA.list({ prefix: `${box}/`, cursor })
    for (const object of page.objects) total += object.size
    cursor = page.truncated ? page.cursor : undefined
  } while (cursor)
  return total
}

/** What a box key signs for a media request: "exo-media/1" || method || box id (16 bytes) || name || time || SHA-256(body). */
export function mediaMessage(method, boxId, name, time, bodyHash) {
  const text = new TextEncoder()
  return new Uint8Array([...LABEL, ...text.encode(method), ...boxId, ...text.encode(name), ...text.encode(time), ...bodyHash])
}

async function signed(request, method, box, name, body) {
  const publicKey = unbase64url(request.headers.get('x-exo-key') ?? '')
  const signature = unbase64url(request.headers.get('x-exo-sig') ?? '')
  const time = request.headers.get('x-exo-time') ?? ''
  if (!publicKey || publicKey.length !== 65 || publicKey[0] !== 4 || !signature || signature.length !== 64 || !/^[0-9]{1,12}$/.test(time)) return false
  if (Math.abs(Date.now() / 1000 - Number(time)) > 300) return false
  if (BigInt('0x' + hex(signature.subarray(32, 64))) > HALF_ORDER) return false
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', publicKey))
  if (hex(hash.subarray(0, 16)) !== box) return false
  const bodyHash = new Uint8Array(await crypto.subtle.digest('SHA-256', body))
  const message = mediaMessage(method, hash.subarray(0, 16), name, time, bodyHash)
  try {
    const key = await crypto.subtle.importKey('raw', publicKey, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
    return await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, signature, message)
  } catch {
    return false
  }
}

function unbase64url(text) {
  if (!/^[A-Za-z0-9_-]*$/.test(text)) return null
  const binary = atob(text.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - text.length % 4) % 4))
  return Uint8Array.from(binary, c => c.charCodeAt(0))
}

function hex(bytes) {
  return [...bytes].map(b => b.toString(16).padStart(2, '0')).join('')
}

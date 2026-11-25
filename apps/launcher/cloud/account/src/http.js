/** Answers and refusals: JSON with no-store, no CORS, and a body read as JSON only (16 KiB unless a route says more). */
import { limits } from './limits.js'

export class Refusal extends Error {
  constructor(status, code, headers = {}) {
    super(code)
    this.status = status
    this.code = code
    this.headers = headers
  }
}

export const refuse = (status, code, headers) => {
  throw new Refusal(status, code, headers)
}

/** On every answer: HSTS for the host (a year; no includeSubDomains, no preload) and no sniffing of a body's type. */
const SECURED = { 'Strict-Transport-Security': 'max-age=31536000', 'X-Content-Type-Options': 'nosniff' }

export const json = (value, status = 200, headers = {}) => new Response(JSON.stringify(value), {
  status,
  headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...SECURED, ...headers },
})

export const done = () => new Response(null, { status: 204, headers: { 'Cache-Control': 'no-store', ...SECURED } })

export const notModified = (headers = {}) => new Response(null, { status: 304, headers: { 'Cache-Control': 'no-store', ...SECURED, ...headers } })

/**
 * The client address as the limits key it: an IPv4 address as sent, an IPv6 address as its /56 (a site or household holds
 * a /56 or larger, and a /64 is one host's share of it: keying by /56 blunts cheap rotation through IPv6). Anything else is
 * keyed as it was sent.
 */
export const address = request => {
  const ip = request.headers.get('CF-Connecting-IP') ?? ''
  return prefix56(ip) ?? ip
}

/**
 * The /56 of an IPv6 address as "x:x:x:y::/56" (lowercase, no leading zeros; y keeps the top 8 bits of the fourth group), or
 * null when text isn't an IPv6 address or is an IPv4-mapped one (those are keyed as sent, so each IPv4 client keeps its own key).
 */
export function prefix56(text) {
  if (!text.includes(':') || !/^[0-9a-fA-F:.]+$/.test(text)) return null
  const halves = text.split('::')
  if (halves.length > 2) return null
  const parts = halves.map(part => (part === '' ? [] : part.split(':')))
  const last = parts[parts.length - 1]
  if (last.length && last[last.length - 1].includes('.')) {
    const quad = last.pop().split('.').map(Number)
    if (quad.length !== 4 || quad.some(n => !Number.isInteger(n) || n < 0 || n > 255)) return null
    last.push(((quad[0] << 8) | quad[1]).toString(16), ((quad[2] << 8) | quad[3]).toString(16))
  }
  const named = parts.flat()
  if (halves.length === 2 && named.length > 7) return null
  const groups = halves.length === 2 ? [...parts[0], ...Array(8 - named.length).fill('0'), ...parts[1]] : named
  if (groups.length !== 8 || groups.some(group => !/^[0-9a-fA-F]{1,4}$/.test(group))) return null
  if (groups.slice(0, 5).every(group => parseInt(group, 16) === 0) && parseInt(groups[5], 16) === 0xffff) return null
  const [a, b, c, d] = groups.slice(0, 4).map(group => parseInt(group, 16))
  return `${[a, b, c, d & 0xff00].map(group => group.toString(16)).join(':')}::/56`
}

/** A JSON object from the body: application/json only (so no browser form can post one), limit bytes at most. */
export async function body(request, limit = limits.bodyBytes) {
  if (!/^application\/json(?:\s*;|$)/i.test(request.headers.get('Content-Type') ?? '')) refuse(415, 'json-only')
  let value
  try {
    value = JSON.parse(await text(request, limit))
  } catch (error) {
    if (error instanceof Refusal) throw error
    refuse(400, 'bad-json')
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) refuse(400, 'bad-json')
  return value
}

/** One read of the body: it must come within limits.bodyReadMs (an idle deadline, reset by each chunk, not a total). */
const readWithin = reader => {
  let timer
  const idle = new Promise((_, reject) => {
    timer = setTimeout(() => {
      // Rejected before the reader is cancelled: a cancelled read would otherwise settle the race as a short body.
      reject(new Refusal(408, 'slow-body'))
      reader.cancel().catch(() => {})
    }, limits.bodyReadMs)
  })
  return Promise.race([reader.read(), idle]).finally(() => clearTimeout(timer))
}

/**
 * The body as text, read in full. Two deadlines: each chunk must arrive within limits.bodyReadMs of the one before it (idle),
 * and the whole body within limits.bodyMaxMs. Either one passing cancels the body and answers 408 slow-body. The idle one
 * alone lets a slow sender keep a body open for as long as its chunks keep coming; the total caps that.
 */
async function text(request, limit) {
  if (Number(request.headers.get('Content-Length') ?? 0) > limit) refuse(413, 'too-big')
  if (!request.body) return ''
  const reader = request.body.getReader()
  let timer
  const total = new Promise((_, reject) => {
    timer = setTimeout(() => {
      reject(new Refusal(408, 'slow-body'))
      reader.cancel().catch(() => {})
    }, limits.bodyMaxMs)
  })
  return Promise.race([collect(reader, limit), total]).finally(() => clearTimeout(timer))
}

async function collect(reader, limit) {
  const chunks = []
  let size = 0
  for (;;) {
    const { done: finished, value } = await readWithin(reader)
    if (finished) break
    size += value.byteLength
    if (size > limit) {
      await reader.cancel().catch(() => {})
      refuse(413, 'too-big')
    }
    chunks.push(value)
  }
  const bytes = new Uint8Array(size)
  let at = 0
  for (const chunk of chunks) {
    bytes.set(chunk, at)
    at += chunk.byteLength
  }
  return new TextDecoder().decode(bytes)
}

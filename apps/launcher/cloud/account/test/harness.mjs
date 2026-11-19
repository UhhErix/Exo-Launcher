/**
 * The test rig for the account Worker: a fresh env per test (the D1 stand-in, a random ACCOUNT_SECRET and a rate limit),
 * requests into the real worker.js, and a clock that moves only when a test says so.
 */
import { hex, random } from '../src/crypto.js'
import { Database } from './d1.mjs'

/** The rate-limit binding: at most `limit` calls per key in `period` seconds. */
export class RateLimit {
  hits = new Map()

  constructor({ limit, period }) {
    this.max = limit
    this.ms = period * 1000
  }

  async limit({ key }) {
    const now = Date.now()
    const recent = (this.hits.get(key) ?? []).filter(at => now - at < this.ms)
    const success = recent.length < this.max
    if (success) recent.push(now)
    this.hits.set(key, recent)
    return { success }
  }
}

export const account = () => ({
  DB: new Database(),
  ACCOUNT_SECRETS: `k1:${hex(random(32))}`,
  SESSION_TRIES: new RateLimit({ limit: 10, period: 60 }),
})

let requests = 0

/** One request into the Worker. Each call comes from its own address unless ip says otherwise (the limit is per address). */
export async function call(env, method, path, { body, token, ip, headers = {} } = {}) {
  const worker = (await import('../src/worker.js')).default
  requests++
  const init = { method, headers: { 'CF-Connecting-IP': ip ?? `10.0.${Math.floor(requests / 256)}.${requests % 256}`, ...headers } }
  if (token) init.headers.Authorization = `Bearer ${token}`
  if (body !== undefined) {
    init.body = typeof body === 'string' ? body : JSON.stringify(body)
    init.headers['Content-Type'] ??= 'application/json'
  }
  const response = await worker.fetch(new Request(`https://accounts.example${path}`, init), env)
  const text = await response.text()
  return { status: response.status, headers: response.headers, body: text ? JSON.parse(text) : null }
}

const realNow = Date.now.bind(Date)
let offset = 0

/** Moves the clock forward by seconds (cumulative). resetClock() puts it back. */
export function later(seconds) {
  offset += seconds * 1000
  Date.now = () => realNow() + offset
}

export function resetClock() {
  offset = 0
  Date.now = realNow
}

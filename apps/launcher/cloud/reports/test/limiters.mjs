/**
 * Fake rate limiting bindings for the tests. `limiters({ allow })` answers every call the same and records it.
 * `configuredLimiters()` behaves like Cloudflare's: a count per key that refuses past the limit wrangler.toml sets for the
 * binding, so a test can put many installs or one flooding sender through the real numbers.
 */
import { readFileSync } from 'node:fs'

export const LIMITER_NAMES = ['REPORTS_PER_INSTALL', 'REPORTS_PER_NETWORK', 'REPORTS_CEILING']

/** Each binding's `limit` from wrangler.toml's top level (the live Worker, not [env.dev]). */
export function configuredLimits() {
  const toml = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')
  const top = toml.split(/^\[env\./m)[0]
  const out = {}
  for (const block of top.split(/^\[\[ratelimits\]\]/m).slice(1)) {
    const name = /^name = "([A-Z_]+)"/m.exec(block)?.[1]
    const limit = /^simple = \{ limit = (\d+), period = 60 \}/m.exec(block)?.[1]
    if (name && limit) out[name] = Number(limit)
  }
  return out
}

/** Bindings that allow (or refuse) every call; `calls` is every call in order, as `{ binding, key }`. */
export function limiters({ allow = true } = {}) {
  const calls = []
  const bindings = {}
  for (const binding of LIMITER_NAMES) {
    bindings[binding] = {
      async limit(options) {
        calls.push({ binding, ...options })
        return { success: typeof allow === 'function' ? allow(binding, options.key) : allow }
      },
    }
  }
  return { bindings, calls }
}

/** Bindings that count per key within one period, refusing past wrangler.toml's limit, as the edge does. */
export function configuredLimiters() {
  const limits = configuredLimits()
  const counts = new Map()
  return limiters({
    allow(binding, key) {
      const id = `${binding}|${key}`
      counts.set(id, (counts.get(id) ?? 0) + 1)
      return counts.get(id) <= limits[binding]
    },
  })
}

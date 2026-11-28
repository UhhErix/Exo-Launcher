/**
 * Exo cover cache (README.md): GET /v1/cover?title=&id=&store=steam|gog (a store only with its id) answers with a store
 * picture's address, found once from keyless store data and then kept for everyone. Steam (its CDN's 600x900 poster, found
 * by app id or by a strict title search), GOG (its vertical cover, by product id or by a strict title search) and Epic
 * (by the title's product slug). GET /health says whether KV works, with no personal data.
 *
 * - A hit is kept in KV for 30 days: under an id's key only what that id's own lookup returned, under a title's key only
 *   what the title search returned (so a caller cannot pin a picture on an id by sending a title with it).
 * - A miss is never final for the PC. The Worker remembers a clean miss (every lookup answered, none had it) for an hour,
 *   only to spare the stores; the Launcher may still search itself and has its own miss memory. A miss with a store that
 *   could not be asked says `partial` and is not remembered.
 * - Misses, backoffs and the KV failure count live in the Cache API, never KV, so misses cannot use up KV's write quota.
 * - A store that refuses or fails (429, 5xx, 401, 403, 408, a timeout, an odd status, a body that is not JSON) is left alone
 *   for the time it asked (Retry-After, at most fifteen minutes) and the next store is asked.
 * - An address asking for uncached titles is limited to 20 a minute and to its share of store requests; all store requests
 *   from one Cloudflare location have a ceiling (rate limits count per location). Past the ceiling an ask is answered
 *   `partial`, never as an outage.
 * Only the title, store and id are read; the address is used for a minute as the rate-limit key and not stored. No logs.
 */
import { acceptScore, isJunkName, normalizeTitle, scoreTitleMatch } from './match.js'
import limits from './limits.json'
import { counted } from './signal.js'

const HIT_TTL_SECONDS = 30 * 86400
const MISS_TTL_SECONDS = 3600
const MAX_BACKOFF_SECONDS = 900
const RATE_LIMIT_BACKOFF_SECONDS = 60
const FAILURE_BACKOFF_SECONDS = 300
const UPSTREAM_TIMEOUT_MS = 3000
/** One ask never runs longer than this in total, however many stores are slow: the Launcher waits longer than this (limits.json is the one number both sides read).
 * Not exported: workerd takes every named export of the main module as an entrypoint and refuses to start on a number. */
const TOTAL_BUDGET_MS = limits.totalBudgetMs
const MAX_TITLE_BYTES = 200
const ID = /^\d{1,12}$/
const STORES = ['steam', 'gog', 'epic']

/** Steam's poster, newest form first (the same order the Launcher probes). */
const STEAM_POSTERS = [
  'https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900_2x.jpg',
  'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/library_600x900_2x.jpg',
  'https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_600x900.jpg',
]
const MIN_POSTER_BYTES = 12_000

/** A store refused or failed; that store is left alone for a while and the next one is tried. */
class Unavailable extends Error {}

/** This address asked for too many uncached titles (or store requests) this minute. */
class Limited extends Error {}

/** The ceiling on store requests from this location is reached: not a store's fault, and no reason to say anything is down. */
class Capped extends Error {}

class Bad extends Error {
  constructor(message) {
    super(message)
    this.status = 400
  }
}

/** The time one upstream request may take: its own limit, cut short by what is left of the whole ask. 0 or less means none. */
export function timeLeft(startedAt, now) {
  return Math.min(UPSTREAM_TIMEOUT_MS, TOTAL_BUDGET_MS - (now - startedAt))
}

/** A status that means the store is refusing or failing us, not that it has no such game. */
export function storeIsRefusing(status, strict) {
  return status === 429 || status >= 500 || (strict && (status === 401 || status === 403 || status === 408))
}

/** How long a refusing store is left alone: what it asked for (at most fifteen minutes), a short time for a bare 429, else five minutes. */
export function backoffSeconds(status, retryAfter) {
  if (status === 429) {
    const asked = Number(retryAfter)
    return Number.isFinite(asked) && asked > 0 ? Math.min(Math.ceil(asked), MAX_BACKOFF_SECONDS) : RATE_LIMIT_BACKOFF_SECONDS
  }
  return FAILURE_BACKOFF_SECONDS
}

const ROUTES = [['cover', /^\/v1\/cover$/], ['health', /^\/health$/]]

export default counted('covers', ROUTES, {
  async fetch(request, env, ctx) {
    const url = new URL(request.url)
    if (request.method !== 'GET' || (url.pathname !== '/v1/cover' && url.pathname !== '/health')) return json({ ok: false, message: 'Not found.' }, 404)

    // Every route, /health included, is behind the per-address limit.
    const address = request.headers.get('CF-Connecting-IP') ?? 'unknown'
    if (env.COVER_LIMIT && !(await env.COVER_LIMIT.limit({ key: address })).success) return tooMany()

    if (url.pathname === '/health') return health(env)

    let input
    try {
      input = parseInput(url.searchParams)
    } catch (e) {
      if (e instanceof Bad) return json({ ok: false, message: e.message }, 400)
      throw e
    }

    try {
      const found = await cover(input, env, ctx, address)
      if (found.url) return json({ ok: true, url: found.url, source: found.source }, 200, { 'Cache-Control': 'public, max-age=86400, stale-while-revalidate=604800' })
      // partial: some store could not be asked, so this is not a final answer and nothing was remembered.
      return json(found.partial ? { ok: false, miss: true, partial: true } : { ok: false, miss: true }, 404)
    } catch (e) {
      if (e instanceof Limited) return tooMany()
      if (e instanceof Unavailable) return json({ ok: false, retry: true }, 503, { 'Retry-After': String(MAX_BACKOFF_SECONDS) })
      return json({ ok: false, message: 'The cover cache had a problem. Try again.' }, 500)
    }
  },
})

/**
 * Is KV working, how many KV reads or writes failed in the last hour here, how many stores are backed off (a count, not which:
 * that would tell a caller which store its pressure is working on). It is behind the per-address limit like every ask, and it
 * does not read KV on each call: the probe's result is kept for 60 seconds. Nothing about anyone.
 */
async function health(env) {
  let state = await flagGet('health:kv')
  if (state === null) {
    try {
      await env.COVERS.get('health:probe')
      state = 'ok'
    } catch {
      state = 'down'
      await recordKvFailure()
    }
    await flagSet('health:kv', state, 60)
  }
  let backedOff = 0
  for (const store of STORES) if (await flagGet(`backoff:${store}`)) backedOff++
  const failures = (await kvFailureTimes()).length
  const kvWorks = state === 'ok'
  return json({ ok: kvWorks, kvFailuresLastHour: failures, storesBackedOffCount: backedOff }, kvWorks ? 200 : 503)
}

function tooMany() {
  return json({ ok: false, message: 'Slow down, then try again in a minute.' }, 429, { 'Retry-After': '60' })
}

/** Validates the query: a title, or a store id with its store (and optionally the title). Anything else is refused. */
export function parseInput(params) {
  const title = (params.get('title') ?? '').trim()
  const store = params.get('store')
  const id = params.get('id')
  // KV keys are at most 512 bytes: the limit is bytes, not characters.
  if (new TextEncoder().encode(title).length > MAX_TITLE_BYTES) throw new Bad('The title is too long.')
  if (store !== null && store !== 'steam' && store !== 'gog') throw new Bad('Unknown store.')
  // A store without its id would ask one store about a title and then answer for all of them.
  if (store !== null && id === null) throw new Bad('A store needs its id.')
  if (id !== null && (!ID.test(id) || store === null)) throw new Bad('A store id needs its store, and is digits only.')
  const norm = normalizeTitle(title)
  if (id === null && norm.length === 0) throw new Bad('Send a title or a store id.')
  return { norm, store, id }
}

// ---- State in the Cache API (backoffs, failure records, final misses): never KV. A failed write is never fatal. ----

const STATE = 'https://covers.getexo.app/__state/'
const stateRequest = (name) => new Request(STATE + encodeURIComponent(name))

export async function flagGet(name) {
  try {
    const response = await caches.default.match(stateRequest(name))
    return response ? await response.text() : null
  } catch {
    return null
  }
}

const written = new Set()

/** For the tests: forgets every flag this isolate wrote (the Cache API cannot be listed). */
export async function forgetAllFlags() {
  for (const name of [...written]) await flagDelete(name)
  written.clear()
}

export async function flagSet(name, value, ttl) {
  written.add(name)
  try {
    await caches.default.put(stateRequest(name), new Response(value, { headers: { 'Cache-Control': `public, max-age=${ttl}` } }))
  } catch {
    // the state is an optimization: losing it only means asking a store again
  }
}

async function flagDelete(name) {
  try {
    await caches.default.delete(stateRequest(name))
  } catch {
    // see flagSet
  }
}

async function kvGet(env, key) {
  try {
    return await env.COVERS.get(key, 'json')
  } catch {
    await recordKvFailure()
    return null // KV unavailable: ask the stores instead
  }
}

/** KV failures in the last hour, counted in the Cache API (per Cloudflare location) so a KV that fails for good shows on /health. */
async function recordKvFailure() {
  const recent = await kvFailureTimes()
  recent.push(Date.now())
  await flagSet('kvfail', JSON.stringify(recent.slice(-200)), 3600)
}

async function kvFailureTimes() {
  try {
    const times = JSON.parse((await flagGet('kvfail')) ?? '[]')
    return Array.isArray(times) ? times.filter((t) => Number.isFinite(t) && Date.now() - t < 3600 * 1000) : []
  } catch {
    return []
  }
}

/**
 * The answer for one ask. What is stored under which key matters: under an id's key goes ONLY what that id's own lookup
 * returned, and a hit found by searching a title goes under the title's key only. Otherwise one caller could pair an id
 * with any title and pin that title's picture on the id for 30 days for everyone.
 */
async function cover(input, env, ctx, address) {
  const idKey = input.id !== null ? `${input.store}:${input.id}` : null
  const titleKey = input.norm.length > 0 ? `title:${input.norm}` : null
  const kvKey = (key) => `cover:v1:${key}`

  if (idKey) {
    const byId = await kvGet(env, kvKey(idKey))
    if (byId?.url) return byId
    // An id-only ask that a store already answered "no" an hour ago is not asked again.
    if (!titleKey && (await flagGet(`miss:${idKey}`)) === '1') return {}
  } else {
    const byTitle = await kvGet(env, kvKey(titleKey))
    if (byTitle?.url) return byTitle
    if ((await flagGet(`miss:${titleKey}`)) === '1') return {}
  }

  // Only an uncached ask can cost store requests, so only it counts against the address.
  if (env.COVER_LOOKUP_LIMIT && !(await env.COVER_LOOKUP_LIMIT.limit({ key: address })).success) throw new Limited()

  const ask = { env, address, startedAt: Date.now() }
  let found = null
  let foundByTitle = false
  let titleSearched = false
  let capped = false
  const failed = new Set()
  // One store failing never stops the next: each step is tried on its own.
  const attempt = async (store, step) => {
    if (capped) return
    try {
      found = await step()
    } catch (e) {
      if (e instanceof Capped) capped = true
      else if (e instanceof Unavailable) failed.add(store)
      else throw e
    }
  }
  const searchTitle = async () => {
    const cachedTitle = await kvGet(env, kvKey(titleKey))
    if (cachedTitle?.url) {
      found = { ...cachedTitle, cached: true }
    } else if ((await flagGet(`miss:${titleKey}`)) !== '1') {
      titleSearched = true
      if (!found) await attempt('steam', () => steamByTitle(input.norm, ask))
      if (!found) await attempt('gog', () => gogByTitle(input.norm, ask))
      if (!found) await attempt('epic', () => epicByTitle(input.norm, ask))
    }
    foundByTitle = found !== null
  }
  if (idKey) {
    await attempt(input.store, () => (input.store === 'steam' ? steamById(input.id, ask) : gogById(input.id, ask)))
    // The id's store had nothing (or could not answer): a title, when one was sent, is searched in every store.
    if (!found && titleKey) await searchTitle()
  } else {
    await searchTitle()
  }

  if (found) {
    // Only a lookup made now is written: under the id's key what the id returned, under the title's key what the title search returned.
    if (!found.cached) keep(ctx, env, env.COVERS.put(kvKey(foundByTitle ? titleKey : idKey), JSON.stringify(found), { expirationTtl: HIT_TTL_SECONDS }))
    return found.cached ? { url: found.url, source: found.source } : found
  }
  // Every store failed: an outage, not a miss. Nothing is remembered.
  if (failed.size >= STORES.length) throw new Unavailable()
  // Some store could not be asked, or the ceiling on store requests was reached: say there is no art for now, remember nothing.
  if (failed.size > 0 || capped) return { partial: true }
  // Every lookup made answered cleanly and none had it: remembered for an hour, only to spare the stores. The PC may still look itself.
  // Only a search that actually ran writes it: an ask that skipped the title (its miss was already remembered) must not keep that miss alive.
  if (titleSearched || !titleKey) keep(ctx, env, flagSet(`miss:${titleKey ?? idKey}`, '1', MISS_TTL_SECONDS))
  return {}
}

/** A write that must not fail the answer or hold it up: it runs after the response, and its failure is swallowed. */
function keep(ctx, env, promise) {
  const safe = Promise.resolve(promise).catch(() => recordKvFailure())
  if (ctx?.waitUntil) ctx.waitUntil(safe)
}

async function steamById(id, ask) {
  for (const template of STEAM_POSTERS) {
    const url = template.replace('{id}', id)
    const response = await upstream(url, { method: 'HEAD' }, 'steam', ask, { strict: false })
    if (response.status === 200) {
      const length = Number(response.headers.get('content-length') ?? MIN_POSTER_BYTES)
      if (length >= MIN_POSTER_BYTES) return { url, source: 'steam' }
    }
  }
  return null
}

async function steamByTitle(norm, ask) {
  const data = await getJson(
    `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(norm)}&l=english&cc=US`,
    'steam',
    ask,
  )
  let best = null
  let bestScore = -1
  for (const item of data?.items ?? []) {
    if (item.type && item.type !== 'app') continue
    const id = String(item.id ?? '')
    if (!ID.test(id)) continue
    const name = normalizeTitle(item.name)
    if (isJunkName(item.name, norm)) continue
    const score = scoreTitleMatch(norm, name)
    if (score > bestScore) {
      bestScore = score
      best = id
    }
  }
  if (best === null || bestScore < acceptScore(norm)) return null
  return steamById(best, ask)
}

async function gogById(id, ask) {
  // GOG answers 404 for a product that does not exist: that is no art, not a failure.
  const data = await getJson(`https://api.gog.com/v2/games/${id}`, 'gog', ask, { allow404: true })
  const href = data?._links?.boxArtImage?.href
  if (typeof href !== 'string') return null
  return gogArt(href.replace('{formatter}', '_glx_vertical_cover'))
}

async function gogByTitle(norm, ask) {
  const data = await getJson(
    `https://catalog.gog.com/v1/catalog?limit=10&query=like:${encodeURIComponent(norm)}&productType=in:game,package&order=desc:score`,
    'gog',
    ask,
  )
  let best = null
  let bestScore = -1
  for (const product of data?.products ?? []) {
    if (typeof product.title !== 'string' || typeof product.coverVertical !== 'string') continue
    const score = scoreTitleMatch(norm, normalizeTitle(product.title))
    if (score > bestScore && score >= acceptScore(norm)) {
      bestScore = score
      best = product.coverVertical
    }
  }
  return best ? gogArt(best) : null
}

/**
 * Epic, the way the Launcher's EpicCatalogArt does it: the store-content product whose slug is the normalized title
 * (a 404 is no Epic product), and the first portrait (1200x1600) on Epic's own CDN in that product's JSON.
 */
async function epicByTitle(norm, ask) {
  const slug = norm.replaceAll(' ', '-')
  if (slug.length < 2) return null
  const data = await getJson(`https://store-content.ak.epicgames.com/api/en-US/content/products/${slug}`, 'epic', ask, { allow404: true })
  if (data === null) return null
  for (const text of strings(data)) {
    if (text.includes('1200x1600') && epicArt(text)) return { url: text, source: 'epic' }
  }
  return null
}

/** Every string in a JSON value, depth first, the Launcher's WalkStrings order. */
function* strings(value) {
  if (typeof value === 'string') {
    if (value.length > 0) yield value
  } else if (Array.isArray(value)) {
    for (const child of value) yield* strings(child)
  } else if (value && typeof value === 'object') {
    for (const key of ['portraitBackgroundImageUrl', 'src', 'url']) {
      if (typeof value[key] === 'string' && value[key].length > 0) yield value[key]
    }
    for (const child of Object.values(value)) yield* strings(child)
  }
}

/** Epic's own art hosts only (CoverArtService.IsOfficialEpicPortraitCdn). */
function epicArt(url) {
  const found = safeUrl(url)
  return found !== null && (found.hostname === 'epicgames.com' || found.hostname.endsWith('.epicgames.com') ||
    found.hostname === 'unrealengine.com' || found.hostname.endsWith('.unrealengine.com'))
}

function gogArt(url) {
  const found = safeUrl(url)
  return found && (found.hostname === 'gog-statics.com' || found.hostname.endsWith('.gog-statics.com'))
    ? { url: found.href, source: 'gog' }
    : null
}

/** An https address on the default port, nothing else: the Launcher downloads what this names. */
function safeUrl(text) {
  try {
    const url = new URL(text)
    return url.protocol === 'https:' && url.port === '' ? url : null
  } catch {
    return null
  }
}

/** A store could not give a usable answer: it is left alone for a while (when it was refusing). */
async function fail(source, seconds) {
  if (seconds > 0) await flagSet(`backoff:${source}`, '1', seconds)
  throw new Unavailable()
}

/**
 * One upstream request. A rate limit, an error, a refusal (401, 403, 408 on a data call) or a timeout backs that store off
 * (for what it asked, at most fifteen minutes). `strict` is false for a picture's HEAD on the CDN, where 403 is how a missing
 * file answers; on a data call (strict) any status that is not ok is a failure, except a 404 the caller allows.
 */
async function upstream(url, init, source, ask, { strict = true, allow404 = false } = {}) {
  const env = ask.env
  if (await flagGet(`backoff:${source}`)) throw new Unavailable()
  // Out of time for this ask: not the store's fault, so no backoff and no failure on record.
  const left = timeLeft(ask.startedAt, Date.now())
  if (left <= 0) throw new Unavailable()
  // Each address has a share of store requests (its own asks past it are refused, 429), and the location has a ceiling on all of
  // them (past it the ask is answered partial: that is not a store being down, so nothing is backed off).
  if (env.COVER_STORE_ADDRESS_LIMIT && !(await env.COVER_STORE_ADDRESS_LIMIT.limit({ key: ask.address })).success) throw new Limited()
  if (env.COVER_STORE_LIMIT && !(await env.COVER_STORE_LIMIT.limit({ key: 'stores' })).success) throw new Capped()
  let response
  try {
    response = await fetch(url, {
      ...init,
      redirect: 'manual',
      headers: { 'User-Agent': 'ExoCovers/1 (+https://getexo.app)', Accept: 'application/json, image/*;q=0.5' },
      signal: AbortSignal.timeout(left),
    })
  } catch {
    return fail(source, FAILURE_BACKOFF_SECONDS)
  }
  if (storeIsRefusing(response.status, strict)) return fail(source, backoffSeconds(response.status, response.headers.get('Retry-After')))
  if (strict && !response.ok && !(allow404 && response.status === 404)) return fail(source, 0)
  return response
}

async function getJson(url, source, ask, options = {}) {
  const response = await upstream(url, { method: 'GET' }, source, ask, options)
  if (response.status === 404) return null
  try {
    return await response.json()
  } catch {
    // a 200 that is not JSON (an error page) is not a clean "no such game"
    return fail(source, 0)
  }
}

function json(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...headers },
  })
}

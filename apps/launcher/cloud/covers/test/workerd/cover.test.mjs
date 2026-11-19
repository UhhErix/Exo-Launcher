/**
 * The cover cache Worker under workerd (@cloudflare/vitest-plugin): SELF.fetch runs the real Worker in the same runtime
 * as this file, so KV and the rate limits are the miniflare stand-ins from wrangler.toml, and the Worker's outbound
 * fetch is routed through the rules below. A request with no rule throws, which the Worker treats as an unreachable store.
 * Run: npm test (or npx vitest run).
 */
import { env, SELF } from 'cloudflare:test'
import { beforeEach, describe, expect, it } from 'vitest'
import worker, {
  TOTAL_BUDGET_MS, backoffSeconds, flagGet, flagSet, forgetAllFlags, timeLeft,
} from '../../src/worker.js'

const POSTER_HOST = 'https://cdn.cloudflare.steamstatic.com'
const POSTER = `${POSTER_HOST}/steam/apps/1145360/library_600x900_2x.jpg`
const GOG_COVER = 'https://images.gog-statics.com/b980dde93fe2de7b695d282085e2edc7e2649dc6074e2002eac67a8f8c448781.jpg'
const EPIC_PORTRAIT = 'https://cdn1.epicgames.com/offer/abc/1200x1600-abc.jpg'

let rules = []
let sent = []
let skew = 0
let nextAddress = 1
const realFetch = globalThis.fetch
const realNow = Date.now

beforeEach(async () => {
  rules = []
  sent = []
  skew = 0
  Date.now = () => realNow() + skew
  // Cached hits live in KV; backoffs, remembered misses and the KV failure count live in the Cache API: start every test with none.
  for (const key of (await env.COVERS.list()).keys) await env.COVERS.delete(key.name)
  await forgetAllFlags()
  globalThis.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === 'string' ? input : input.url)
    const method = (init.method ?? (typeof input === 'string' ? 'GET' : input.method) ?? 'GET').toUpperCase()
    sent.push({ method, url: url.href, redirect: init.redirect })
    const index = rules.findIndex((rule) => rule.method === method && rule.origin === url.origin &&
      (typeof rule.path === 'function' ? rule.path(url) : url.pathname === rule.path))
    if (index < 0) throw new TypeError(`no mock for ${method} ${url}`)
    const rule = rules[index]
    if (!rule.persist) rules.splice(index, 1)
    return rule.reply()
  }
  return () => {
    globalThis.fetch = realFetch
    Date.now = realNow
  }
})

const rule = (method, origin, path, reply, persist = false) => rules.push({ method, origin, path, reply, persist })
const jsonReply = (body, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
/** Every ask comes from its own address unless a test names one, so one test cannot use up another's per-address limit. */
const ask = (query, ip = `203.0.113.${nextAddress++ % 250}`) =>
  SELF.fetch(`https://covers.getexo.app/v1/cover?${query}`, { headers: { 'CF-Connecting-IP': ip } })
/** KV writes run after the answer (ctx.waitUntil): wait until a key shows. */
const kvHas = async (key) => {
  for (let i = 0; i < 100; i++) {
    if (await env.COVERS.get(key)) return true
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  return false
}
/** Cache API writes run after the answer too: wait until a flag has this value. */
const flagBecomes = async (name, value) => {
  for (let i = 0; i < 100; i++) {
    if ((await flagGet(name)) === value) return true
    await new Promise((resolve) => setTimeout(resolve, 10))
  }
  return false
}
const kvKeys = async () => (await env.COVERS.list()).keys.map((k) => k.name)

const STEAM = 'https://store.steampowered.com'
const storeSearch = (items) =>
  rule('GET', STEAM, (u) => u.pathname.startsWith('/api/storesearch/'), () => jsonReply({ items }))
const noSteamSearch = () => storeSearch([])
const steamSearchReplies = (reply) => rule('GET', STEAM, (u) => u.pathname.startsWith('/api/storesearch/'), reply)
const posterAt = (id, length = 50_000) =>
  rule('HEAD', POSTER_HOST, `/steam/apps/${id}/library_600x900_2x.jpg`,
    () => new Response(null, { status: 200, headers: { 'content-length': String(length) } }))
const POSTER_HOSTS = [POSTER_HOST, 'https://shared.akamai.steamstatic.com', 'https://cdn.akamai.steamstatic.com']
const posterMissing = (id) => {
  for (const host of POSTER_HOSTS) rule('HEAD', host, (u) => u.pathname.includes(`/steam/apps/${id}/`), () => new Response(null, { status: 404 }), true)
}
const GOG = 'https://catalog.gog.com'
const gogCatalog = (products) =>
  rule('GET', GOG, (u) => u.pathname.startsWith('/v1/catalog'), () => jsonReply({ products }))
const noGogCatalog = () => gogCatalog([])
const gogCatalogReplies = (reply) => rule('GET', GOG, (u) => u.pathname.startsWith('/v1/catalog'), reply)
const GOG_API = 'https://api.gog.com'
const gogGameReplies = (reply) => rule('GET', GOG_API, (u) => u.pathname.startsWith('/v2/games/'), reply)
const EPIC = 'https://store-content.ak.epicgames.com'
const epicProduct = (body, slug) =>
  rule('GET', EPIC, `/api/en-US/content/products/${slug}`, () => jsonReply(body))
const epicMissing = (slug) =>
  rule('GET', EPIC, `/api/en-US/content/products/${slug}`, () => new Response('', { status: 404 }))
/** Every store answers "no" cleanly for this title. */
const everyStoreSaysNo = (slug) => {
  noSteamSearch()
  noGogCatalog()
  epicMissing(slug)
}
const storeRequests = () => sent.filter((r) => !r.url.startsWith('https://covers.getexo.app'))

/** The Worker called directly, with some bindings replaced (SELF.fetch cannot change what the Worker is given). */
const callWorker = async (query, overrides = {}, ip = `198.18.0.${nextAddress++ % 250}`, path = '/v1/cover') => {
  const pending = []
  const ctx = { waitUntil: (p) => pending.push(p), passThroughOnException() {} }
  const request = new Request(`https://covers.getexo.app${path}${query ? `?${query}` : ''}`, { headers: { 'CF-Connecting-IP': ip } })
  const response = await worker.fetch(request, { ...env, ...overrides }, ctx)
  await Promise.allSettled(pending)
  return response
}

describe('input', () => {
  it('refuses bad input before any store is contacted', async () => {
    for (const query of [
      'store=epic&title=Hades',
      `title=${'x'.repeat(201)}`,
      `title=${'%C3%A9'.repeat(101)}`, // 101 characters, 202 bytes: the limit is bytes
      'store=steam&id=12a',
      'id=1145360',
      'store=steam',
      'store=gog&title=Hades', // a store without its id would answer for every store after asking one
      'store=gog&id=../etc',
      'title=%21%21%21',
      '',
    ]) {
      const response = await ask(query)
      expect(response.status, query).toBe(400)
    }
    expect(sent).toEqual([])
  })

  it('a store without its id cannot poison the answer for the plain title', async () => {
    // Before: ?title=Hades&store=gog asked only GOG and stored a day-long empty miss under the plain title's key.
    expect((await ask('title=Hades&store=gog')).status).toBe(400)
    storeSearch([{ type: 'app', id: 1145360, name: 'Hades' }])
    posterAt('1145360')
    const plain = await ask('title=Hades')
    expect(await plain.json()).toEqual({ ok: true, url: POSTER, source: 'steam' })
  })

  it('answers 404 for other paths', async () => {
    const response = await SELF.fetch('https://covers.getexo.app/v1/nope')
    expect(response.status).toBe(404)
  })

  it("rate-limits per address: the 121st request in a minute is refused, with the wait", async () => {
    posterAt('1145360')
    expect((await ask('store=steam&id=1145360', '198.51.100.9')).status).toBe(200)
    expect(await kvHas('cover:v1:steam:1145360')).toBe(true)
    for (let i = 0; i < 119; i++) expect((await ask('store=steam&id=1145360', '198.51.100.9')).status).toBe(200)
    const refused = await ask('store=steam&id=1145360', '198.51.100.9')
    expect(refused.status).toBe(429)
    expect(refused.headers.get('Retry-After')).toBe('60')
  })

  it('uncached asks have a lower limit per address, and cached ones do not count against it', async () => {
    everyStoreSaysNo('nothing-here')
    posterAt('1145360')
    expect((await ask('store=steam&id=1145360', '198.51.100.20')).status).toBe(200)
    expect(await kvHas('cover:v1:steam:1145360')).toBe(true)
    // Thirty cached answers from one address: no limit.
    for (let i = 0; i < 30; i++) expect((await ask('store=steam&id=1145360', '198.51.100.21')).status).toBe(200)
    // Uncached asks from one address: the 21st in a minute is refused before any store is asked.
    const statuses = []
    for (let i = 0; i < 21; i++) statuses.push((await ask(`title=Unknown%20Game%20${i}`, '198.51.100.22')).status)
    expect(statuses.slice(0, 20).every((status) => status !== 429)).toBe(true)
    expect(statuses[20]).toBe(429)
  })
})

describe('Steam', () => {
  it('a Steam app id is found on the poster CDN, then kept for everyone', async () => {
    posterAt('1145360')
    const first = await ask('store=steam&id=1145360')
    expect(await first.json()).toEqual({ ok: true, url: POSTER, source: 'steam' })
    expect(await kvHas('cover:v1:steam:1145360')).toBe(true)
    // The rule was used up, so a second upstream request would fail: the answer must come from KV.
    const second = await ask('store=steam&id=1145360')
    expect(await second.json()).toEqual({ ok: true, url: POSTER, source: 'steam' })
  })

  it('a tiny placeholder poster does not count, and an id-only miss is just a miss', async () => {
    rule('HEAD', POSTER_HOST, (u) => u.pathname.includes('/steam/apps/777/'),
      () => new Response(null, { status: 200, headers: { 'content-length': '400' } }), true)
    rule('HEAD', 'https://shared.akamai.steamstatic.com', () => true, () => new Response(null, { status: 404 }), true)
    rule('HEAD', 'https://cdn.akamai.steamstatic.com', () => true, () => new Response(null, { status: 404 }), true)
    const response = await ask('store=steam&id=777')
    expect(response.status).toBe(404)
    // Only Steam was asked: the answer is a plain miss, and Steam's own "no" is remembered for an hour.
    expect(await response.json()).toEqual({ ok: false, miss: true })
    expect(await flagBecomes('miss:steam:777', '1')).toBe(true)
  })

  it('a clean miss in every store is remembered for an hour, so the stores are not searched again, but it is never final', async () => {
    posterMissing('999')
    everyStoreSaysNo('nine-nine-nine')
    const first = await ask('store=steam&id=999&title=Nine%20Nine%20Nine')
    expect(first.status).toBe(404)
    expect(await first.json()).toEqual({ ok: false, miss: true })
    expect(await flagBecomes('miss:title:nine nine nine', '1')).toBe(true)
    const entry = await caches.default.match(new Request('https://covers.getexo.app/__state/miss%3Atitle%3Anine%20nine%20nine'))
    expect(entry.headers.get('Cache-Control')).toBe('public, max-age=3600')
    const searches = () => storeRequests().filter((r) => !r.url.includes('steamstatic.com')).length
    const before = searches()
    const second = await ask('store=steam&id=999&title=Nine%20Nine%20Nine')
    expect(await second.json()).toEqual({ ok: false, miss: true })
    expect(searches()).toBe(before) // the title was not searched again
    // A remembered miss goes in the Cache API, never KV.
    expect((await kvKeys()).filter((k) => k.startsWith('cover:'))).toEqual([])
  })

  it('a title is found only when a Steam result matches it exactly', async () => {
    storeSearch([{ type: 'app', id: 1145360, name: 'Hades' }])
    posterAt('1145360')
    const response = await ask('title=Hades')
    expect(await response.json()).toEqual({ ok: true, url: POSTER, source: 'steam' })
  })

  it('a sequel is never the cover of the game it follows', async () => {
    storeSearch([{ type: 'app', id: 1, name: 'Dying Light 2 Stay Human' }])
    noGogCatalog()
    epicMissing('dying-light')
    const response = await ask('title=Dying%20Light')
    expect(response.status).toBe(404)
  })

  it('a soundtrack result is ignored', async () => {
    storeSearch([{ type: 'app', id: 2, name: 'Hades Soundtrack' }])
    noGogCatalog()
    epicMissing('hades')
    const response = await ask('title=Hades')
    expect(response.status).toBe(404)
  })

  it('real games with a junk word inside their name are found', async () => {
    for (const [title, id] of [['Observer: System Redux', 11], ['Democracy 4', 12], ['Pandemonium', 13], ['Tools Up!', 14], ['DLC Quest', 15]]) {
      storeSearch([{ type: 'app', id, name: title }])
      posterAt(String(id))
      const response = await ask(`title=${encodeURIComponent(title)}`)
      const body = await response.json()
      expect(body.ok, title).toBe(true)
      expect(body.url, title).toContain(`/apps/${id}/`)
    }
  })

  it('a Steam store error backs Steam off for what it asked, with 503 and Retry-After', async () => {
    steamSearchReplies(() => new Response('', { status: 429, headers: { 'Retry-After': '120' } }))
    const first = await ask('title=Hades')
    expect(first.status).toBe(503)
    expect(first.headers.get('Retry-After')).toBe('900')
    expect(await flagGet('backoff:steam')).toBe('1')
    // No rule is left for Steam: a second upstream request would fail, and the backoff stops it before it is sent.
    const before = storeRequests().filter((r) => r.url.startsWith(STEAM)).length
    const second = await ask('title=Hades')
    expect(second.status).toBe(503)
    expect(storeRequests().filter((r) => r.url.startsWith(STEAM)).length).toBe(before)
  })
})

describe('GOG', () => {
  it('a GOG title is found by its exact catalog title and its vertical cover', async () => {
    noSteamSearch()
    gogCatalog([{ title: 'Guards II: Chaos in Hell', coverVertical: GOG_COVER }])
    const response = await ask('title=Guards%20II%3A%20Chaos%20in%20Hell')
    expect(await response.json()).toEqual({ ok: true, url: GOG_COVER, source: 'gog' })
  })

  it("a GOG cover off GOG's own host is refused", async () => {
    noSteamSearch()
    gogCatalog([{ title: 'Hades', coverVertical: 'https://evil.example/x.jpg' }])
    epicMissing('hades')
    const response = await ask('title=Hades')
    expect(response.status).toBe(404)
  })

  it('a cover address on another port is refused', async () => {
    noSteamSearch()
    gogCatalog([{ title: 'Hades', coverVertical: 'https://images.gog-statics.com:8443/x.jpg' }])
    epicMissing('hades')
    const response = await ask('title=Hades')
    expect(response.status).toBe(404)
  })

  it('a title with no store tries Steam, then GOG', async () => {
    noSteamSearch()
    gogCatalog([{ title: 'Hades', coverVertical: GOG_COVER }])
    const response = await ask('title=Hades')
    expect(await response.json()).toEqual({ ok: true, url: GOG_COVER, source: 'gog' })
  })

  it('a GOG error does not stop Epic from being asked', async () => {
    noSteamSearch()
    gogCatalogReplies(() => new Response('', { status: 429 }))
    epicProduct({ tiles: [{ src: EPIC_PORTRAIT }] }, 'wuchang')
    const response = await ask('title=WUCHANG')
    expect(await response.json()).toEqual({ ok: true, url: EPIC_PORTRAIT, source: 'epic' })
  })

  it('a GOG id with no art is not the end: the title is searched in every store', async () => {
    // Before: the id's miss ended the ask, and a title that another store had was never found.
    gogGameReplies(() => new Response('', { status: 404 }))
    storeSearch([{ type: 'app', id: 1145360, name: 'Hades' }])
    posterAt('1145360')
    const response = await ask('store=gog&id=555&title=Hades')
    expect(await response.json()).toEqual({ ok: true, url: POSTER, source: 'steam' })
  })

  it('a GOG id and a title that nothing has is a miss only after every store said no', async () => {
    gogGameReplies(() => new Response('', { status: 404 }))
    everyStoreSaysNo('nothing-here')
    const response = await ask('store=gog&id=556&title=Nothing%20Here')
    expect(await response.json()).toEqual({ ok: false, miss: true })
  })

  it('a title sent with an id never pins its picture on the id: only what the id itself returned is kept under the id', async () => {
    // GOG is failing. A caller pairs a real GOG id with a title of its choosing: the hit for that title is its own, under the title.
    gogGameReplies(() => new Response('', { status: 500 }))
    storeSearch([{ type: 'app', id: 666, name: 'Evil Game' }])
    posterAt('666')
    const evil = await ask('store=gog&id=1207658924&title=Evil%20Game')
    expect((await evil.json()).url).toContain('/apps/666/')
    expect(await kvHas('cover:v1:title:evil game')).toBe(true)
    expect(await kvKeys()).not.toContain('cover:v1:gog:1207658924')
    // An honest ask for the same id gets the honest game, not Evil Game's poster.
    storeSearch([{ type: 'app', id: 292030, name: 'The Witcher 3 Wild Hunt' }])
    posterAt('292030')
    const honest = await ask('store=gog&id=1207658924&title=The%20Witcher%203%20Wild%20Hunt')
    expect((await honest.json()).url).toContain('/apps/292030/')
    // And with the id alone (GOG answering this time, with no art) nothing is served from Evil Game.
    await forgetAllFlags()
    gogGameReplies(() => new Response('', { status: 404 }))
    const idOnly = await ask('store=gog&id=1207658924')
    expect(idOnly.status).toBe(404)
  })
})

describe('a remembered miss', () => {
  it('is not kept alive by an ask that skipped the search because of it', async () => {
    // The miss was remembered for 100 seconds. An id-plus-title ask for the same title skips the title search; it must not rewrite the miss.
    await flagSet('miss:title:evil game', '1', 100)
    gogGameReplies(() => new Response('', { status: 404 }))
    const response = await ask('store=gog&id=555&title=Evil%20Game')
    expect(await response.json()).toEqual({ ok: false, miss: true })
    await new Promise((resolve) => setTimeout(resolve, 50))
    const entry = await caches.default.match(new Request('https://covers.getexo.app/__state/miss%3Atitle%3Aevil%20game'))
    expect(entry.headers.get('Cache-Control')).toBe('public, max-age=100')
  })
})

describe('a store that cannot be asked', () => {
  const steamDown = (status = 500) => steamSearchReplies(() => new Response('', { status }))

  it('Steam down and GOG has the game: the GOG cover is answered', async () => {
    steamDown()
    gogCatalog([{ title: 'Hades', coverVertical: GOG_COVER }])
    const response = await ask('title=Hades')
    expect(await response.json()).toEqual({ ok: true, url: GOG_COVER, source: 'gog' })
  })

  it('a 403 from a store is unavailable: it is not a miss, and nothing is remembered as one', async () => {
    steamDown(403)
    noGogCatalog()
    epicMissing('hades')
    const response = await ask('title=Hades')
    expect(response.status).toBe(404)
    expect(await response.json()).toEqual({ ok: false, miss: true, partial: true })
    expect(await flagGet('miss:title:hades')).toBeNull()
    expect(await flagGet('backoff:steam')).toBe('1')
  })

  it('every store failing answers 503 and remembers no miss', async () => {
    steamDown(403)
    gogCatalogReplies(() => new Response('', { status: 403 }))
    rule('GET', EPIC, () => true, () => new Response('', { status: 403 }))
    const response = await ask('title=Hades')
    expect(response.status).toBe(503)
    expect(response.headers.get('Retry-After')).toBe('900')
    expect(await flagGet('miss:title:hades')).toBeNull()
  })

  it('one GOG failure on an id ask is a partial miss, not a 503 that switches every client off', async () => {
    gogGameReplies(() => new Response('', { status: 500 }))
    const noTitle = await ask('store=gog&id=7')
    expect(noTitle.status).toBe(404)
    expect(await noTitle.json()).toEqual({ ok: false, miss: true, partial: true })
  })

  it('an id ask with a title where one store fails is partial, and 503 stays for none being askable', async () => {
    gogGameReplies(() => new Response('', { status: 500 }))
    noSteamSearch()
    epicMissing('nothing-here')
    const partial = await ask('store=gog&id=8&title=Nothing%20Here')
    expect(partial.status).toBe(404)
    expect((await partial.json()).partial).toBe(true)
  })

  it('a clean miss from every store is remembered, and a partial one is not', async () => {
    everyStoreSaysNo('nothing-here')
    const first = await ask('title=Nothing%20Here')
    expect(await first.json()).toEqual({ ok: false, miss: true })
    expect(await flagBecomes('miss:title:nothing here', '1')).toBe(true)
  })

  it('a missing poster on the CDN answering 403 is a missing poster, not an outage', async () => {
    for (const host of POSTER_HOSTS)
      rule('HEAD', host, (u) => u.pathname.includes('/steam/apps/555/'), () => new Response(null, { status: 403 }), true)
    const response = await ask('store=steam&id=555')
    expect(await response.json()).toEqual({ ok: false, miss: true })
    expect(await flagGet('backoff:steam')).toBeNull()
  })

  it('an odd reply is a failure, never a miss: an error page, a 404 from a search, a redirect', async () => {
    const odd = [
      () => new Response('<html>Maintenance</html>', { status: 200, headers: { 'content-type': 'text/html' } }),
      () => new Response('', { status: 404 }),
      () => new Response('', { status: 302, headers: { Location: 'https://evil.example/' } }),
      () => new Response('', { status: 418 }),
    ]
    for (const reply of odd) {
      await forgetAllFlags()
      rules = []
      steamSearchReplies(reply)
      noGogCatalog()
      epicMissing('hades')
      const response = await ask('title=Hades')
      expect((await response.json()).partial).toBe(true)
      expect(await flagGet('miss:title:hades')).toBeNull()
    }
  })

  it('a 404 is "no such product" where the store says so: Epic and a GOG product id', async () => {
    noSteamSearch()
    noGogCatalog()
    epicMissing('hades')
    const response = await ask('title=Hades')
    expect(await response.json()).toEqual({ ok: false, miss: true })
  })

  it('stores are asked without following redirects', async () => {
    everyStoreSaysNo('nothing-here')
    await ask('title=Nothing%20Here')
    expect(storeRequests().length).toBeGreaterThan(0)
    expect(storeRequests().every((r) => r.redirect === 'manual')).toBe(true)
  })
})

describe('Epic', () => {
  it("a title is found by its Epic product slug and its 1200x1600 portrait on Epic's own CDN", async () => {
    noSteamSearch()
    noGogCatalog()
    epicProduct({
      title: 'Wuchang',
      tiles: [{ src: 'https://cdn1.epicgames.com/offer/abc/2560x1440-abc.jpg' }, { src: EPIC_PORTRAIT }],
    }, 'wuchang')
    const response = await ask('title=WUCHANG')
    expect(await response.json()).toEqual({ ok: true, url: EPIC_PORTRAIT, source: 'epic' })
  })

  it('a portrait on any other host is refused', async () => {
    noSteamSearch()
    noGogCatalog()
    epicProduct({ portraitBackgroundImageUrl: 'https://evil.example/1200x1600.jpg' }, 'wuchang')
    const response = await ask('title=WUCHANG')
    expect(response.status).toBe(404)
  })

  it('a portrait on another port is refused', async () => {
    noSteamSearch()
    noGogCatalog()
    epicProduct({ portraitBackgroundImageUrl: 'https://cdn1.epicgames.com:444/1200x1600.jpg' }, 'wuchang')
    const response = await ask('title=WUCHANG')
    expect(response.status).toBe(404)
  })

  it('a title that is no Epic product is a miss', async () => {
    noSteamSearch()
    noGogCatalog()
    epicMissing('nothing-here')
    const response = await ask('title=Nothing%20Here')
    expect(response.status).toBe(404)
  })
})

describe('writes that fail', () => {
  const failingKv = () => ({
    get: (...args) => env.COVERS.get(...args),
    put: () => Promise.reject(new Error('KV put limit exceeded')),
  })

  it('a KV write that fails does not throw away the cover just found', async () => {
    storeSearch([{ type: 'app', id: 1145360, name: 'Hades' }])
    posterAt('1145360')
    const response = await callWorker('title=Hades', { COVERS: failingKv() })
    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ ok: true, url: POSTER, source: 'steam' })
  })

  it('a miss and a backoff never use KV writes', async () => {
    let puts = 0
    const counting = { get: (...a) => env.COVERS.get(...a), put: () => { puts++; return Promise.resolve() } }
    steamSearchReplies(() => new Response('', { status: 429 }))
    noGogCatalog()
    epicMissing('nothing-here')
    await callWorker('title=Nothing%20Here', { COVERS: counting })
    everyStoreSaysNo('another-miss')
    await forgetAllFlags()
    await callWorker('title=Another%20Miss', { COVERS: counting })
    expect(puts).toBe(0)
  })
})

describe('one caller cannot switch a store off for everyone', () => {
  it('the location ceiling on store requests is not an outage: past it an ask is partial, with no 503 and no backoff', async () => {
    const full = { limit: async () => ({ success: false }) }
    everyStoreSaysNo('hades')
    const response = await callWorker('title=Hades', { COVER_STORE_LIMIT: full })
    expect(response.status).toBe(404)
    expect(response.headers.get('Retry-After')).toBeNull()
    expect(await response.json()).toEqual({ ok: false, miss: true, partial: true })
    expect(storeRequests()).toEqual([])
    expect(await flagGet('backoff:steam')).toBeNull()
    expect(await flagGet('miss:title:hades')).toBeNull()
  })

  it("an address past its own share of store requests is refused with 429, not the others' stores", async () => {
    const spent = { limit: async () => ({ success: false }) }
    everyStoreSaysNo('hades')
    const response = await callWorker('title=Hades', { COVER_STORE_ADDRESS_LIMIT: spent })
    expect(response.status).toBe(429)
    expect(response.headers.get('Retry-After')).toBe('60')
    expect(storeRequests()).toEqual([])
  })

  it('an address share is counted per address, so three addresses cannot use the whole ceiling', async () => {
    const keys = []
    const counting = { limit: async ({ key }) => { keys.push(key); return { success: true } } }
    for (const ip of ['198.18.1.1', '198.18.1.2', '198.18.1.3']) {
      everyStoreSaysNo('hades')
      await callWorker('title=Hades', { COVER_STORE_ADDRESS_LIMIT: counting }, ip)
      await forgetAllFlags()
    }
    expect(new Set(keys)).toEqual(new Set(['198.18.1.1', '198.18.1.2', '198.18.1.3']))
  })

  it('a store is left alone only for as long as it asked', () => {
    expect(backoffSeconds(429, '5')).toBe(5)
    expect(backoffSeconds(429, '99999')).toBe(900)
    expect(backoffSeconds(429, null)).toBe(60)
    expect(backoffSeconds(500, null)).toBe(300)
  })

  it('a 429 from a store with a short Retry-After backs it off, not for fifteen minutes', async () => {
    steamSearchReplies(() => new Response('', { status: 429, headers: { 'Retry-After': '5' } }))
    noGogCatalog()
    epicMissing('hades')
    await ask('title=Hades')
    expect(await flagGet('backoff:steam')).toBe('1')
    // The flag's own lifetime is the asked five seconds: it is gone once they pass.
    const entry = await caches.default.match(new Request('https://covers.getexo.app/__state/backoff%3Asteam'))
    expect(entry.headers.get('Cache-Control')).toBe('public, max-age=5')
  })
})

describe('time', () => {
  it("one ask never gets more than its share of the Worker's own total budget", () => {
    expect(timeLeft(0, 0)).toBe(3000)
    expect(timeLeft(0, TOTAL_BUDGET_MS - 1000)).toBe(1000)
    expect(timeLeft(0, TOTAL_BUDGET_MS)).toBeLessThanOrEqual(0)
  })

  it('once the budget is spent no further store is contacted', async () => {
    // Steam's reply takes the whole budget; GOG and Epic must not be sent anything after it.
    steamSearchReplies(() => {
      skew += TOTAL_BUDGET_MS + 1000
      return jsonReply({ items: [] })
    })
    noGogCatalog()
    epicMissing('hades')
    const response = await ask('title=Hades')
    expect(response.status).toBe(404)
    expect((await response.json()).partial).toBe(true)
    expect(rules.length).toBe(2) // the GOG and Epic rules were never used
  })
})

describe('/health', () => {
  const get = (overrides = {}) => callWorker('', overrides, undefined, '/health')

  it('says KV works, with nothing about anyone', async () => {
    const response = await get()
    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ ok: true, kvFailuresLastHour: 0, storesBackedOffCount: 0 })
  })

  it('counts KV writes and reads that fail, and says so when KV does not answer', async () => {
    const failingWrites = { get: (...a) => env.COVERS.get(...a), put: () => Promise.reject(new Error('quota')) }
    storeSearch([{ type: 'app', id: 1145360, name: 'Hades' }])
    posterAt('1145360')
    await callWorker('title=Hades', { COVERS: failingWrites })
    const afterWrite = await (await get()).json()
    expect(afterWrite.ok).toBe(true)
    expect(afterWrite.kvFailuresLastHour).toBe(1)

    const deadKv = { get: () => Promise.reject(new Error('down')), put: () => Promise.reject(new Error('down')) }
    await caches.default.delete(new Request('https://covers.getexo.app/__state/health%3Akv')) // the kept probe result expires
    const dead = await get({ COVERS: deadKv })
    expect(dead.status).toBe(503)
    const body = await dead.json()
    expect(body.ok).toBe(false)
    expect(body.kvFailuresLastHour).toBeGreaterThan(1)
  })

  it('counts a KV read that fails during an ask, and still answers from the stores', async () => {
    const failingReads = { get: () => Promise.reject(new Error('KV read failed')), put: (...a) => env.COVERS.put(...a) }
    storeSearch([{ type: 'app', id: 1145360, name: 'Hades' }])
    posterAt('1145360')
    const response = await callWorker('title=Hades', { COVERS: failingReads })
    expect(response.status).toBe(200)
    expect((await (await get()).json()).kvFailuresLastHour).toBeGreaterThanOrEqual(1)
  })

  it('lists the stores that are backed off', async () => {
    steamSearchReplies(() => new Response('', { status: 429, headers: { 'Retry-After': '120' } }))
    noGogCatalog()
    epicMissing('hades')
    await ask('title=Hades')
    const body = await (await get()).json()
    expect(body.storesBackedOffCount).toBe(1)
    expect(body).not.toHaveProperty('storesBackedOff') // a count only: which store is under pressure is not told
  })

  it('is behind the per-address limit, like every other ask', async () => {
    for (let i = 0; i < 120; i++) expect((await callWorker('', {}, '198.51.100.77', '/health')).status).toBe(200)
    const refused = await callWorker('', {}, '198.51.100.77', '/health')
    expect(refused.status).toBe(429)
    expect(refused.headers.get('Retry-After')).toBe('60')
  })

  it('does not read KV on every call: the probe is kept for sixty seconds', async () => {
    let reads = 0
    const counting = { get: () => { reads++; return Promise.resolve(null) }, put: () => Promise.resolve() }
    for (let i = 0; i < 20; i++) expect((await get({ COVERS: counting })).status).toBe(200)
    expect(reads).toBe(1)
  })
})

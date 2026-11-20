/**
 * The release card's tests: announce.js and the Worker's POST /v1/<app>/announce route, with R2 and Discord stood in for.
 * Nothing reaches Discord or Cloudflare.
 *
 * How it could fail: it posts a card for a tag that isn't live, posts a tag twice, posts to a host that isn't Discord,
 * prints or stores a webhook URL in a result, fails the release when the secret is missing or Discord is down, or puts
 * the wrong version, change highlights or role mention on the card.
 *   node --test test
 */
import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'
const { announce, cardPayload, isDiscordWebhook } = await import('../src/announce.js')
const worker = (await import('../src/worker.js')).default

const ORIGIN = 'https://exo-updates.test'
const HOOK = 'https://discord.com/api/webhooks/123456789012345678/tok_En-1?thread_id=9'
const ROLE = '123456789012345678'
const WHEN = new Date('2026-11-06T12:00:00Z')
const WHATS_NEW = {
  version: '1.5.0',
  items: [
    { icon: 'Record', title: 'Clips play as you open them', detail: 'A clip starts on its own.' },
    { icon: 'Capture', title: 'Snips go straight to Discord', detail: 'One press.' },
  ],
}

/** R2 as the Worker sees it: get, delete, and put with the etagDoesNotMatch precondition R2 uses for a claim. */
function fakeMirror(objects = {}) {
  const store = new Map(Object.entries(objects).map(([key, value]) => [key, typeof value === 'string' ? value : JSON.stringify(value)]))
  const etag = (key) => `"${store.get(key).length}"`
  return {
    store,
    get: async (key) => {
      if (!store.has(key)) return null
      const text = store.get(key)
      return { size: text.length, httpEtag: etag(key), text: async () => text, json: async () => JSON.parse(text) }
    },
    put: async (key, value, options = {}) => {
      if (options.onlyIf?.etagDoesNotMatch === '*' && store.has(key)) return null
      store.set(key, value)
      return { key }
    },
    delete: async (key) => { store.delete(key) },
    list: async ({ prefix = '', limit = 1000 } = {}) => ({
      objects: [...store.keys()].filter((key) => key.startsWith(prefix)).sort().slice(0, limit).map((key) => ({ key })),
    }),
  }
}

const liveLatest = (tag) => ({ tag, schema: 2 })

/** A Discord that answers each POST with a message id, each PATCH with 200 and each DELETE with `deleteStatus` (204: deleted). */
function fakeDiscord({ status = 200, deleteStatus = 204 } = {}) {
  const calls = []
  let next = 500000000000000000n
  const fetcher = async (url, init = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body ? JSON.parse(init.body) : undefined })
    if (status !== 200) return new Response('no', { status })
    if (init.method === 'DELETE') return new Response(null, { status: typeof deleteStatus === 'function' ? deleteStatus() : deleteStatus })
    next += 1n
    return new Response(JSON.stringify({ id: String(next) }), { status: 200, headers: { 'content-type': 'application/json' } })
  }
  return { calls, fetcher }
}

const env = (overrides = {}) => ({ DISCORD_WEBHOOK_CINEMA: HOOK, ...overrides })

let mirror
beforeEach(() => {
  mirror = fakeMirror({
    'cinema/latest.json': liveLatest('v1.5.0'),
    'cinema/v1.5.0/whats-new.json': WHATS_NEW,
  })
})

test('the card names the product, version and download, and lists the change highlights', () => {
  const p = cardPayload({ app: 'cinema', version: '1.5.0', highlights: ['Clips play as you open them'], roleId: '', when: WHEN })
  const e = p.embeds[0]
  assert.equal(e.title, 'Exo Cinema 1.5.0')
  assert.equal(e.description, '• Clips play as you open them\n\nOpen Exo Cinema to update. [Full changelog](https://getexo.app/cinema/changelog) · [GitHub](https://github.com/UhhErix/Exo-Cinema/releases/tag/v1.5.0)')
  assert.equal(e.url, 'https://getexo.app/cinema/changelog')
  assert.equal(e.color, 0x010101)
  assert.equal(e.timestamp, '2026-11-06T12:00:00Z')
  assert.equal(p.username, 'Exo Cinema Releases')
  assert.equal(p.avatar_url, 'https://getexo.app/brand/discord/exo-cinema-avatar.png')
  assert.equal(p.flags, 4096)
  assert.equal(p.content, undefined)
  assert.equal(p.allowed_mentions.roles, undefined)
})

test('the role ping is in content and allowed, and a launcher card has no download link', () => {
  const p = cardPayload({ app: 'cinema', version: '1.5.0', highlights: [], roleId: ROLE, when: WHEN })
  assert.equal(p.content, `<@&${ROLE}>`)
  assert.deepEqual(p.allowed_mentions.roles, [ROLE])
  const b = cardPayload({ app: 'launcher', version: '3.26.0', highlights: [], roleId: '', when: WHEN })
  assert.equal(b.embeds[0].description, 'Open Exo Launcher to update. [Full changelog](https://getexo.app/launcher/changelog) · [GitHub](https://github.com/UhhErix/Exo-Launcher/releases/tag/v3.26.0)')
})

test('only an https Discord webhook URL is accepted', () => {
  assert.equal(isDiscordWebhook('https://discord.com/api/webhooks/123456789012345678/abc_DEF-123'), true)
  assert.equal(isDiscordWebhook('https://discordapp.com/api/webhooks/1/tok?thread_id=5'), true)
  for (const url of ['http://discord.com/api/webhooks/1/tok', 'https://discord.com.evil.example/api/webhooks/1/tok',
    'https://evil.example/api/webhooks/1/tok', 'https://discord.com/api/channels/1/messages',
    'https://user@discord.com/api/webhooks/1/tok', 'https://discord.com:8443/api/webhooks/1/tok',
    'https://discord.com/api/webhooks/', '', undefined]) {
    assert.equal(isDiscordWebhook(url), false, String(url))
  }
})

test('a live release posts its card once, and a second call for the same tag posts nothing', async () => {
  const discord = fakeDiscord()
  const first = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(first.status, 'posted')
  assert.equal(discord.calls.length, 1)
  assert.equal(discord.calls[0].method, 'POST')
  assert.match(discord.calls[0].url, /\/api\/webhooks\/123456789012345678\/tok_En-1\?wait=true&thread_id=9$/)
  assert.equal(discord.calls[0].body.embeds[0].title, 'Exo Cinema 1.5.0')
  assert.match(discord.calls[0].body.embeds[0].description, /• Snips go straight to Discord/)
  const second = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(second.status, 'already')
  assert.equal(discord.calls.length, 1, 'no second post for the same tag')
})

test('a missing secret skips the card, logs it, and leaves no claim so a later call with the secret posts', async () => {
  const discord = fakeDiscord()
  const warnings = []
  const original = console.warn
  console.warn = (...args) => warnings.push(args.join(' '))
  try {
    const skipped = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: {}, mirror, fetcher: discord.fetcher, now: WHEN })
    assert.equal(skipped.status, 'no-webhook')
  } finally {
    console.warn = original
  }
  assert.equal(discord.calls.length, 0)
  assert.equal(warnings.length, 1)
  assert.equal([...mirror.store.keys()].some((key) => key.startsWith('announce/')), false)
  const later = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(later.status, 'posted')
})

test('a secret that is not a Discord webhook is refused, posts nothing, and never echoes the URL', async () => {
  const discord = fakeDiscord()
  const bad = 'https://evil.example/api/webhooks/1/secrettoken'
  const original = console.warn
  console.warn = () => {}
  let result
  try {
    result = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: { DISCORD_WEBHOOK_CINEMA: bad }, mirror, fetcher: discord.fetcher, now: WHEN })
  } finally {
    console.warn = original
  }
  assert.equal(result.status, 'refused')
  assert.equal(JSON.stringify(result).includes('secrettoken'), false)
  assert.equal(discord.calls.length, 0)
  assert.equal([...mirror.store.keys()].some((key) => key.startsWith('announce/')), false)
})

test('a tag the mirror does not serve as newest gets no card', async () => {
  const discord = fakeDiscord()
  const result = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror: fakeMirror({ 'cinema/latest.json': liveLatest('v1.4.9') }), fetcher: discord.fetcher, now: WHEN })
  assert.equal(result.status, 'not-live')
  assert.equal(discord.calls.length, 0)
})

test('whats-new for another version gets no card', async () => {
  const discord = fakeDiscord()
  const stale = fakeMirror({ 'cinema/latest.json': liveLatest('v1.5.0'), 'cinema/v1.5.0/whats-new.json': { ...WHATS_NEW, version: '1.4.9' } })
  const result = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror: stale, fetcher: discord.fetcher, now: WHEN })
  assert.equal(result.status, 'wrong-version')
  assert.equal(discord.calls.length, 0)
})

test('-NoAnnounce is a no-op: it writes no claim, and a real announce after it still posts', async () => {
  const discord = fakeDiscord()
  const skipped = await announce({ app: 'cinema', tag: 'v1.5.0', announce: false, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(skipped.status, 'skipped')
  assert.equal(discord.calls.length, 0)
  assert.equal([...mirror.store.keys()].some((key) => key.startsWith('announce/')), false, 'no claim written')
  const later = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(later.status, 'posted')
  assert.equal(discord.calls.length, 1)
})

test('a Discord failure releases the claim, so a retry can post the card', async () => {
  const down = fakeDiscord({ status: 500 })
  const failed = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: down.fetcher, now: WHEN })
  assert.equal(failed.status, 'failed')
  assert.equal([...mirror.store.keys()].some((key) => key.startsWith('announce/')), false)
  const up = fakeDiscord()
  const retried = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: up.fetcher, now: WHEN })
  assert.equal(retried.status, 'posted')
})

test('the role is mentioned once and cleared from the card after it fires', async () => {
  const discord = fakeDiscord()
  await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env({ DISCORD_ROLE_CINEMA: ROLE }), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(discord.calls[0].body.content, `<@&${ROLE}>`)
  assert.equal(discord.calls[1].method, 'PATCH')
  assert.match(discord.calls[1].url, /\/messages\/500000000000000001\?/)
  assert.deepEqual(discord.calls[1].body, { content: '' })
})

test('a role that is not a snowflake is ignored, and the card posts without a mention', async () => {
  const discord = fakeDiscord()
  await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env({ DISCORD_ROLE_CINEMA: '@everyone' }), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(discord.calls.length, 1)
  assert.equal(discord.calls[0].body.content, undefined)
})

const OLD_CARD = '400000000000000001'
const OLDER_CARD = '400000000000000000'
const posted = (id) => ({ state: 'posted', id, at: '2026-11-01T00:00:00.000Z' })
const cardsUp = () => JSON.parse(mirror.store.get('announce/cinema/cards.json')).cards
const deletes = (discord) => discord.calls.filter((call) => call.method === 'DELETE').map((call) => call.url)

test('a new card deletes the cards of earlier tags, found from their claims, and records the one card left', async () => {
  mirror.store.set('announce/cinema/v1.4.9.json', JSON.stringify(posted(OLD_CARD)))
  mirror.store.set('announce/cinema/v1.4.8.json', JSON.stringify(posted(OLDER_CARD)))
  mirror.store.set('announce/cinema/v1.4.7.json', JSON.stringify({ state: 'posting', at: 'x' }))
  const discord = fakeDiscord()
  const result = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(result.status, 'posted')
  assert.equal(discord.calls[0].method, 'POST', 'the new card goes up before anything is deleted')
  assert.deepEqual(deletes(discord).map((url) => url.replace(/^.*\/messages\//, '')).sort(), [`${OLDER_CARD}?thread_id=9`, `${OLD_CARD}?thread_id=9`])
  assert.deepEqual(cardsUp(), [{ tag: 'v1.5.0', id: '500000000000000001' }])
})

test('a card Discord would not delete stays listed, and a second call for the same tag deletes it without posting again', async () => {
  mirror.store.set('announce/cinema/v1.4.9.json', JSON.stringify(posted(OLD_CARD)))
  let refuse = true
  const discord = fakeDiscord({ deleteStatus: () => (refuse ? 500 : 204) })
  await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.deepEqual(cardsUp(), [{ tag: 'v1.4.9', id: OLD_CARD }, { tag: 'v1.5.0', id: '500000000000000001' }])
  refuse = false
  const again = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(again.status, 'already')
  assert.equal(discord.calls.filter((call) => call.method === 'POST').length, 1, 'no second card')
  assert.equal(deletes(discord).length, 2)
  assert.deepEqual(cardsUp(), [{ tag: 'v1.5.0', id: '500000000000000001' }])
  await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(discord.calls.length, 3, 'nothing left to do: a third call sends nothing to Discord')
})

test('once cards.json exists only the cards it lists are deleted, and a card already gone (404) is dropped', async () => {
  mirror.store.set('announce/cinema/v1.4.8.json', JSON.stringify(posted(OLDER_CARD)))
  mirror.store.set('announce/cinema/cards.json', JSON.stringify({ cards: [{ tag: 'v1.4.9', id: OLD_CARD }, { tag: 'bad', id: 'nope' }] }))
  const discord = fakeDiscord({ deleteStatus: 404 })
  await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.deepEqual(deletes(discord).map((url) => url.replace(/^.*\/messages\//, '')), [`${OLD_CARD}?thread_id=9`])
  assert.deepEqual(cardsUp(), [{ tag: 'v1.5.0', id: '500000000000000001' }])
})

test('a clean-up that cannot read the mirror still leaves the card posted', async () => {
  mirror.list = async () => { throw new Error('R2 is down') }
  const discord = fakeDiscord()
  const result = await announce({ app: 'cinema', tag: 'v1.5.0', announce: true, env: env(), mirror, fetcher: discord.fetcher, now: WHEN })
  assert.equal(result.status, 'posted')
  assert.equal(JSON.parse(mirror.store.get('announce/cinema/v1.5.0.json')).state, 'posted')
})

test('the route answers 400 to a body it cannot read, 413 to a long body, and posts the card for a valid POST', async () => {
  const discord = fakeDiscord()
  const originalFetch = globalThis.fetch
  globalThis.fetch = discord.fetcher
  try {
    const ctx = { waitUntil() {} }
    const route = { ...env(), MIRROR: mirror }
    const post = (body) => worker.fetch(new Request(`${ORIGIN}/v1/cinema/announce`, { method: 'POST', body }), route, ctx)
    assert.equal((await post('not json')).status, 400)
    assert.equal((await post(JSON.stringify({ tag: 'latest' }))).status, 400)
    assert.equal((await worker.fetch(new Request(`${ORIGIN}/v1/cinema/announce`), route, ctx)).status, 404, 'a GET is not a route')
    assert.equal((await worker.fetch(new Request(`${ORIGIN}/v1/cinema/latest`, { method: 'PUT' }), route, ctx)).status, 405)
    assert.equal((await post('x'.repeat(2000))).status, 413)
    const ok = await post(JSON.stringify({ tag: 'v1.5.0', announce: true }))
    assert.equal(ok.status, 200)
    assert.equal((await ok.json()).status, 'posted')
    assert.equal(discord.calls.length, 1)
  } finally {
    globalThis.fetch = originalFetch
  }
})

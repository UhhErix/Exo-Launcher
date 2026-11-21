import { env, SELF } from 'cloudflare:test'
import { beforeEach, describe, expect, it } from 'vitest'
import worker, { backoffSeconds, flagGet, forgetAllFlags, timeLeft } from '../../src/worker.js'
import limits from '../../src/limits.json'

let rules = []
let sent = []
let nextAddress = 1
const realFetch = globalThis.fetch

beforeEach(async () => {
  rules = []
  sent = []
  for (const key of (await env.RIOT_CACHE.list()).keys) await env.RIOT_CACHE.delete(key.name)
  await forgetAllFlags()
  globalThis.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === 'string' ? input : input.url)
    const method = (init.method ?? (typeof input === 'string' ? 'GET' : input.method) ?? 'GET').toUpperCase()
    sent.push({ method, url: url.href, headers: init.headers })
    const index = rules.findIndex(
      (r) => r.method === method && r.origin === url.origin && (typeof r.path === 'function' ? r.path(url) : url.pathname === r.path),
    )
    if (index < 0) throw new TypeError(`no mock for ${method} ${url}`)
    const matched = rules[index]
    if (!matched.persist) rules.splice(index, 1)
    return matched.reply(url)
  }
  return () => {
    globalThis.fetch = realFetch
  }
})

const rule = (method, origin, path, reply, persist = false) => rules.push({ method, origin, path, reply, persist })
const jsonReply = (body, status = 200, headers = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } })

const askWorker = (path, customEnv, ip = `198.51.100.${nextAddress++ % 250}`) => {
  const pending = []
  const ctx = { waitUntil: (promise) => pending.push(promise) }
  const req = new Request(`https://riot.getexo.app${path}`, { headers: { 'CF-Connecting-IP': ip } })
  return worker.fetch(req, { ...env, ...customEnv }, ctx).then(async (response) => {
    await Promise.allSettled(pending)
    return response
  })
}

describe('exo-riot worker', () => {
  it('reports health and rejects unknown paths or bad input', async () => {
    const health = await SELF.fetch('https://riot.getexo.app/health', { headers: { 'CF-Connecting-IP': '198.51.100.1' } })
    expect(health.status).toBe(200)
    expect(health.headers.get('access-control-allow-origin')).toBeNull()
    const body = await health.json()
    expect(body.ok).toBe(true)

    const bad = await askWorker('/v1/hours?riotId=notvalid', { RIOT_API_KEY: 'test-riot' })
    expect(bad.status).toBe(400)

    const badRegion = await askWorker('/v1/hours?riotId=Player%23NA1&region=invalid', { RIOT_API_KEY: 'test-riot' })
    expect(badRegion.status).toBe(400)

    const missing = await SELF.fetch('https://riot.getexo.app/v1/other', { headers: { 'CF-Connecting-IP': '198.51.100.2' } })
    expect(missing.status).toBe(404)
  })

  it('returns 503 unconfigured when no provider secrets are set', async () => {
    const res = await askWorker('/v1/hours?riotId=Player%23NA1', {})
    expect(res.status).toBe(503)
    const body = await res.json()
    expect(body.ok).toBe(false)
    expect(body.unconfigured).toBe(true)
  })

  it('aggregates Valorant, League, and TFT hours and caches results in KV', async () => {
    rule('GET', 'https://api.henrikdev.xyz', '/valorant/v2/account/Player/NA1', () =>
      jsonReply({ data: { puuid: 'val-puuid-1', region: 'na' } }))
    rule('GET', 'https://api.henrikdev.xyz', '/valorant/v1/by-puuid/stored-matches/na/val-puuid-1', () =>
      jsonReply({
        data: [
          { meta: { id: 'v1', mode: 'Competitive', game_length: 2400, started_at: '2026-10-10T10:00:00Z' } },
          { meta: { id: 'v2', mode: 'Unrated', game_length: 1800, started_at: '2026-10-12T10:00:00Z' } },
        ],
      }))

    rule('GET', 'https://americas.api.riotgames.com', '/riot/account/v1/accounts/by-riot-id/Player/NA1', () =>
      jsonReply({ puuid: 'riot-puuid-1' }))
    rule('GET', 'https://americas.api.riotgames.com', '/lol/match/v5/matches/by-puuid/riot-puuid-1/ids', () =>
      jsonReply(['NA1_100', 'NA1_101']))
    rule('GET', 'https://americas.api.riotgames.com', '/lol/match/v5/matches/NA1_100', () =>
      jsonReply({ info: { gameDuration: 1800, queueId: 420, gameEndTimestamp: 1760000000000 } }))
    rule('GET', 'https://americas.api.riotgames.com', '/lol/match/v5/matches/NA1_101', () =>
      jsonReply({ info: { gameDuration: 1200, queueId: 450, gameEndTimestamp: 1760100000000 } }))
    rule('GET', 'https://americas.api.riotgames.com', '/tft/match/v1/matches/by-puuid/riot-puuid-1/ids', () =>
      jsonReply(['NA1_TFT_1']))
    rule('GET', 'https://americas.api.riotgames.com', '/tft/match/v1/matches/NA1_TFT_1', () =>
      jsonReply({ info: { game_length: 2100, game_datetime: 1760200000000 } }))

    const keys = { RIOT_API_KEY: 'riot-secret', HENRIK_API_KEY: 'henrik-secret' }
    const first = await askWorker('/v1/hours?riotId=Player%23NA1&region=na', keys)
    expect(first.status).toBe(200)
    const body = await first.json()
    expect(body.ok).toBe(true)
    expect(body.cached).toBe(false)
    expect(body.games['riot:valorant'].minutes).toBe(70)
    expect(body.games['riot:valorant'].modes).toEqual({ competitive: 40, unrated: 30 })
    expect(body.games['riot:league_of_legends'].minutes).toBe(50)
    expect(body.games['riot:league_of_legends'].modes).toEqual({ 'ranked solo': 30, aram: 20 })
    expect(body.games['riot:teamfighttactics'].minutes).toBe(35)

    const sentAfterFirst = sent.length
    const second = await askWorker('/v1/hours?riotId=player%23na1', keys)
    expect(second.status).toBe(200)
    const cachedBody = await second.json()
    expect(cachedBody.cached).toBe(true)
    expect(cachedBody.games['riot:valorant'].minutes).toBe(70)
    expect(sent.length).toBe(sentAfterFirst)

    const kvKeys = (await env.RIOT_CACHE.list()).keys.map((k) => k.name)
    expect(kvKeys.every((k) => !k.toLowerCase().includes('player#na1'))).toBe(true)
  })

  it('backs off on upstream 429 and returns 404 when player has no history', async () => {
    rule('GET', 'https://api.henrikdev.xyz', '/valorant/v2/account/Ghost/NA1', () => jsonReply({}, 404))
    const missing = await askWorker('/v1/hours?riotId=Ghost%23NA1', { HENRIK_API_KEY: 'henrik-secret' })
    expect(missing.status).toBe(404)

    rule('GET', 'https://api.henrikdev.xyz', '/valorant/v2/account/Busy/NA1', () =>
      jsonReply({}, 429, { 'Retry-After': '45' }))
    const throttled = await askWorker('/v1/hours?riotId=Busy%23NA1', { HENRIK_API_KEY: 'henrik-secret' })
    expect(throttled.status).toBe(503)
    expect(await flagGet('backoff:henrik')).toBe('1')
    expect(backoffSeconds(429, '45')).toBe(45)
    expect(timeLeft(Date.now())).toBeLessThanOrEqual(limits.upstreamTimeoutMs)
  })
})

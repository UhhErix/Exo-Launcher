import {
  hashRiotId,
  leagueModeFromQueue,
  normalizeDurationSeconds,
  normalizeRegionHint,
  normalizeRiotId,
  orderedClusters,
  parseTimestampMs,
  sumMatchRecords,
  valorantModeFromName,
} from './aggregate.js'
import limits from './limits.json'

const RESULT_TTL_SECONDS = 6 * 3600
const MATCH_TTL_SECONDS = 30 * 86400
const MISS_TTL_SECONDS = 600
const MAX_BACKOFF_SECONDS = 900
const RATE_LIMIT_BACKOFF_SECONDS = 60
const FAILURE_BACKOFF_SECONDS = 300
const TOTAL_BUDGET_MS = limits.totalBudgetMs
const UPSTREAM_TIMEOUT_MS = limits.upstreamTimeoutMs
const MAX_MATCHES_PER_GAME = limits.maxMatchesPerGame
const MAX_MATCH_FETCH_PER_REQUEST = limits.maxMatchFetchPerRequest
const MAX_HENRIK_PAGES = 5
const STATE_ORIGIN = 'https://riot.getexo.app/__state/'

class UpstreamError extends Error {
  constructor(status, retryAfter) {
    super(`upstream ${status}`)
    this.status = status
    this.retryAfter = retryAfter
  }
}

class Limited extends Error {}
class Capped extends Error {}

export function timeLeft(startedAt, now = Date.now()) {
  return Math.min(UPSTREAM_TIMEOUT_MS, TOTAL_BUDGET_MS - (now - startedAt))
}

export function backoffSeconds(status, retryAfter) {
  if (status === 429) {
    const asked = Number(retryAfter)
    return Number.isFinite(asked) && asked > 0 ? Math.min(Math.ceil(asked), MAX_BACKOFF_SECONDS) : RATE_LIMIT_BACKOFF_SECONDS
  }
  return FAILURE_BACKOFF_SECONDS
}

const stateRequest = (name) => new Request(STATE_ORIGIN + encodeURIComponent(name))
const writtenFlags = new Set()

export async function flagGet(name) {
  try {
    const response = await caches.default.match(stateRequest(name))
    return response ? await response.text() : null
  } catch {
    return null
  }
}

export async function flagSet(name, value, ttl) {
  writtenFlags.add(name)
  try {
    await caches.default.put(stateRequest(name), new Response(value, { headers: { 'Cache-Control': `public, max-age=${ttl}` } }))
  } catch {
  }
}

export async function forgetAllFlags() {
  for (const name of [...writtenFlags]) {
    try {
      await caches.default.delete(stateRequest(name))
    } catch {
    }
  }
  writtenFlags.clear()
}

function json(body, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...extraHeaders },
  })
}

function tooMany() {
  return json({ ok: false, message: 'Slow down, then try again in a minute.' }, 429, { 'Retry-After': '60' })
}

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url)
    if (request.method !== 'GET' || (url.pathname !== '/v1/hours' && url.pathname !== '/health')) {
      return json({ ok: false, message: 'Not found.' }, 404)
    }

    const address = request.headers.get('CF-Connecting-IP') ?? 'unknown'
    if (env.RIOT_LIMIT && !(await env.RIOT_LIMIT.limit({ key: address })).success) return tooMany()

    if (url.pathname === '/health') return health(env)

    const id = normalizeRiotId(url.searchParams.get('riotId') ?? '')
    if (!id) return json({ ok: false, message: 'Riot ID must look like Name#TAG.' }, 400)

    const region = normalizeRegionHint(url.searchParams.get('region'))
    if (!region.valid) return json({ ok: false, message: 'Unknown Riot region.' }, 400)

    const hasRiot = typeof env.RIOT_API_KEY === 'string' && env.RIOT_API_KEY.trim().length > 0
    const hasHenrik = typeof env.HENRIK_API_KEY === 'string' && env.HENRIK_API_KEY.trim().length > 0
    if (!hasRiot && !hasHenrik) {
      return json({ ok: false, unconfigured: true, message: 'Exo Riot hours service is not configured yet.' }, 503)
    }

    try {
      return await lookupHours(id, region, env, ctx, address, { hasRiot, hasHenrik })
    } catch (err) {
      if (err instanceof Limited) return tooMany()
      if (err instanceof Capped) return json({ ok: false, retry: true, message: 'Exo Riot hours service is busy. Try again shortly.' }, 503, { 'Retry-After': '60' })
      return json({ ok: false, message: 'Riot hours lookup failed. Try again.' }, 500)
    }
  },
}

async function health(env) {
  let state = await flagGet('health:kv')
  if (state === null) {
    try {
      await env.RIOT_CACHE.get('health:probe')
      state = 'ok'
    } catch {
      state = 'down'
    }
    await flagSet('health:kv', state, 60)
  }
  const riotConfigured = typeof env.RIOT_API_KEY === 'string' && env.RIOT_API_KEY.trim().length > 0
  const henrikConfigured = typeof env.HENRIK_API_KEY === 'string' && env.HENRIK_API_KEY.trim().length > 0
  const providersBackedOff = (await flagGet('backoff:riot') ? 1 : 0) + (await flagGet('backoff:henrik') ? 1 : 0)
  const kvWorks = state === 'ok'
  return json(
    { ok: kvWorks, providers: { riot: riotConfigured, henrik: henrikConfigured }, providersBackedOffCount: providersBackedOff },
    kvWorks ? 200 : 503,
  )
}

async function lookupHours(id, region, env, ctx, address, config) {
  const keyHash = await hashRiotId(id.normalized)
  const cacheKey = `hours:v1:${keyHash}`
  const cached = await kvGetJson(env, cacheKey)
  if (cached && typeof cached === 'object' && cached.games && Object.keys(cached.games).length > 0) {
    return json({ ok: true, cached: true, ...cached })
  }

  if (await flagGet(`miss:${keyHash}`)) {
    return json({ ok: false, notFound: true, message: 'No Riot match history was found for that Riot ID.' }, 404)
  }

  if (env.RIOT_LOOKUP_LIMIT && !(await env.RIOT_LOOKUP_LIMIT.limit({ key: address })).success) throw new Limited()

  const startedAt = Date.now()
  const games = {}
  const coverage = {
    valorant: config.hasHenrik ? 'unavailable' : 'unconfigured',
    league: config.hasRiot ? 'unavailable' : 'unconfigured',
    tft: config.hasRiot ? 'unavailable' : 'unconfigured',
  }

  const jobs = []
  if (config.hasHenrik) {
    jobs.push(fetchValorant(id, region.henrikRegion, env, startedAt).then((res) => {
      coverage.valorant = res.status
      if (res.game) games['riot:valorant'] = res.game
    }))
  }
  if (config.hasRiot) {
    jobs.push(fetchRiotClusterGames(id, region.cluster, env, ctx, startedAt).then((res) => {
      coverage.league = res.leagueStatus
      coverage.tft = res.tftStatus
      if (res.league) games['riot:league_of_legends'] = res.league
      if (res.tft) games['riot:teamfighttactics'] = res.tft
    }))
  }

  await Promise.all(jobs)

  if (Object.keys(games).length > 0) {
    const payload = { games, coverage }
    ctx.waitUntil(kvPutJson(env, cacheKey, payload, RESULT_TTL_SECONDS))
    return json({ ok: true, cached: false, ...payload })
  }

  const activeStatuses = Object.values(coverage).filter((s) => s !== 'unconfigured')
  if (activeStatuses.length > 0 && activeStatuses.every((s) => s === 'not_found')) {
    ctx.waitUntil(flagSet(`miss:${keyHash}`, '1', MISS_TTL_SECONDS))
    return json({ ok: false, notFound: true, coverage, message: 'No Riot match history was found for that Riot ID.' }, 404)
  }

  return json(
    { ok: false, retry: true, coverage, message: 'Riot playtime data is temporarily unavailable. Try again later.' },
    503,
    { 'Retry-After': String(RATE_LIMIT_BACKOFF_SECONDS) },
  )
}

async function upstreamJson(provider, url, headers, env, startedAt) {
  if (await flagGet(`backoff:${provider}`)) return { status: 'backed_off', data: null }
  const left = timeLeft(startedAt)
  if (left <= 0) return { status: 'timeout', data: null }
  if (env.RIOT_UPSTREAM_LIMIT && !(await env.RIOT_UPSTREAM_LIMIT.limit({ key: provider })).success) throw new Capped()

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), left)
  try {
    const response = await fetch(url, { headers, signal: controller.signal })
    if (response.status === 404) return { status: 'not_found', data: null }
    if (response.status === 401 || response.status === 403 || response.status === 429 || response.status >= 500) {
      const wait = backoffSeconds(response.status, response.headers.get('Retry-After'))
      await flagSet(`backoff:${provider}`, '1', wait)
      throw new UpstreamError(response.status, wait)
    }
    if (!response.ok) return { status: 'error', data: null }
    const data = await response.json()
    return { status: 'ok', data }
  } catch (err) {
    if (err instanceof UpstreamError || err instanceof Capped) throw err
    return { status: 'error', data: null }
  } finally {
    clearTimeout(timer)
  }
}

async function fetchValorant(id, regionHint, env, startedAt) {
  const headers = { Authorization: env.HENRIK_API_KEY.trim(), Accept: 'application/json' }
  const encodedName = encodeURIComponent(id.gameName)
  const encodedTag = encodeURIComponent(id.tagLine)
  try {
    const accountRes = await upstreamJson(
      'henrik',
      `https://api.henrikdev.xyz/valorant/v2/account/${encodedName}/${encodedTag}`,
      headers,
      env,
      startedAt,
    )
    if (accountRes.status === 'not_found') return { status: 'not_found', game: null }
    if (accountRes.status !== 'ok' || !accountRes.data?.data) return { status: 'unavailable', game: null }

    const account = accountRes.data.data
    const region = (typeof account.region === 'string' && account.region.trim().toLowerCase()) || regionHint || 'na'
    const puuid = typeof account.puuid === 'string' ? account.puuid.trim() : ''
    if (!puuid) return { status: 'not_found', game: null }

    const records = []
    let truncated = false
    for (let page = 1; page <= MAX_HENRIK_PAGES; page++) {
      if (timeLeft(startedAt) <= 0) {
        truncated = true
        break
      }
      const matchesRes = await upstreamJson(
        'henrik',
        `https://api.henrikdev.xyz/valorant/v1/by-puuid/stored-matches/${encodeURIComponent(region)}/${encodeURIComponent(puuid)}?page=${page}&size=100`,
        headers,
        env,
        startedAt,
      )
      if (matchesRes.status === 'not_found') break
      if (matchesRes.status !== 'ok' || !Array.isArray(matchesRes.data?.data)) {
        if (records.length === 0) return { status: 'unavailable', game: null }
        truncated = true
        break
      }
      const batch = matchesRes.data.data
      for (const item of batch) {
        const meta = item?.meta ?? item?.metadata
        if (!meta || typeof meta !== 'object') continue
        const seconds = normalizeDurationSeconds(meta.game_length ?? meta.gameLengthMillis ?? meta.length, true)
        if (seconds <= 0) continue
        records.push({
          id: typeof meta.id === 'string' ? meta.id : typeof meta.matchid === 'string' ? meta.matchid : '',
          seconds,
          mode: valorantModeFromName(meta.mode ?? meta.queue),
          atMs: parseTimestampMs(meta.started_at ?? meta.game_start),
        })
      }
      if (batch.length < 100 || records.length >= MAX_MATCHES_PER_GAME) {
        if (batch.length >= 100) truncated = true
        break
      }
    }

    const summary = sumMatchRecords(records)
    if (!summary) return { status: 'not_found', game: null }
    return {
      status: 'ok',
      game: {
        minutes: summary.minutes,
        matches: summary.matches,
        modes: summary.modes,
        latestMatchAt: summary.latestMatchAt,
        partial: truncated || summary.matches >= MAX_MATCHES_PER_GAME,
        source: 'henrikdev',
      },
    }
  } catch (err) {
    if (err instanceof Capped) throw err
    return { status: 'unavailable', game: null }
  }
}

async function fetchRiotClusterGames(id, preferredCluster, env, ctx, startedAt) {
  const headers = { 'X-Riot-Token': env.RIOT_API_KEY.trim(), Accept: 'application/json' }
  const encodedName = encodeURIComponent(id.gameName)
  const encodedTag = encodeURIComponent(id.tagLine)
  try {
    let puuid = null
    let cluster = null
    let sawUnavailable = false
    for (const candidate of orderedClusters(preferredCluster)) {
      const res = await upstreamJson(
        'riot',
        `https://${candidate}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/${encodedName}/${encodedTag}`,
        headers,
        env,
        startedAt,
      )
      if (res.status === 'ok' && typeof res.data?.puuid === 'string' && res.data.puuid.length > 0) {
        puuid = res.data.puuid
        cluster = candidate
        break
      }
      if (res.status !== 'not_found') sawUnavailable = true
    }

    if (!puuid || !cluster) {
      const status = sawUnavailable ? 'unavailable' : 'not_found'
      return { leagueStatus: status, tftStatus: status, league: null, tft: null }
    }

    const [league, tft] = await Promise.all([
      fetchRiotMatchGame('lol', cluster, puuid, headers, env, ctx, startedAt),
      fetchRiotMatchGame('tft', cluster, puuid, headers, env, ctx, startedAt),
    ])

    return {
      leagueStatus: league.status,
      tftStatus: tft.status,
      league: league.game,
      tft: tft.game,
    }
  } catch (err) {
    if (err instanceof Capped) throw err
    return { leagueStatus: 'unavailable', tftStatus: 'unavailable', league: null, tft: null }
  }
}

async function fetchRiotMatchGame(kind, cluster, puuid, headers, env, ctx, startedAt) {
  const ids = []
  let truncated = false
  for (let start = 0; start < MAX_MATCHES_PER_GAME; start += 100) {
    if (timeLeft(startedAt) <= 0) {
      truncated = true
      break
    }
    const basePath = kind === 'lol'
      ? `/lol/match/v5/matches/by-puuid/${encodeURIComponent(puuid)}/ids?start=${start}&count=100`
      : `/tft/match/v1/matches/by-puuid/${encodeURIComponent(puuid)}/ids?start=${start}&count=100`
    const res = await upstreamJson('riot', `https://${cluster}.api.riotgames.com${basePath}`, headers, env, startedAt)
    if (res.status === 'not_found') break
    if (res.status !== 'ok' || !Array.isArray(res.data)) {
      if (ids.length === 0) return { status: 'unavailable', game: null }
      truncated = true
      break
    }
    ids.push(...res.data.filter((v) => typeof v === 'string' && v.length > 0))
    if (res.data.length < 100) break
  }

  if (ids.length === 0) return { status: 'not_found', game: null }
  const uniqueIds = [...new Set(ids)].slice(0, MAX_MATCHES_PER_GAME)
  if (ids.length >= MAX_MATCHES_PER_GAME) truncated = true

  const records = []
  let fetched = 0
  for (const matchId of uniqueIds) {
    const kvKey = `m:${kind}:${matchId}`
    const cached = await kvGetJson(env, kvKey)
    if (cached && typeof cached.d === 'number' && cached.d > 0) {
      records.push({
        id: matchId,
        seconds: cached.d,
        mode: kind === 'lol' ? leagueModeFromQueue(cached.q) : undefined,
        atMs: cached.t ?? 0,
      })
      continue
    }

    if (fetched >= MAX_MATCH_FETCH_PER_REQUEST || timeLeft(startedAt) <= 0) {
      truncated = true
      break
    }
    fetched++
    const detailPath = kind === 'lol'
      ? `/lol/match/v5/matches/${encodeURIComponent(matchId)}`
      : `/tft/match/v1/matches/${encodeURIComponent(matchId)}`
    const detail = await upstreamJson('riot', `https://${cluster}.api.riotgames.com${detailPath}`, headers, env, startedAt)
    if (detail.status !== 'ok' || !detail.data?.info) {
      if (detail.status !== 'not_found') truncated = true
      continue
    }

    const info = detail.data.info
    const seconds = kind === 'lol'
      ? normalizeDurationSeconds(info.gameDuration, Boolean(info.gameEndTimestamp))
      : normalizeDurationSeconds(info.game_length, true)
    if (seconds <= 0) continue

    const queueId = kind === 'lol' && typeof info.queueId === 'number' ? info.queueId : 0
    const atMs = kind === 'lol'
      ? parseTimestampMs(info.gameEndTimestamp ?? info.gameStartTimestamp ?? info.gameCreation)
      : parseTimestampMs(info.game_datetime)

    ctx.waitUntil(kvPutJson(env, kvKey, { d: seconds, q: queueId, t: atMs }, MATCH_TTL_SECONDS))
    records.push({
      id: matchId,
      seconds,
      mode: kind === 'lol' ? leagueModeFromQueue(queueId) : undefined,
      atMs,
    })
  }

  const summary = sumMatchRecords(records)
  if (!summary) return { status: truncated ? 'unavailable' : 'not_found', game: null }
  return {
    status: 'ok',
    game: {
      minutes: summary.minutes,
      matches: summary.matches,
      modes: summary.modes,
      latestMatchAt: summary.latestMatchAt,
      partial: true,
      source: 'riot-api',
    },
  }
}

async function kvGetJson(env, key) {
  try {
    const raw = await env.RIOT_CACHE.get(key)
    return raw ? JSON.parse(raw) : null
  } catch {
    return null
  }
}

async function kvPutJson(env, key, value, expirationTtl) {
  try {
    await env.RIOT_CACHE.put(key, JSON.stringify(value), { expirationTtl })
  } catch {
  }
}

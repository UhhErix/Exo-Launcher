const LEAGUE_QUEUES = new Map([
  [420, 'ranked solo'],
  [440, 'ranked flex'],
  [400, 'normal draft'],
  [430, 'normal blind'],
  [490, 'quickplay'],
  [450, 'aram'],
  [900, 'urf'],
  [1700, 'arena'],
])

const VALORANT_MODES = new Map([
  ['competitive', 'competitive'],
  ['ranked', 'competitive'],
  ['unrated', 'unrated'],
  ['spikerush', 'spike rush'],
  ['deathmatch', 'deathmatch'],
  ['ggteam', 'team deathmatch'],
  ['teamdeathmatch', 'team deathmatch'],
  ['swiftplay', 'swiftplay'],
  ['escalation', 'escalation'],
  ['replication', 'replication'],
])

const REGION_CLUSTERS = new Map([
  ['na', 'americas'],
  ['latam', 'americas'],
  ['br', 'americas'],
  ['eu', 'europe'],
  ['ap', 'asia'],
  ['kr', 'asia'],
  ['americas', 'americas'],
  ['europe', 'europe'],
  ['asia', 'asia'],
])

const HENRIK_REGIONS = new Set(['na', 'eu', 'ap', 'kr', 'latam', 'br'])
const MAX_MATCH_SECONDS = 12 * 3600

export function normalizeRiotId(raw) {
  if (typeof raw !== 'string') return null
  const trimmed = raw.trim()
  const hash = trimmed.indexOf('#')
  if (hash <= 0 || hash >= trimmed.length - 2) return null
  const gameName = trimmed.slice(0, hash).trim()
  const tagLine = trimmed.slice(hash + 1).trim()
  if (gameName.length < 1 || gameName.length > 22) return null
  if (tagLine.length < 2 || tagLine.length > 8) return null
  if (gameName.includes('#') || tagLine.includes('#')) return null
  return { gameName, tagLine, normalized: `${gameName}#${tagLine}` }
}

export function normalizeRegionHint(raw) {
  if (raw === null || raw === undefined || raw === '') return { cluster: 'americas', henrikRegion: null, valid: true }
  if (typeof raw !== 'string') return { cluster: 'americas', henrikRegion: null, valid: false }
  const key = raw.trim().toLowerCase()
  const cluster = REGION_CLUSTERS.get(key)
  if (!cluster) return { cluster: 'americas', henrikRegion: null, valid: false }
  return { cluster, henrikRegion: HENRIK_REGIONS.has(key) ? key : null, valid: true }
}

export function orderedClusters(preferred) {
  const all = ['americas', 'europe', 'asia']
  return preferred && all.includes(preferred) ? [preferred, ...all.filter((c) => c !== preferred)] : all
}

export function leagueModeFromQueue(queueId) {
  const id = Number(queueId)
  if (!Number.isFinite(id) || id <= 0) return 'unknown'
  return LEAGUE_QUEUES.get(id) ?? 'other'
}

export function valorantModeFromName(raw) {
  if (typeof raw !== 'string' || raw.trim().length === 0) return 'unknown'
  const key = raw.toLowerCase().replace(/[^a-z0-9]/g, '')
  return VALORANT_MODES.get(key) ?? 'other'
}

export function normalizeDurationSeconds(rawDuration, hasEndTimestamp = true) {
  const value = Number(rawDuration)
  if (!Number.isFinite(value) || value <= 0) return 0
  const seconds = value > 86400 || (!hasEndTimestamp && value > 20000) ? Math.round(value / 1000) : Math.round(value)
  if (seconds <= 0 || seconds > MAX_MATCH_SECONDS) return 0
  return seconds
}

export function parseTimestampMs(raw) {
  if (typeof raw === 'number' && Number.isFinite(raw) && raw > 0) {
    return raw < 10_000_000_000 ? raw * 1000 : Math.round(raw)
  }
  if (typeof raw === 'string' && raw.trim().length > 0) {
    const parsed = Date.parse(raw)
    if (Number.isFinite(parsed) && parsed > 0) return parsed
  }
  return 0
}

export function sumMatchRecords(records) {
  const seen = new Set()
  let totalSeconds = 0
  let count = 0
  let latestMs = 0
  const modeSeconds = new Map()
  for (const record of records) {
    if (!record || typeof record !== 'object') continue
    const id = typeof record.id === 'string' && record.id.length > 0 ? record.id : null
    if (id) {
      if (seen.has(id)) continue
      seen.add(id)
    }
    const seconds = Number(record.seconds)
    if (!Number.isFinite(seconds) || seconds <= 0 || seconds > MAX_MATCH_SECONDS) continue
    totalSeconds += seconds
    count++
    if (typeof record.mode === 'string' && record.mode.length > 0 && record.mode !== 'unknown') {
      modeSeconds.set(record.mode, (modeSeconds.get(record.mode) ?? 0) + seconds)
    }
    const ts = Number(record.atMs)
    if (Number.isFinite(ts) && ts > latestMs) latestMs = ts
  }
  if (totalSeconds <= 0 || count === 0) return null
  const minutes = Math.max(1, Math.round(totalSeconds / 60))
  const modes = {}
  for (const [mode, secs] of modeSeconds.entries()) {
    const m = Math.round(secs / 60)
    if (m > 0) modes[mode] = m
  }
  return {
    minutes,
    matches: count,
    modes: Object.keys(modes).length > 0 ? modes : undefined,
    latestMatchAt: latestMs > 0 ? new Date(latestMs).toISOString() : undefined,
  }
}

export async function hashRiotId(normalized) {
  const bytes = new TextEncoder().encode(normalized.toLowerCase())
  const digest = await crypto.subtle.digest('SHA-256', bytes)
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('').slice(0, 32)
}

import type { Game } from './host'

/** A URL plus whether it needs the wash (portrait art standing in for a banner). */
export type ArtCandidate = { url: string; derived: boolean }

export type ArtGame = Pick<Game, 'id' | 'coverUrl' | 'coverSource' | 'launchTarget' | 'variants' | 'artRevision' | 'includes'> & {
  heroUrl?: string | null
}

export type CoverGame = Pick<
  Game,
  'id' | 'title' | 'coverUrl' | 'coverSource' | 'store' | 'launchTarget' | 'variants' | 'artRevision' | 'includes'
>

export const COVER_CACHE_ORIGIN = 'https://covers.exo-launcher.local'
const THUMB_ORIGIN = 'https://thumbs.exo-launcher.local'

/** Survives grid remounts so covers don't flash to the title plate. Key includes fit. */
const loadedUrlByKey = new Map<string, string>()
const LOADED_URL_MAX_ENTRIES = 4096

/**
 * Wide URLs that failed, and when. Keeps a remount off a dead URL without making
 * the miss permanent — the native cover warm may cache real hero art later.
 */
const wideMissAtMs = new Map<string, number>()
const WIDE_MISS_TTL_MS = 10_000
const WIDE_MISS_MAX_ENTRIES = 1024

export function rememberLoadedUrl(key: string, url: string): void {
  if (!loadedUrlByKey.has(key) && loadedUrlByKey.size >= LOADED_URL_MAX_ENTRIES) {
    const oldest = loadedUrlByKey.keys().next().value
    if (oldest !== undefined) loadedUrlByKey.delete(oldest)
  }
  loadedUrlByKey.set(key, url)
}

export function getRememberedLoadedUrl(key: string): string | undefined {
  return loadedUrlByKey.get(key)
}

export function forgetLoadedUrl(key: string): void {
  loadedUrlByKey.delete(key)
}

export function recordWideMiss(url: string): void {
  wideMissAtMs.delete(url)
  if (wideMissAtMs.size >= WIDE_MISS_MAX_ENTRIES) {
    const oldest = wideMissAtMs.keys().next().value
    if (oldest !== undefined) wideMissAtMs.delete(oldest)
  }
  wideMissAtMs.set(url, Date.now())
}

export function clearWideMiss(url: string): void {
  wideMissAtMs.delete(url)
}

export function cacheKey(gameId: string, fit: 'poster' | 'full' | 'wash', artRevision?: number): string {
  return `${gameId}::${fit}::${artRevision ?? 0}`
}

/** Grid cards and the sheet's full-size cover remember their bitmaps separately. */
export function posterCacheKey(game: CoverGame, large?: boolean): string {
  return cacheKey(game.id, large ? 'full' : 'poster', game.artRevision)
}

/** Revalidate only Exo-owned cache URLs; official CDN URLs keep their stable identity. */
export function withArtRevision(url: string, artRevision?: number): string {
  if (!artRevision || !url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return url
  return `${url}${url.includes('?') ? '&' : '?'}rev=${artRevision}`
}

/** Read during render (HeroWash's candidates), so it only reads: a stale entry goes when its art lands or the map is full. */
function isFreshWideMiss(url: string): boolean {
  const at = wideMissAtMs.get(url)
  return at !== undefined && Date.now() - at < WIDE_MISS_TTL_MS
}

function httpsHostIs(url: string, host: string): boolean {
  try {
    const parsed = new URL(url)
    return (
      parsed.protocol === 'https:' &&
      (parsed.port === '' || parsed.port === '443') &&
      (parsed.hostname === host || parsed.hostname.endsWith(`.${host}`))
    )
  } catch {
    return false
  }
}

function isOfficialEpicPortraitCdn(url: string): boolean {
  try {
    const parsed = new URL(url)
    return (
      parsed.protocol === 'https:' &&
      (parsed.hostname === 'cdn1.epicgames.com' || parsed.hostname === 'cdn2.unrealengine.com')
    )
  } catch {
    return false
  }
}

function isOfficialSteamPortraitCdn(url: string): boolean {
  const hostOk = httpsHostIs(url, 'steamstatic.com') || httpsHostIs(url, 'steamcdn-a.akamaihd.net')
  if (!hostOk) return false
  if (
    url.includes('library_hero') ||
    url.includes('header.jpg') ||
    url.includes('capsule_231') ||
    url.includes('capsule_184') ||
    url.includes('capsule_616') ||
    url.includes('capsule_sm')
  ) {
    return false
  }
  return (
    url.includes('library_600x900') ||
    url.includes('library_capsule') ||
    url.includes('portrait.png') ||
    url.includes('library_600x900_schinese')
  )
}

function isOfficialSteamHeroCdn(url: string): boolean {
  const hostOk = httpsHostIs(url, 'steamstatic.com') || httpsHostIs(url, 'steamcdn-a.akamaihd.net')
  return hostOk && (url.includes('library_hero') || url.includes('page_bg_raw') || url.includes('page_bg_generated'))
}

function isOfficialSteamHeaderCdn(url: string): boolean {
  const hostOk = httpsHostIs(url, 'steamstatic.com') || httpsHostIs(url, 'steamcdn-a.akamaihd.net')
  return (
    hostOk &&
    (url.includes('/header.jpg') || url.includes('/header_2x.jpg') || url.includes('capsule_616x353'))
  )
}

const SAFE_COVER_HOSTS: readonly string[] = [
  'gog-statics.com',
  'ddragon.leagueoflegends.com',
  'riotgames.com',
  'playvalorant.com',
  'leagueoflegends.com',
  'store-images.s-microsoft.com',
  'images-eds-ssl.xboxlive.com',
  'ubisoft.com',
  'ubi.com',
  'ea.com',
  'origin.com',
  'blizzard.com',
  'battle.net',
  'cdn.akamai.steamstatic.com',
]

export function isSafeCoverUrl(url: string | null | undefined, allowHero = false): url is string {
  if (!url) return false
  if (url.startsWith('data:image/') || url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return true
  if (isOfficialSteamPortraitCdn(url) || isOfficialEpicPortraitCdn(url)) return true
  if (allowHero && (isOfficialSteamHeroCdn(url) || isOfficialSteamHeaderCdn(url))) return true
  return SAFE_COVER_HOSTS.some((host) => httpsHostIs(url, host))
}

export function isIconCover(url: string | null | undefined, coverSource?: string | null): boolean {
  if (coverSource === 'icon') return true
  if (!url) return false
  return /\/icon_[^/?#]+\.png(?:$|[?#])/i.test(url)
}

export function isRejectedCoverUrl(url: string, allowHero: boolean): boolean {
  if (allowHero && /(?:^|[/_])hero[_./-]/i.test(url)) return false
  if (allowHero && isOfficialSteamHeaderCdn(url)) return false
  return (
    /(?:^|[/_])hero[_./-]/i.test(url) ||
    /(?:^|[/_])banner[_./-]/i.test(url) ||
    /(?:^|[/_])header(?:\.[a-z0-9]+|$)/i.test(url) ||
    url.includes('capsule_231') ||
    url.includes('capsule_184') ||
    url.includes('capsule_sm') ||
    url.includes('capsule_616') ||
    url.includes('library_hero') ||
    url.includes('product_card') ||
    (url.includes('product_tile') && !/product_tile_256(?!0)/.test(url))
  )
}

/**
 * Mirrors CoverArtService.SanitizeId across UTF-16 code units.
 */
export function sanitizeCacheId(gameId: string): string {
  let out = ''
  for (let i = 0; i < gameId.length; i += 1) {
    const unit = gameId[i]
    out += /[\p{L}\p{Nd}]/u.test(unit) ? unit : '_'
  }
  return out
}

export function buildWideArtCandidates(
  game: ArtGame,
  appId: string | null,
  wideCache: string | null,
): ArtCandidate[] {
  const list: ArtCandidate[] = []
  const push = (url: string | null | undefined, derived: boolean) => {
    if (!url || !isSafeCoverUrl(url, true) || isRejectedCoverUrl(url, true)) return
    if (isIconCover(url, game.coverSource)) return
    if (!derived && isFreshWideMiss(url)) return
    if (list.some((candidate) => candidate.url === url)) return
    list.push({ url, derived })
  }
  push(wideCache, false)
  if (appId) {
    const fastly = `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}`
    const cloudflare = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
    push(`${fastly}/library_hero_2x.jpg`, false)
    push(`${fastly}/library_hero.jpg`, false)
    push(`${cloudflare}/library_hero_2x.jpg`, false)
    push(`${cloudflare}/library_hero.jpg`, false)
    push(`${fastly}/page_bg_raw.jpg`, false)
    push(`${cloudflare}/page_bg_raw.jpg`, false)
  }
  push(game.heroUrl, false)
  if (appId) {
    const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
    push(`${base}/header.jpg`, true)
    push(`${base}/capsule_616x353.jpg`, true)
  }
  push(game.coverUrl ? withArtRevision(game.coverUrl, game.artRevision) : null, true)
  return list
}

function pushSteamPortraitCandidates(
  appId: string,
  steamCacheUrls: readonly (string | null)[],
  raw: string | null,
  rawIsHiRes: boolean,
  pushUnique: (url: string | null | undefined) => void,
  list: ArtCandidate[],
): void {
  const fastly = `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}`
  const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
  for (const url of steamCacheUrls) {
    if (url?.includes('_2x')) pushUnique(url)
  }
  if (!rawIsHiRes) {
    pushUnique(`${fastly}/library_600x900_2x.jpg`)
    pushUnique(`${base}/library_600x900_2x.jpg`)
    pushUnique(raw)
  }
  for (const url of steamCacheUrls) pushUnique(url)
  for (const url of [
    `${fastly}/library_600x900_2x.jpg`,
    `${fastly}/library_600x900.jpg`,
    `${fastly}/library_capsule.jpg`,
    `${base}/library_600x900_2x.jpg`,
    `${base}/library_600x900.jpg`,
    `${base}/library_capsule.jpg`,
  ]) {
    pushUnique(url)
  }
  for (const storeUrl of [`${fastly}/header.jpg`, `${base}/header.jpg`, `${base}/capsule_616x353.jpg`]) {
    if (!list.some((candidate) => candidate.url === storeUrl)) list.push({ url: storeUrl, derived: true })
  }
}

export function buildPortraitArtCandidates(
  game: Pick<CoverGame, 'coverUrl' | 'artRevision'>,
  appId: string | null,
  steamCacheUrls: readonly (string | null)[],
  toThumb?: (url: string) => string,
  toDimThumb?: (url: string) => string,
): ArtCandidate[] {
  const list: ArtCandidate[] = []
  const pushUnique = (url: string | null | undefined) => {
    if (!url || list.some((candidate) => candidate.url === url)) return
    if (!isSafeCoverUrl(url, false) || isRejectedCoverUrl(url, false)) return
    list.push({ url, derived: false })
  }
  const raw = game.coverUrl ? withArtRevision(game.coverUrl, game.artRevision) : null
  const rawIsHiRes = !!raw && (raw.includes('custom_') || raw.includes('_2x'))
  if (rawIsHiRes) {
    pushUnique(raw)
    if (raw.includes('library_600x900_2x')) pushUnique(raw.replace('library_600x900_2x', 'library_600x900'))
  }
  if (appId) {
    pushSteamPortraitCandidates(appId, steamCacheUrls, raw, rawIsHiRes, pushUnique, list)
  } else if (!rawIsHiRes) {
    pushUnique(raw)
  }
  if (!toThumb) return list
  return list.flatMap((candidate) => {
    const small = toThumb(candidate.url)
    if (small === candidate.url) return [candidate]
    return toDimThumb
      ? [{ ...candidate, url: toDimThumb(candidate.url) }, { ...candidate, url: small }, candidate]
      : [{ ...candidate, url: small }, candidate]
  })
}

export function artTimeoutMs(url: string): number {
  if (url.startsWith(`${THUMB_ORIGIN}/`)) return 2000
  if (url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return 1500
  if (url.includes('library_600x900') || url.includes('library_capsule') || url.includes('portrait.png')) return 800
  return 1200
}

export function rememberedIndex(key: string, candidates: ArtCandidate[]): number {
  const hit = loadedUrlByKey.get(key)
  return hit ? candidates.findIndex((c) => c.url === hit) : -1
}

export function rememberedCandidateUrl(key: string, candidates: ArtCandidate[]): string | null {
  const hit = loadedUrlByKey.get(key)
  return hit && candidates.some((entry) => entry.url === hit) ? hit : null
}

/** A retry must reach the host again, not a cached miss; only Exo's own origins ignore the query. */
export function withAttempt(url: string, attempt: number): string {
  if (!attempt || !(url.startsWith(`${COVER_CACHE_ORIGIN}/`) || url.startsWith(`${THUMB_ORIGIN}/`))) return url
  return `${url}${url.includes('?') ? '&' : '?'}retry=${attempt}`
}

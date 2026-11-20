import { artSteamAppId } from './library'
import { cachedCoverUrl, dimThumbUrl, thumbUrl } from './coverThumb'
import {
  artTimeoutMs,
  type ArtCandidate,
  type ArtGame,
  buildPortraitArtCandidates,
  buildWideArtCandidates,
  cacheKey,
  clearWideMiss,
  type CoverGame,
  forgetLoadedUrl,
  getRememberedLoadedUrl,
  isIconCover,
  posterCacheKey,
  recordWideMiss,
  rememberedCandidateUrl,
  rememberedIndex,
  rememberLoadedUrl,
  sanitizeCacheId,
  withArtRevision,
  withAttempt,
} from './coverUrlPolicy'

export {
  artTimeoutMs,
  type ArtCandidate,
  type ArtGame,
  cacheKey,
  clearWideMiss,
  type CoverGame,
  forgetLoadedUrl,
  getRememberedLoadedUrl,
  isIconCover,
  posterCacheKey,
  recordWideMiss,
  rememberedCandidateUrl,
  rememberedIndex,
  rememberLoadedUrl,
  withAttempt,
}

function cachedArt(name: string, artRevision?: number): string | null {
  const url = cachedCoverUrl(name)
  return url ? withArtRevision(url, artRevision) : null
}

/** Landscape art for one title, best first: native cache, Steam heroes, then blurred portrait cover wash. */
export function wideArtCandidates(game: ArtGame): ArtCandidate[] {
  const cacheTarget = game.includes?.[0] ? `steam:${game.includes[0].appId}` : game.id
  const wideCache = cachedArt(`hero_${sanitizeCacheId(cacheTarget)}.jpg`, game.artRevision)
  return buildWideArtCandidates(game, artSteamAppId(game), wideCache)
}

/** Portrait art candidates for library grid cards and large sheet covers. */
export function portraitArtCandidates(
  game: Pick<CoverGame, 'coverUrl' | 'artRevision'>,
  appId: string | null,
  thumb: number | null = null,
  dim = false,
): ArtCandidate[] {
  const steamCacheUrls = appId
    ? [
        cachedArt(`${appId}_2x.jpg`, game.artRevision),
        cachedArt(`${appId}.jpg`, game.artRevision),
        cachedArt(`steam_${appId}.jpg`, game.artRevision),
      ]
    : []
  return buildPortraitArtCandidates(
    game,
    appId,
    steamCacheUrls,
    thumb == null ? undefined : (url) => thumbUrl(url, thumb),
    thumb != null && dim ? (url) => dimThumbUrl(url, thumb) : undefined,
  )
}

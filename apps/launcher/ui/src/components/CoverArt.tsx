import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type RefObject } from 'react'
import { cn } from '../lib/utils'
import { artSteamAppId } from '../lib/library'
import { isDimThumbUrl, thumbUrl, useCoverThumbWidth } from '../lib/coverThumb'
import {
  coverBitmapFit,
  isHeroShaped,
  isWashableBitmap,
  isWideBitmap,
  shouldArmCoverTimeout,
  shouldKeepCoverBitmap,
} from '../lib/coverFit'
import {
  type ArtCandidate,
  type ArtGame,
  artTimeoutMs,
  cacheKey,
  clearWideMiss,
  type CoverGame,
  forgetLoadedUrl,
  getRememberedLoadedUrl,
  isIconCover,
  portraitArtCandidates,
  posterCacheKey,
  recordWideMiss,
  rememberedCandidateUrl,
  rememberedIndex,
  rememberLoadedUrl,
  wideArtCandidates,
  withAttempt,
} from '../lib/coverCandidates'
import {
  preloadInitialCoverArt,
  preloadLibraryArtwork,
  useCoverPageVisible,
  useNearViewport,
  useRetryOnReturn,
} from './coverBitmap'

export { preloadInitialCoverArt, preloadLibraryArtwork, wideArtCandidates }

/** Where this card is on its fallback ladder, for one key and one candidate list. */
type Ladder = { id: string; idx: number; failed: boolean; attempt: number }

function freshLadder(id: string, key: string, candidates: ArtCandidate[], attempt: number): Ladder {
  const remembered = rememberedIndex(key, candidates)
  return { id, idx: remembered >= 0 && !candidates[remembered].derived ? remembered : 0, failed: false, attempt }
}

const decodingFor = (large: boolean | undefined) => (large ? 'sync' : 'async')

function GridBitmap({ url, large }: { url: string | null; large?: boolean }) {
  return url ? <img src={url} alt="" className="exo-cover-front" data-under="" draggable={false} decoding={decodingFor(large)} /> : null
}

function isDecoded(el: HTMLImageElement | null): el is HTMLImageElement {
  return !!el && el.complete && el.naturalWidth > 0
}

type CoverFit = { url: string; fit: 'cover' | 'contain' }
function currentCoverFit(metrics: CoverFit | null, url: string | null, icon: boolean) {
  if (icon) return 'contain'
  return metrics?.url === url ? metrics.fit : 'cover'
}

function usePortraitCandidates(game: CoverGame, large: boolean, dim = false): ArtCandidate[] {
  const gridThumb = useCoverThumbWidth()
  const thumb = large ? null : gridThumb
  const { coverUrl, artRevision } = game
  const appId = artSteamAppId(game)
  return useMemo(() => portraitArtCandidates({ coverUrl, artRevision }, appId, thumb, dim), [coverUrl, artRevision, appId, thumb, dim])
}

function useCoverLadder(key: string, candidates: ArtCandidate[]) {
  const ladderId = `${key}\n${candidates[0]?.url ?? ''}`
  const [stored, setLadder] = useState(() => freshLadder(ladderId, key, candidates, 0))
  const ladder = stored.id === ladderId ? stored : freshLadder(ladderId, key, candidates, stored.attempt)
  if (ladder !== stored) setLadder(ladder)
  const count = candidates.length
  const advance = useCallback(() => {
    setLadder((current) => {
      if (current.id !== ladderId || current.failed) return current
      return current.idx + 1 >= count ? { ...current, failed: true } : { ...current, idx: current.idx + 1 }
    })
  }, [count, ladderId])
  const retry = () => {
    setLadder((current) => current.id === ladderId ? { ...current, idx: 0, failed: false, attempt: current.attempt + 1 } : current)
  }
  return { ...ladder, advance, retry }
}

function useCoverTimeout(url: string | null, armed: boolean, imageRef: RefObject<HTMLImageElement | null>, advance: () => void): void {
  const pageVisible = useCoverPageVisible()
  useEffect(() => {
    if (!url || !armed || !pageVisible) return
    const timeout = window.setTimeout(() => {
      if (!isDecoded(imageRef.current)) advance()
    }, artTimeoutMs(url))
    return () => window.clearTimeout(timeout)
  }, [advance, armed, imageRef, pageVisible, url])
}

/** What a card draws this frame: the rung it tries, the URL it shows, and the last good bitmap under it. */
function coverFrame({ key, candidates, idx, failed, attempt, loadedUrl, fallbackSrc }: {
  key: string
  candidates: ArtCandidate[]
  idx: number
  failed: boolean
  attempt: number
  loadedUrl: string | null
  fallbackSrc: string | null
}) {
  const safeUrl = (candidates[idx] ?? candidates[0])?.url ?? null
  const displayUrl = safeUrl && !failed ? safeUrl : rememberedCandidateUrl(key, candidates)
  const src = displayUrl ? withAttempt(displayUrl, attempt) : null
  const loaded = !!displayUrl && (loadedUrl === displayUrl || getRememberedLoadedUrl(key) === displayUrl)
  const underlay = !loaded && fallbackSrc !== src ? fallbackSrc : null
  return { safeUrl, displayUrl, src, loaded, underlay }
}

/**
 * Cover art: gradient + title underneath; image after onLoad.
 * Library tiles stay portrait while large surfaces prefer high-res posters.
 */
export function CoverArt({
  game,
  className,
  large,
  preload = false,
  dim = false,
  undim = false,
}: {
  game: CoverGame
  className?: string
  large?: boolean
  preload?: boolean
  dim?: boolean
  undim?: boolean
}) {
  const key = posterCacheKey(game, large)
  const candidates = usePortraitCandidates(game, !!large, dim)
  const eager = !!(large || preload)
  const { idx, failed, attempt, advance, retry } = useCoverLadder(key, candidates)

  const [loadedUrl, setLoadedUrl] = useState<string | null>(null)
  const [shownSrc, setShownSrc] = useState<string | null>(() => getRememberedLoadedUrl(key) ?? null)
  const [bitmapFit, setBitmapFit] = useState<CoverFit | null>(null)
  const imageRef = useRef<HTMLImageElement>(null)
  const coverRef = useRef<HTMLDivElement>(null)
  const nearViewport = useNearViewport(coverRef, eager, game.id)
  const lastCandidate = idx + 1 >= candidates.length
  const { safeUrl, displayUrl, src, loaded, underlay } = coverFrame({
    key,
    candidates,
    idx,
    failed,
    attempt,
    loadedUrl,
    fallbackSrc: shownSrc ?? (large ? getRememberedLoadedUrl(posterCacheKey(game)) ?? null : null),
  })
  const icon = isIconCover(displayUrl, game.coverSource)

  useCoverTimeout(
    safeUrl,
    !failed && getRememberedLoadedUrl(key) !== safeUrl && shouldArmCoverTimeout({ eager, visible: nearViewport }),
    imageRef,
    advance,
  )
  useRetryOnReturn(failed || (loaded && !!candidates[idx]?.derived), coverRef, retry)

  function rejectCurrent() {
    if (displayUrl !== safeUrl) return
    if (lastCandidate && getRememberedLoadedUrl(key) === safeUrl) forgetLoadedUrl(key)
    advance()
  }

  const acceptCover = async (el: HTMLImageElement) => {
    const acceptedUrl = displayUrl
    const acceptedSrc = src
    try {
      if (getRememberedLoadedUrl(key) !== acceptedUrl) await el.decode()
    } catch { }
    if (imageRef.current !== el || !el.isConnected || !acceptedUrl) return
    const ok = shouldKeepCoverBitmap(el.naturalWidth, el.naturalHeight, {
      icon,
      derived: candidates.find((entry) => entry.url === acceptedUrl)?.derived,
      lastCandidate,
    })
    if (!ok) {
      rejectCurrent()
      return
    }
    rememberLoadedUrl(key, acceptedUrl)
    const fit = coverBitmapFit(el.naturalWidth, el.naturalHeight, icon)
    setBitmapFit((previous) => previous?.url === acceptedUrl && previous.fit === fit ? previous : { url: acceptedUrl, fit })
    setLoadedUrl(acceptedUrl)
    setShownSrc(acceptedSrc)
  }
  const acceptLatest = useRef(acceptCover)
  acceptLatest.current = acceptCover
  const attachImage = useCallback((el: HTMLImageElement | null) => {
    if (imageRef.current === el || (!el && imageRef.current?.isConnected)) return
    imageRef.current = el
    if (isDecoded(el)) void acceptLatest.current(el)
  }, [])

  return (
    <div ref={coverRef} className={cn('exo-cover', icon && 'is-icon exo-icon', className)}>
      <div className={cn('exo-cover-mono', (loaded || underlay) && 'is-under', large && 'is-large')} aria-hidden>
        <span>{game.title}</span>
      </div>
      <GridBitmap url={underlay} large={large} />
      <CoverFaces
        coverKey={key}
        src={src}
        dim={dim}
        undim={dim && undim}
        loaded={loaded}
        icon={icon}
        eager={eager}
        large={large}
        fit={currentCoverFit(bitmapFit, displayUrl, icon)}
        attachImage={attachImage}
        onLoad={acceptCover}
        onError={rejectCurrent}
      />
    </div>
  )
}

function CoverFaces({ coverKey, src, dim, undim, loaded, icon, eager, large, fit, attachImage, onLoad, onError }: {
  coverKey: string
  src: string | null
  dim: boolean
  undim: boolean
  loaded: boolean
  icon: boolean
  eager: boolean
  large?: boolean
  fit: CSSProperties['objectFit']
  attachImage: (el: HTMLImageElement | null) => void
  onLoad: (el: HTMLImageElement) => void
  onError: () => void
}) {
  const [undimArmed, setUndimArmed] = useState(false)
  if (undim && !undimArmed) setUndimArmed(true)
  return (
    <>
      {src && fit === 'contain' && !icon && loaded ? (
        <img
          src={src}
          alt=""
          className="exo-cover-front exo-cover-backdrop-fill"
          draggable={false}
          decoding="async"
          aria-hidden="true"
        />
      ) : null}
      {src && (
        <img
          key={`${coverKey}:${src}`}
          src={src}
          alt=""
          className={cn('exo-cover-front', icon && 'exo-icon', dim && !isDimThumbUrl(src) && 'is-dim-fallback')}
          style={{ opacity: loaded ? 1 : 0, objectFit: fit }}
          draggable={false}
          loading={eager ? 'eager' : 'lazy'}
          fetchPriority={eager ? 'high' : 'low'}
          decoding={decodingFor(large)}
          ref={attachImage}
          onLoad={(e) => onLoad(e.currentTarget)}
          onError={onError}
        />
      )}
      <UndimCover src={src} undimArmed={undimArmed} undim={undim} fit={fit} />
    </>
  )
}

function UndimCover({ src, undimArmed, undim, fit }: {
  src: string | null
  undimArmed: boolean
  undim: boolean
  fit: CSSProperties['objectFit']
}) {
  if (!undimArmed || !src || !isDimThumbUrl(src)) return null
  return (
    <img
      src={src.replace('&dim=1', '')}
      alt=""
      className={cn('exo-cover-front exo-cover-undim', undim && 'is-on')}
      style={{ objectFit: fit }}
      draggable={false}
      decoding="async"
      aria-hidden="true"
    />
  )
}

/**
 * Wide store art behind the game sheet and Continue banners, falling back to a blurred portrait wash.
 */
export function HeroWash({
  game,
  className,
  poster = false,
  fill = true,
  thumb = null,
}: {
  game: ArtGame
  className?: string
  poster?: boolean
  fill?: boolean
  thumb?: number | null
}) {
  const key = cacheKey(game.id, 'wash', game.artRevision)
  const candidates = useMemo(() => wideArtCandidates(game).map((c) => ({ ...c, url: thumbUrl(c.url, thumb) })), [game, thumb])
  const primary = candidates[0]?.url ?? ''

  return <HeroWashImage key={`${key}:${primary}`} imageCacheKey={key} candidates={candidates} className={className} poster={poster} fill={fill} />
}

function HeroWashImage({ imageCacheKey: key, candidates, className, poster, fill }: {
  imageCacheKey: string
  candidates: ArtCandidate[]
  className?: string
  poster: boolean
  fill: boolean
}) {
  const cachedHit = getRememberedLoadedUrl(key)
  const cachedIdx = cachedHit ? candidates.findIndex((c) => c.url === cachedHit) : -1
  const [idx, setIdx] = useState(cachedIdx >= 0 ? cachedIdx : 0)
  const [ok, setOk] = useState(cachedIdx >= 0)
  const [washed, setWashed] = useState(false)
  const [letterbox, setLetterbox] = useState(false)
  const appliedKey = useRef('')

  const candidate = candidates[idx]
  if (!candidate) return null

  const advance = () => setIdx((i) => i + 1)

  const applyWashMetrics = (img: HTMLImageElement) => {
    const token = `${idx}:${candidate.url}:${img.naturalWidth}x${img.naturalHeight}`
    if (appliedKey.current === token) return
    appliedKey.current = token
    const wide = isWideBitmap(img.naturalWidth, img.naturalHeight)
    const hero = isHeroShaped(img.naturalWidth, img.naturalHeight)
    if (!candidate.derived) {
      if (!wide) {
        advance()
        return
      }
      clearWideMiss(candidate.url)
      rememberLoadedUrl(key, candidate.url)
      setWashed(false)
      setLetterbox(!hero)
      setOk(true)
      return
    }
    if (!isWashableBitmap(img.naturalWidth, img.naturalHeight)) {
      advance()
      return
    }
    if (wide) rememberLoadedUrl(key, candidate.url)
    setWashed(!wide)
    setLetterbox(wide && !hero)
    setOk(true)
  }

  return (
    <>
      {fill && letterbox && ok ? (
        <img
          src={candidate.url}
          alt=""
          className="exo-now-wash-img exo-cover-fill exo-cover-derived is-on"
          draggable={false}
          decoding="async"
          aria-hidden
        />
      ) : null}
      <img
        src={candidate.url}
        alt=""
        className={cn(
          'exo-now-wash-img',
          letterbox && 'is-letterbox',
          washed && 'exo-cover-derived',
          washed && 'is-derived',
          ok && 'is-on',
          className,
        )}
        draggable={false}
        decoding="async"
        ref={(el) => {
          if (el && el.complete && el.naturalWidth > 0) applyWashMetrics(el)
        }}
        onLoad={(e) => applyWashMetrics(e.currentTarget)}
        onError={() => {
          if (!candidate.derived) recordWideMiss(candidate.url)
          advance()
        }}
      />
      {poster && washed && ok ? <img src={candidate.url} alt="" className="exo-now-wash-poster" draggable={false} decoding="async" aria-hidden /> : null}
    </>
  )
}

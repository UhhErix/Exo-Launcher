import { useEffect, useRef, useState, useSyncExternalStore, type RefObject } from 'react'
import { isWideBitmap, shouldKeepCoverBitmap } from '../lib/coverFit'
import { coverThumbWidth, isThumbUrl } from '../lib/coverThumb'
import { artSteamAppId } from '../lib/library'
import { observeNearViewport } from '../lib/nearViewport'
import { onPageActivityChanged, pageIsVisible } from '../lib/pageActivity'
import {
  artTimeoutMs,
  cacheKey,
  type CoverGame,
  isIconCover,
  portraitArtCandidates,
  rememberedIndex,
  rememberLoadedUrl,
  wideArtCandidates,
} from '../lib/coverCandidates'

/** First-screen posters at the default 1400×900 library grid (~7×3 plus slack). */
const VISIBLE_COVER_PREFETCH = 14

type BitmapSize = { naturalWidth: number; naturalHeight: number }
const pendingBitmaps = new Map<string, Promise<BitmapSize | null>>()

function loadBitmap(url: string, timeoutMs: number): Promise<BitmapSize | null> {
  const existing = pendingBitmaps.get(url)
  if (existing) return existing
  const loading = new Promise<BitmapSize | null>((resolve) => {
    const image = new Image()
    let settled = false
    const finish = (value: BitmapSize | null) => {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      image.onload = null
      image.onerror = null
      if (!value) image.removeAttribute('src')
      resolve(value)
    }
    const timeout = window.setTimeout(() => finish(null), timeoutMs)
    image.decoding = 'async'
    image.onload = () => {
      if (settled) return
      const decoded = typeof image.decode === 'function' ? image.decode() : Promise.resolve()
      void decoded
        .catch(() => undefined)
        .then(() => finish({ naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight }))
    }
    image.onerror = () => finish(null)
    image.src = url
  })
  const pending = loading.finally(() => {
    if (pendingBitmaps.get(url) === pending) pendingBitmaps.delete(url)
  })
  pendingBitmaps.set(url, pending)
  return pending
}

/** Decode the first screen's posters before the boot mark yields to the shell. */
export async function preloadInitialCoverArt(
  games: readonly CoverGame[],
  limit = VISIBLE_COVER_PREFETCH,
  signal?: AbortSignal,
): Promise<void> {
  if (typeof window === 'undefined' || typeof Image === 'undefined') return
  const unique = Array.from(new Map(games.map((game) => [game.id, game])).values()).slice(0, limit)
  const thumb = coverThumbWidth()
  await Promise.all(unique.map(async (game) => {
    const candidates = portraitArtCandidates(game, artSteamAppId(game), thumb)
    if (rememberedIndex(cacheKey(game.id, 'poster', game.artRevision), candidates) >= 0) return
    const own = isThumbUrl(candidates[0]?.url ?? '') ? 2 : 1
    for (let index = 0; index < Math.min(own, candidates.length); index += 1) {
      if (signal?.aborted) return
      const candidate = candidates[index]
      const image = await loadBitmap(candidate.url, artTimeoutMs(candidate.url))
      if (!image) continue
      const icon = isIconCover(candidate.url, game.coverSource)
      if (!shouldKeepCoverBitmap(image.naturalWidth, image.naturalHeight, {
        icon,
        derived: candidate.derived,
        lastCandidate: index + 1 >= candidates.length,
      })) continue
      rememberLoadedUrl(cacheKey(game.id, 'poster', game.artRevision), candidate.url)
      return
    }
  }))
}

/**
 * Warm both posters and detail backgrounds for the complete library. A game with no cover has no banner on disk either.
 */
export async function preloadLibraryArtwork(
  games: readonly CoverGame[],
  concurrency = 6,
  signal?: AbortSignal,
): Promise<void> {
  const unique = Array.from(new Map(games.filter((game) => game.coverUrl).map((game) => [game.id, game])).values())
  let cursor = 0
  await Promise.all(Array.from({ length: Math.min(concurrency, unique.length) }, async () => {
    while (cursor < unique.length && !signal?.aborted && pageIsVisible()) {
      const game = unique[cursor++]
      await Promise.all([
        preloadInitialCoverArt([game], 1, signal),
        (async () => {
          const candidates = wideArtCandidates(game)
          if (rememberedIndex(cacheKey(game.id, 'wash', game.artRevision), candidates) >= 0) return
          for (const candidate of candidates) {
            if (signal?.aborted || !pageIsVisible()) return
            const bitmap = await loadBitmap(candidate.url, artTimeoutMs(candidate.url))
            if (!bitmap || (!candidate.derived && !isWideBitmap(bitmap.naturalWidth, bitmap.naturalHeight))) continue
            rememberLoadedUrl(cacheKey(game.id, 'wash', game.artRevision), candidate.url)
            break
          }
        })(),
      ])
    }
  }))
}

/** Every card's page listener, through one pair on the document (lib/pageActivity), not one per card. */
const pageListeners = new Set<() => void>()
let stopWatchingPage: (() => void) | null = null

function watchPage(listener: () => void): () => void {
  pageListeners.add(listener)
  stopWatchingPage ??= onPageActivityChanged(() => { for (const each of pageListeners) each() })
  return () => {
    pageListeners.delete(listener)
    if (pageListeners.size > 0) return
    stopWatchingPage?.()
    stopWatchingPage = null
  }
}

export function useCoverPageVisible(): boolean {
  return useSyncExternalStore(watchPage, pageIsVisible, () => true)
}

/** A card waiting to try its art again: whether it is in view, and whether it left view since it was armed. */
type ReturnWatch = { inView: boolean; wasOut: boolean; done: boolean; fire: () => void }
const returnWatches = new Map<Element, ReturnWatch>()
let returnObserver: IntersectionObserver | null = null

function onReturnShown() {
  if (!pageIsVisible()) return
  for (const watch of returnWatches.values()) if (watch.inView) watch.fire()
}

function watchReturn(element: Element, watch: ReturnWatch): () => void {
  if (!returnObserver) {
    returnObserver = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const each = returnWatches.get(entry.target)
        if (!each) continue
        each.inView = entry.isIntersecting
        if (!each.inView) each.wasOut = true
        else if (each.wasOut) each.fire()
      }
    })
    document.addEventListener('visibilitychange', onReturnShown)
    window.addEventListener('focus', onReturnShown)
  }
  returnWatches.set(element, watch)
  returnObserver.observe(element)
  return () => {
    if (returnWatches.get(element) !== watch) return
    returnWatches.delete(element)
    returnObserver?.unobserve(element)
    if (returnWatches.size > 0) return
    returnObserver?.disconnect()
    returnObserver = null
    document.removeEventListener('visibilitychange', onReturnShown)
    window.removeEventListener('focus', onReturnShown)
  }
}

/**
 * A card that ran out of art (or stands on a landscape stand-in) tries again
 * when it returns into view or when the window is shown or focused again.
 */
export function useRetryOnReturn(armed: boolean, ref: RefObject<HTMLElement | null>, retry: () => void): void {
  const retryLatest = useRef(retry)
  const leftArmed = useRef(false)
  useEffect(() => { retryLatest.current = retry })
  useEffect(() => {
    const element = ref.current
    if (!armed || !element || typeof IntersectionObserver === 'undefined') return
    const watch: ReturnWatch = {
      inView: false,
      wasOut: leftArmed.current,
      done: false,
      fire: () => {
        if (watch.done) return
        watch.done = true
        retryLatest.current()
      },
    }
    leftArmed.current = false
    const stop = watchReturn(element, watch)
    return () => {
      leftArmed.current = !watch.done
      stop()
    }
  }, [armed, ref])
}

/** Lazy tiles only start their fallback ladder once they are near the viewport. */
export function useNearViewport(
  ref: RefObject<HTMLElement | null>,
  eager: boolean,
  resetKey: string,
): boolean {
  const [near, setNear] = useState(eager)
  useEffect(() => {
    if (eager) {
      setNear(true)
      return
    }
    setNear(false)
    const element = ref.current
    if (!element || typeof IntersectionObserver === 'undefined') {
      setNear(true)
      return
    }
    return observeNearViewport(element, () => setNear(true))
  }, [eager, ref, resetKey])
  return near
}

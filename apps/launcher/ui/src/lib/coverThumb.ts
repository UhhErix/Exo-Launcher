import { useSyncExternalStore } from 'react'
import { resolveGridCardLayout } from './gridWindow'

/**
 * Grid cards draw posters at ~150–310 CSS px; the cached files are up to
 * 1200 px wide. The host serves a downscale of the exact cached file at
 * https://thumbs.exo-launcher.local/<file>?w=<px> (CoverArtService.Thumbnails).
 *
 * The width is the widest card this screen can produce at any window size in
 * device pixels, rounded up to an 8 px step: the host's Lanczos pass is the
 * one resample and Chromium draws it about 1:1. Twice the card (Chromium
 * halving it through its mipmaps) read softer than the game page's cover. The
 * host serves the original whenever the thumbnail would be within a quarter
 * of it. Resizing the window never changes the URL. It only grows (a larger
 * or denser monitor), so a card never swaps to a softer bitmap mid-session.
 */

const THUMB_ORIGIN = 'https://thumbs.exo-launcher.local'
const COVER_ORIGIN = 'https://covers.exo-launcher.local'

/**
 * The cover cache's address this run, with the token LocalFileHost asks of every request (art.base); null until the
 * host has said. Addresses the host sends already carry it; this is for the files the page names itself.
 */
let coverBase: string | null = null

export function setCoverBase(base: string | null | undefined): void {
  coverBase = base?.startsWith(`${COVER_ORIGIN}/`) && base.endsWith('/') ? base : null
}

/** A file in Exo's cover cache by its name, at this run's address; null before the host has given it. */
export function cachedCoverUrl(name: string): string | null {
  return coverBase ? coverBase + name : null
}
const THUMB_STEP = 8
const THUMB_MIN = 120
/** Thumbnail pixels per device pixel of the widest card. */
const THUMB_OVERSAMPLE = 1
/** Past this the original is about as small as the thumbnail would be. */
const THUMB_MAX = 720
/** MainWindow's logical minimum width; the library pane spans the window. */
const MIN_PANE_WIDTH = 1100

let current: number | null = null
const listeners = new Set<() => void>()

function widestCard(maxPaneWidth: number): number {
  let widest = 0
  for (let pane = MIN_PANE_WIDTH; pane <= maxPaneWidth; pane += 1) {
    widest = Math.max(widest, resolveGridCardLayout(pane).cardWidth)
  }
  return widest || resolveGridCardLayout(MIN_PANE_WIDTH).cardWidth
}

/** Thumbnail width for this screen, or null when the full file is the right size. */
export function thumbWidthFor(screenCssWidth: number, devicePixelRatio: number): number | null {
  const wanted = Math.ceil(widestCard(Math.ceil(screenCssWidth)) * Math.max(1, devicePixelRatio) * THUMB_OVERSAMPLE)
  const width = Math.max(THUMB_MIN, Math.ceil(wanted / THUMB_STEP) * THUMB_STEP)
  return width > THUMB_MAX ? null : width
}

function screenThumbWidth(): number | null {
  return thumbWidthFor(Math.max(window.screen?.width ?? 0, window.innerWidth), window.devicePixelRatio || 1)
}

/** All the width depends on: the screen's width (as thumbWidthFor rounds it) and its pixel ratio. */
const screenKey = () => `${Math.ceil(Math.max(window.screen?.width ?? 0, window.innerWidth))}@${window.devicePixelRatio || 1}`

let measured = false
function ensureMeasured(): void {
  if (measured || typeof window === 'undefined') return
  measured = true
  current = screenThumbWidth()
  let measuredFor = screenKey()
  const remeasure = () => {
    // Only grow; null (the full file) is the largest.
    if (current === null) return
    // Worked out again only when what it depends on changed: dragging the window's edge on one screen changes neither.
    const key = screenKey()
    if (key === measuredFor) return
    measuredFor = key
    const next = screenThumbWidth()
    if (next !== null && next <= current) return
    current = next
    for (const listener of listeners) listener()
  }
  window.addEventListener('resize', remeasure)
  // A move to a denser monitor (or a zoom) can change the ratio without a resize.
  const watchRatio = () => {
    window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`).addEventListener('change', () => {
      remeasure()
      watchRatio()
    }, { once: true })
  }
  watchRatio()
}

export function coverThumbWidth(): number | null {
  ensureMeasured()
  return current
}

function subscribe(listener: () => void): () => void {
  ensureMeasured()
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useCoverThumbWidth(): number | null {
  return useSyncExternalStore(subscribe, coverThumbWidth, () => null)
}

/** The narrowest wide art a banner is sized for: 16:9 store art; Steam's heroes (about 3:1) are wider still. */
const BANNER_ART_ASPECT = 16 / 9

/**
 * The thumbnail width (thumbUrl's ?w=) that covers a `width` by `height` CSS px banner with wide art at device pixels,
 * or null when the original is the right size. The host sizes a thumbnail for a 2:3 card, so for wide art its height is
 * 1.5 times the asked width; that height covers the banner's, and its width the banner's for art 16:9 or wider.
 */
export function bannerThumbWidth(width: number, height: number, devicePixelRatio: number): number | null {
  const tall = Math.max(height, width / BANNER_ART_ASPECT) * Math.max(1, devicePixelRatio)
  const asked = Math.max(THUMB_MIN, Math.ceil(tall / 1.5 / THUMB_STEP) * THUMB_STEP)
  return asked > THUMB_MAX ? null : asked
}

/** Exo's cached cover URL as its grid thumbnail (the same token and name on the thumbnail host); other URLs pass through. */
export function thumbUrl(url: string, width: number | null): string {
  if (width == null || !url.startsWith(`${COVER_ORIGIN}/`)) return url
  const rest = url.slice(COVER_ORIGIN.length)
  return `${THUMB_ORIGIN}${rest}${rest.includes('?') ? '&' : '?'}w=${width}`
}

/** The not-installed variant of a grid thumbnail: the host applies the CSS filter's
 *  saturate(0.45) brightness(0.42) once while it resizes, so a dim card paints plain. */
export function dimThumbUrl(url: string, width: number | null): string {
  return `${thumbUrl(url, width)}&dim=1`
}

/** A URL the host dimmed (&dim=1); a dim card showing anything else gets the CSS filter instead. */
export function isDimThumbUrl(url: string | null | undefined): boolean {
  return !!url && url.includes('dim=1')
}

export function isThumbUrl(url: string): boolean {
  return url.startsWith(`${THUMB_ORIGIN}/`)
}
import type { PlayerView } from '../../lib/player'

/** Pages, which the player keeps per service and shows again from memory. Live state (the queue, devices, Jam, friends) is not here: it always loads fresh. */
export const PAGES: ReadonlySet<PlayerView> = new Set<PlayerView>(['home', 'playlist', 'album', 'artist', 'show', 'collection', 'library', 'lyrics', 'explore', 'browse',
  'radio', 'credits', 'related', 'discography'])

/**
 * The service's own picks (Home, Explore, New, Radio) come back in a new order on every read. The room's effects run again
 * each time it shows (React's Activity), so Home read again on every visit and its shelves swapped cards under the pointer
 * a second after the room appeared. A pick page read within PICK_HOLD_MS stays as it was.
 */
export const PICKS: ReadonlySet<PlayerView> = new Set<PlayerView>(['home', 'explore', 'browse', 'radio'])
export const PICK_HOLD_MS = 10 * 60_000
/** Any other page read within this stands in for a new read (stale-while-revalidate with a short hold; an action forgets it at once). */
export const PAGE_HOLD_MS = 20_000

/** How long a read of this view may stand in for a new read: 0 for live state, which always loads. */
export function holdMs(view: PlayerView): number {
  if (!PAGES.has(view)) return 0
  return PICKS.has(view) ? PICK_HOLD_MS : PAGE_HOLD_MS
}

/** Whether a read of this view that is `ageMs` old may be shown without asking the service again. */
export function standsIn(view: PlayerView, ageMs: number): boolean {
  return ageMs >= 0 && ageMs < holdMs(view)
}

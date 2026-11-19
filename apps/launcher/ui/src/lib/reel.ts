// Which clips a highlight reel takes (Captures › Make reel). Plain data, no DOM, so `npm test` (test/reel.test.ts) can run it.
import type { CaptureItem } from './capture'

/** Clips made this far apart or less are one sitting. */
export const SESSION_GAP_MS = 3 * 60 * 60 * 1000

const time = (item: CaptureItem) => Date.parse(item.created)

/** The videos of the newest sitting (not a trim, export or reel, whose picture is in another clip): from the newest back, until two are more than a sitting's gap apart. Newest first. */
export function latestSession(items: readonly CaptureItem[]): CaptureItem[] {
  const newestFirst = items.filter(item => item.kind === 'video' && !item.derived).sort((a, b) => time(b) - time(a))
  const session: CaptureItem[] = []
  for (const item of newestFirst) {
    const previous = session.at(-1)
    if (previous && time(previous) - time(item) > SESSION_GAP_MS) break
    session.push(item)
  }
  return session
}

/**
 * What a game's Make reel takes: the starred clips of its newest sitting when two or more are starred (the highlights),
 * else all that sitting's clips. Fewer than two is no reel.
 */
export function reelOfGame(items: readonly CaptureItem[]): CaptureItem[] {
  const session = latestSession(items)
  const starred = session.filter(item => item.favourite)
  const picked = starred.length >= 2 ? starred : session
  return picked.length >= 2 ? picked : []
}

/** The picked clips that are videos (a snip cannot be in a reel). */
export function reelOfPicked(items: readonly CaptureItem[], picked: ReadonlySet<string>): CaptureItem[] {
  return items.filter(item => item.kind === 'video' && picked.has(item.id))
}

// Listen along's clock: where the listener should be in a followed song, and when the listener's own player is far
// enough from that to be moved. No imports: the Node test in ui/test runs this file as it is.

/** Where the followed song's clock comes from. A call song's start is worked out on this PC from the position it was
    sent with, on a monotonic clock, so it is as good as the relay's delay; a Discord activity's start is the friend's
    own PC's wall clock, so it carries that clock's error and is trusted less. */
export type SyncSource = 'call' | 'presence'

/** tolerance: drift (ms) that is left alone; cooldown: the least time (ms) between two corrections of one song. */
export const SYNC: Record<SyncSource, { tolerance: number; cooldown: number }> = {
  call: { tolerance: 1500, cooldown: 8000 },
  presence: { tolerance: 5000, cooldown: 20000 },
}

/** A seek this close to the end of a song (ms) is never worth it: the next song's own start follows. */
const NEAR_END = 3000
const MAX_LATENCY = 1500

/** Where the friend is in the song at wallNow (ms since the epoch); latency: how long a seek takes to land. */
export function expectedPosition(start: number, wallNow: number, latency = 0) {
  return Math.max(0, wallNow - start + latency)
}

/** The seek time measured (ms) folded into the running estimate; one slow outlier moves it a third of the way. */
export function nextLatency(previous: number | null, sample: number) {
  if (!Number.isFinite(sample) || sample < 0) return previous
  const clamped = Math.min(sample, MAX_LATENCY)
  return previous === null ? clamped : Math.round(previous + (clamped - previous) / 3)
}

/**
 * Whether the listener's player (actual, ms into the song) should be moved to expected now. Never while paused or
 * unknown, never twice inside the cooldown (the first seek may still be landing), never near the song's end, and never
 * for drift inside the source's tolerance.
 */
export function shouldCorrect(input: {
  source: SyncSource; actual: number | null; expected: number; playing: boolean
  sinceLastSeek: number; endsIn: number | null
}) {
  const { tolerance, cooldown } = SYNC[input.source]
  if (!input.playing || input.actual === null || !Number.isFinite(input.actual)) return false
  if (input.sinceLastSeek < cooldown) return false
  if (input.endsIn !== null && input.endsIn < NEAR_END) return false
  return Math.abs(input.actual - input.expected) > tolerance
}

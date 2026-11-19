// The heartbeat's behaviour, apart from the host bridge so it can be tested alone (heartbeat.ts wires it to the page).
const EVERY_MS = 1000

export type BeatDeps = {
  /** Whether the page is shown and in front now. */
  active: () => boolean
  send: () => Promise<unknown>
  every: (run: () => void, ms: number) => number
  stop: (timer: number) => void
}

/** `sync` after every activity change: the timer runs only while the page is active, one beat at a time. */
export function createBeat({ active, send, every, stop }: BeatDeps) {
  let timer = 0
  let inFlight = false
  const beat = () => {
    if (inFlight) return
    inFlight = true
    send().catch(() => {}).finally(() => { inFlight = false })
  }
  const sync = () => {
    const want = active()
    if (want && !timer) {
      timer = every(beat, EVERY_MS)
      beat()
    } else if (!want && timer) {
      stop(timer)
      timer = 0
    }
  }
  return { sync, running: () => timer !== 0 }
}

/**
 * The page beats while the host can see the page: not hidden or minimized. Not "while focused": the host's watchdog is armed
 * whenever any Exo window is in front (a file picker, a pop-out), so a page that stopped beating when it lost focus would be
 * reported as frozen. `exoInactive` (no focus) is deliberately ignored here.
 */
export function beatWanted(flags: { exoHidden?: string, exoInactive?: string }): boolean {
  return flags.exoHidden !== 'true'
}

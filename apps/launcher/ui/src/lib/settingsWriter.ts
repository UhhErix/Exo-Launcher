/**
 * The one way settings are saved. Saves go to the host one at a time, in the order they were made, and only the newest
 * save's answer is taken into the page: an older answer, arriving late, would put back what a newer save already changed. A
 * reload is skipped while saves are still on their way for the same reason.
 *
 * State table, per save: queued -> sent -> answered. `apply` runs for an answer only when no newer save was made since (the
 * newer one's answer carries the whole result). When the newest save fails, the last answer that was held back is applied
 * instead (it is what the host stores), and the failure rejects to the caller, who tells the player. Rows:
 *   answered, newest                         -> apply it
 *   answered, a newer save exists            -> hold it (the newer answer will carry it)
 *   failed, newest, an answer was held back  -> apply the held answer, reject
 *   failed, newest, nothing held back        -> reject (the caller reads the settings again)
 *   failed, a newer save exists              -> reject, nothing applied
 */
export function createSettingsWriter<S, P>(deps: { send: (patch: P) => Promise<S>, apply: (saved: S) => void }) {
  let made = 0
  let open = 0
  let held: { saved: S } | null = null
  let tail: Promise<unknown> = Promise.resolve()
  // Reads that wait for the saves to be done (see read).
  let idleWaiters: Array<() => void> = []
  const settle = () => {
    if (open !== 0) return
    const waiting = idleWaiters
    idleWaiters = []
    for (const resume of waiting) resume()
  }
  return {
    save(patch: P): Promise<S> {
      const mine = ++made
      open++
      const run = tail.then(() => deps.send(patch))
      tail = run.catch(() => undefined)
      return run.then(
        saved => {
          open--
          settle()
          if (mine === made) {
            held = null
            deps.apply(saved)
          } else {
            held = { saved }
          }
          return saved
        },
        (error: unknown) => {
          open--
          settle()
          if (mine === made && held) {
            const keep = held.saved
            held = null
            deps.apply(keep)
          }
          throw error
        },
      )
    },
    /**
     * Reads the settings once no save is open, so a queue of saves cannot overtake it. The answer is still null when a save was
     * made after the read began or is on its way when the read answers: the read may not have seen that change, and taking it
     * would put the old value back.
     */
    async read(fetch: () => Promise<S>): Promise<S | null> {
      while (open > 0) await new Promise<void>(resume => { idleWaiters.push(resume) })
      const at = made
      const value = await fetch()
      return at === made && open === 0 ? value : null
    },
    /** No save is on its way. */
    idle: () => open === 0,
  }
}

/**
 * Reads until a read stands (see read), a few times over. When none does, it throws instead of answering nothing, so the page
 * shows its normal settings error and offers a retry rather than staying on its gate with no word.
 */
export async function readUntilSettled<S>(read: () => Promise<S | null>, retries = 3): Promise<S> {
  for (let again = 0; again <= retries; again++) {
    const value = await read()
    if (value) return value
  }
  throw new Error('Settings could not be loaded')
}

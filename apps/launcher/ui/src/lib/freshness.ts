/**
 * What a list read shows (DESIGN.md › Waits): `loading` until the first answer (its skeleton, never the empty line),
 * `failed` when the read did not answer (why, with Try again, never "nothing here"), `empty` only when the answer said
 * so, and `ready` otherwise. A list already on screen stays `ready` through a failed read behind it: what was shown is
 * still the best there is, and the failure says itself beside it.
 */
export type ReadView = 'loading' | 'failed' | 'empty' | 'ready'

export function readView(list: readonly unknown[] | null | undefined, failed?: string | boolean | null): ReadView {
  if (list && list.length > 0) return 'ready'
  if (failed) return 'failed'
  if (!list) return 'loading'
  return 'empty'
}

type Target = Pick<EventTarget, 'addEventListener' | 'removeEventListener'>
export type ReturnEnv = { win: Target; doc: Target & { hidden: boolean }; now: () => number }

const browserEnv = (): ReturnEnv => ({ win: window, doc: document, now: () => performance.now() })

/**
 * Calls `run` when what the page shows may have gone stale behind it: Exo's window coming back to the front (focus),
 * the page coming back on screen (visibilitychange to visible) and the network coming back (online). One return is
 * often all three at once, so calls within `gapMs` of the last are one. Returns the unsubscribe.
 */
export function whenBack(run: () => void, gapMs = 2000, env: ReturnEnv = browserEnv()): () => void {
  let last = -Infinity
  const go = () => {
    if (env.doc.hidden) return
    const at = env.now()
    if (at - last < gapMs) return
    last = at
    run()
  }
  env.win.addEventListener('focus', go)
  env.win.addEventListener('online', go)
  env.doc.addEventListener('visibilitychange', go)
  return () => {
    env.win.removeEventListener('focus', go)
    env.win.removeEventListener('online', go)
    env.doc.removeEventListener('visibilitychange', go)
  }
}

/**
 * What an account leaves behind in the page's own memory (module-level caches of lists, channels, messages): each
 * cache registers how to forget itself under the account it belongs to, and signing out of (or switching) that
 * account forgets them all, so the next account never sees the last one's data, not even for a frame.
 */
const forgetters = new Map<string, Set<() => void>>()

export function forgetOnSignOut(account: string, forget: () => void): () => void {
  let set = forgetters.get(account)
  if (!set) forgetters.set(account, set = new Set())
  set.add(forget)
  return () => {
    set.delete(forget)
    if (set.size === 0 && forgetters.get(account) === set) forgetters.delete(account)
  }
}

export function signedOut(account: string): void {
  for (const forget of [...(forgetters.get(account) ?? [])]) {
    try { forget() } catch { /* one cache failing to clear never stops the rest */ }
  }
}

/**
 * The account a cache was filled under, so a change of account (a Steam switch, another Discord sign-in) is seen even
 * when no sign-out event came between them. `changed(id)` is true when `id` differs from the one before (and remembers
 * it); the first id seen is not a change.
 */
export function accountWatch(): { changed: (id: string | null | undefined) => boolean } {
  let seen: string | null | undefined
  let any = false
  return {
    changed(id) {
      const was = seen
      const first = !any
      any = true
      seen = id ?? null
      return !first && was !== seen
    },
  }
}

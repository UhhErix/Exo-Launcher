/**
 * One copy of this PC's account state for the whole page: the shell's account gate, setup's account step and Settings ›
 * Account all read it, so signing in (or choosing Continue without an account) in one is seen by the others at once.
 * Before, each kept its own copy read when it mounted, and the shell (mounted while setup ran) still thought the PC was
 * signed out after setup signed it in, and asked again.
 *
 * The state, and what moves it:
 *
 * | Event                          | overview                                        | laterThisRun | codesOnScreen |
 * | ------------------------------ | ----------------------------------------------- | ------------ | ------------- |
 * | start                          | null (not read yet: no sheet)                   | false        | 0             |
 * | refresh answers                | the answer, if no newer refresh was asked       | kept         | kept          |
 * | refresh fails, nothing read yet| null, and the read is tried again (2, 5, 10 s, then every 30 s) | kept | kept     |
 * | refresh fails after a read     | kept (reported)                                 | kept         | kept          |
 * | continueOffline                | kept                                            | true         | kept          |
 * | serviceBack                    | kept                                            | false        | kept          |
 * | a recovery code shows / goes   | kept                                            | kept         | +1 / -1 (by code: the same code held twice is one, and it stays while either holds it) |
 * | an account request starts/ends | kept                                            | kept         | requests +1 / -1 |
 *
 * A refresh is asked at first use, when the host says the session changed (exoAccount.changed: a sign-in, a sign-out, a
 * deleted account, a confirmed code, a session the service ended: watchAccount), and after each account action.
 */
export interface AccountView<T> {
  overview: T | null
  /** Continue without an account, offered only while Exo's account service can't be reached: until it answers again. */
  laterThisRun: boolean
  /** Recovery codes on screen now, each counted once (RecoveryCodeScreen, and the request that brought it). */
  codesOnScreen: number
  /** Account requests on their way (accountRequest): the screen that sent one is waiting on its answer. */
  requests: number
}

export interface AccountStore<T> {
  get: () => AccountView<T>
  subscribe: (listener: () => void) => () => void
  refresh: () => Promise<void>
  continueOffline: () => void
  serviceBack: () => void
  codeShown: (code: string) => () => void
  /** How many holders (a request's answer, a code screen) have this code on screen now. */
  codeHolders: (code: string) => number
  requestStarted: () => () => void
}

/** The waits before a failed first read is tried again: a host that failed once must not leave the gate shut for good. */
export const FIRST_READ_RETRY_MS = [2_000, 5_000, 10_000, 30_000] as const

export function accountStore<T>(
  load: () => Promise<T>,
  onError: (error: unknown) => void,
  schedule: (run: () => void, ms: number) => void = (run, ms) => { setTimeout(run, ms) },
): AccountStore<T> {
  let view: AccountView<T> = { overview: null, laterThisRun: false, codesOnScreen: 0, requests: 0 }
  const codes = new Map<string, number>()
  let asked = 0
  let failedFirst = 0
  const listeners = new Set<() => void>()
  const set = (next: AccountView<T>) => {
    view = next
    for (const listener of listeners) listener()
  }
  const store: AccountStore<T> = {
    get: () => view,
    subscribe(listener) {
      listeners.add(listener)
      return () => { listeners.delete(listener) }
    },
    refresh() {
      const turn = ++asked
      return load().then(
        (overview) => { if (turn === asked) set({ ...view, overview }) },
        (error: unknown) => {
          onError(error)
          if (view.overview !== null || turn !== asked) return
          const wait = FIRST_READ_RETRY_MS[Math.min(failedFirst++, FIRST_READ_RETRY_MS.length - 1)]
          schedule(() => { if (view.overview === null) void store.refresh() }, wait)
        },
      )
    },
    continueOffline() {
      if (!view.laterThisRun) set({ ...view, laterThisRun: true })
    },
    serviceBack() {
      if (view.laterThisRun) set({ ...view, laterThisRun: false })
    },
    codeShown(code) {
      const first = !codes.has(code)
      codes.set(code, (codes.get(code) ?? 0) + 1)
      // A second holder changes no count, but accountRequest listens for it: its hold ends when a screen holds the code too.
      if (first) set({ ...view, codesOnScreen: codes.size })
      else for (const listener of [...listeners]) listener()
      let released = false
      return () => {
        if (released) return
        released = true
        const left = (codes.get(code) ?? 1) - 1
        if (left > 0) { codes.set(code, left); return }
        codes.delete(code)
        set({ ...view, codesOnScreen: codes.size })
      }
    },
    codeHolders: (code) => codes.get(code) ?? 0,
    requestStarted() {
      set({ ...view, requests: view.requests + 1 })
      let done = false
      return () => {
        if (done) return
        done = true
        set({ ...view, requests: Math.max(0, view.requests - 1) })
      }
    },
  }
  return store
}

/** Reads the state at once and again on every exoAccount.changed. `on` is onHostEvent (lib/host.ts); returns the unsubscribe. */
export function watchAccount<T>(store: AccountStore<T>, on: (event: 'exoAccount.changed', handler: () => void) => () => void): () => void {
  const off = on('exoAccount.changed', () => { void store.refresh() })
  void store.refresh()
  return off
}

/**
 * One account request as the account state sees it: counted while it is on its way, and a recovery code in its answer is
 * on screen from the moment the answer lands (before the screen that shows it has mounted), so nothing reads that moment
 * as "no code anywhere". That registration is the request's own and ends once a code screen has taken the code over (it
 * then holds the code itself, and its leaving is what ends it) or after HANDOVER_MS if none ever does, so a code that no
 * screen shows can never hold the sheet up for good. Returns the answer.
 */
export const HANDOVER_MS = 3_000

export async function accountRequest<T, R>(
  store: AccountStore<T>,
  call: () => Promise<R>,
  codeOf: (answer: R) => string | null | undefined,
  schedule: (run: () => void, ms: number) => () => void = (run, ms) => {
    const timer = setTimeout(run, ms)
    return () => { clearTimeout(timer) }
  },
): Promise<R> {
  const done = store.requestStarted()
  try {
    const answer = await call()
    const code = codeOf(answer)
    if (code) {
      const release = store.codeShown(code)
      let stop = () => {}
      const finish = () => {
        stop()
        release()
      }
      const unsubscribe = store.subscribe(() => { if (store.codeHolders(code) > 1) finish() })
      const cancel = schedule(finish, HANDOVER_MS)
      stop = () => {
        unsubscribe()
        cancel()
      }
    }
    return answer
  } finally {
    done()
  }
}
import { useCallback, useEffect, useRef, useState } from 'react'
import { visibleInstallPercent } from '../../lib/utils'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { toRelease, type WhatsNewRelease } from '../../lib/whatsNew'
import { isUpdateState, sheetAfterCheck, updatePhase, type UpdateState } from '../../lib/updatePhase'
import { errorText } from '../../lib/errorWords'

/** The startup check may hold the boot mark this long, never more. */
const BOOT_HOLD_MS = 800

export type AppUpdate = {
  busy: boolean
  percent: number | null
  status: string
  /** True while an update is offered and not being installed. */
  available: boolean
  /** Why the last install failed (or the update before this start didn't take); null otherwise. */
  error: string | null
  /** The update before this start didn't take: "The update didn't install. Exo will try the next one." once; null otherwise. */
  problem: string | null
  dismissProblem: () => void
  /** Downloaded and verified while a game runs: when it goes in (after the game, at the next start); null otherwise. */
  waiting: string | null
  /** The first check is still running inside its short startup hold. */
  checking: boolean
  /** The launch's check hasn't answered yet: What's New waits for it, since the update sheet comes first. */
  pending: boolean
  latest: string | null
  /** The offered version's What's New (its release's whats-new.json), or null when it has none. */
  upcoming: WhatsNewRelease | null
  /** The update sheet is up: the launch's check found a newer release (update on open). */
  sheet: boolean
  /** An update has failed this launch, or the one before it didn't take: the sheet offers Not now. */
  notNow: boolean
  /** Not now: the sheet goes for this launch; the update stays in Settings › General. */
  setAside: () => void
  /** A failure's notice dismissed: the update stays offered in Settings › General. */
  dismiss: () => void
  install: () => Promise<void>
  /** Manual check from Settings. Never rejects; failure is a message. */
  check: () => Promise<UpdateCheck>
}

type UpdateCheck = { ok: boolean; updateAvailable: boolean; latest?: string | null; message: string }

/**
 * What the update going in says and how far, the same on the update sheet, Settings › General and its notice (DESIGN.md ›
 * Motion › Waits): "Downloading" with its share while it downloads (ProgressWords adds " · 54%"; the megabytes when the
 * share isn't known), then "Installing" with none, the bar gliding.
 */
export function updateWords(status: string, percent: number | null): { words: string; share: number | null } {
  if (!/^(Preparing|Downloading)/.test(status)) return { words: 'Installing', share: null }
  if (percent != null) return { words: 'Downloading', share: percent / 100 }
  const amount = /[\d.]+ MB/.exec(status)?.[0]
  return { words: amount ? `Downloading · ${amount}` : 'Downloading', share: null }
}

/**
 * App self-update: the launch's one check (the host starts it before this page) read on the boot mark, briefly held; a
 * newer release then opens the update sheet, once per launch. Nothing checks again while Exo runs but Settings' Check for
 * updates, which never opens the sheet. Once the shell has rendered it never returns to the splash, so hosted rooms keep
 * their layout.
 */
export function holdsBoot(update: AppUpdate): boolean {
  return update.checking
}

export function useAppUpdate(): AppUpdate {
  const [message, setMessage] = useState<string | null>(null)
  const [latest, setLatest] = useState<string | null>(null)
  const [upcoming, setUpcoming] = useState<WhatsNewRelease | null>(null)
  // The host's own state (UpdateFlow), from its answers and app.updateState: the phase below is drawn from it alone.
  const [hostState, setHostState] = useState<UpdateState>('checking')
  const [answered, setAnswered] = useState(false)
  const [asking, setAsking] = useState(false)
  // The update before this start didn't take: its one sentence, said once as a notice of its own.
  const [problem, setProblem] = useState<string | null>(null)
  const [dismissed, setDismissed] = useState(false)
  const [sheet, setSheet] = useState(false)
  const [notNow, setNotNow] = useState(false)
  const installing = useRef(false)
  const checkGeneration = useRef(Symbol())
  const phase = answered || hostState !== 'checking' ? updatePhase(hostState, !!latest, asking) : 'checking'
  const busy = phase === 'installing' || phase === 'restarting'
  const [percent, setPercent] = useState<number | null>(null)
  const [status, setStatus] = useState("Preparing…")
  const [bootHold, setBootHold] = useState(true)
  const take = useCallback((state: unknown) => {
    if (!isUpdateState(state)) return
    setHostState(state)
    setDismissed(false)
  }, [])

  useEffect(() => {
    const generation = Symbol()
    checkGeneration.current = generation
    const holdTimer = window.setTimeout(() => setBootHold(false), BOOT_HOLD_MS)
    void host.checkUpdate().then((r) => {
      if (generation !== checkGeneration.current || installing.current) return
      setAnswered(true)
      take(r.state)
      setProblem(r.problem ?? null)
      if (!r.ok || !r.updateAvailable) return
      setLatest(r.latest || null)
      setUpcoming(r.upcoming ? toRelease(r.upcoming) : null)
      setNotNow(!!r.notNow)
      setSheet(!!r.offerOnOpen)
    }).catch((e: unknown) => {
      report(e, 'useAppUpdate.startupCheck', { level: 'warn' })
      if (generation === checkGeneration.current) { setAnswered(true); setHostState(current => current === 'checking' ? 'idle' : current) }
    }).finally(() => setBootHold(false))
    const offProgress = onHostEvent('app.updateProgress', (data) => {
      if (!installing.current) return
      const d = data as { percent?: number | null; status?: string }
      setPercent(visibleInstallPercent(d?.percent))
      if (typeof d?.status === 'string') setStatus(d.status)
    })
    const offState = onHostEvent('app.updateState', (data) => take(data?.state))
    return () => {
      checkGeneration.current = Symbol()
      window.clearTimeout(holdTimer)
      offProgress()
      offState()
    }
  }, [take])

  const setAside = useCallback(() => setSheet(false), [])
  const dismiss = useCallback(() => setDismissed(true), [])

  const install = useCallback(async () => {
    if (installing.current || busy || (!latest && phase !== 'available' && phase !== 'failed' && phase !== 'waiting')) return
    installing.current = true
    checkGeneration.current = Symbol()
    setAsking(true)
    setPercent(null)
    setStatus("Preparing…")
    try {
      const r = await host.installUpdate()
      if (r.notNow) setNotNow(true)
      take(r.state)
      if (r.ok && r.staged) {
        // A game is running: the update is downloaded and kept, and the sheet goes; the notice says when it goes in.
        setSheet(false)
        setMessage(r.message || 'Your update is ready and goes in after your game, the next time Exo opens.')
      } else if (r.ok && (r.shouldExit || r.installed)) {
        setPercent(null)
        setStatus('Restarting…')
        setMessage(r.message || 'Restarting…')
      } else if (r.alreadyLatest) {
        setMessage(null)
        setLatest(null)
        setSheet(false)
      } else {
        setMessage(r.message || 'The update didn’t install. Try again.')
      }
    } catch (e) {
      setNotNow(true)
      setMessage(errorText(e, 'The update didn’t install. Try again.'))
      setHostState('failed')
    }
    setAsking(false)
    installing.current = false
  }, [busy, latest, phase, take])

  const check = useCallback(async (): Promise<UpdateCheck> => {
    if (installing.current || busy) return { ok: false, updateAvailable: false, message: 'An update is already being installed.' }
    const generation = Symbol()
    checkGeneration.current = generation
    try {
      const r = await host.checkUpdate(true)
      if (generation !== checkGeneration.current) return { ok: false, updateAvailable: false, message: 'Another update check started.' }
      take(r.state)
      setSheet(sheet => sheetAfterCheck(sheet, r))
      if (!r.ok || (!r.updateAvailable && !r.alreadyLatest))
        return { ok: false, updateAvailable: false, message: r.message || 'Couldn’t check for updates. Try again.' }
      if (r.updateAvailable) {
        setMessage(r.message || `Version ${r.latest} is available.`)
        setLatest(r.latest ?? null)
        setUpcoming(r.upcoming ? toRelease(r.upcoming) : null)
        return { ok: true, updateAvailable: true, latest: r.latest, message: r.message || 'Update available.' }
      }
      // Up to date: nothing is offered (the sheet went with sheetAfterCheck above).
      setMessage(null)
      setLatest(null)
      return { ok: true, updateAvailable: false, message: r.message || "You're up to date." }
    } catch (e) {
      return { ok: false, updateAvailable: false, message: errorText(e, 'Couldn’t check for updates. Try again.') }
    }
  }, [busy, take])

  return {
    busy,
    percent,
    status,
    available: phase === 'available' || phase === 'failed' || phase === 'waiting',
    error: phase === 'failed' && !dismissed ? message ?? 'The update didn’t install. Try again.' : null,
    problem,
    dismissProblem: () => setProblem(null),
    waiting: phase === 'waiting' && !dismissed ? message ?? 'Your update is ready and goes in after your game, the next time Exo opens.' : null,
    checking: bootHold && phase === 'checking',
    pending: phase === 'checking',
    latest,
    upcoming,
    sheet,
    notNow,
    setAside,
    dismiss,
    install,
    check,
  }
}

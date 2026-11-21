import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { createPortal } from 'react-dom'
import { Alert, Check } from '../../brand/icons'
import { focusWhenActive } from '../../lib/focusTrap'
import { host, onHostEvent, type DriverProgress } from '../../lib/host'
import { report } from '../../lib/report'
import { CopyLogs } from '../ui/ErrorNote'
import { Progress, ProgressWords } from '../ui/Progress'
import { useHoldWindow } from './HoldWindow'
import { pageGet, pageSet, pageRemove } from '../../lib/pageStore'
import '../../styles/driver-install.css'
import { errorText } from '../../lib/errorWords'

type Phase = 'starting' | 'downloading' | 'unpacking' | 'waiting' | 'installing' | 'reloading' | 'restoring' | 'done' | 'failed'
type InstallKind = 'gpu' | 'chipset'
type Install = {
  kind: InstallKind
  phase: Phase
  /** The vendor ("NVIDIA") and the version going in ("621.12"), for the title. */
  name: string | null
  version: string | null
  /** The download's share, 0 to 1, while it downloads. */
  share: number | null
  /** The download's size in bytes, when the driver row knew it. */
  size: number | null
  message: string | null
  restored: string[]
  notRestored: string[]
}

/** The order an install goes through: a progress event never takes it back a step. */
const ORDER: Phase[] = ['starting', 'downloading', 'unpacking', 'waiting', 'installing', 'reloading', 'restoring', 'done', 'failed']
const WORDS_GPU: Record<Exclude<Phase, 'done' | 'failed'>, string> = {
  starting: 'Starting the update', downloading: 'Downloading the driver', unpacking: 'Unpacking the driver', waiting: 'Waiting to install',
  installing: 'Installing the driver', reloading: 'Reloading the graphics driver', restoring: 'Putting your settings back',
}
const WORDS_CHIPSET: Record<Exclude<Phase, 'done' | 'failed'>, string> = {
  starting: 'Starting the chipset update', downloading: 'Downloading the chipset package', unpacking: 'Unpacking the chipset installer', waiting: 'Waiting to install',
  installing: 'Installing the CPU & chipset drivers', reloading: 'Applying chipset updates', restoring: 'Verifying system devices',
}
const GPU_MILESTONES: ReadonlyArray<{ phase: Phase; label: string }> = [
  { phase: 'downloading', label: 'Download' },
  { phase: 'unpacking', label: 'Prepare' },
  { phase: 'installing', label: 'Install' },
  { phase: 'restoring', label: 'Restore' },
]
const CHIPSET_MILESTONES: ReadonlyArray<{ phase: Phase; label: string }> = [
  { phase: 'starting', label: 'Prepare' },
  { phase: 'installing', label: 'Install' },
  { phase: 'done', label: 'Verify' },
]
const VENDOR: Record<string, string> = { nvidia: 'NVIDIA', amd: 'AMD', intel: 'Intel' }
/** Kept through a page reload (the host reloads the page once the driver is in, and on a page that stops drawing), so the screen comes back at once. */
const KEPT = 'exo.driverInstall'

let current: Install | null = null
const listeners = new Set<() => void>()
const subscribe = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener) } }
const read = () => current

function put(next: Install | null) {
  current = next
  try {
    if (next && next.phase !== 'done' && next.phase !== 'failed') pageSet(KEPT, JSON.stringify(next))
    else pageRemove(KEPT)
  } catch { /* the screen still shows; only a reload would lose it */ }
  for (const listener of listeners) listener()
}

/** A step from the host: a later phase takes over; done and failed always land. */
function advance(phase: Phase, change: Partial<Install>) {
  const base: Install = current ?? { kind: change.kind ?? 'gpu', phase, name: null, version: null, share: null, size: null, message: null, restored: [], notRestored: [] }
  if (current && phase !== 'failed' && ORDER.indexOf(phase) < ORDER.indexOf(current.phase)) return
  put({ ...base, ...change, phase })
}

/** The driver row's Update, pressed: the screen comes up at once, before the host's first step. */
export function driverInstallStarted(name: string, version: string, size?: number | null, kind: InstallKind = 'gpu') {
  put({ kind, phase: 'starting', name, version, share: null, size: size ?? null, message: null, restored: [], notRestored: [] })
}

export function driverInstallPhase(phase: Phase, change: Partial<Install> = {}) {
  advance(phase, { kind: 'chipset', ...change })
}

export function driverInstallDismiss() {
  put(null)
}

/** The update didn't start (the row says why): the screen goes. */
export function driverInstallStopped() {
  if (current?.phase === 'starting') put(null)
}

const STAGE: Partial<Record<DriverProgress['stage'], Phase>> = {
  downloading: 'downloading', unpacking: 'unpacking', waiting: 'waiting', installing: 'installing',
}

/** The download's words: "Downloading the driver · 420 MB of 1.0 GB" when its size is known (its percent follows them). */
function downloadWords(install: Install) {
  const base = install.kind === 'chipset' ? WORDS_CHIPSET.downloading : WORDS_GPU.downloading
  if (install.share === null || !install.size) return base
  const mb = (bytes: number) => bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`
  return `${base} · ${mb(install.size * install.share)} of ${mb(install.size)}`
}

/** The host's steps, as they come: the download and install from gpu.driver, the rest from gpu.driverInstall. */
function listen() {
  const offProgress = onHostEvent('gpu.driver', progress => {
    if (current?.kind === 'chipset') return
    const phase = STAGE[progress.stage]
    if (phase) advance(phase, { share: progress.stage === 'downloading' ? progress.percent / 100 : null, message: progress.message })
    else if (progress.stage === 'failed' && current) advance('failed', { message: progress.message })
  })
  const offStep = onHostEvent('gpu.driverInstall', step => {
    if (current?.kind === 'chipset') return
    advance(step.phase, {
      version: step.version ?? current?.version ?? null,
      message: step.message ?? null,
      share: null,
      restored: step.restored ?? [],
      notRestored: step.notRestored ?? [],
    })
  })
  return () => { offProgress(); offStep() }
}

/** After a reload mid-install, the screen comes back at once from what was kept, then follows the host's own state. */
function recover() {
  let kept: Install | null
  try { kept = JSON.parse(pageGet(KEPT) ?? 'null') as Install | null } catch { kept = null }
  if (!kept) return
  put({ ...kept, kind: kept.kind ?? 'gpu' })
  if (kept.kind === 'chipset') {
    host.systemHealth().then(health => {
      const item = health.items.find(entry => entry.key === 'chipset')
      if (!item || item.busy) advance('installing', { kind: 'chipset', name: item?.label ?? current?.name ?? null })
      else if (item.applied || item.current) advance('done', { kind: 'chipset', name: item.label })
      else advance('failed', { kind: 'chipset', message: item.detail ?? 'Windows kept the chipset driver you had.' })
    }, (error: unknown) => report(error, 'DriverInstallOverlay.chipsetRecover', { level: 'warn' }))
    return
  }
  host.gpuDriver('state').then(state => {
    const phase = state.progress ? STAGE[state.progress.stage] : undefined
    if (phase) advance(phase, { name: current?.name ?? (state.vendor ? VENDOR[state.vendor] : null) })
    else if (state.progress?.stage === 'failed') advance('failed', { message: state.progress.message })
    else if (current && ORDER.indexOf(current.phase) < ORDER.indexOf('reloading')) put(null)
  }, (error: unknown) => report(error, 'DriverInstallOverlay.state', { level: 'warn' }))
}

const ended = (phase: Phase) => phase === 'done' || phase === 'failed'
const named = (install: Install) => [install.name, install.version].filter(Boolean).join(' ')

function useChipsetPoll(kind: InstallKind, working: boolean) {
  useEffect(() => {
    if (kind !== 'chipset' || !working) return
    const timer = window.setInterval(() => {
      host.systemHealth().then(health => {
        if (current?.kind !== 'chipset' || ended(current.phase)) return
        const item = health.items.find(entry => entry.key === 'chipset')
        if (!item) return
        if (item.busy) advance('installing', { kind: 'chipset', name: item.label })
        else if (item.applied || item.current) advance('done', { kind: 'chipset', name: item.label })
        else if (current.phase === 'installing') advance('failed', { kind: 'chipset', message: item.detail ?? 'Windows kept the chipset driver you had.' })
      }, (error: unknown) => report(error, 'DriverInstallOverlay.chipsetPoll', { level: 'warn' }))
    }, 3000)
    return () => window.clearInterval(timer)
  }, [kind, working])
}

function InstallStage({ phase, kind }: { phase: Phase; kind: InstallKind }) {
  const working = !ended(phase)
  const stageState = phase === 'done' ? ' is-done' : phase === 'failed' ? ' is-failed' : ''
  return (
    <div className={`exo-driver-stage${stageState}`}>
      {working ? <span className="exo-driver-halo" aria-hidden="true" /> : null}
      {working ? <span className="exo-driver-orbit" aria-hidden="true" /> : null}
      {working ? <span className="exo-driver-orbit is-outer" aria-hidden="true" /> : null}
      {phase === 'done'
        ? <span className="exo-driver-install-done" aria-hidden="true"><Check size={36} /></span>
        : kind === 'chipset' ? <Processor /> : <Screen />}
    </div>
  )
}

function InstallFoot({ install, onHide }: { install: Install; onHide: () => void }) {
  return (
    <div className="exo-driver-install-foot">
      {install.phase === 'waiting' ? <button type="button" className="exo-set-btn" onClick={onHide}>Hide until it installs</button> : null}
      {install.phase === 'failed' ? <CopyLogs error={install.message ?? 'Driver install failed'} /> : null}
      {install.phase === 'done' ? <RestartChoice /> : null}
      {install.phase === 'failed' ? (
        <button ref={focusWhenActive} type="button" className="exo-set-btn is-primary" onClick={() => put(null)}>Close</button>
      ) : null}
    </div>
  )
}

/**
 * The driver screen (DESIGN.md › PC): while a graphics driver goes in, from the Update press through done or failed, one
 * layer over the whole window, so nothing under it can be pressed or typed into. It is full screen on purpose: no
 * window buttons (owner, 2026-11-14), so the window can't be closed or hidden mid-install. The driver and its version, the step in words, the Exo bar
 * (measured while it downloads, gliding after), and a screen that dims and lights, slowly: the screen going black and
 * coming back mid-install is expected. Done says the driver is in and that Exo's settings are back in one line (only
 * what it couldn't put back is listed), and offers Restart now or Not now; failed says what happened with Copy logs. It comes back after a page reload: the host reloads the page once the driver is in, before restoring.
 */
export function DriverInstallOverlay() {
  const install = useSyncExternalStore(subscribe, read)
  const screen = useRef<HTMLDivElement>(null)
  // Waiting (on a game or a call) can be set aside, so the call can be left or the game closed; it returns to install.
  const [aside, setAside] = useState(false)
  useEffect(() => { recover(); return listen() }, [])

  const phase = install?.phase ?? null
  const kind = install?.kind ?? 'gpu'
  const working = phase !== null && !ended(phase)
  useChipsetPoll(kind, working)
  const shown = install !== null && !(aside && phase === 'waiting')
  if (aside && phase !== 'waiting') setAside(false)
  // Everything under it is inert and keys go nowhere else (Ctrl+K, a room's number); Escape closes it once it's over.
  useHoldWindow(screen, shown, working ? null : () => put(null))

  if (!install || !shown) return null
  return createPortal(
    <div ref={screen} className={`exo-portal exo-driver-install${working ? ' is-working' : ''}`} role="alertdialog" aria-modal="true" aria-labelledby="exo-driver-install-title" aria-busy={working}>
      <div className="exo-driver-install-body">
        <InstallStage phase={install.phase} kind={kind} />
        {ended(install.phase) ? <Ended install={install} /> : <Working install={install} />}
        <InstallFoot install={install} onHide={() => setAside(true)} />
      </div>
    </div>,
    document.body,
  )
}

/** While it works: the driver and its version, the step in words (what holds it, while it waits), and the Exo bar. */
function Working({ install }: { install: Install }) {
  const words = install.kind === 'chipset' ? WORDS_CHIPSET : WORDS_GPU
  const step = install.phase === 'downloading' ? downloadWords(install) : words[install.phase as keyof typeof WORDS_GPU]
  const share = install.phase === 'downloading' ? install.share : null
  const milestones = install.kind === 'chipset' ? CHIPSET_MILESTONES : GPU_MILESTONES
  const at = ORDER.indexOf(install.phase)
  return (
    <>
      <h2 id="exo-driver-install-title">{named(install) || (install.kind === 'chipset' ? 'CPU & chipset drivers' : 'Graphics driver')}</h2>
      <p className="exo-driver-install-step" role="status">{install.phase === 'waiting' && install.message ? install.message : <ProgressWords words={step} value={share} />}</p>
      <Progress size="large" value={share} label={step} />
      <ul className="exo-driver-steps" aria-hidden="true">
        {milestones.map(m => {
          const stepAt = ORDER.indexOf(m.phase)
          const done = at > stepAt
          const active = at === stepAt || (install.kind === 'gpu' && install.phase === 'reloading' && m.phase === 'installing')
          return (
            <li key={m.label} className={done ? 'is-done' : active ? 'is-active' : undefined}>
              <span className="exo-driver-step-dot" />
              <span>{m.label}</span>
            </li>
          )
        })}
      </ul>
    </>
  )
}

/** Once it's over: Driver updated with what Exo put back, or what went wrong and what to do. */
function Ended({ install }: { install: Install }) {
  const failed = install.phase === 'failed'
  const fallbackName = install.kind === 'chipset' ? 'The chipset drivers didn’t install' : 'The driver didn’t install'
  const doneTitle = install.kind === 'chipset' ? 'Chipset drivers updated' : 'Driver updated'
  const retryHint = install.kind === 'chipset' ? 'Windows kept the driver you had. Try the update again from PC › Updates.' : 'Windows kept the driver you had. Try the update again from PC › Graphics.'
  return (
    <>
      <h2 id="exo-driver-install-title">{failed ? fallbackName : doneTitle}</h2>
      {named(install) ? <p className="exo-driver-install-step">{named(install)}</p> : null}
      {failed
        ? <p className="exo-driver-install-error" role="alert">{install.message || retryHint}</p>
        : <Outcome install={install} />}
    </>
  )
}

/**
 * After the install: one line for what Exo put back (never a list of every setting), then only what it couldn't, each
 * saying what to do, and the restart that finishes the driver.
 */
function Outcome({ install }: { install: Install }) {
  const back = install.restored.length
  const missed = install.notRestored
  const settings = back === 0 ? null
    : missed.length ? 'Exo put the rest of your settings back.' : 'Exo put your settings back on the new driver.'
  return (
    <>
      <p className="exo-driver-install-step">{[settings, 'Restart your PC to finish.'].filter(Boolean).join(' ')}</p>
      {missed.length ? (
        <ul className="exo-driver-install-list">
          {missed.map(item => <li key={item} className="is-not"><Alert size={16} />{item}</li>)}
        </ul>
      ) : null}
    </>
  )
}

/** Restart now (Windows restarts in 5 seconds) or Not now, which closes the screen. */
function RestartChoice() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const restart = () => {
    setBusy(true)
    setError(null)
    host.biosUpdate('windows').then(result => {
      if (!result.ok) { setBusy(false); setError(errorText(result.message, 'Windows didn’t restart. Restart from the Start menu.')) }
    }, (failure: unknown) => {
      report(failure, 'DriverInstallOverlay.restart', { level: 'warn' })
      setBusy(false)
      setError('Windows didn’t restart. Restart from the Start menu.')
    })
  }
  return (
    <>
      {error ? <p className="exo-driver-install-error" role="alert">{error}</p> : null}
      <button type="button" className="exo-set-btn" disabled={busy} onClick={() => put(null)}>Not now</button>
      <button ref={focusWhenActive} type="button" className="exo-set-btn is-primary" disabled={busy} onClick={restart}>{busy ? 'Restarting…' : 'Restart now'}</button>
    </>
  )
}

/** A screen that dims and lights again, slowly, as the real one may during the install; still under Reduce motion. */
function Screen() {
  return (
    <svg className="exo-driver-screen" viewBox="0 0 64 50" width="84" height="66" aria-hidden="true">
      <rect className="exo-driver-screen-glass" x="5" y="4" width="54" height="34" rx="4" />
      <rect className="exo-driver-screen-frame" x="5" y="4" width="54" height="34" rx="4" />
      <path className="exo-driver-screen-frame" d="M24 46h16M32 38v8" />
    </svg>
  )
}

function Processor() {
  return (
    <svg className="exo-driver-cpu" viewBox="0 0 64 64" width="76" height="76" aria-hidden="true">
      <path className="exo-driver-cpu-pins" d="M22 6v6M32 6v6M42 6v6M22 52v6M32 52v6M42 52v6M6 22h6M6 32h6M6 42h6M52 22h6M52 32h6M52 42h6" />
      <rect className="exo-driver-screen-frame" x="14" y="14" width="36" height="36" rx="5" />
      <rect className="exo-driver-cpu-die" x="23" y="23" width="18" height="18" rx="3" />
    </svg>
  )
}

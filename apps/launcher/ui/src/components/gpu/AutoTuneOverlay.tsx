import { useCallback, useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Alert, Bolt, Check, Close } from '../../brand/icons'
import { focusWhenActive } from '../../lib/focusTrap'
import type { AutoTune, ExoAiState, HdrState, LauncherSettings } from '../../lib/host'
import { cn } from '../../lib/utils'
import { Spinner } from '../ui/Spinner'
import { AutoTuneRows } from './AutoTune'
import { bestSteps } from './BestSetup'
import { initialAutoTuneStages, runAutoTuneStage, type AutoTuneStageState } from './autoTuneStages'
import type { PcState } from './PcRoom'
import '../../styles/auto-tune-overlay.css'

function countPendingAutoTune(
  state: PcState,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean> = async () => false,
): number | null {
  if (!state.profile || !state.health) return null
  const steps = bestSteps(state.profile, state.health, hdr, settings, onSave, state.drives, state.tuning).filter(s => !s.done).length
  const screens = state.display?.displays.filter(screen => !screen.isBest).length ?? 0
  return steps + screens
}

export function AutoTuneChip({ state, hdr, settings, onOpen }: {
  state: PcState
  hdr: HdrState | null
  settings: LauncherSettings | null
  onOpen: () => void
}) {
  const testing = state.tune?.parts.some(part => part.state === 'testing') ?? false
  const tuned = state.tune?.parts.some(part => part.state === 'tuned') ?? false
  const pending = countPendingAutoTune(state, hdr, settings)

  let label = 'Auto Tune'
  let tone: 'is-good' | 'is-bad' | null = null
  if (testing) {
    label = 'Auto Tune · Tuning'
    tone = 'is-good'
  } else if (pending != null && pending > 0) {
    label = `Auto Tune · ${pending} ready`
    tone = 'is-bad'
  } else if (tuned) {
    label = 'Auto Tune · Tuned'
    tone = 'is-good'
  }

  return (
    <button type="button" className={cn('exo-pc-chip', tone)} data-pc-menu="auto-tune" aria-haspopup="dialog" onClick={onOpen}>
      <Bolt size={14} />
      {label}
    </button>
  )
}

function StageBadge({ status, label }: { status: AutoTuneStageState['status']; label: string }) {
  if (status === 'active') return <span className="exo-at-badge is-active"><Spinner label={label} /></span>
  if (status === 'done') return <span className="exo-at-badge is-done"><Check size={16} /></span>
  if (status === 'warn') return <span className="exo-at-badge is-warn"><Alert size={16} /></span>
  return <span className="exo-at-badge"><span className="exo-at-dot" /></span>
}

function hardwareLine(state: PcState): string {
  const gpu = state.profile?.gpu || state.about?.graphics || state.driver?.card
  const cpu = state.about?.processor
  return [gpu, cpu].filter(Boolean).join(' · ') || 'Cooling, graphics, Windows, privacy, VRR LFC & silicon curve tuning'
}

function statusText(
  siliconTesting: boolean,
  running: boolean,
  finished: boolean,
  hasWarn: boolean,
  activeStage: AutoTuneStageState | undefined,
): string {
  if (siliconTesting) return 'Silicon voltage & stability test in progress'
  if (running) return activeStage ? `${activeStage.label}…` : 'Optimizing PC…'
  if (finished) return hasWarn ? 'Optimization finished with notes below' : 'All safe PC optimizations applied'
  return 'One-click optimization across cooling, display, Windows, privacy & caches'
}

export function AutoTuneOverlay({
  open,
  onClose,
  state,
  exoAi,
  hdr,
  settings,
  onSave,
  onTune,
  onRefresh,
}: {
  open: boolean
  onClose: () => void
  state: PcState
  exoAi: ExoAiState | null
  hdr: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  onTune: (next: AutoTune) => void
  onRefresh: () => Promise<void>
}) {
  const closeRef = useRef<HTMLButtonElement>(null)
  const abortedRef = useRef(false)
  const [running, setRunning] = useState(false)
  const [finished, setFinished] = useState(false)
  const [stages, setStages] = useState<AutoTuneStageState[]>(() => initialAutoTuneStages(state, exoAi, hdr, settings))

  useEffect(() => {
    if (!open || running || finished) return
    setStages(initialAutoTuneStages(state, exoAi, hdr, settings))
  }, [open, running, finished, state, exoAi, hdr, settings])

  useEffect(() => {
    if (!open) return
    abortedRef.current = false
    window.requestAnimationFrame(() => focusWhenActive(closeRef.current))
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !running) onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => {
      abortedRef.current = true
      window.removeEventListener('keydown', onKey)
    }
  }, [open, running, onClose])

  const startPipeline = useCallback(async () => {
    if (running) return
    abortedRef.current = false
    setRunning(true)
    setFinished(false)
    const initial = initialAutoTuneStages(state, exoAi, hdr, settings)
    setStages(initial)

    for (const item of initial) {
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: 'active' } : s))
      const result = await runAutoTuneStage(item.id, state, hdr, settings, onSave, onRefresh)
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: result.status, detail: result.detail } : s))
    }

    if (!abortedRef.current) {
      setRunning(false)
      setFinished(true)
    }
  }, [running, state, exoAi, hdr, settings, onSave, onRefresh])

  if (!open) return null

  const siliconTesting = Boolean(state.tune?.parts.some(part => part.state === 'testing'))
  const working = running || siliconTesting
  const completedCount = stages.filter(s => s.status === 'done' || s.status === 'warn').length
  const ratio = running ? (completedCount + 0.5) / stages.length : finished ? 1 : 0
  const statusLine = statusText(
    siliconTesting,
    running,
    finished,
    stages.some(s => s.status === 'warn'),
    stages.find(s => s.status === 'active'),
  )
  const noteLine = state.tuning?.restart
    ? 'Restart Windows to finish pending NVIDIA VRR LFC or system changes.'
    : 'Reversible via Exo restore records · Zero kernel anti-cheat tampering'
  const ctaLabel = running ? 'Running…' : finished ? 'Run again' : 'Run Auto Tune'
  const hasSilicon = (state.tune?.parts.length ?? 0) > 0

  return createPortal(
    <div className={cn('exo-auto-tune', working && 'is-working')} role="dialog" aria-modal="true" aria-labelledby="exo-auto-tune-title" data-log-area="PC">
      <div className="exo-at-shell">
        <header className="exo-at-topbar">
          <div className="exo-at-heading">
            <h2 id="exo-auto-tune-title">Auto Tune</h2>
            <p className="exo-at-sub">{hardwareLine(state)}</p>
          </div>
          <button
            ref={closeRef}
            type="button"
            className="exo-at-close"
            aria-label="Close Auto Tune"
            onClick={() => {
              abortedRef.current = true
              setRunning(false)
              onClose()
            }}
          >
            <Close size={16} />
          </button>
        </header>

        <section className="exo-at-hero" aria-label="Optimization status">
          <div className="exo-at-stage" aria-hidden="true">
            <span className="exo-at-halo" />
            <span className="exo-at-orbit" />
            <span className="exo-at-orbit is-outer" />
            <div className={cn('exo-at-core', finished && !working && 'is-done')}>
              {finished && !working ? <Check size={28} /> : <Bolt size={28} />}
            </div>
          </div>
          <div className="exo-at-summary">
            <p className="exo-at-status">{statusLine}</p>
            <p className="exo-at-note">{noteLine}</p>
            {running || finished ? (
              <div className="exo-at-progress" aria-hidden="true">
                <div className="exo-at-progress-bar" style={{ transform: `scaleX(${ratio})` }} />
              </div>
            ) : null}
          </div>
          <div className="exo-at-hero-actions">
            <button type="button" className="exo-set-btn is-primary" disabled={running} onClick={() => void startPipeline()}>
              {ctaLabel}
            </button>
          </div>
        </section>

        <section className="exo-at-stages" aria-label="Optimization pipeline">
          <div className="exo-at-section-head">
            <h3>PC Optimization Pipeline</h3>
            <span>{completedCount} of {stages.length} complete</span>
          </div>
          <ul className="exo-at-list">
            {stages.map(stage => (
              <li key={stage.id} className="exo-at-stage-row">
                <StageBadge status={stage.status} label={stage.label} />
                <div className="exo-at-stage-copy">
                  <span className="exo-at-stage-title">{stage.label}</span>
                  <span className="exo-at-stage-detail">{stage.detail}</span>
                </div>
              </li>
            ))}
          </ul>
        </section>

        {hasSilicon ? (
          <section className="exo-at-silicon" aria-label="Silicon Auto Tune">
            <div className="exo-at-section-head">
              <h3>Silicon Auto Tune</h3>
              <span>GPU overclock &amp; undervolt · AMD Ryzen Curve Optimizer</span>
            </div>
            <AutoTuneRows tune={state.tune} profile={state.profile} about={state.about} onTune={onTune} />
          </section>
        ) : null}
      </div>
    </div>,
    document.body,
  )
}

import { useCallback, useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Alert, Bolt, Check, ChevronLeft } from '../../brand/icons'
import { focusWhenActive } from '../../lib/focusTrap'
import {
  host,
  type AutoTune,
  type AutopilotState,
  type DisplayState,
  type ExoAiState,
  type HdrState,
  type Interrupts,
  type LauncherSettings,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Progress } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'
import { AutoTuneRows } from './AutoTune'
import {
  initialAutoTuneStages,
  runAutoTuneStage,
  type AutoTuneStageState,
  type BloatAppInfo,
} from './autoTuneStages'
import { bestSteps } from './BestSetup'
import { ExoAiFooter } from './ExoAiPanel'
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

export function AutoTuneChip({ state, exoAi, hdr, settings, onOpen }: {
  state: PcState
  exoAi: ExoAiState | null
  hdr: HdrState | null
  settings: LauncherSettings | null
  onOpen: () => void
}) {
  const gaming = exoAi?.gaming.active ?? false
  const testing = state.tune?.parts.some(part => part.state === 'testing') ?? false
  const tuned = state.tune?.parts.some(part => part.state === 'tuned') ?? false
  const pending = countPendingAutoTune(state, hdr, settings)

  let label = 'Auto Tune'
  let tone: 'is-good' | 'is-bad' | null = null
  if (gaming) {
    label = exoAi?.gaming.gameTitle ? `Auto Tune · ${exoAi.gaming.gameTitle}` : 'Auto Tune · Boosting'
    tone = 'is-good'
  } else if (testing) {
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

function StageMarker({ status, label }: { status: AutoTuneStageState['status']; label: string }) {
  if (status === 'active') return <span className="exo-at-stage-marker"><Spinner label={label} /></span>
  if (status === 'done') return <span className="exo-at-stage-marker"><Check size={15} /></span>
  if (status === 'warn') return <span className="exo-at-stage-marker"><Alert size={15} /></span>
  return <span className="exo-at-stage-marker"><span className="exo-at-stage-dot" /></span>
}

function hardwareLine(state: PcState): string {
  const gpu = state.profile?.gpu || state.about?.graphics || state.driver?.card
  const cpu = state.about?.processor
  return [gpu, cpu].filter(Boolean).join(' · ') || 'Maximum performance PC & hardware tuning'
}

function computeTuneProgress(stages: readonly AutoTuneStageState[], running: boolean, finished: boolean) {
  const completedCount = stages.filter(s => s.status === 'done' || s.status === 'warn').length
  const hasWarn = stages.some(s => s.status === 'warn')
  const activeStage = stages.find(s => s.status === 'active')
  const activeIndex = stages.findIndex(s => s.status === 'active')
  const phase = running ? 'running' : finished ? 'done' : 'idle'
  const ratio = stages.length > 0 ? completedCount / stages.length : 0
  const percent = finished ? 100 : Math.round(ratio * 100)
  const stageCounter = running && activeIndex >= 0 ? `Stage ${activeIndex + 1} of ${stages.length}` : `${completedCount} of ${stages.length} stages`
  const progressLabel = running
    ? (activeStage ? activeStage.label : 'Optimizing PC')
    : hasWarn
      ? 'Finished with notes'
      : 'Optimization complete'
  const ctaLabel = running ? 'Optimizing PC' : finished ? 'Run Again' : 'Run Auto Tune'
  const horizonScale = running ? Math.max(0.08, ratio) : finished ? 1 : 0
  return { completedCount, hasWarn, phase, percent, stageCounter, progressLabel, ctaLabel, horizonScale }
}

export function AutoTuneOverlay({
  active,
  open,
  initialOverrides = false,
  onClose,
  state,
  exoAi,
  autopilot,
  onAutopilot,
  hdr,
  settings,
  onSave,
  onTune,
  onDisplay,
  onHealth,
  onInterrupts,
  onTuning,
  onRefresh,
}: {
  active: boolean
  open: boolean
  initialOverrides?: boolean
  onClose: () => void
  state: PcState
  exoAi: ExoAiState | null
  autopilot: AutopilotState | null
  onAutopilot: (next: AutopilotState) => void
  hdr: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  onTune: (next: AutoTune) => void
  onDisplay: (next: DisplayState) => void
  onHealth: (next: PcHealth) => void
  onInterrupts: (next: Interrupts) => void
  onTuning: (next: Tuning) => void
  onRefresh: () => Promise<void>
}) {
  const backRef = useRef<HTMLButtonElement>(null)
  const abortedRef = useRef(false)
  const [running, setRunning] = useState(false)
  const [finished, setFinished] = useState(false)
  const [overridesOpen, setOverridesOpen] = useState(initialOverrides)
  const [bloatApps, setBloatApps] = useState<BloatAppInfo[]>([])
  const [stages, setStages] = useState<AutoTuneStageState[]>(() => initialAutoTuneStages(state, exoAi, hdr, settings))

  useEffect(() => {
    if (!open || !active) return
    setOverridesOpen(initialOverrides)
    let alive = true
    host.appsPackages().then(
      res => {
        if (!alive || !res.ok) return
        setBloatApps(res.items.filter(item => item.forMe && item.bloat).map(item => ({ id: item.id, name: item.name })))
      },
      (e: unknown) => report(e, 'AutoTuneOverlay.packages', { level: 'warn' }),
    )
    return () => { alive = false }
  }, [open, active, initialOverrides])

  useEffect(() => {
    if (!open || !active || running || finished) return
    setStages(initialAutoTuneStages(state, exoAi, hdr, settings, bloatApps))
  }, [open, active, running, finished, state, exoAi, hdr, settings, bloatApps])

  const handleClose = useCallback(() => {
    abortedRef.current = true
    setRunning(false)
    setOverridesOpen(false)
    onClose()
  }, [onClose])

  useEffect(() => {
    if (!open || !active) return
    abortedRef.current = false
    window.requestAnimationFrame(() => focusWhenActive(backRef.current))
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented && !overridesOpen && !running) handleClose()
    }
    window.addEventListener('keydown', onKey)
    return () => {
      abortedRef.current = true
      window.removeEventListener('keydown', onKey)
    }
  }, [open, active, overridesOpen, running, handleClose])

  const startPipeline = useCallback(async () => {
    if (running) return
    abortedRef.current = false
    setRunning(true)
    setFinished(false)
    const initial = initialAutoTuneStages(state, exoAi, hdr, settings, bloatApps)
    setStages(initial)

    for (const item of initial) {
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: 'active' } : s))
      const result = await runAutoTuneStage(item.id, state, hdr, settings, onSave, onRefresh, bloatApps)
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: result.status, detail: result.detail } : s))
    }

    if (!abortedRef.current) {
      setRunning(false)
      setFinished(true)
    }
  }, [running, state, exoAi, hdr, settings, bloatApps, onSave, onRefresh])

  if (!open || !active) return null

  const { completedCount, hasWarn, phase, percent, stageCounter, progressLabel, ctaLabel, horizonScale } = computeTuneProgress(stages, running, finished)

  return createPortal(
    <div
      className="exo-auto-tune"
      role="dialog"
      aria-modal="true"
      aria-labelledby="exo-auto-tune-title"
      data-log-area="PC"
      data-tune-phase={phase}
    >
      <div className="exo-at-stage-fx" aria-hidden="true">
        <div
          className="exo-at-horizon"
          style={{ transform: `scaleX(${horizonScale})` }}
        />
        <div className="exo-at-sweep" />
        <div className="exo-at-crest" />
      </div>
      <div className="exo-at-shell">
        <header className="exo-at-top">
          <div className="exo-at-top-left">
            <button ref={backRef} type="button" className="exo-set-btn exo-at-back" onClick={handleClose}>
              <ChevronLeft size={16} />
              PC
            </button>
            <div className="exo-at-heading">
              <h1 id="exo-auto-tune-title" className="exo-at-title">Auto Tune</h1>
              <p className="exo-at-sub">{hardwareLine(state)}</p>
            </div>
          </div>
          <button type="button" className="exo-at-run" disabled={running} onClick={() => void startPipeline()}>
            {running ? <Spinner label="Optimizing PC" /> : null}
            {ctaLabel}
          </button>
        </header>

        <div className="exo-at-grid">
          <section className="exo-system-panel exo-at-panel" aria-labelledby="exo-at-pc-title">
            <h2 id="exo-at-pc-title">PC optimization</h2>
            {running || finished ? (
              <div className="exo-at-cinema">
                <div className="exo-at-cinema-head">
                  <div className="exo-at-cinema-copy">
                    <span className="exo-at-cinema-step">{stageCounter}</span>
                    <span className="exo-at-cinema-title">{progressLabel}</span>
                  </div>
                  <span className="exo-at-cinema-pct">{percent}%</span>
                </div>
                <Progress
                  size="large"
                  value={running ? completedCount / stages.length : 1}
                  label={progressLabel}
                  tone={finished && !hasWarn ? 'done' : undefined}
                />
              </div>
            ) : null}
            <ul className="exo-at-stages">
              {stages.map(stage => (
                <li
                  key={stage.id}
                  data-status={stage.status}
                  className={cn(
                    'exo-at-stage',
                    stage.status === 'active' && 'is-active',
                    stage.status === 'done' && 'is-done',
                    stage.status === 'warn' && 'is-warn',
                  )}
                >
                  <StageMarker status={stage.status} label={stage.label} />
                  <div className="exo-at-stage-copy">
                    <span className="exo-at-stage-title">{stage.label}</span>
                    <span className="exo-at-stage-detail">{stage.detail}</span>
                  </div>
                </li>
              ))}
            </ul>
            <ExoAiFooter
              active={active}
              exoAi={exoAi}
              autopilot={autopilot}
              onAutopilot={onAutopilot}
              state={state}
              hdr={hdr}
              settings={settings}
              onSave={onSave}
              onBestRead={onRefresh}
              onDisplay={onDisplay}
              onHealth={onHealth}
              onInterrupts={onInterrupts}
              onTuning={onTuning}
              overridesOpen={overridesOpen}
              onToggleOverrides={() => setOverridesOpen(v => !v)}
              onCloseOverrides={() => setOverridesOpen(false)}
            />
          </section>

          <section className="exo-system-panel exo-at-panel" aria-labelledby="exo-at-hw-title">
            <h2 id="exo-at-hw-title">Overclock &amp; undervolt</h2>
            <AutoTuneRows tune={state.tune} profile={state.profile} about={state.about} onTune={onTune} />
          </section>
        </div>
      </div>
    </div>,
    document.body,
  )
}

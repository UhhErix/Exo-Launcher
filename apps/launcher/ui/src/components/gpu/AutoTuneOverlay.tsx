import { useCallback, useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Alert, Bolt, Check, Close, Performance } from '../../brand/icons'
import { focusWhenActive } from '../../lib/focusTrap'
import type {
  AutoTune,
  AutopilotState,
  DisplayState,
  ExoAiState,
  HdrState,
  Interrupts,
  LauncherSettings,
  PcHealth,
  Tuning,
} from '../../lib/host'
import { cn } from '../../lib/utils'
import { Spinner } from '../ui/Spinner'
import { AutoTuneRows } from './AutoTune'
import { bestSteps } from './BestSetup'
import { initialAutoTuneStages, runAutoTuneStage, type AutoTuneStageState } from './autoTuneStages'
import { ExoAiPanel } from './ExoAiPanel'
import type { PcState } from './PcRoom'
import '../../styles/auto-tune-overlay.css'

export type AutoTuneTab = 'studio' | 'exo-ai'

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

function StageBadge({ status, label }: { status: AutoTuneStageState['status']; label: string }) {
  if (status === 'active') return <span className="exo-at-badge is-active"><Spinner label={label} /></span>
  if (status === 'done') return <span className="exo-at-badge is-done"><Check size={15} /></span>
  if (status === 'warn') return <span className="exo-at-badge is-warn"><Alert size={15} /></span>
  return <span className="exo-at-badge"><span className="exo-at-dot" /></span>
}

function hardwareLine(state: PcState): string {
  const gpu = state.profile?.gpu || state.about?.graphics || state.driver?.card
  const cpu = state.about?.processor
  return [gpu, cpu].filter(Boolean).join(' · ') || 'Maximum performance cooling, graphics, Windows, privacy & silicon tuning'
}

function statusText(
  siliconTesting: boolean,
  running: boolean,
  finished: boolean,
  hasWarn: boolean,
  activeStage: AutoTuneStageState | undefined,
): string {
  if (siliconTesting) return 'Silicon stability & max boost test running'
  if (running) return activeStage ? `${activeStage.label}…` : 'Optimizing PC…'
  if (finished) return hasWarn ? 'Finished with notes' : 'Peak PC setup active'
  return 'Ready for full-stack optimization'
}

function exoAiBadgeCopy(exoAi: ExoAiState | null): string {
  if (exoAi?.gaming.active) {
    return exoAi.gaming.gameTitle ? `Exo AI · Boosting ${exoAi.gaming.gameTitle}` : 'Exo AI · Boosting game'
  }
  return 'Exo AI · Active'
}

function fanMetricValue(exoAi: ExoAiState | null): string {
  const fans = exoAi?.fans
  if (!fans) return 'Auto Curve'
  if (fans.tempC != null && fans.fanPercent != null) return `${fans.tempC} °C · ${fans.fanPercent}%`
  if (fans.tempC != null) return `${fans.tempC} °C`
  return fans.supported ? 'Automatic' : 'VBIOS'
}

function StudioTopbar({
  titleHardware,
  exoAi,
  tab,
  onTab,
  closeRef,
  onClose,
}: {
  titleHardware: string
  exoAi: ExoAiState | null
  tab: AutoTuneTab
  onTab: (next: AutoTuneTab) => void
  closeRef: React.RefObject<HTMLButtonElement | null>
  onClose: () => void
}) {
  const gaming = exoAi?.gaming.active ?? false
  return (
    <header className="exo-at-topbar">
      <div className="exo-at-heading">
        <div className="exo-at-title-row">
          <h2 id="exo-auto-tune-title">Auto Tune</h2>
          <button
            type="button"
            className={cn('exo-at-live-badge', gaming && 'is-gaming')}
            onClick={() => onTab('exo-ai')}
          >
            <span className="exo-at-live-dot" aria-hidden="true" />
            <span>{exoAiBadgeCopy(exoAi)}</span>
          </button>
        </div>
        <p className="exo-at-sub">{titleHardware}</p>
      </div>
      <div className="exo-at-header-right">
        <div className="exo-at-tabs" role="tablist" aria-label="Auto Tune sections">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'studio'}
            className={cn('exo-at-tab', tab === 'studio' && 'is-active')}
            onClick={() => onTab('studio')}
          >
            <Bolt size={14} />
            Studio
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'exo-ai'}
            className={cn('exo-at-tab', tab === 'exo-ai' && 'is-active')}
            onClick={() => onTab('exo-ai')}
          >
            <Performance size={14} />
            Exo AI
          </button>
        </div>
        <button
          ref={closeRef}
          type="button"
          className="exo-at-close"
          aria-label="Close Auto Tune"
          onClick={onClose}
        >
          <Close size={16} />
        </button>
      </div>
    </header>
  )
}

function ReactorPane({
  working,
  done,
  ratio,
  statusLine,
  noteLine,
  ctaLabel,
  running,
  completedCount,
  totalCount,
  exoAi,
  onStart,
  onOpenExoAi,
}: {
  working: boolean
  done: boolean
  ratio: number
  statusLine: string
  noteLine: string
  ctaLabel: string
  running: boolean
  completedCount: number
  totalCount: number
  exoAi: ExoAiState | null
  onStart: () => void
  onOpenExoAi: () => void
}) {
  return (
    <section className={cn('exo-at-reactor-card', done && !working && 'is-done')} aria-label="Optimization status">
      <div className="exo-at-stage" aria-hidden="true">
        <span className="exo-at-aura" />
        <span className="exo-at-scan" />
        <span className="exo-at-ring-dash" />
        <span className="exo-at-orbit is-outer" />
        <span className="exo-at-orbit" />
        <span className="exo-at-orbit is-inner" />
        <div className={cn('exo-at-core', done && !working && 'is-done')}>
          {done && !working ? <Check size={36} /> : <Bolt size={36} />}
        </div>
      </div>

      <div className="exo-at-summary">
        <p className="exo-at-status">{statusLine}</p>
        <p className="exo-at-note">{noteLine}</p>
        <div className="exo-at-progress" aria-hidden="true">
          <div className="exo-at-progress-bar" style={{ transform: `scaleX(${ratio})` }} />
        </div>
      </div>

      <div className="exo-at-metrics">
        <button type="button" className="exo-at-metric is-interactive" onClick={onOpenExoAi}>
          <span className="exo-at-metric-label">Exo AI</span>
          <span className="exo-at-metric-value">{exoAi?.gaming.active ? 'Boosting' : 'Watching'}</span>
        </button>
        <div className="exo-at-metric">
          <span className="exo-at-metric-label">GPU Cooling</span>
          <span className="exo-at-metric-value">{fanMetricValue(exoAi)}</span>
        </div>
        <div className="exo-at-metric">
          <span className="exo-at-metric-label">Pipeline</span>
          <span className="exo-at-metric-value">{completedCount}/{totalCount} Ready</span>
        </div>
      </div>

      <button type="button" className="exo-at-cta" disabled={running} onClick={onStart}>
        <Bolt size={16} />
        {ctaLabel}
      </button>
    </section>
  )
}

export function AutoTuneOverlay({
  active,
  open,
  initialTab = 'studio',
  onClose,
  state,
  exoAi,
  onExoAi,
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
  initialTab?: AutoTuneTab
  onClose: () => void
  state: PcState
  exoAi: ExoAiState | null
  onExoAi: (next: ExoAiState) => void
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
  const closeRef = useRef<HTMLButtonElement>(null)
  const abortedRef = useRef(false)
  const [tab, setTab] = useState<AutoTuneTab>(initialTab)
  const [running, setRunning] = useState(false)
  const [finished, setFinished] = useState(false)
  const [stages, setStages] = useState<AutoTuneStageState[]>(() => initialAutoTuneStages(state, exoAi, hdr, settings))

  useEffect(() => {
    if (open) setTab(initialTab)
  }, [open, initialTab])

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
  const ratio = running ? (completedCount + 0.5) / stages.length : finished ? 1 : 0.12
  const statusLine = statusText(
    siliconTesting,
    running,
    finished,
    stages.some(s => s.status === 'warn'),
    stages.find(s => s.status === 'active'),
  )
  const noteLine = state.tuning?.restart
    ? 'Restart Windows to finish pending NVIDIA VRR LFC or system changes'
    : 'Reversible via Exo restore records · Zero anti-cheat tampering'
  const ctaLabel = running ? 'Optimizing PC…' : finished ? 'Run Again' : 'Run Auto Tune'
  const hasSilicon = (state.tune?.parts.length ?? 0) > 0

  return createPortal(
    <div
      className={cn('exo-auto-tune', working && 'is-working', finished && !working && 'is-done')}
      role="dialog"
      aria-modal="true"
      aria-labelledby="exo-auto-tune-title"
      data-log-area="PC"
    >
      <div className="exo-at-bg-glow" aria-hidden="true" />
      <div className="exo-at-bg-grid" aria-hidden="true" />
      <div className="exo-at-shell">
        <StudioTopbar
          titleHardware={hardwareLine(state)}
          exoAi={exoAi}
          tab={tab}
          onTab={setTab}
          closeRef={closeRef}
          onClose={() => {
            abortedRef.current = true
            setRunning(false)
            onClose()
          }}
        />

        {tab === 'studio' ? (
          <div className="exo-at-studio">
            <ReactorPane
              working={working}
              done={finished}
              ratio={ratio}
              statusLine={statusLine}
              noteLine={noteLine}
              ctaLabel={ctaLabel}
              running={running}
              completedCount={completedCount}
              totalCount={stages.length}
              exoAi={exoAi}
              onStart={() => void startPipeline()}
              onOpenExoAi={() => setTab('exo-ai')}
            />

            <div className="exo-at-deck">
              <section className="exo-at-stages" aria-label="Optimization pipeline">
                <div className="exo-at-section-head">
                  <h3>PC Optimization Pipeline</h3>
                  <span>{completedCount} of {stages.length} complete</span>
                </div>
                <ul className="exo-at-list">
                  {stages.map(stage => (
                    <li
                      key={stage.id}
                      className={cn(
                        'exo-at-stage-row',
                        stage.status === 'active' && 'is-active',
                        stage.status === 'done' && 'is-done',
                        stage.status === 'warn' && 'is-warn',
                      )}
                    >
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
                    <h3>Max Performance Silicon</h3>
                    <span>Peak stable GPU clock &amp; Ryzen Curve Optimizer</span>
                  </div>
                  <AutoTuneRows variant="cards" tune={state.tune} profile={state.profile} about={state.about} onTune={onTune} />
                </section>
              ) : null}
            </div>
          </div>
        ) : (
          <div className="exo-at-ai-pane">
            <div className="exo-at-ai-inner">
              <ExoAiPanel
                active={active}
                exoAi={exoAi}
                onExoAi={onExoAi}
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
              />
            </div>
          </div>
        )}
      </div>
    </div>,
    document.body,
  )
}

import { useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { Alert, Check } from '../../brand/icons'
import { errorText } from '../../lib/errorWords'
import { focusWhenActive } from '../../lib/focusTrap'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { useHoldWindow } from '../shell/HoldWindow'
import { Progress } from '../ui/Progress'
import type { AutoTuneStageState } from './autoTuneStages'

type AutoTuneStageId = AutoTuneStageState['id']

const SHORT_LABELS: Record<AutoTuneStageId, string> = {
  cooling: 'Power',
  graphics: 'Graphics',
  system: 'System',
  bloat: 'Apps',
  cleanup: 'Cleanup',
  drivers: 'Drivers',
}

const SPARKS = ['is-s1', 'is-s2', 'is-s3', 'is-s4', 'is-s5', 'is-s6', 'is-s7', 'is-s8'] as const

function CoolingGlyph() {
  return (
    <svg className="exo-at-svg" viewBox="0 0 64 64" width="76" height="76" aria-hidden="true">
      <path className="exo-at-svg-pins" d="M22 6v6M32 6v6M42 6v6M22 52v6M32 52v6M42 52v6M6 22h6M6 32h6M6 42h6M52 22h6M52 32h6M52 42h6" />
      <rect className="exo-at-svg-frame" x="14" y="14" width="36" height="36" rx="5" />
      <rect className="exo-at-svg-core" x="23" y="23" width="18" height="18" rx="3" />
    </svg>
  )
}

function GraphicsGlyph() {
  return (
    <svg className="exo-at-svg" viewBox="0 0 64 52" width="84" height="68" aria-hidden="true">
      <rect className="exo-at-svg-glass" x="5" y="4" width="54" height="34" rx="4" />
      <rect className="exo-at-svg-frame" x="5" y="4" width="54" height="34" rx="4" />
      <line className="exo-at-svg-scan" x1="12" y1="21" x2="52" y2="21" />
      <path className="exo-at-svg-frame" d="M24 46h16M32 38v8" />
    </svg>
  )
}

function SystemGlyph() {
  return (
    <svg className="exo-at-svg" viewBox="0 0 64 64" width="76" height="76" aria-hidden="true">
      <path className="exo-at-svg-pins" d="M10 18h10M10 32h10M10 46h10M44 18h10M44 32h10M44 46h10" />
      <rect className="exo-at-svg-core" x="20" y="12" width="24" height="40" rx="5" />
      <rect className="exo-at-svg-frame" x="20" y="12" width="24" height="40" rx="5" />
      <path className="exo-at-svg-scan" d="M34 22l-6 10h6l-4 10" />
    </svg>
  )
}

function BloatGlyph() {
  return (
    <svg className="exo-at-svg" viewBox="0 0 64 64" width="76" height="76" aria-hidden="true">
      <rect className="exo-at-svg-core" x="12" y="12" width="16" height="16" rx="3" />
      <rect className="exo-at-svg-frame" x="12" y="12" width="16" height="16" rx="3" />
      <rect className="exo-at-svg-frame" x="36" y="12" width="16" height="16" rx="3" />
      <rect className="exo-at-svg-frame" x="12" y="36" width="16" height="16" rx="3" />
      <rect className="exo-at-svg-core" x="36" y="36" width="16" height="16" rx="3" />
      <rect className="exo-at-svg-frame" x="36" y="36" width="16" height="16" rx="3" />
      <line className="exo-at-svg-scan" x1="10" y1="32" x2="54" y2="32" />
    </svg>
  )
}

function CleanupGlyph() {
  return (
    <svg className="exo-at-svg" viewBox="0 0 64 64" width="76" height="76" aria-hidden="true">
      <circle className="exo-at-svg-core" cx="32" cy="32" r="21" />
      <circle className="exo-at-svg-frame" cx="32" cy="32" r="21" />
      <circle className="exo-at-svg-pins" cx="32" cy="32" r="13" />
      <circle className="exo-at-svg-frame" cx="32" cy="32" r="5" />
    </svg>
  )
}

function DriversGlyph() {
  return (
    <svg className="exo-at-svg" viewBox="0 0 64 64" width="76" height="76" aria-hidden="true">
      <path className="exo-at-svg-core" d="M32 8L12 16v15c0 12.5 8.5 21.5 20 25 11.5-3.5 20-12.5 20-25V16L32 8z" />
      <path className="exo-at-svg-frame" d="M32 8L12 16v15c0 12.5 8.5 21.5 20 25 11.5-3.5 20-12.5 20-25V16L32 8z" />
      <path className="exo-at-svg-pins" d="M24 32l6 6 11-12" />
    </svg>
  )
}

function StageHardwareGlyph({ id }: { id: AutoTuneStageId }) {
  if (id === 'graphics') return <GraphicsGlyph />
  if (id === 'system') return <SystemGlyph />
  if (id === 'bloat') return <BloatGlyph />
  if (id === 'cleanup') return <CleanupGlyph />
  if (id === 'drivers') return <DriversGlyph />
  return <CoolingGlyph />
}

function AutoTuneHeroStage({
  activeId,
  finished,
  hasWarn,
}: {
  activeId: AutoTuneStageId
  finished: boolean
  hasWarn: boolean
}) {
  if (finished) {
    return (
      <div className="exo-at-hero-stage">
        <span className="exo-at-done-burst" aria-hidden="true" />
        <span className={cn('exo-at-done-ring', hasWarn && 'is-warn')} aria-hidden="true">
          <span className="exo-at-done-icon">
            {hasWarn ? <Alert size={40} /> : <Check size={40} />}
          </span>
        </span>
      </div>
    )
  }

  return (
    <div className="exo-at-hero-stage">
      <span className="exo-at-hero-halo" aria-hidden="true" />
      <span className="exo-at-pulse-wave" aria-hidden="true" />
      <span className="exo-at-pulse-wave is-delayed" aria-hidden="true" />
      <span className="exo-at-ring is-inner" aria-hidden="true" />
      <span className="exo-at-ring is-mid" aria-hidden="true" />
      <span className="exo-at-ring is-outer" aria-hidden="true" />
      <div key={activeId} className="exo-at-glyph-wrap">
        <StageHardwareGlyph id={activeId} />
      </div>
    </div>
  )
}

function AutoTuneRestartChoice({ onLater }: { onLater: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const restart = () => {
    setBusy(true)
    setError(null)
    host.biosUpdate('windows').then(
      result => {
        if (!result.ok) {
          setBusy(false)
          setError(errorText(result.message, 'Windows did not restart. Restart from the Start menu.'))
        }
      },
      (failure: unknown) => {
        report(failure, 'AutoTuneCinema.restart', { level: 'warn' })
        setBusy(false)
        setError('Windows did not restart. Restart from the Start menu.')
      },
    )
  }

  return (
    <>
      {error ? <p className="exo-at-cine-error" role="alert">{error}</p> : null}
      <button type="button" className="exo-set-btn" disabled={busy} onClick={onLater}>Not now</button>
      <button ref={focusWhenActive} type="button" className="exo-set-btn is-primary" disabled={busy} onClick={restart}>
        {busy ? 'Restarting...' : 'Restart now'}
      </button>
    </>
  )
}

function AutoTuneCineMilestones({ stages }: { stages: readonly AutoTuneStageState[] }) {
  return (
    <ul className="exo-at-milestones" aria-hidden="true">
      {stages.map(stage => {
        const tone = stage.status === 'done'
          ? 'is-done'
          : stage.status === 'warn'
            ? 'is-warn'
            : stage.status === 'active'
              ? 'is-active'
              : undefined
        return (
          <li key={stage.id} className={tone}>
            <span className="exo-at-milestone-dot" />
            <span>{SHORT_LABELS[stage.id]}</span>
          </li>
        )
      })}
    </ul>
  )
}

function AutoTuneCineSummary({
  stages,
  needsRestart,
  onExit,
  onRunAgain,
}: {
  stages: readonly AutoTuneStageState[]
  needsRestart: boolean
  onExit: () => void
  onRunAgain: () => void
}) {
  return (
    <>
      <ul className="exo-at-cine-results">
        {stages.map(stage => (
          <li key={stage.id} className={stage.status === 'warn' ? 'is-warn' : undefined}>
            {stage.status === 'warn' ? <Alert size={15} /> : <Check size={15} />}
            <div className="exo-at-cine-results-copy">
              <span className="exo-at-cine-results-name">{stage.label}</span>
              <span className="exo-at-cine-results-detail">{stage.detail}</span>
            </div>
          </li>
        ))}
      </ul>
      <div className="exo-at-cine-foot">
        {needsRestart ? (
          <AutoTuneRestartChoice onLater={onExit} />
        ) : (
          <>
            <button type="button" className="exo-set-btn" onClick={onRunAgain}>Run again</button>
            <button ref={focusWhenActive} type="button" className="exo-set-btn is-primary" onClick={onExit}>Done</button>
          </>
        )}
      </div>
    </>
  )
}

function deriveCineCopy(
  activeStage: AutoTuneStageState | undefined,
  running: boolean,
  hasWarn: boolean,
  needsRestart: boolean,
  activeIndex: number,
  completedCount: number,
  total: number,
) {
  const stageCounter = running && activeIndex >= 0
    ? `Stage ${activeIndex + 1} of ${total}`
    : `${completedCount} of ${total} stages`
  const doneHeadline = hasWarn ? 'Auto Tune finished' : 'PC tuned for peak performance'
  const headline = running ? (activeStage?.label ?? 'Optimizing PC') : doneHeadline
  const doneDetail = needsRestart
    ? 'Deep kernel and system tweaks are staged. Restart to finish.'
    : 'All stages applied and verified.'
  const detailText = running ? (activeStage?.detail ?? 'Applying performance playbook...') : doneDetail
  return { stageCounter, headline, detailText }
}

function deriveCineState(
  stages: readonly AutoTuneStageState[],
  running: boolean,
  finished: boolean,
  needsRestart: boolean,
) {
  const total = Math.max(1, stages.length)
  const completedCount = stages.filter(s => s.status === 'done' || s.status === 'warn').length
  const hasWarn = stages.some(s => s.status === 'warn')
  const activeIndex = stages.findIndex(s => s.status === 'active')
  const activeStage = activeIndex >= 0 ? stages[activeIndex] : undefined
  const activeId: AutoTuneStageId = activeStage?.id ?? stages[0]?.id ?? 'cooling'
  const percent = finished ? 100 : Math.round((completedCount / total) * 100)
  const scale = finished ? 1 : Math.max(0.08, completedCount / total)
  const copy = deriveCineCopy(activeStage, running, hasWarn, needsRestart, activeIndex, completedCount, total)
  const phase = running ? 'running' : finished ? 'done' : 'idle'
  const progressValue = running ? completedCount / total : 1
  const progressTone: 'done' | undefined = finished && !hasWarn ? 'done' : undefined
  return { completedCount, hasWarn, activeId, percent, scale, ...copy, phase, progressValue, progressTone }
}

export function AutoTuneCinema({
  stages,
  running,
  finished,
  needsRestart,
  onExit,
  onRunAgain,
}: {
  stages: readonly AutoTuneStageState[]
  running: boolean
  finished: boolean
  needsRestart: boolean
  onExit: () => void
  onRunAgain: () => void
}) {
  const screenRef = useRef<HTMLDivElement>(null)
  useHoldWindow(screenRef, true, running ? null : onExit)

  const { hasWarn, activeId, percent, scale, stageCounter, headline, detailText, phase, progressValue, progressTone } =
    deriveCineState(stages, running, finished, needsRestart)

  return createPortal(
    <div
      ref={screenRef}
      className={cn(
        'exo-portal exo-at-cine',
        running && 'is-working',
        finished && 'is-done',
        finished && hasWarn && 'is-warn',
      )}
      role="alertdialog"
      aria-modal="true"
      aria-labelledby="exo-at-cine-title"
      aria-busy={running}
      data-tune-phase={phase}
    >
      <div className="exo-at-cine-bg" aria-hidden="true">
        <div className="exo-at-cine-spot" />
        <div className="exo-at-cine-beam" />
        <div className="exo-at-cine-beam is-second" />
        <div className="exo-at-cine-sparks">
          {SPARKS.map(cls => <span key={cls} className={cn('exo-at-spark', cls)} />)}
        </div>
        <div className="exo-at-cine-rail is-top" style={{ transform: `scaleX(${scale})` }} />
        <div className="exo-at-cine-rail is-bottom" style={{ transform: `scaleX(${scale})` }} />
      </div>

      <div className="exo-at-cine-body">
        <AutoTuneHeroStage activeId={activeId} finished={finished} hasWarn={hasWarn} />

        <span className="exo-at-cine-meta">{stageCounter} · {percent}%</span>
        <h2 key={headline} id="exo-at-cine-title" className="exo-at-cine-title">{headline}</h2>
        <p key={detailText} className="exo-at-cine-step" role="status">{detailText}</p>

        <Progress
          size="large"
          value={progressValue}
          label={headline}
          tone={progressTone}
        />

        <AutoTuneCineMilestones stages={stages} />

        {finished ? (
          <AutoTuneCineSummary
            stages={stages}
            needsRestart={needsRestart}
            onExit={onExit}
            onRunAgain={onRunAgain}
          />
        ) : null}
      </div>
    </div>,
    document.body,
  )
}

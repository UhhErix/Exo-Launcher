import { useCallback, useEffect, useState } from 'react'
import { Bolt } from '../../brand/icons'
import {
  host,
  type AutopilotState,
  type DisplayState,
  type DrivesState,
  type ExoAiState,
  type GpuProfile,
  type HdrState,
  type Interrupts,
  type LauncherSettings,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { usePageVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Group, Row, State } from '../settings/parts'
import { ActionButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { AutopilotControls, AutopilotReports } from './Autopilot'
import { BestSetup } from './BestSetup'
import { BestScreenRows } from './DisplayTab'
import { InterruptRows } from './InterruptRows'
import { NetworkGroup } from './NetworkGroup'
import type { PcState } from './PcRoom'
import { SystemGroup } from './SystemGroup'
import { SetupRows } from './ThisPcTab'

/** Polls unified Exo AI status when PC room is visible, and faster while Auto Tune is open. */
export function useExoAi(active: boolean, open: boolean) {
  const [state, setState] = useState<ExoAiState | null>(null)
  const visible = usePageVisible()
  const refresh = useCallback(() => {
    host.exoAiState().then(setState, (e: unknown) => report(e, 'ExoAi.state', { level: 'warn' }))
  }, [])
  useEffect(() => {
    if (!active && !open) return
    refresh()
    const intervalMs = visible ? (open ? 5000 : 15000) : 30000
    const timer = window.setInterval(refresh, intervalMs)
    return () => window.clearInterval(timer)
  }, [active, open, visible, refresh])
  return { state, setState, refresh }
}

function formatFanDetail(fans: ExoAiState['fans'] | undefined): string {
  if (!fans) return 'Automatic GPU fan control; restores defaults on exit'
  if (fans.note) return fans.note
  const parts: string[] = []
  if (fans.tempC != null) parts.push(`${fans.tempC} °C`)
  if (fans.fanPercent != null) parts.push(`${fans.fanPercent}%`)
  if (fans.targetPercent != null) parts.push(`target ${fans.targetPercent}%`)
  if (fans.fanCount > 0) parts.push(`${fans.fanCount} ${fans.fanCount === 1 ? 'fan' : 'fans'}`)
  if (parts.length === 0) return 'Automatic GPU fan control; restores defaults on exit'
  return parts.join(' · ')
}

function formatCoreDetail(exoAi: ExoAiState | null): string {
  const sec = Math.max(1, Math.round((exoAi?.gaming.lookEveryMs ?? 2000) / 1000))
  const cores = exoAi?.gaming.hogCores ?? 0.25
  if (exoAi?.gaming.active) {
    const parts = [`${sec}s · ${cores} cores`]
    if (exoAi.gaming.timerResActive) parts.push('1 ms timer')
    if (exoAi.gaming.altTabbed) parts.push('Alt-tab paused')
    if (exoAi.gaming.demotedCount > 0) parts.push(`${exoAi.gaming.demotedCount} background apps slowed`)
    return parts.join(' · ')
  }
  return `Checking every 30m · ${sec}s loop · ${cores} cores`
}

function ExoAiStatusBanner({ exoAi, onCheckNow }: { exoAi: ExoAiState | null; onCheckNow: () => Promise<unknown> }) {
  const gaming = exoAi?.gaming.active ?? false
  const title = gaming ? `Boosting ${exoAi?.gaming.gameTitle ?? 'active game'}` : 'Watching in background'
  return (
    <div className={cn('exo-ai-status', gaming && 'is-gaming')}>
      <Bolt size={16} />
      <div className="exo-ai-status-copy">
        <span className="exo-ai-status-title">{title}</span>
        <span className="exo-ai-status-detail">{formatCoreDetail(exoAi)}</span>
      </div>
      <ActionButton className="exo-set-btn" timeoutMs={15_000} why="none" context="ExoAi.runNow" onPress={onCheckNow}>
        Check now
      </ActionButton>
    </div>
  )
}

function ExoAiCoreGroup({ exoAi }: { exoAi: ExoAiState | null }) {
  const applied = exoAi?.autoApplied ?? []
  const attention = exoAi?.needsAttention ?? []
  if (applied.length === 0 && attention.length === 0) return null
  return (
    <Group id="exo-ai-core" title="Applied automatically">
      {applied.length > 0 ? (
        <div className="exo-ai-pills" aria-label="Managed automatically">
          {applied.map(item => (
            <span key={item} className="exo-ai-pill">{item}</span>
          ))}
        </div>
      ) : null}
      {attention.length > 0 ? (
        <Row label="Needs admin approval" detail={attention.join(' · ')} />
      ) : null}
    </Group>
  )
}

/** Unified Exo AI panel embedded inside Auto Tune: background core, NVML GPU fans, Autopilot & collapsible overrides. */
export function ExoAiPanel({
  active,
  exoAi,
  onExoAi,
  autopilot,
  onAutopilot,
  state,
  hdr,
  settings,
  onSave,
  onBestRead,
  onDisplay,
  onHealth,
  onInterrupts,
  onTuning,
}: {
  active: boolean
  exoAi: ExoAiState | null
  onExoAi: (next: ExoAiState) => void
  autopilot: AutopilotState | null
  onAutopilot: (next: AutopilotState) => void
  state: PcState
  hdr: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  onBestRead: () => Promise<void>
  onDisplay: (next: DisplayState) => void
  onHealth: (next: PcHealth) => void
  onInterrupts: (next: Interrupts) => void
  onTuning: (next: Tuning) => void
}) {
  const [error, setError] = useState<string | null>(null)

  const runNow = async () => {
    setError(null)
    const next = await host.exoAiSet({ runNow: true })
    onExoAi(next)
    await onBestRead()
    if (!next.ok && next.message) setError(next.message)
    return next
  }

  const profile: GpuProfile | null = state.profile
  const health: PcHealth | null = state.health
  const tuning: Tuning | null = state.tuning
  const drives: DrivesState | null = state.drives

  return (
    <>
      <ExoAiStatusBanner exoAi={exoAi} onCheckNow={runNow} />
      {error ? <ErrorNote message={error} /> : null}
      <ExoAiCoreGroup exoAi={exoAi} />

      <Group id="exo-ai-fans" title="Cooling">
        <Row label="GPU fans" detail={formatFanDetail(exoAi?.fans)}>
          <State on={!!exoAi?.fans.supported}>
            {exoAi?.fans.supported ? 'Automatic' : 'Hardware VBIOS'}
          </State>
        </Row>
      </Group>

      <Group id="exo-ai-autopilot" title="Autopilot">
        <AutopilotControls state={autopilot} onState={onAutopilot} />
        <AutopilotReports state={autopilot} />
      </Group>

      <details className="exo-at-details" open={Boolean(tuning?.restart)}>
        <summary className="exo-at-details-summary">Manual overrides &amp; undo</summary>
        <div className="exo-at-details-body">
          <Group id="exo-ai-best" title="Best Settings &amp; Undo">
            <BestSetup
              profile={profile}
              health={health}
              tuning={tuning}
              drives={drives}
              screen={hdr}
              settings={settings}
              onSave={onSave}
              onRead={onBestRead}
            />
            <BestScreenRows display={state.display} onDisplay={onDisplay} />
            <SetupRows place="needs" active={active} health={health} onHealth={onHealth} />
            <InterruptRows place="needs" state={state.interrupts} onState={onInterrupts} />
            <SetupRows place="section" active={active} health={health} onHealth={onHealth} />
            <InterruptRows place="section" state={state.interrupts} onState={onInterrupts} />
            <NetworkGroup health={health} onHealth={onHealth} />
            <SystemGroup tuning={tuning} onTuning={onTuning} />
          </Group>
        </div>
      </details>
    </>
  )
}

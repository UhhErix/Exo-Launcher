import { useCallback, useEffect, useState } from 'react'
import { Bolt, Performance } from '../../brand/icons'
import {
  host,
  type AutoTune,
  type AutopilotState,
  type DisplayState,
  type DrivesState,
  type ExoAiChange,
  type ExoAiFanPreset,
  type ExoAiState,
  type GpuProfile,
  type HdrState,
  type Interrupts,
  type LauncherSettings,
  type PcAbout,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { ChoiceRow, Group, Row, SwitchRow } from '../settings/parts'
import { ActionButton } from '../ui/ConfirmButton'
import { ErrorNote } from '../ui/ErrorNote'
import { AutopilotControls, AutopilotReports } from './Autopilot'
import { AutoTuneRows } from './AutoTune'
import { BestSetup } from './BestSetup'
import { BestScreenRows } from './DisplayTab'
import { InterruptRows } from './InterruptRows'
import { NetworkGroup } from './NetworkGroup'
import type { PcState } from './PcRoom'
import { SystemGroup } from './SystemGroup'
import { SetupRows } from './ThisPcTab'

const FAN_PRESET_CHOICES: readonly { value: ExoAiFanPreset; label: string }[] = [
  { value: 'auto', label: 'Automatic (VBIOS)' },
  { value: 'quiet', label: 'Quiet' },
  { value: 'balanced', label: 'Balanced' },
  { value: 'performance', label: 'Performance' },
]

/** Polls unified Exo AI status when PC room is visible, and faster while Exo AI is open. */
export function useExoAi(active: boolean, open: boolean) {
  const [state, setState] = useState<ExoAiState | null>(null)
  const refresh = useCallback(() => {
    host.exoAiState().then(setState, (e: unknown) => report(e, 'ExoAi.state', { level: 'warn' }))
  }, [])
  useEffect(() => {
    if (!active && !open) return
    refresh()
    const timer = window.setInterval(refresh, open ? 4000 : 15000)
    return () => window.clearInterval(timer)
  }, [active, open, refresh])
  return { state, setState, refresh }
}

/** The unified Exo AI top chip in This PC, folding Best Settings, Autopilot, Auto-Tune, and Fans into one entry. */
export function ExoAiChip({ state, left, tune, onOpen }: {
  state: ExoAiState | null
  left: number | null
  tune: AutoTune | null
  onOpen: () => void
}) {
  const gaming = state?.gaming.active ?? false
  const testing = tune?.parts.some(part => part.state === 'testing') ?? false
  const tuned = tune?.parts.some(part => part.state === 'tuned') ?? false
  let label = 'Exo AI'
  let tone: 'is-good' | 'is-bad' | null = null

  if (gaming) {
    label = state?.gaming.gameTitle ? `Exo AI · ${state.gaming.gameTitle}` : 'Exo AI · Active'
    tone = 'is-good'
  } else if (testing) {
    label = 'Exo AI · Tuning'
    tone = 'is-good'
  } else if (left != null && left > 0) {
    label = `Exo AI · ${left} to check`
    tone = 'is-bad'
  } else if (state?.autoApplyBestSettings || tuned || (state?.fans.preset && state.fans.preset !== 'auto')) {
    label = 'Exo AI'
    tone = 'is-good'
  }

  return (
    <button type="button" className={cn('exo-pc-chip', tone)} data-pc-menu="exo-ai" aria-haspopup="dialog" onClick={onOpen}>
      <Performance size={14} />
      {label}
    </button>
  )
}

function formatFanDetail(fans: ExoAiState['fans'] | undefined): string {
  if (!fans) return 'Anti-cheat safe user-mode NVML fan control; restores VBIOS on shutdown'
  if (fans.note) return fans.note
  const parts: string[] = []
  if (fans.tempC != null) parts.push(`${fans.tempC} °C`)
  if (fans.fanPercent != null) parts.push(`${fans.fanPercent}%`)
  if (fans.targetPercent != null && fans.preset !== 'auto') parts.push(`target ${fans.targetPercent}%`)
  if (fans.fanCount > 0) parts.push(`${fans.fanCount} ${fans.fanCount === 1 ? 'fan' : 'fans'}`)
  if (parts.length === 0) return 'Anti-cheat safe user-mode NVML fan control; restores VBIOS on shutdown'
  return `${parts.join(' · ')} · user-mode NVML`
}

function formatCoreDetail(exoAi: ExoAiState | null): string {
  const sec = Math.max(1, Math.round((exoAi?.gaming.lookEveryMs ?? 2000) / 1000))
  const cores = exoAi?.gaming.hogCores ?? 0.25
  if (exoAi?.gaming.active) {
    const parts = [`${sec}s · ${cores} cores`]
    if (exoAi.gaming.timerResActive) parts.push('1 ms timer')
    if (exoAi.gaming.altTabbed) parts.push('Alt-tab paused')
    if (exoAi.gaming.demotedCount > 0) parts.push(`${exoAi.gaming.demotedCount} demoted`)
    return parts.join(' · ')
  }
  const parts = [`Idle loop (${sec}s · ${cores} cores)`]
  if (exoAi?.backgroundDriftCheck ?? true) parts.push('30m drift check')
  return parts.join(' · ')
}

function ExoAiStatusBanner({ exoAi, onCheckNow }: { exoAi: ExoAiState | null; onCheckNow: () => Promise<unknown> }) {
  const gaming = exoAi?.gaming.active ?? false
  const title = gaming
    ? `Protecting ${exoAi?.gaming.gameTitle ?? 'active game'}`
    : (exoAi?.autoApplyBestSettings ?? true)
      ? 'Exo AI watching in background'
      : 'Exo AI passive'
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

function ExoAiCoreGroup({ exoAi, onApply }: { exoAi: ExoAiState | null; onApply: (change: ExoAiChange) => Promise<string | null> }) {
  const applied = exoAi?.autoApplied ?? []
  const attention = exoAi?.needsAttention ?? []
  return (
    <Group id="exo-ai-core" title="Real-Time Core">
      <SwitchRow
        id="exo-ai-auto-apply"
        label="Always apply best settings silently"
        detail="Keeps safe Windows power plan, Game Mode, and pointer acceleration optimal without UAC prompts"
        checked={exoAi?.autoApplyBestSettings ?? true}
        onChange={autoApplyBestSettings => onApply({ autoApplyBestSettings })}
      />
      <SwitchRow
        id="exo-ai-drift"
        label="Background drift check"
        detail="Re-verifies non-elevated Windows settings every 30 minutes while no game runs"
        checked={exoAi?.backgroundDriftCheck ?? true}
        onChange={backgroundDriftCheck => onApply({ backgroundDriftCheck })}
      />
      {applied.length > 0 ? (
        <div className="exo-ai-pills" aria-label="Managed automatically">
          {applied.map(item => (
            <span key={item} className="exo-ai-pill">{item}</span>
          ))}
        </div>
      ) : null}
      {attention.length > 0 ? (
        <Row label="Admin items waiting" detail={attention.join(' · ')} />
      ) : null}
    </Group>
  )
}

/** Unified Exo AI optimization hub: background core, NVML GPU fans, Auto-Tune, Autopilot, and Best Settings. */
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
  onTune,
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
  onTune: (next: AutoTune) => void
  onBestRead: () => Promise<void>
  onDisplay: (next: DisplayState) => void
  onHealth: (next: PcHealth) => void
  onInterrupts: (next: Interrupts) => void
  onTuning: (next: Tuning) => void
}) {
  const [error, setError] = useState<string | null>(null)

  const apply = async (change: ExoAiChange): Promise<string | null> => {
    setError(null)
    try {
      const next = await host.exoAiSet(change)
      onExoAi(next)
      return next.ok ? null : (next.message ?? "Couldn't update Exo AI.")
    } catch (e: unknown) {
      report(e, 'ExoAiPanel.set')
      return "Couldn't update Exo AI."
    }
  }

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
  const about: PcAbout | null = state.about

  return (
    <>
      <ExoAiStatusBanner exoAi={exoAi} onCheckNow={runNow} />
      {error ? <ErrorNote message={error} /> : null}
      <ExoAiCoreGroup exoAi={exoAi} onApply={apply} />

      <Group id="exo-ai-fans" title="Cooling">
        <ChoiceRow<ExoAiFanPreset>
          id="exo-ai-fan-preset"
          label="GPU fan curve"
          detail={formatFanDetail(exoAi?.fans)}
          value={exoAi?.fans.preset ?? 'auto'}
          choices={FAN_PRESET_CHOICES}
          disabled={exoAi ? !exoAi.fans.supported : false}
          onChange={fanPreset => apply({ fanPreset })}
        />
      </Group>

      <Group id="exo-ai-tune" title="Auto-Tune">
        <AutoTuneRows tune={state.tune} profile={profile} about={about} onTune={onTune} />
      </Group>

      <Group id="exo-ai-autopilot" title="Autopilot">
        <AutopilotControls state={autopilot} onState={onAutopilot} />
        <AutopilotReports state={autopilot} />
      </Group>

      <Group id="exo-ai-best" title="Best Settings">
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
    </>
  )
}

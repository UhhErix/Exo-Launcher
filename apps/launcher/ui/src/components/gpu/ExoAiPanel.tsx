import { useCallback, useEffect, useRef, useState } from 'react'
import {
  host,
  type AutopilotState,
  type DisplayState,
  type ExoAiState,
  type HdrState,
  type Interrupts,
  type LauncherSettings,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { usePageVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Group } from '../settings/parts'
import { Popover } from '../ui/Dialog'
import { AutopilotControls, AutopilotReports } from './Autopilot'
import { BestSetup } from './BestSetup'
import { BestScreenRows } from './DisplayTab'
import { InterruptRows } from './InterruptRows'
import { NetworkGroup } from './NetworkGroup'
import type { PcState } from './PcRoom'
import { SectionSlot } from './SectionHead'
import { SystemGroup } from './SystemGroup'
import { SetupRows } from './ThisPcTab'

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

export function ExoAiFooter({
  active,
  exoAi,
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
  overridesOpen,
  onToggleOverrides,
  onCloseOverrides,
}: {
  active: boolean
  exoAi: ExoAiState | null
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
  overridesOpen: boolean
  onToggleOverrides: () => void
  onCloseOverrides: () => void
}) {
  const btnRef = useRef<HTMLButtonElement>(null)
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  const gaming = exoAi?.gaming.active ?? false
  const statusLabel = gaming
    ? (exoAi?.gaming.gameTitle ? `Exo AI · Boosting ${exoAi.gaming.gameTitle}` : 'Exo AI · Boosting game')
    : 'Exo AI · Active'

  return (
    <footer className="exo-at-ai-foot">
      <div className={cn('exo-at-ai-status', gaming && 'is-gaming')}>
        <span className="exo-at-ai-dot" aria-hidden="true" />
        <span>{statusLabel}</span>
      </div>
      <button
        ref={btnRef}
        type="button"
        className="exo-set-btn is-compact"
        aria-haspopup="dialog"
        aria-expanded={overridesOpen}
        onClick={onToggleOverrides}
      >
        Overrides
      </button>
      {overridesOpen ? (
        <Popover
          label="Overrides"
          anchor={btnRef.current}
          className="exo-glass is-panel exo-pc-menu exo-at-overrides"
          onClose={onCloseOverrides}
        >
          <header className="exo-pc-menu-head">
            <h2>Overrides</h2>
            <div ref={setSlot} className="exo-pc-section-actions" />
          </header>
          <SectionSlot.Provider value={slot}>
            <div className="exo-pc-menu-body">
              <Group id="exo-ai-autopilot" title="Autopilot">
                <AutopilotControls state={autopilot} onState={onAutopilot} />
                <AutopilotReports state={autopilot} />
              </Group>
              <Group id="exo-ai-best" title="Best settings &amp; undo">
                <BestSetup
                  profile={state.profile}
                  health={state.health}
                  tuning={state.tuning}
                  drives={state.drives}
                  screen={hdr}
                  settings={settings}
                  onSave={onSave}
                  onRead={onBestRead}
                />
                <BestScreenRows display={state.display} onDisplay={onDisplay} />
                <SetupRows place="needs" active={active} health={state.health} onHealth={onHealth} />
                <InterruptRows place="needs" state={state.interrupts} onState={onInterrupts} />
                <SetupRows place="section" active={active} health={state.health} onHealth={onHealth} />
                <InterruptRows place="section" state={state.interrupts} onState={onInterrupts} />
                <NetworkGroup health={state.health} onHealth={onHealth} />
                <SystemGroup tuning={state.tuning} onTuning={onTuning} />
              </Group>
            </div>
          </SectionSlot.Provider>
        </Popover>
      ) : null}
    </footer>
  )
}

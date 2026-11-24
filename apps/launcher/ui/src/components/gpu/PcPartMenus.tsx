import { useState, type ReactNode } from 'react'
import { Close, Drive } from '../../brand/icons'
import { gpuOffers } from '../../lib/canUse'
import { errorText } from '../../lib/errorWords'
import {
  host,
  type AutopilotState,
  type CompressList,
  type DisplayState,
  type DriverState,
  type DrivesState,
  type Game,
  type GpuProfile,
  type HdrState,
  type Interrupts,
  type LauncherSettings,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { report } from '../../lib/report'
import { cn, formatSize } from '../../lib/utils'
import { Group, Row, SwitchRow } from '../settings/parts'
import { Popover } from '../ui/Dialog'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { AutopilotControls, AutopilotReports } from './Autopilot'
import { BestSetup } from './BestSetup'
import { BestScreenRows, DisplayRows, HdrRows } from './DisplayTab'
import { DriverRow } from './DriverRow'
import { GpuManualTuning } from './GpuManualTuning'
import { InterruptRows } from './InterruptRows'
import { NetworkGroup } from './NetworkGroup'
import { BoardRows, type PcPartMenu } from './PcOverview'
import { ProfileRows } from './ProfileTab'
import { SectionSlot } from './SectionHead'
import { SystemGroup } from './SystemGroup'
import { SetupRows } from './ThisPcTab'
import type { PcState } from './usePcState'

const PANEL: Record<Exclude<NonNullable<GpuProfile['vendor']>, 'nvidia'>, string> = {
  amd: 'AMD Software',
  intel: 'Intel Graphics',
}

function PcMenu({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  return (
    <Popover label={title} className="exo-glass is-panel exo-pc-menu" onClose={onClose}>
      <header className="exo-pc-menu-head">
        <h2>{title}</h2>
        <div ref={setSlot} className="exo-pc-section-actions" />
        <button type="button" className="exo-props-close" aria-label={`Close ${title}`} onClick={onClose}>
          <Close size={15} />
        </button>
      </header>
      <SectionSlot.Provider value={slot}>
        <div className="exo-pc-menu-body">{children}</div>
      </SectionSlot.Provider>
    </Popover>
  )
}

function MakerPanelRow({ vendor }: { vendor: GpuProfile['vendor'] }) {
  const [error, setError] = useState<string | null>(null)
  if (!vendor || vendor === 'nvidia' || !gpuOffers(vendor).makerPanel) return null
  const name = PANEL[vendor]
  const open = () => {
    setError(null)
    host.gpuControlPanel().then(
      result => { if (!result.ok) setError(errorText(result.message, `${name} didn't open.`)) },
      (e: unknown) => { report(e, 'PcPartMenus.controlPanel'); setError(`${name} didn't open.`) },
    )
  }
  return (
    <Row label={name} detail="Open your GPU maker's own control panel" error={error}>
      <button type="button" className="exo-set-btn" onClick={open}>Open</button>
    </Row>
  )
}

export function StorageChip({
  drives,
  list,
  onOpen,
}: {
  drives: DrivesState | null
  list: CompressList | null
  onOpen: () => void
}) {
  const needsFix = drives?.fixes.some(fix => !fix.applied) ?? false
  const hasBadDrive = drives?.drives.some(drive => drive.verdicts.some(v => v.bad)) ?? false
  const potential = list?.potential ?? 0
  const saved = list?.saved ?? 0

  let label = 'Storage'
  let tone: 'is-good' | 'is-bad' | null = null
  if (hasBadDrive || needsFix) {
    label = 'Storage · Needs check'
    tone = 'is-bad'
  } else if (potential > 0) {
    label = `Storage · ${formatSize(potential)}`
  } else if (saved > 0) {
    label = `Storage · ${formatSize(saved)}`
    tone = 'is-good'
  }

  return (
    <button type="button" className={cn('exo-pc-chip', tone)} data-pc-menu="storage" aria-haspopup="dialog" onClick={onOpen}>
      <Drive size={14} />
      {label}
    </button>
  )
}

export function PcPartMenus({
  active,
  menu,
  onClose,
  state,
  hdr,
  games,
  settings,
  onSave,
  autopilot,
  onHealth,
  onInterrupts,
  onTuning,
  onProfile,
  onDriver,
  onDisplay,
  onRead,
}: {
  active: boolean
  menu: PcPartMenu | null
  onClose: () => void
  state: PcState
  hdr: HdrState | null
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  autopilot: { state: AutopilotState | null; setState: (next: AutopilotState) => void }
  onHealth: (next: PcHealth) => void
  onInterrupts: (next: Interrupts) => void
  onTuning: (next: Tuning) => void
  onProfile: (next: GpuProfile) => void
  onDriver: (next: DriverState) => void
  onDisplay: (next: DisplayState) => void
  onRead: () => Promise<void>
}) {
  const vendor = state.profile?.vendor ?? null

  return (
    <>
      {menu === 'board' ? (
        <PcMenu title="Motherboard" onClose={onClose}>
          <div className="exo-pc-menu-rows"><BoardRows about={state.about} /></div>
          <Group id="board-autopilot" title="Autopilot">
            <AutopilotControls state={autopilot.state} onState={autopilot.setState} />
            <AutopilotReports state={autopilot.state} />
          </Group>
          <Group id="board-system" title="System & undo">
            <BestSetup
              profile={state.profile}
              health={state.health}
              tuning={state.tuning}
              drives={state.drives}
              screen={hdr}
              settings={settings}
              onSave={onSave}
              onRead={onRead}
            />
            <SetupRows place="needs" active={active} health={state.health} onHealth={onHealth} />
            <InterruptRows place="needs" state={state.interrupts} onState={onInterrupts} />
            <SetupRows place="section" active={active} health={state.health} onHealth={onHealth} />
            <InterruptRows place="section" state={state.interrupts} onState={onInterrupts} />
            <NetworkGroup health={state.health} onHealth={onHealth} />
            <SystemGroup tuning={state.tuning} onTuning={onTuning} />
          </Group>
        </PcMenu>
      ) : null}
      {menu === 'graphics' ? (
        <PcMenu title="Graphics" onClose={onClose}>
          <div className="exo-pc-menu-rows">
            <DriverRow active={active} state={state.driver} onState={onDriver} />
            <ProfileRows place="needs" profile={state.profile} onProfile={onProfile} />
            <ProfileRows place="section" profile={state.profile} onProfile={onProfile} />
            <MakerPanelRow vendor={vendor} />
            {settings?.frameGenEverywhereCard ? (
              <SwitchRow
                id="set-frame-gen-everywhere"
                label="Frame generation in games without anti-cheat"
                checked={settings.frameGenEverywhere === true}
                disabled={false}
                onChange={async value => await onSave({ frameGenEverywhere: value }) ? null : 'Not saved. Try again.'}
              />
            ) : null}
          </div>
          <Group id="graphics-manual-tuning" title="Manual tuning">
            <GpuManualTuning active={active && menu === 'graphics'} />
          </Group>
          <Group id="graphics-upscalers" title="Upscalers">
            <UpscalersTab active={active && menu === 'graphics'} games={games} />
          </Group>
        </PcMenu>
      ) : null}
      {menu === 'display' ? (
        <PcMenu title="Display" onClose={onClose}>
          <div className="exo-pc-menu-rows">
            <BestScreenRows display={state.display} onDisplay={onDisplay} />
            <DisplayRows display={state.display} onDisplay={onDisplay} />
            <HdrRows screen={hdr} settings={settings} onSave={onSave} />
          </div>
        </PcMenu>
      ) : null}
    </>
  )
}

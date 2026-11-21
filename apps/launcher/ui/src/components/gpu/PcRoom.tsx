import { useCallback, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import {
  type DisplayState,
  type DriverState,
  type DrivesState,
  type Game,
  type GpuProfile,
  type Interrupts,
  type LauncherSettings,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { useAutopilot } from './Autopilot'
import { useCompressList } from './CompressGames'
import { DriversSection } from './DriversSection'
import { useAppsSummary } from './PcApps'
import type { PcPartMenu } from './PcOverview'
import { PcPartMenus, StorageChip } from './PcPartMenus'
import { SectionSlot } from './SectionHead'
import { StorageRoom } from './StorageRoom'
import type { PcLink } from './pcSections'
import { usePcState } from './usePcState'
import '../../styles/pc-room.css'

export type { PcState } from './usePcState'

type Menu = PcPartMenu

export function PcRoom({
  active,
  jump,
  games,
  settings,
  onSave,
}: {
  active: boolean
  jump: { tab: PcLink } | null
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const room = useRef<HTMLElement>(null)
  const { state, put, read } = usePcState(active)
  const hdr = useHdrState(active)
  const [menu, setMenu] = useState<Menu | null>(null)
  const [storageOpen, setStorageOpen] = useState(false)
  const apps = useAppsSummary(active, 0)
  const compress = useCompressList(active)

  const setHealth = useCallback((next: PcHealth) => put('health', next), [put])
  const setInterrupts = useCallback((next: Interrupts) => put('interrupts', next), [put])
  const setTuning = useCallback((next: Tuning) => put('tuning', next), [put])
  const setProfile = useCallback((next: GpuProfile) => put('profile', next), [put])
  const setDriver = useCallback((next: DriverState) => put('driver', next), [put])
  const setDisplay = useCallback((next: DisplayState) => put('display', next), [put])
  const setDrives = useCallback((next: DrivesState) => put('drives', next), [put])

  const autopilot = useAutopilot(active, menu === 'board')
  const openStorage = useCallback(() => {
    setMenu(null)
    setStorageOpen(true)
  }, [])
  const closeMenu = useCallback(() => setMenu(null), [])

  const vendor = state.profile?.vendor ?? null
  const handled = useRef<typeof jump>(null)
  useLayoutEffect(() => {
    const scroller = room.current
    if (!active || !jump || handled.current === jump || !scroller) return
    if (jump.tab === 'control' && !vendor) return
    handled.current = jump
    if (jump.tab === 'drives' || jump.tab === 'compress') { openStorage(); return }
    const opens: Partial<Record<PcLink, Menu>> = {
      graphics: 'graphics',
      upscalers: 'graphics',
      control: 'graphics',
      display: 'display',
      board: 'board',
    }
    if (jump.tab === 'control' && vendor && vendor !== 'nvidia') {
      import('../../lib/host').then(({ host }) => host.gpuControlPanel())
        .catch((e: unknown) => report(e, 'PcRoom.controlPanelLink'))
      return
    }
    const wanted = opens[jump.tab]
    if (wanted) {
      scroller.querySelector<HTMLElement>(`[data-pc-menu="${wanted}"]`)?.focus({ preventScroll: true })
      setMenu(wanted)
      return
    }
    if (jump.tab === 'overview') { scroller.scrollTop = 0; return }
    scroller.querySelector(`[data-pc-part="${jump.tab}"]`)?.scrollIntoView({ block: 'start' })
  }, [active, jump, vendor, openStorage])

  return (
    <main ref={room} className="exo-pc" aria-label="PC">
      <div className="exo-pc-main">
        <section className="exo-system-panel exo-pc-hero" data-pc-part="overview" aria-label="This PC">
          <div className="exo-pc-about">
            <div className="exo-pc-about-line">
              <h1 className="exo-pc-about-name">
                {state.about?.name || state.profile?.gpu || state.driver?.card || 'This PC'}
              </h1>
              <div className="exo-pc-chips">
                <StorageChip drives={state.drives} list={compress.list} onOpen={openStorage} />
              </div>
            </div>
          </div>
        </section>
        <Section id="drivers" title="Updates">
          <DriversSection
            active={active}
            driver={state.driver}
            onDriver={setDriver}
            health={state.health}
            onHealth={setHealth}
            about={state.about}
            runtimes={apps?.runtimes ?? null}
          />
        </Section>
      </div>
      <Section id="shaders" title="Shaders">
        <ShadersBoard active={active} games={games} />
      </Section>

      <PcPartMenus
        active={active}
        menu={menu}
        onClose={closeMenu}
        state={state}
        hdr={hdr}
        games={games}
        settings={settings}
        onSave={onSave}
        autopilot={autopilot}
        onHealth={setHealth}
        onInterrupts={setInterrupts}
        onTuning={setTuning}
        onProfile={setProfile}
        onDriver={setDriver}
        onDisplay={setDisplay}
        onRead={read}
      />

      <StorageRoom
        active={active}
        open={storageOpen}
        onClose={() => setStorageOpen(false)}
        drives={state.drives}
        onDrives={setDrives}
        list={compress.list}
        onList={compress.setList}
        onReadCompress={compress.read}
      />
    </main>
  )
}

function Section({ id, title, children }: { id: PcLink; title: string; children: ReactNode }) {
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  return (
    <section className={cn('exo-system-panel exo-pc-section', `is-${id}`)} data-pc-part={id} aria-labelledby={`pc-${id}-title`}>
      <header className="exo-pc-section-head">
        <h2 id={`pc-${id}-title`}>{title}</h2>
        <div ref={setSlot} className="exo-pc-section-actions" />
      </header>
      <SectionSlot.Provider value={slot}>
        <div className="exo-pc-pane">{children}</div>
      </SectionSlot.Provider>
    </section>
  )
}

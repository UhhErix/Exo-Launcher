import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { Check } from '../../brand/icons'
import { host, type AutoTune, type DisplayState, type DriverState, type DrivesState, type Game, type GpuProfile, type HdrState, type Interrupts, type LauncherSettings, type PcAbout, type PcHealth } from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { GameOverlay } from '../../motion'
import { SwitchRow } from '../settings/parts'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { Popover } from '../ui/Dialog'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { AutoTuneChip, AutoTuneRows, useTuneReads } from './AutoTune'
import { BestSetup, bestSteps } from './BestSetup'
import { NetworkGroup } from './NetworkGroup'
import { CompressChip, CompressGames, useCompressList } from './CompressGames'
import { BestScreenRows, DisplayRows, HdrRows } from './DisplayTab'
import { DriverRow, savedBranch } from './DriverRow'
import { DriversSection } from './DriversSection'
import { InterruptRows } from './InterruptRows'
import { AppsChip, AppsSheet, useAppsSummary } from './PcApps'
import { BoardRows, ThisPc, type PcPartMenu } from './PcOverview'
import { ProfileRows } from './ProfileTab'
import { SectionSlot } from './SectionHead'
import { SetupRows } from './ThisPcTab'
import type { PcLink } from './pcSections'
import { gpuOffers } from '../../lib/canUse'
import '../../styles/pc-room.css'

/** What the room reads from the host, shared by every row that shows or changes it. */
export type PcState = {
  about: PcAbout | null
  profile: GpuProfile | null
  driver: DriverState | null
  health: PcHealth | null
  interrupts: Interrupts | null
  display: DisplayState | null
  drives: DrivesState | null
  tune: AutoTune | null
}

/** What the room last read, for this run: it shows at once the next time the room opens and is read again behind it. */
const last: PcState = { about: null, profile: null, driver: null, health: null, interrupts: null, display: null, drives: null, tune: null }

/**
 * This PC, its card, driver, setup, interrupts and screens: read each time the room shows and when Exo's window comes
 * back (a setting changed in Windows, a driver installed), never while the room is away. A row that changes something
 * hands the host's answer back through `put`, so every row that shows it agrees.
 */
function usePcState(active: boolean) {
  const [state, setState] = useState<PcState>(() => ({ ...last }))
  const put = useCallback(<K extends keyof PcState>(key: K, value: PcState[K]) => {
    last[key] = value
    setState(current => current[key] === value ? current : { ...current, [key]: value })
  }, [])
  const read = useCallback(async () => {
    const one = <K extends keyof PcState>(key: K, call: () => Promise<PcState[K]>) =>
      call().then(value => put(key, value), (error: unknown) => report(error, `PcRoom.${key}`, { level: 'warn' }))
    await Promise.all([
      one('about', () => host.systemAbout()),
      one('profile', () => host.gpuProfile()),
      one('driver', () => host.gpuDriver('state', savedBranch())),
      one('health', () => host.systemHealth()),
      one('interrupts', () => host.systemInterrupts()),
      one('display', () => host.gpuDisplay('state')),
      one('tune', () => host.systemTune()),
      // At once: what fills each drive is a walk of the disk (6.6 s here), and only a hard drive's row uses it.
      one('drives', async () => {
        const drives = await host.systemDrives()
        if (!drives.drives?.some(drive => drive.kind === 'Hard drive')) return drives
        put('drives', drives)
        return host.systemDrives(undefined, true)
      }),
    ])
  }, [put])
  useEffect(() => {
    if (!active) return
    const again = () => { void read() }
    again()
    window.addEventListener('focus', again)
    return () => window.removeEventListener('focus', again)
  }, [active, read])
  return { state, put, read }
}

// Setup's rows Best settings counts itself (power, the Network group and its items) or other parts show (netlink in This PC, chipset in Drivers).
const SETUP_ELSEWHERE: readonly string[] = ['power', 'network', 'net-adapter', 'net-dns', 'net-tcp', 'netlink', 'chipset']

/**
 * What Best settings still has to set on this PC: its steps not yet set, a screen not at its best, setup it can't set by
 * itself (memory below its rated speed, Resizable BAR off) and interrupts to optimize. Null until the room has read
 * enough to say.
 */
function stillToSet(state: PcState, hdr: HdrState | null, settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>): number | null {
  const { profile, health, display, drives, interrupts } = state
  if (!profile || !health) return null
  const steps = bestSteps(profile, health, hdr, settings, onSave, drives).filter(step => !step.done).length
  const setup = health.items.filter(item => !SETUP_ELSEWHERE.includes(item.key) && !item.applied && !item.current).length
  const screens = display?.displays.filter(screen => !screen.isBest).length ?? 0
  const irq = interrupts && interrupts.devices.length > 0 &&
    (interrupts.reset || (interrupts.devices.some(device => device.plan === 'change') && !interrupts.restart)) ? 1 : 0
  return steps + setup + screens + irq
}

type Menu = 'best' | 'tune' | 'compress' | PcPartMenu

/**
 * The PC room (DESIGN.md › PC): one page, no scrolling on a 1080p screen. This PC leads, its name with its chips at the
 * line's right end (Best settings, Upscalers, Apps, the card maker's own control panel), then what it is made of; the
 * card, the screen and the board open their own menus. Under it Updates, and beside both Shaders, each scrolling in its
 * own pane. What is set or up to date shows nothing. `jump` opens what a game's link, a notice or Ctrl+K asks for.
 */
export function PcRoom({ active, jump, games, settings, onSave, onDevices }: {
  active: boolean
  /** Devices: your mouse, keyboard, audio and controllers, a page of this room. */
  onDevices?: () => void
  jump: { tab: PcLink } | null
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const room = useRef<HTMLElement>(null)
  const { state, put, read } = usePcState(active)
  const hdr = useHdrState(active)
  const [menu, setMenu] = useState<Menu | null>(null)
  const [appsOpen, setAppsOpen] = useState(false)
  const [upscalersOpen, setUpscalersOpen] = useState(false)
  const [upscalers, setUpscalers] = useState<number | null>(null)
  // A new number reads the apps again (after the sheet closes, after junk is cleaned).
  const [appsRead, setAppsRead] = useState(0)
  const apps = useAppsSummary(active, appsRead)
  const compress = useCompressList(active)

  const setHealth = useCallback((next: PcHealth) => put('health', next), [put])
  const setInterrupts = useCallback((next: Interrupts) => put('interrupts', next), [put])
  const setProfile = useCallback((next: GpuProfile) => put('profile', next), [put])
  const setDriver = useCallback((next: DriverState) => put('driver', next), [put])
  const setDisplay = useCallback((next: DisplayState) => put('display', next), [put])
  const setTune = useCallback((next: AutoTune) => put('tune', next), [put])
  useTuneReads(active, state.tune, menu === 'tune', setTune)
  const openApps = () => setAppsOpen(true)
  const readApps = () => setAppsRead(n => n + 1)
  const closeMenu = useCallback(() => setMenu(null), [])

  const vendor = state.profile?.vendor ?? null
  // A link opens its menu, hung from the part it belongs to, or brings its section into view on a small window.
  const handled = useRef<typeof jump>(null)
  useLayoutEffect(() => {
    const scroller = room.current
    if (!active || !jump || handled.current === jump || !scroller) return
    // The control panel's link waits for the card to be known: it opens a different app per maker.
    if (jump.tab === 'control' && !vendor) return
    handled.current = jump
    // On a GeForce the control panel is Exo's own: the card's menu and the screen's.
    const opens: Partial<Record<PcLink, Menu>> = { graphics: 'graphics', display: 'display', board: 'board', control: 'graphics', performance: 'best', compress: 'compress' }
    // AMD's and Intel's control panels are their own apps (the chip opens them): the link opens that app, not NVIDIA's menu.
    if (jump.tab === 'control' && vendor && vendor !== 'nvidia') {
      host.gpuControlPanel().catch((e: unknown) => report(e, 'PcRoom.controlPanelLink'))
      return
    }
    const wanted = opens[jump.tab]
    if (wanted) {
      scroller.querySelector<HTMLElement>(`[data-pc-menu="${wanted}"]`)?.focus({ preventScroll: true })
      setMenu(wanted)
      return
    }
    // Apps is a sheet; the network and the drives are parts of This PC: their links land on the hero.
    if (jump.tab === 'apps') { setAppsOpen(true); return }
    if (jump.tab === 'overview' || jump.tab === 'network' || jump.tab === 'drives') { scroller.scrollTop = 0; return }
    scroller.querySelector(`[data-pc-part="${jump.tab}"]`)?.scrollIntoView({ block: 'start' })
  }, [active, jump, vendor])

  const chips = (
    <>
      <BestChip left={stillToSet(state, hdr, settings, onSave)} onOpen={() => setMenu('best')} />
      <button type="button" className={cn('exo-pc-chip', upscalers === 0 ? 'is-good' : upscalers ? 'is-bad' : null)} onClick={() => setUpscalersOpen(true)}>
        {upscalers === 0 ? <Check size={14} /> : null}Upscalers{upscalers ? ` · ${upscalers}` : null}
      </button>
      <AppsChip summary={apps} onOpen={openApps} />
      <CompressChip list={compress.list} onOpen={() => setMenu('compress')} />
      {onDevices ? <button type="button" className="exo-pc-chip" onClick={onDevices}>Devices</button> : null}
      <AutoTuneChip tune={state.tune} onOpen={() => setMenu('tune')} />
      <MakerPanel vendor={vendor} />
    </>
  )

  return (
    <main ref={room} className="exo-pc" aria-label="PC">
      <div className="exo-pc-main">
        <section className="exo-system-panel exo-pc-hero" data-pc-part="overview" aria-label="This PC">
          <ThisPc state={state} chips={chips} onMenu={setMenu} />
        </section>
        <Section id="drivers" title="Updates">
          <DriversSection active={active} driver={state.driver} onDriver={setDriver} health={state.health} onHealth={setHealth}
            about={state.about} runtimes={apps?.runtimes ?? null} />
        </Section>
      </div>
      <Section id="shaders" title="Shaders">
        <ShadersBoard active={active} games={games} />
      </Section>

      {menu === 'best' ? (
        <PcMenu title="Best settings" onClose={closeMenu}>
          <BestSetup profile={state.profile} health={state.health} drives={state.drives} screen={hdr} settings={settings} onSave={onSave} onRead={read} />
          <div className="exo-pc-menu-rows">
            <BestScreenRows display={state.display} onDisplay={setDisplay} />
            <SetupRows place="needs" active={active} health={state.health} onHealth={setHealth} />
            <InterruptRows place="needs" state={state.interrupts} onState={setInterrupts} />
            <SetupRows place="section" active={active} health={state.health} onHealth={setHealth} />
            <InterruptRows place="section" state={state.interrupts} onState={setInterrupts} />
          </div>
          <NetworkGroup health={state.health} onHealth={setHealth} />
        </PcMenu>
      ) : null}
      {menu === 'tune' ? (
        <PcMenu title="Auto tune" onClose={closeMenu}>
          <AutoTuneRows tune={state.tune} profile={state.profile} about={state.about} onTune={setTune} />
        </PcMenu>
      ) : null}
      {menu === 'graphics' ? (
        <PcMenu title="Graphics" onClose={closeMenu}>
          <div className="exo-pc-menu-rows">
            <DriverRow active={active} state={state.driver} onState={setDriver} />
            <ProfileRows place="needs" profile={state.profile} onProfile={setProfile} />
            <ProfileRows place="section" profile={state.profile} onProfile={setProfile} />
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
        </PcMenu>
      ) : null}
      {menu === 'display' ? (
        <PcMenu title="Display" onClose={closeMenu}>
          <div className="exo-pc-menu-rows">
            <BestScreenRows display={state.display} onDisplay={setDisplay} />
            <DisplayRows display={state.display} onDisplay={setDisplay} />
            <HdrRows screen={hdr} settings={settings} onSave={onSave} />
          </div>
        </PcMenu>
      ) : null}
      {menu === 'compress' ? (
        <PcMenu title="Compress games" onClose={closeMenu}>
          <CompressGames list={compress.list} onList={compress.setList} onRead={compress.read} />
        </PcMenu>
      ) : null}
      {menu === 'board' ? (
        <PcMenu title="Motherboard" onClose={closeMenu}>
          <div className="exo-pc-menu-rows"><BoardRows about={state.about} /></div>
        </PcMenu>
      ) : null}

      <AppsSheet open={appsOpen} onClose={() => { setAppsOpen(false); readApps() }} />
      <GameOverlay open={upscalersOpen} keep label="Upscalers"
        scrim={<button type="button" className="exo-game-overlay-scrim" aria-label="Close" tabIndex={-1} onClick={() => setUpscalersOpen(false)} />}>
        <section className="exo-pc-upscalers-sheet" aria-labelledby="pc-upscalers-title">
          <h2 id="pc-upscalers-title">Upscalers</h2>
          <UpscalersTab active={active} games={games} onCount={setUpscalers} />
        </section>
      </GameOverlay>
    </main>
  )
}

/** Best settings: green once all is set, else how many things are left, in Bad; nothing until the room has read enough to say. */
function BestChip({ left, onOpen }: { left: number | null; onOpen: () => void }) {
  if (left === null) return null
  return (
    <button type="button" className={cn('exo-pc-chip', left === 0 ? 'is-good' : 'is-bad')} data-pc-menu="best" aria-haspopup="dialog" onClick={onOpen}>
      {left === 0 ? <><Check size={14} />Best settings on</> : `${left} ${left === 1 ? 'thing' : 'things'} left to set`}
    </button>
  )
}

/** A menu hung from the part that opened it (Dialog.tsx's Popover): its name with what it holds putting its action beside it (SectionHead.tsx), its rows scrolling under. */
function PcMenu({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  return (
    <Popover label={title} className="exo-glass is-panel exo-pc-menu" onClose={onClose}>
      <header className="exo-pc-menu-head">
        <h2>{title}</h2>
        <div ref={setSlot} className="exo-pc-section-actions" />
      </header>
      <SectionSlot.Provider value={slot}>
        <div className="exo-pc-menu-body">{children}</div>
      </SectionSlot.Provider>
    </Popover>
  )
}

const PANEL: Record<Exclude<NonNullable<GpuProfile['vendor']>, 'nvidia'>, string> = { amd: 'AMD Software', intel: 'Intel Graphics' }

/** A Radeon's or an Intel card's own control panel, for what Exo doesn't set; what stopped it opening, in its place. None on a GeForce (Exo is its panel). */
function MakerPanel({ vendor }: { vendor: GpuProfile['vendor'] }) {
  const [error, setError] = useState<string | null>(null)
  if (!vendor || vendor === 'nvidia' || !gpuOffers(vendor).makerPanel) return null
  const name = PANEL[vendor]
  const open = () => {
    setError(null)
    host.gpuControlPanel().then(result => { if (!result.ok) setError(result.message ?? `${name} didn't open.`) },
      (e: unknown) => { report(e, 'PcRoom.controlPanel'); setError(`${name} didn't open.`) })
  }
  return <button type="button" className={cn('exo-pc-chip', error && 'is-bad')} onClick={open}>{error ?? name}</button>
}

/**
 * A section of the room, a pane of glass: its name in the rooms' title size over a hairline, with what it holds putting
 * its actions at the line's right end (SectionHead.tsx), then what it holds. Its anchor is the link that opens it.
 */
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

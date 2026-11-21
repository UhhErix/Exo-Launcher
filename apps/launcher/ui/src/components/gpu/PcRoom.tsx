import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { Drive } from '../../brand/icons'
import { host, type AutoTune, type CompressList, type DisplayState, type DriverState, type DrivesState, type Game, type GpuProfile, type Interrupts, type LauncherSettings, type PcAbout, type PcHealth, type Tuning } from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { report } from '../../lib/report'
import { cn, formatSize } from '../../lib/utils'
import { Group, Row, SwitchRow } from '../settings/parts'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { Popover } from '../ui/Dialog'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { useAutopilot } from './Autopilot'
import { useTuneReads } from './AutoTune'
import { useCompressList } from './CompressGames'
import { BestScreenRows, DisplayRows, HdrRows } from './DisplayTab'
import { DriverRow, savedBranch } from './DriverRow'
import { DriversSection } from './DriversSection'
import { useExoAi } from './ExoAiPanel'
import { AutoTuneChip, AutoTuneOverlay } from './AutoTuneOverlay'
import { AppsChip, AppsSheet, useAppsSummary } from './PcApps'
import { BoardRows, ThisPc, type PcPartMenu } from './PcOverview'
import { ProfileRows } from './ProfileTab'
import { SectionSlot } from './SectionHead'
import { StorageRoom } from './StorageRoom'
import type { PcLink } from './pcSections'
import { gpuOffers } from '../../lib/canUse'
import '../../styles/pc-room.css'
import { errorText } from '../../lib/errorWords'

/** What the room reads from the host, shared by every row that shows or changes it. */
export type PcState = {
  about: PcAbout | null
  profile: GpuProfile | null
  driver: DriverState | null
  health: PcHealth | null
  interrupts: Interrupts | null
  tuning: Tuning | null
  display: DisplayState | null
  drives: DrivesState | null
  tune: AutoTune | null
}

/** What the room last read, for this run: it shows at once the next time the room opens and is read again behind it. */
const last: PcState = { about: null, profile: null, driver: null, health: null, interrupts: null, tuning: null, display: null, drives: null, tune: null }
let lastReadAt = 0

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
    lastReadAt = Date.now()
    const one = <K extends keyof PcState>(key: K, call: () => Promise<PcState[K]>) =>
      call().then(value => put(key, value), (error: unknown) => report(error, `PcRoom.${key}`, { level: 'warn' }))
    await Promise.all([
      one('about', () => host.systemAbout()),
      one('profile', () => host.gpuProfile()),
      one('driver', () => host.gpuDriver('state', savedBranch())),
      one('health', () => host.systemHealth()),
      one('interrupts', () => host.systemInterrupts()),
      one('tuning', () => host.systemTuning()),
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
    if (Date.now() - lastReadAt >= 45_000) void read()
    let focusTimer: number | undefined
    const again = () => {
      window.clearTimeout(focusTimer)
      focusTimer = window.setTimeout(() => {
        if (Date.now() - lastReadAt >= 45_000) void read()
      }, 250)
    }
    window.addEventListener('focus', again)
    return () => {
      window.clearTimeout(focusTimer)
      window.removeEventListener('focus', again)
    }
  }, [active, read])
  return { state, put, read }
}

type Menu = PcPartMenu

/**
 * The PC room (DESIGN.md › PC): one page, no scrolling on a 1080p screen. This PC leads, its name with its chips at the
 * line's right end (Best settings, Upscalers, Apps, the card maker's own control panel), then what it is made of; the
 * card, the screen and the board open their own menus. Under it Updates, and beside both Shaders, each scrolling in its
 * own pane. What is set or up to date shows nothing. `jump` opens what a game's link, a notice or Ctrl+K asks for.
 */
export function PcRoom({ active, jump, games, settings, onSave }: {
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
  const [autoTuneOpen, setAutoTuneOpen] = useState(false)
  const [autoTuneOverrides, setAutoTuneOverrides] = useState(false)
  const [storageOpen, setStorageOpen] = useState(false)
  const [appsOpen, setAppsOpen] = useState(false)
  // A new number reads the apps again (after the sheet closes, after junk is cleaned).
  const [appsRead, setAppsRead] = useState(0)
  const apps = useAppsSummary(active, appsRead)
  const compress = useCompressList(active)

  const setHealth = useCallback((next: PcHealth) => put('health', next), [put])
  const setInterrupts = useCallback((next: Interrupts) => put('interrupts', next), [put])
  const setTuning = useCallback((next: Tuning) => put('tuning', next), [put])
  const setProfile = useCallback((next: GpuProfile) => put('profile', next), [put])
  const setDriver = useCallback((next: DriverState) => put('driver', next), [put])
  const setDisplay = useCallback((next: DisplayState) => put('display', next), [put])
  const setDrives = useCallback((next: DrivesState) => put('drives', next), [put])
  const setTune = useCallback((next: AutoTune) => put('tune', next), [put])
  useTuneReads(active, state.tune, autoTuneOpen, setTune)
  const autopilot = useAutopilot(active, autoTuneOpen)
  const exoAi = useExoAi(active, autoTuneOpen)
  const openAutoTune = useCallback((withOverrides = false) => {
    setMenu(null)
    setStorageOpen(false)
    setAutoTuneOverrides(withOverrides)
    setAutoTuneOpen(true)
  }, [])
  const openStorage = useCallback(() => {
    setMenu(null)
    setAutoTuneOpen(false)
    setStorageOpen(true)
  }, [])
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
    if (jump.tab === 'performance') { openAutoTune(false); return }
    if (jump.tab === 'autopilot') { openAutoTune(true); return }
    if (jump.tab === 'drives' || jump.tab === 'compress') { openStorage(); return }
    const opens: Partial<Record<PcLink, Menu>> = {
      graphics: 'graphics',
      upscalers: 'graphics',
      control: 'graphics',
      display: 'display',
      board: 'board',
    }
    // AMD's and Intel's control panels are their own apps: the link opens that app, not NVIDIA's menu.
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
    if (jump.tab === 'apps') { setAppsOpen(true); return }
    if (jump.tab === 'overview' || jump.tab === 'network') { scroller.scrollTop = 0; return }
    scroller.querySelector(`[data-pc-part="${jump.tab}"]`)?.scrollIntoView({ block: 'start' })
  }, [active, jump, vendor, openAutoTune, openStorage])

  const chips = (
    <>
      <AutoTuneChip state={state} exoAi={exoAi.state} hdr={hdr} settings={settings} onOpen={() => openAutoTune(false)} />
      <AppsChip summary={apps} onOpen={openApps} />
      <StorageChip drives={state.drives} list={compress.list} onOpen={openStorage} />
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

      {menu === 'board' ? (
        <PcMenu title="Motherboard" onClose={closeMenu}>
          <div className="exo-pc-menu-rows"><BoardRows about={state.about} /></div>
        </PcMenu>
      ) : null}
      {menu === 'graphics' ? (
        <PcMenu title="Graphics" onClose={closeMenu}>
          <div className="exo-pc-menu-rows">
            <DriverRow active={active} state={state.driver} onState={setDriver} />
            <ProfileRows place="needs" profile={state.profile} onProfile={setProfile} />
            <ProfileRows place="section" profile={state.profile} onProfile={setProfile} />
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
          <Group id="graphics-upscalers" title="Upscalers">
            <UpscalersTab active={active && menu === 'graphics'} games={games} />
          </Group>
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

      <AutoTuneOverlay
        active={active}
        open={autoTuneOpen}
        initialOverrides={autoTuneOverrides}
        onClose={() => { setAutoTuneOpen(false); setAutoTuneOverrides(false) }}
        state={state}
        exoAi={exoAi.state}
        autopilot={autopilot.state}
        onAutopilot={autopilot.setState}
        hdr={hdr}
        settings={settings}
        onSave={onSave}
        onTune={setTune}
        onDisplay={setDisplay}
        onHealth={setHealth}
        onInterrupts={setInterrupts}
        onTuning={setTuning}
        onRefresh={async () => { await read(); exoAi.refresh() }}
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
      <AppsSheet open={appsOpen} onClose={() => { setAppsOpen(false); readApps() }} />
    </main>
  )
}

function StorageChip({ drives, list, onOpen }: { drives: DrivesState | null; list: CompressList | null; onOpen: () => void }) {
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

/** A Radeon's or an Intel card's own control panel, surfaced inside Graphics for vendor-specific settings. */
function MakerPanelRow({ vendor }: { vendor: GpuProfile['vendor'] }) {
  const [error, setError] = useState<string | null>(null)
  if (!vendor || vendor === 'nvidia' || !gpuOffers(vendor).makerPanel) return null
  const name = PANEL[vendor]
  const open = () => {
    setError(null)
    host.gpuControlPanel().then(result => { if (!result.ok) setError(errorText(result.message, `${name} didn't open.`)) },
      (e: unknown) => { report(e, 'PcRoom.controlPanel'); setError(`${name} didn't open.`) })
  }
  return (
    <Row label={name} detail="Open your GPU maker's own control panel" error={error}>
      <button type="button" className="exo-set-btn" onClick={open}>Open</button>
    </Row>
  )
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

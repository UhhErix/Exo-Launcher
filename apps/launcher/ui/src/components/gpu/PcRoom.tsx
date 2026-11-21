import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { Drive } from '../../brand/icons'
import { host, type AutoTune, type CompressList, type DisplayState, type DriverState, type DrivesState, type Game, type GpuProfile, type HdrState, type Interrupts, type LauncherSettings, type PcAbout, type PcHealth, type Tuning } from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { report } from '../../lib/report'
import { cn, formatSize } from '../../lib/utils'
import { Group, Row, State, SwitchRow } from '../settings/parts'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { ActionButton } from '../ui/ConfirmButton'
import { Popover } from '../ui/Dialog'
import { UpscalersTab } from '../upscalers/UpscalersSheet'
import { useAutopilot } from './Autopilot'
import { useTuneReads } from './AutoTune'
import { bestSteps } from './BestSetup'
import { CompressGames, useCompressList } from './CompressGames'
import { BestScreenRows, DisplayRows, HdrRows } from './DisplayTab'
import { DriverRow, savedBranch } from './DriverRow'
import { DriversSection } from './DriversSection'
import { ExoAiChip, ExoAiPanel, useExoAi } from './ExoAiPanel'
import { AutoTuneChip, AutoTuneOverlay } from './AutoTuneOverlay'
import { AppsChip, AppsSheet, useAppsSummary } from './PcApps'
import { BoardRows, ThisPc, type PcPartMenu } from './PcOverview'
import { ProfileRows } from './ProfileTab'
import { SectionSlot } from './SectionHead'
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

// Setup's rows Best settings counts itself (power, the Network group and its items) or other parts show (netlink in This PC, chipset in Drivers).
const SETUP_ELSEWHERE: readonly string[] = ['power', 'network', 'net-adapter', 'net-dns', 'net-tcp', 'netlink', 'chipset']

/**
 * What Best settings still has to set on this PC: its steps not yet set, a screen not at its best, setup it can't set by
 * itself (memory below its rated speed, Resizable BAR off) and interrupts to optimize. Null until the room has read
 * enough to say.
 */
function stillToSet(state: PcState, hdr: HdrState | null, settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>): number | null {
  const { profile, health, display, drives, interrupts, tuning } = state
  if (!profile || !health) return null
  const steps = bestSteps(profile, health, hdr, settings, onSave, drives, tuning).filter(step => !step.done).length
  const setup = health.items.filter(item => !SETUP_ELSEWHERE.includes(item.key) && !item.applied && !item.current).length
  const screens = display?.displays.filter(screen => !screen.isBest).length ?? 0
  const irq = interrupts && interrupts.devices.length > 0 &&
    (interrupts.reset || (interrupts.devices.some(device => device.plan === 'change') && !interrupts.restart)) ? 1 : 0
  return steps + setup + screens + irq
}

type Menu = 'exo-ai' | 'storage' | PcPartMenu

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
  useTuneReads(active, state.tune, menu === 'exo-ai' || autoTuneOpen, setTune)
  const autopilot = useAutopilot(active, menu === 'exo-ai')
  const exoAi = useExoAi(active, menu === 'exo-ai')
  const openAutoTune = useCallback(() => { setMenu(null); setAutoTuneOpen(true) }, [])
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
    const opens: Partial<Record<PcLink, Menu>> = {
      graphics: 'graphics',
      upscalers: 'graphics',
      control: 'graphics',
      display: 'display',
      board: 'board',
      performance: 'exo-ai',
      autopilot: 'exo-ai',
      drives: 'storage',
      compress: 'storage',
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
  }, [active, jump, vendor])

  const chips = (
    <>
      <AutoTuneChip state={state} hdr={hdr} settings={settings} onOpen={openAutoTune} />
      <ExoAiChip state={exoAi.state} left={stillToSet(state, hdr, settings, onSave)} tune={state.tune} onOpen={() => setMenu('exo-ai')} />
      <AppsChip summary={apps} onOpen={openApps} />
      <StorageChip drives={state.drives} list={compress.list} onOpen={() => setMenu('storage')} />
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

      {menu === 'exo-ai' ? (
        <PcMenu title="Exo AI" onClose={closeMenu}>
          <ExoAiPanel
            active={active}
            exoAi={exoAi.state}
            onExoAi={exoAi.setState}
            autopilot={autopilot.state}
            onAutopilot={autopilot.setState}
            state={state}
            hdr={hdr}
            settings={settings}
            onSave={onSave}
            onTune={setTune}
            onBestRead={async () => { await read(); exoAi.refresh() }}
            onDisplay={setDisplay}
            onHealth={setHealth}
            onInterrupts={setInterrupts}
            onTuning={setTuning}
            onOpenAutoTune={openAutoTune}
          />
        </PcMenu>
      ) : null}
      {menu === 'storage' ? (
        <PcMenu title="Storage" onClose={closeMenu}>
          <StorageSheet drives={state.drives} onDrives={setDrives} list={compress.list} onList={compress.setList} onRead={compress.read} />
        </PcMenu>
      ) : null}
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
        open={autoTuneOpen}
        onClose={() => setAutoTuneOpen(false)}
        state={state}
        exoAi={exoAi.state}
        hdr={hdr}
        settings={settings}
        onSave={onSave}
        onTune={setTune}
        onRefresh={async () => { await read(); exoAi.refresh() }}
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

function StorageSheet({ drives, onDrives, list, onList, onRead }: {
  drives: DrivesState | null
  onDrives: (next: DrivesState) => void
  list: CompressList | null
  onList: (next: CompressList) => void
  onRead: () => void
}) {
  return (
    <div className="exo-storage-section">
      {drives && (drives.drives.length > 0 || drives.fixes.length > 0) ? (
        <Group id="storage-drives" title="Drives">
          {drives.drives.map(drive => {
            const letters = drive.volumes.map(v => `${v.letter}:`).join(', ')
            const freeGb = Math.round(drive.volumes.reduce((sum, v) => sum + v.freeGb, 0))
            const bad = drive.verdicts.some(v => v.bad)
            const detail = [
              drive.kind,
              drive.volumes.length > 0 ? `${freeGb} GB free` : null,
              drive.temperature?.now != null ? `${drive.temperature.now} °C` : null,
              ...drive.verdicts.filter(v => v.bad).map(v => v.text),
            ].filter(Boolean).join(' · ')
            return (
              <Row key={drive.id} label={[letters, drive.model].filter(Boolean).join(' · ') || drive.kind} detail={detail}>
                <State on={!bad}>{bad ? 'Check' : 'Healthy'}</State>
              </Row>
            )
          })}
          {drives.fixes.map(fix => (
            <Row key={fix.key} label={fix.label} detail={fix.detail}>
              {fix.applied ? (
                <State on>{fix.restart ? 'After restart' : 'Optimal'}</State>
              ) : (
                <ActionButton
                  className="exo-set-btn"
                  timeoutMs={15_000}
                  why="none"
                  context={`Storage.fix.${fix.key}`}
                  onPress={async () => {
                    const next = await host.systemDrives(fix.key)
                    onDrives(next)
                    return next
                  }}
                >
                  Apply
                </ActionButton>
              )}
            </Row>
          ))}
        </Group>
      ) : null}
      <Group id="storage-compress" title="Compress games">
        <CompressGames list={list} onList={onList} onRead={onRead} />
      </Group>
    </div>
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

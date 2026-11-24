import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { host, onHostEvent, type ControllerRead, type DriverEntry, type DriversState, type DriverState, type DriverStep, type PcAbout, type PcHealth, type RuntimeItem } from '../../lib/host'
import { RuntimesPanel } from '../runtimes/RuntimesPanel'
import { releaseOf } from './PcOverview'
import { report } from '../../lib/report'
import { sentenceStart, trimStop } from '../../lib/utils'
import { driveName } from '../../lib/partNames'
import { Row } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Progress, ProgressWords } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'
import { DriverRow } from './DriverRow'
import { SectionActions } from './SectionHead'
import { SetupRows } from './ThisPcTab'
import { driverInstallDismiss, driverInstallPhase, driverInstallStarted, driverInstallStopped } from '../shell/DriverInstallOverlay'

/** The row's one line: the known problem with what runs, in a few words. Versions are not shown: the button says what it does. */
function problem(item: DriverEntry) {
  const why = item.advised?.why
  return why ? sentenceStart(why) : null
}

/** Counts as an update: something newer to install or open. A vendor's app never does. */
function needs(item: DriverEntry) {
  return !item.app && item.how !== 'none'
}

/** The chipset row asks for an update: an old driver, not installing and not waiting for its restart. */
function chipsetNeeds(health: PcHealth | null) {
  return health?.items.some(item => item.key === 'chipset' && !item.current && !item.applied && !item.busy) === true
}

/** The graphics driver's row in Updates: while it has something to do, installs, or Update all asked for it. */
const gpuShown = (driver: DriverState | null, ask: number) =>
  gpuNeeds(driver) || ask > 0 || (!!driver?.progress && driver.progress.stage !== 'idle' && driver.progress.stage !== 'done')

/** The graphics driver asks for an install: a newer one, or going back from one with a known problem here. */
function gpuNeeds(driver: DriverState | null) {
  return !!driver?.update || !!driver?.advised?.back
}

/** The rows to show (and those an install just ran), whether nothing is left to do, and whether Update all has anything to do. */
function summarize(state: DriversState | null, steps: Record<string, DriverStep>, health: PcHealth | null, driver: DriverState | null,
  runtimes: RuntimeItem[] | null) {
  // Only what the person can act on (or a row an install just ran): a driver at its newest, or one with a known problem
  // and no way out of it, isn't shown.
  const rows = state?.items.filter(item => item.how !== 'none' || steps[item.id]) ?? []
  const chipset = chipsetNeeds(health)
  const gpu = gpuNeeds(driver)
  const count = rows.filter(needs).length + (chipset ? 1 : 0) + (gpu ? 1 : 0)
  const installable = rows.some(item => item.how === 'install' && !item.app) || chipset || gpu
  const waiting = count + (runtimes?.filter(item => (item.state === 'missing' && item.bulk) || item.state === 'outdated').length ?? 0)
  return { rows, installable, waiting: state ? waiting : null }
}

/** Update all's steps, one after another: Windows Update's and the network's (one batch), the chipset, then the graphics driver's own consent. */
type Phase = 'catalog' | 'chipset' | null

/** What Update all set out to install when pressed: its batch's drivers, and whether the chipset follows them. */
type Plan = { ids: string[]; chipset: boolean }

/**
 * How far Update all is: of what it set out to install, how much has landed (each driver as it finishes, installed or
 * not, then the chipset); null outside a run started here, so the bar glides and shows no number.
 */
function planCount(plan: Plan | null, steps: Record<string, DriverStep>, phase: Phase) {
  if (!plan || phase === null || (phase === 'chipset' && !plan.chipset)) return null
  const total = plan.ids.length + (plan.chipset ? 1 : 0)
  const landed = phase === 'chipset' ? plan.ids.length : plan.ids.filter(id => steps[id]?.ok != null).length
  return total > 0 ? { landed, total } : null
}

/**
 * PC › Updates: every device with a driver or firmware. Update all on its title line; under it, what has something newer
 * or a known problem: the graphics driver (Game Ready or Studio, consent, the full-screen install), the chipset, Windows
 * Update's drivers, the network adapters, the BIOS and SSD firmware (which open their maker's page), a plugged-in
 * controller's firmware, a newer Windows, then the game runtimes. A vendor's app is its own row with its own Install, never
 * in Update all. Read each time the room shows.
 */
async function runChipsetStep(
  chipLabel: string,
  hasGpu: boolean,
  onHealth: (next: PcHealth) => void,
  onError: (msg: string) => void,
): Promise<'busy' | 'done'> {
  driverInstallStarted(chipLabel, '', null, 'chipset')
  try {
    const answer = await host.systemHealth('chipset')
    onHealth(answer)
    const updated = answer.items.find(item => item.key === 'chipset')
    if (!answer.ok) {
      const msg = trimStop(answer.message) || 'The chipset driver didn’t update.'
      onError(msg)
      driverInstallPhase('failed', { message: msg })
      return 'done'
    }
    if (updated?.busy) {
      driverInstallPhase('installing', { name: updated.label })
      return 'busy'
    }
    if (updated && (updated.applied || updated.current)) {
      if (hasGpu) driverInstallDismiss()
      else driverInstallPhase('done', { name: updated.label })
    } else {
      driverInstallStopped()
    }
  } catch (e) {
    report(e, 'DriversSection.chipset')
    driverInstallPhase('failed', { message: 'The chipset driver didn’t update.' })
  }
  return 'done'
}

export function DriversSection({ active, driver, onDriver, health, onHealth, about, runtimes }: {
  active: boolean
  driver: DriverState | null
  onDriver: (next: DriverState) => void
  health: PcHealth | null
  onHealth: (next: PcHealth) => void
  /** Windows' version against Microsoft's newest. */
  about: PcAbout | null
  /** The game runtimes as the room read them (runtimes.list); null until read. */
  runtimes: RuntimeItem[] | null
}) {
  const [state, setState] = useState<DriversState | null>(null)
  const [unread, setUnread] = useState(false)
  const [steps, setSteps] = useState<Record<string, DriverStep>>({})
  const [phase, setPhase] = useState<Phase>(null)
  const [plan, setPlan] = useState<Plan | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Bumped to open the graphics driver's consent, the last step of Update all.
  const [askGpu, setAskGpu] = useState(0)
  const healthRef = useRef(health)
  const phaseRef = useRef<Phase>(null)
  useEffect(() => { healthRef.current = health; phaseRef.current = phase })

  const load = useCallback(async () => {
    try {
      setState(await host.systemDrivers())
      setUnread(false)
    } catch (e) {
      report(e, 'DriversSection.state')
      setUnread(true)
    }
  }, [])

  useEffect(() => { if (active) void load() }, [active, load])

  // After the catalog's batch: the chipset (it installs on its own; the effect below goes on once it's in), then the
  // graphics driver's consent, whose install covers Exo until it's done.
  const next = useCallback(async (from: Phase) => {
    if (from === 'chipset') {
      if (gpuNeeds(driver)) driverInstallDismiss()
      else {
        const chip = healthRef.current?.items.find(item => item.key === 'chipset')
        if (chip && (chip.applied || chip.current)) driverInstallPhase('done', { name: chip.label })
      }
    }
    if (from === 'catalog' && chipsetNeeds(healthRef.current)) {
      const chip = healthRef.current?.items.find(item => item.key === 'chipset')
      if (await runChipsetStep(chip?.label || 'CPU & chipset drivers', gpuNeeds(driver), onHealth, setError) === 'busy') {
        setPhase('chipset')
        return
      }
    }
    setPhase(null)
    if (gpuNeeds(driver)) setAskGpu(n => n + 1)
  }, [driver, onHealth])

  useEffect(() => onHostEvent('drivers.step', step => {
    if (step.id) { setSteps(all => ({ ...all, [step.id]: step })); return }
    void load()
    if (phaseRef.current === 'catalog') void next('catalog')
  }), [load, next])

  // The chipset installs on its own; Update all goes on once it's in.
  useEffect(() => {
    if (phase === 'chipset' && !health?.items.some(item => item.key === 'chipset' && item.busy)) void next('chipset')
  }, [phase, health, next])

  const updateAll = async () => {
    setError(null)
    setSteps({})
    setPhase('catalog')
    // The host's batch is the same rows: every one it can install, never a vendor's app.
    const ids = state?.items.filter(item => item.how === 'install' && !item.app).map(item => item.id) ?? []
    setPlan({ ids, chipset: chipsetNeeds(health) })
    if (ids.length === 0) { void next('catalog'); return }
    try {
      const answer = await host.systemDrivers('update-all')
      setState(answer)
      if (!answer.ok) {
        setPhase(null)
        setError(trimStop(answer.message) || 'The update didn’t start. Try again.')
      }
    } catch (e) {
      report(e, 'DriversSection.updateAll')
      setPhase(null)
      setError('The update didn’t start. Try again.')
    }
  }

  // One row's own Install: a vendor's app, or the update past a known problem.
  const install = async (id: string) => {
    setError(null)
    try {
      const answer = await host.systemDrivers('install', false, id)
      setState(answer)
      if (!answer.ok) setError(trimStop(answer.message) || 'The install didn’t start. Try again.')
    } catch (e) {
      report(e, 'DriversSection.install')
      setError('The install didn’t start. Try again.')
    }
  }

  const { rows, installable, waiting } = summarize(state, steps, health, driver, runtimes)
  const working = phase !== null || state?.running === true
  const going = planCount(plan, steps, phase)
  const share = going ? going.landed / going.total : null
  const detail = going ? <ProgressWords words={`${going.landed} of ${going.total}`} value={share} /> : null

  const blocked = error ?? (unread ? 'Exo couldn’t check for updates. Try again in a moment.' : installable && !working ? state?.busy ?? null : null)
  return (
    <>
      {/* Update all on the section's title line; under it only what a run is doing or what went wrong. */}
      <SectionActions>
        <UpdatesWord read={!!state || unread} waiting={waiting} />
        {/* One elevated run for all of them: Windows' one prompt is the confirm (DESIGN.md › Confirm). */}
        {installable && (
          <button type="button" className="exo-set-btn is-primary" disabled={working || !!state?.busy} aria-busy={working} onClick={() => void updateAll()}>
            Update all
            {working ? <Progress edge value={share} label="Updating drivers" /> : null}
          </button>
        )}
      </SectionActions>
      <RunRow going={!!going} restart={!!state?.restart} detail={detail} blocked={blocked} />
      {/* The graphics driver only while it has something to do; the Graphics menu always shows it. */}
      {gpuShown(driver, askGpu) ? <DriverRow active={active} state={driver} onState={onDriver} ask={askGpu} /> : null}
      <SetupRows place="drivers" active={active} health={health} onHealth={onHealth} />
      {rows.map(item => item.kind === 'bios' && item.source === 'ASUS' && (item.how !== 'none' || item.advised) ? <BiosRow key={item.id} item={item} /> : <DeviceRow key={item.id} item={item} step={steps[item.id]} disabled={working || !!state?.busy} onInstall={() => void install(item.id)} />)}
      <ControllerRows active={active} />
      <WindowsRow about={about} />
      <Runtimes read={runtimes} />
    </>
  )
}

/** On the title line: the ring until the first read, then Up to date when nothing waits (what waits is the rows themselves). */
function UpdatesWord({ read, waiting }: { read: boolean; waiting: number | null }) {
  if (!read) return <Spinner label="Checking for updates" />
  return waiting === 0 ? <span className="exo-pc-section-count">Up to date</span> : null
}

/** What a run is doing, that a restart finishes it, or what went wrong; else nothing. */
function RunRow({ going, restart, detail, blocked }: { going: boolean; restart: boolean; detail: ReactNode; blocked: string | null }) {
  if (!going && !restart && !blocked) return null
  return <Row label={going ? 'Updating' : restart ? 'Restart to finish' : 'Updates'} detail={detail} error={blocked} />
}

/** A newer feature version of Windows than this PC runs, with its page (Exo never installs Windows; Windows Update keeps the monthly ones). */
function WindowsRow({ about }: { about: PcAbout | null }) {
  const release = about ? releaseOf(about) : null
  if (!release || release.insider || release.upToDate !== false) return null
  return (
    <Row label={release.newest ? `${release.name} ${release.newest}` : release.name}>
      <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('ms-settings:windowsupdate')}>Open Windows Update</button>
    </Row>
  )
}

/** The game runtimes and dependencies (Visual C++, DirectX, .NET and the rest), while one is missing or older. */
function Runtimes({ read }: { read: RuntimeItem[] | null }) {
  const [items, setItems] = useState(read)
  useEffect(() => { if (read) setItems(read) }, [read])
  // Shown only while one is missing or older: runtimes at their newest aren't.
  if (!items || !items.some(item => (item.state === 'missing' && item.bulk) || item.state === 'outdated')) return null
  return (
    <section className="exo-pc-updates-part" aria-labelledby="pc-runtimes-title">
      <h3 id="pc-runtimes-title">Game runtimes</h3>
      <RuntimesPanel items={items} onItems={setItems} />
    </section>
  )
}

type PadFirmware = { id: string; name: string; firmware: NonNullable<ControllerRead['firmware']> }

/**
 * The controllers plugged in, as the Devices room reads them: a DualSense's or an Edge's firmware while older; an
 * update goes through the console or Sony's PC app, so the row's button is Sony's page for it.
 */
function ControllerRows({ active }: { active: boolean }) {
  const [pads, setPads] = useState<PadFirmware[]>([])
  useEffect(() => {
    if (!active) return
    let live = true
    void host.devicesList().then(async answer => {
      const labs = (answer.devices ?? []).filter(device => device.kind === 'controller' && device.lab)
      const read = await Promise.all(labs.map(device => host.controllerRead(device.id).then(r => (r.firmware ? { id: device.id, name: device.name, firmware: r.firmware } : null), () => null)))
      if (live) setPads(read.filter((pad): pad is PadFirmware => !!pad))
    }, (e: unknown) => report(e, 'DriversSection.controllers', { level: 'warn' }))
    return () => { live = false }
  }, [active])
  return (
    <>
      {pads.filter(({ firmware }) => firmware.current === false).map(({ id, name }) => (
        <Row key={id} label={`${name.replace(/\s+Wireless Controller$/i, '')} firmware`}>
          <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('https://www.playstation.com/support/hardware/ps5-controller-update/')}>Open Sony</button>
        </Row>
      ))}
    </>
  )
}

/**
 * The way out of a known problem, in words: "Update to 3645" (the newest, else the version the advice names) or "Go back
 * to 617.42"; null when there's no advice to act on.
 */
function adviceWords(item: DriverEntry) {
  const advice = item.advised
  if (!advice?.to || advice.action === 'skip') return null
  return advice.action === 'back' ? `Go back to ${advice.to}` : `Update to ${item.skipped ? advice.to : item.latest ?? advice.to}`
}

/**
 * A device to show, by name; a known problem with what runs is the one line under it. Its maker's page when only the page has the update
 * (or the way out of a known problem); its own Install for a vendor's app and for an update past a known problem; Update
 * all installs the rest.
 */
function DeviceRow({ item, step, disabled, onInstall }: { item: DriverEntry; step: DriverStep | undefined; disabled: boolean; onInstall: () => void }) {
  const detail = step?.working ? step.working : step?.ok ? trimStop(step.message) || 'Updated' : problem(item)
  return (
    <Row label={rowName(item)} truncateLabel detail={detail} error={step && step.ok === false ? trimStop(step.message) || 'Didn’t update' : null}>
      {deviceAction(item, step?.working ?? null, disabled, onInstall)}
    </Row>
  )
}

/**
 * The BIOS of an ASUS board with a newer release: Exo gets it ready for the board's own EZ Flash (downloaded from ASUS,
 * checked against ASUS's checksum, put on the EFI partition, BitLocker paused for one restart; Windows' one prompt is its
 * confirm), then Restart to update, asked in place, goes into the BIOS, what to do there on the row. Exo never flashes it.
 */
function BiosRow({ item }: { item: DriverEntry }) {
  const [staged, setStaged] = useState<{ version: string; file: string } | null>(null)
  const [busy, setBusy] = useState<'prepare' | 'restart' | null>(null)
  const [share, setShare] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { host.biosUpdate().then(answer => setStaged(answer.staged), (e: unknown) => report(e, 'BiosRow.state')) }, [])
  useEffect(() => onHostEvent('bios.progress', ({ share: next }) => setShare(next)), [])
  const run = async (action: 'prepare' | 'restart') => {
    setBusy(action)
    setError(null)
    setShare(null)
    try {
      const answer = await host.biosUpdate(action)
      setStaged(answer.staged)
      if (!answer.ok) setError(trimStop(answer.message) || 'It didn’t work. Try again.')
    } catch (e) {
      report(e, `BiosRow.${action}`)
      setError('It didn’t work. Try again.')
    }
    setBusy(null)
  }
  const ready = staged && staged.version === (item.latest ?? staged.version)
  const detail = busy === 'prepare' ? <ProgressWords words="Getting the BIOS ready" value={share} />
    : ready ? `${staged.version} is ready. In the BIOS: Tool › ASUS EZ Flash 3, pick ${staged.file} and keep the power on; if it asks to reset fTPM, choose No.`
    : problem(item)
  return (
    <Row label="BIOS" detail={detail} error={error}>
      {ready ? (
        <ConfirmButton className="exo-set-btn is-danger" danger disabled={busy !== null} aria-busy={busy === 'restart'} question="Restart now?" onConfirm={() => void run('restart')}>Restart to update</ConfirmButton>
      ) : (
        <button type="button" className="exo-set-btn" disabled={busy !== null} aria-busy={busy === 'prepare'} onClick={() => void run('prepare')}>
          {`Get ${item.latest ?? 'it'} ready`}
          {busy === 'prepare' ? <Progress edge value={share} label="Getting the BIOS ready" /> : null}
        </button>
      )}
    </Row>
  )
}

/** A device as people say it: no ® or ™ marks; a drive by its short name ("Samsung 990 PRO firmware"). */
function rowName(item: DriverEntry) {
  if (item.kind === 'ssd') return `${driveName(item.name)} firmware`
  return item.name.replace(/\((?:R|TM)\)|®|™/gi, '').replace(/\s+/g, ' ').trim()
}

/** The row's one control: its maker's page, its own Install (carrying the bar while it runs), or the bar alone under Update all. */
function deviceAction(item: DriverEntry, working: string | null, disabled: boolean, onInstall: () => void) {
  const words = adviceWords(item)
  if (item.how === 'page' && item.page) {
    const page = item.page
    return <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(page)}>{words ?? (item.source ? `Open ${item.source}` : 'Open')}</button>
  }
  if (item.how === 'install' && item.app) {
    return (
      <ConfirmButton className="exo-set-btn" disabled={disabled || !!working} aria-busy={!!working} question={`Install ${item.name}?`} onConfirm={onInstall}>
        Install
        {working ? <Progress edge value={null} label={working} /> : null}
      </ConfirmButton>
    )
  }
  // A driver from Windows Update installs elevated: Windows' prompt is its confirm.
  if (item.how === 'install' && words) {
    return (
      <button type="button" className="exo-set-btn" disabled={disabled || !!working} aria-busy={!!working} onClick={onInstall}>
        {words}
        {working ? <Progress edge value={null} label={working} /> : null}
      </button>
    )
  }
  return working ? <Progress value={null} label={working} /> : null
}

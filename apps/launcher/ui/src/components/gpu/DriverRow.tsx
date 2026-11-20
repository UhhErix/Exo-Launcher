import { useCallback, useEffect, useState } from 'react'
import { host, onHostEvent, type DriverProgress, type DriverState } from '../../lib/host'
import { report } from '../../lib/report'
import { setUiPref, uiPref } from '../../lib/uiPrefs'
import { sentenceStart, trimStop } from '../../lib/utils'
import { Row } from '../settings/parts'
import { Choice } from '../settings/Choice'
import { ConfirmDialog } from '../ui/Dialog'
import { driverInstallStarted, driverInstallStopped } from '../shell/DriverInstallOverlay'
import { gpuOffers } from '../../lib/canUse'
import { Progress, ProgressWords } from '../ui/Progress'

type Vendor = NonNullable<DriverState['vendor']>
type Latest = NonNullable<DriverState['latest']>

const NVIDIA_LICENSE = 'https://www.nvidia.com/en-us/drivers/nvidia-license/'
const AMD_LICENSE = 'https://www.amd.com/en/legal/eula.html'

/** What each vendor's update installs and leaves out, and whether Exo puts its profile back after: the consent's lines. */
const VENDOR: Record<Vendor, { name: string; installs: string; leavesOut?: string; restores: boolean }> = {
  nvidia: { name: 'NVIDIA', installs: 'The driver alone, as a clean install, with HDMI/DisplayPort audio while a screen plays sound', leavesOut: 'NVIDIA app, telemetry, ShadowPlay, FrameView, PhysX', restores: true },
  amd: { name: 'AMD', installs: 'The driver alone', leavesOut: 'AMD Software, its overlay, recording and tuning', restores: true },
  intel: { name: 'Intel', installs: 'The driver alone', leavesOut: 'Intel Graphics Software, Driver & Support Assistant, GameAssist, data collection', restores: false },
}

/** AMD with AMD Software's minimal view, where the card runs Fluid Motion Frames (it adds frames only while AMD Software runs). */
const AMD_SOFTWARE = { installs: 'The driver and AMD Software’s minimal view, which Fluid Motion Frames needs', leavesOut: 'AMD Software’s overlay, recording, streaming and tuning' }

const STAGE: Record<DriverProgress['stage'], string> = {
  idle: '', downloading: 'Downloading', unpacking: 'Unpacking', waiting: 'Waiting', installing: 'Installing', done: 'Installed', failed: 'Stopped',
}

function size(bytes: number) {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`
}

/** How much of the download is in, after the stage's word: " · 420 MB of 1.0 GB" when its size is known (the percent follows). */
function downloaded(percent: number, total: number | undefined) {
  return total ? ` · ${size(total * percent / 100)} of ${size(total)}` : ''
}

/** The download's share, for the bar and its percent; null (gliding, no figure) for the steps with no measure. */
function shareOf(progress: DriverProgress | null, starting: boolean) {
  return !starting && progress?.stage === 'downloading' ? progress.percent / 100 : null
}

const WORKING = new Set<DriverProgress['stage']>(['downloading', 'unpacking', 'waiting', 'installing'])

type Branch = 'game' | 'studio'
const BRANCHES = [{ value: 'game', label: 'Game Ready' }, { value: 'studio', label: 'Studio' }] as const
const KIND: Record<Latest['kind'], string> = {
  game: 'Game Ready driver', studio: 'Studio driver', hotfix: 'hotfix driver', security: 'security update driver', workstation: 'NVIDIA RTX Enterprise driver',
  recommended: 'Adrenalin', optional: 'optional Adrenalin', intel: 'Intel driver',
}

/** Game Ready or Studio, kept in Exo's settings (the room reads the driver's state for it). */
export function savedBranch(): Branch {
  return uiPref('driverBranch') === 'studio' ? 'studio' : 'game'
}

/** The row's one line: what it is doing and how much while it works, then how it ended, else installed vs newest. */
function driverDetail(state: DriverState, vendor: Vendor, progress: DriverProgress | null, starting: boolean) {
  const latest = state.latest
  if (starting) return 'Starting the update'
  if (progress && WORKING.has(progress.stage))
    return `${progress.message ?? STAGE[progress.stage]}${progress.stage === 'downloading' ? downloaded(progress.percent, latest?.size) : ''}`
  if (progress?.stage === 'done' || progress?.stage === 'failed') {
    const msg = trimStop(progress.message) || STAGE[progress.stage]
    return state.installed && !msg.includes(state.installed) ? `${state.installed} · ${msg}` : msg
  }
  return restingDetail(state, vendor)
}

/** The line at rest: always includes the installed driver version alongside status, advisory, or update target. */
function restingDetail(state: DriverState, vendor: Vendor) {
  const installed = state.installed
  const why = state.advised?.why
  if (why) return installed ? `${installed} · ${sentenceStart(why)}` : sentenceStart(why)
  if (!installed) return 'No driver'
  if (state.advised?.back) return `${installed} → ${state.advised.back}`
  if (!state.latest && !state.skipped) {
    return `${installed} · ${vendor === 'nvidia' ? "NVIDIA's driver list couldn't be reached" : `${VENDOR[vendor].name}'s newest driver for this card couldn't be found`}`
  }
  if (state.latest) return state.update ? `${installed} → ${state.latest.version}` : `${installed} · Up to date`
  return installed
}

/**
 * What the row's button installs, its words and its consent's question: the version to go back to when what runs has a
 * known problem here (Go back), else the newest (Update, or Reinstall when it's what runs).
 */
function target(state: DriverState, name: string) {
  if (state.advised?.back) {
    const version = state.advised.back
    const latest: Latest = { version, size: 0, released: null, kind: 'game' }
    return { latest, back: true, primary: true, words: `Go back to ${version}`, title: `Go back to ${name} ${version}?` }
  }
  if (!state.latest) return null
  const words = state.update ? 'Update' : 'Reinstall'
  return { latest: state.latest, back: false, primary: !!state.update, words, title: `${words} the ${name} driver?` }
}

/**
 * PC › Drivers, under its Update all line: the installed graphics driver and the
 * vendor's newest for this card. Update (or Reinstall) asks first, then downloads from the vendor and installs. NVIDIA: a
 * clean install of the driver alone (with HDMI/DisplayPort audio while a screen plays sound), Game Ready or Studio. AMD:
 * the driver, with AMD Software's minimal view where Fluid Motion Frames needs it. Intel: the driver without Intel's
 * extras. Exo puts its profile back afterwards. A version with a known problem on this PC is never offered (the line
 * names it and why); when it's what runs, the line says why and the button goes back where the advice says to. The room holds the state; the row reads it
 * again when the branch changes, when a game or a call that blocked it ends, and when an install ends. ask: Update all
 * bumps it to open the consent, the same as Update.
 */
export function DriverRow({ active, state, onState, ask }: { active: boolean; state: DriverState | null; onState: (next: DriverState) => void; ask?: number }) {
  // Progress from the host's events; until the first one, what the last read said.
  const [heard, setHeard] = useState<DriverProgress | null>(null)
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [branch, setBranch] = useState<Branch>(savedBranch)
  // Between Install and the host's first progress event, so the button can't be pressed again.
  const [starting, setStarting] = useState(false)
  const progress = heard ?? state?.progress ?? null

  useEffect(() => { if (ask) setAsking(true) }, [ask])

  const load = useCallback(async (pick: Branch = branch) => {
    try { onState(await host.gpuDriver('state', pick)) } catch (e) { report(e, 'DriverRow.state') }
  }, [branch, onState])

  // "Close your game first" and "Leave your Discord call first" go away when the game or the call does.
  useEffect(() => active && state?.blocked ? onHostEvent('game.runState', () => void load()) : undefined, [active, state?.blocked, load])
  useEffect(() => active && state?.blocked ? onHostEvent('discord.voice', () => void load()) : undefined, [active, state?.blocked, load])
  useEffect(() => onHostEvent('gpu.driver', next => {
    setStarting(false)
    setHeard(next)
    if (next.stage === 'done' || next.stage === 'failed') void load()
  }), [load])

  const install = async (back: boolean, latest: Latest) => {
    setAsking(false)
    setError(null)
    setStarting(true)
    // The driver screen covers Exo from the press until the driver is in and Exo's settings are back.
    if (state?.vendor) driverInstallStarted(VENDOR[state.vendor].name, latest.version, latest.size)
    try {
      const answer = await host.gpuDriver(back ? 'goback' : 'update', branch)
      if (!answer.ok) {
        setStarting(false)
        driverInstallStopped()
        setError(trimStop(answer.message) || 'The update didn’t start. Try again.')
      }
    } catch (e) {
      report(e, 'DriverRow.update')
      setStarting(false)
      driverInstallStopped()
      setError('The update didn’t start. Try again.')
    }
  }

  const vendor = state?.vendor
  if (!state || !vendor) return null
  const working = starting || (progress !== null && WORKING.has(progress.stage))
  const goal = target(state, VENDOR[vendor].name)
  const detail = driverDetail(state, vendor, progress, starting)
  const share = working ? shareOf(progress, starting) : null

  return (
    <>
      {/* NVIDIA's Game Ready or Studio sits beside Update: the driver it installs. */}
      <Row label={`${VENDOR[vendor].name} driver`} detail={detail || share !== null ? <ProgressWords words={detail} value={share} /> : undefined} error={rowError(error, working, state.blocked)}>
        {gpuOffers(vendor, state.workstation).driverType && (
          <Choice label="Driver type" value={branch} choices={BRANCHES} disabled={working}
            onChange={value => {
              const next: Branch = value === 'studio' ? 'studio' : 'game'
              setUiPref('driverBranch', next)
              setBranch(next)
              void load(next)
            }} />
        )}
        {(working || goal) && (
          <DriverButton goal={goal} working={working} share={share} label={detail} disabled={!!state.blocked} onPress={() => setAsking(true)} />
        )}
      </Row>
      {asking && goal && <DriverConsent vendor={vendor} software={!!state.software} latest={goal.latest} title={goal.title} onCancel={() => setAsking(false)} onInstall={() => void install(goal.back, goal.latest)} />}
    </>
  )
}

/** The row's red line: what failed, else (while nothing runs) what blocks the update. */
function rowError(error: string | null, working: boolean, blocked: string | null | undefined) {
  return error ?? (working ? null : blocked ?? null)
}

/** Update, Reinstall or Go back. While the install runs it keeps its words and carries the bar: the download's share, else gliding. */
function DriverButton({ goal, working, share, label, disabled, onPress }: {
  goal: ReturnType<typeof target>
  working: boolean
  /** The download's share, null while a step has no measure. */
  share: number | null
  /** What it is doing and how much, for the bar. */
  label: string
  disabled: boolean
  onPress: () => void
}) {
  return (
    <button type="button" className={`exo-set-btn${goal?.primary ? ' is-primary' : ''}`} disabled={working || disabled} aria-busy={working} onClick={onPress}>
      {goal?.words ?? 'Update'}
      {working ? <Progress edge value={share} label={label} /> : null}
    </button>
  )
}

/** What the update downloads, installs and leaves out, and the vendor's license, before anything happens. */
function DriverConsent({ vendor, software, latest, title, onCancel, onInstall }: {
  vendor: Vendor
  software: boolean
  latest: Latest
  title: string
  onCancel: () => void
  onInstall: () => void
}) {
  const { name, restores } = VENDOR[vendor]
  const { installs, leavesOut } = vendor === 'amd' && software ? AMD_SOFTWARE : VENDOR[vendor]
  const link = (url: string, label: string) => <button type="button" className="exo-set-link" onClick={() => void host.openUrl(url)}>{label}</button>
  const version = vendor === 'amd' ? `AMD Software: Adrenalin Edition ${latest.version}${latest.kind === 'optional' ? ' (optional)' : ''}`
    : vendor === 'intel' ? `Intel's graphics driver ${latest.version}, driver only`
      : `NVIDIA ${KIND[latest.kind]} ${latest.version}, driver only`
  return (
    <ConfirmDialog title={title} action="Install" form onConfirm={onInstall} onClose={onCancel}>
      <p>{version}</p>
      <ul className="exo-graphics-import-list">
        <li><span>Downloads from {name}</span><span className="exo-graphics-import-value">{latest.size ? size(latest.size) : 'About 1 GB'}</span></li>
        <li><span>Installs</span><span className="exo-graphics-import-value">{installs}</span></li>
        {leavesOut && <li><span>Leaves out</span><span className="exo-graphics-import-value">{leavesOut}</span></li>}
        {restores && <li><span>Afterwards</span><span className="exo-graphics-import-value">Exo puts your GPU profile back</span></li>}
      </ul>
      <p className="exo-graphics-import-extra">
        Installing means you accept {vendor === 'nvidia' ? link(NVIDIA_LICENSE, "NVIDIA's license")
          : vendor === 'amd' ? link(AMD_LICENSE, "AMD's license")
            : <>the license on {latest.page ? link(latest.page, "Intel's download page") : "Intel's download page"}</>}.
      </p>
    </ConfirmDialog>
  )
}

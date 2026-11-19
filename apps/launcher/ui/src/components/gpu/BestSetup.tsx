import { useState } from 'react'
import { CHECK_PATH, Close } from '../../brand/icons'
import { host, type DrivesState, type GpuProfile, type HdrState, type LauncherSettings, type PcHealth } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { ErrorNote } from '../ui/ErrorNote'
import { Spinner } from '../ui/Spinner'
import { SectionActions } from './SectionHead'

export type BestKey = 'power' | 'profile' | 'network' | 'drives' | 'hdr' | 'frameGen'
export type BestStep = { key: BestKey; what: string; done: boolean; run: () => Promise<string | null> }

/**
 * Everything Exo sets for this PC, each done or still to set, from what was read. Each is something the PC room offers
 * on its own row too. Only what this PC has: a power plan to change, a driver profile for its card, the Network group
 * (the adapters' own settings, Cloudflare DNS encrypted, Windows' TCP defaults: each with its switch, all in one Windows
 * prompt), drives with write-cache flushing off or Windows' weekly optimize unable to run (one step, one Windows
 * prompt for both; flushing finishes with a restart), a screen that shows HDR well (on a dim one HDR usually looks worse,
 * so it stays the user's choice), and frame generation in
 * every game without anti-cheat it works in, on a card that gains from it (DlssSwapService.FrameGenAuto.cs; PC ›
 * Graphics turns it off again, and a game's own Off keeps that game out).
 */
export function bestSteps(
  profile: GpuProfile | null, health: PcHealth | null, screen: HdrState | null, settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>, drives: DrivesState | null,
): BestStep[] {
  const steps: BestStep[] = []
  const power = health?.items.find(item => item.key === 'power')
  if (power) {
    steps.push({ key: 'power', what: `Exo’s power plan for ${power.cpu ?? 'this PC'}`, done: !!power.applied, run: async () => {
      const next = await host.systemHealth('apply')
      return next.ok ? null : trimStop(next.message) || 'Not changed'
    } })
  }
  if (profile?.ok && profile.vendor && ['applied', 'ready', 'changed'].includes(profile.status)) {
    const maker = profile.vendor === 'nvidia' ? 'NVIDIA' : profile.vendor === 'amd' ? 'Radeon' : 'Intel'
    steps.push({ key: 'profile', what: `Exo’s ${maker} driver profile`, done: profile.status === 'applied', run: async () => {
      const next = await host.gpuProfile('apply')
      return next.ok ? null : trimStop(next.message) || 'Not changed'
    } })
  }
  const network = health?.items.find(item => item.key === 'network')
  if (network) {
    // The Network group (NetworkGroup.tsx): every item the player keeps on, in one Windows prompt.
    steps.push({ key: 'network', what: 'Exo’s network settings', done: !!network.applied, run: async () => {
      const next = await host.systemHealth('network')
      return next.ok ? null : trimStop(next.message) || 'Not changed'
    } })
  }
  const drivesStep = driveStep(drives)
  if (drivesStep) steps.push(drivesStep)
  if (screen?.supported && screen.verdict !== 'weak' && settings) {
    steps.push({ key: 'hdr', what: 'HDR while an HDR game runs', done: settings.hdrForGames === true, run: async () => await onSave({ hdrForGames: true }) ? null : 'Not saved' })
  }
  const frameGen = frameGenStep(settings, onSave)
  if (frameGen) steps.push(frameGen)
  return steps
}

/** Frame generation in every game without anti-cheat it works in, on a card that gains from it. */
function frameGenStep(settings: LauncherSettings | null, onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>): BestStep | null {
  if (!settings?.frameGenEverywhereCard) return null
  return { key: 'frameGen', what: 'Frame generation in games without anti-cheat', done: settings.frameGenEverywhere === true,
    run: async () => await onSave({ frameGenEverywhere: true }) ? null : 'Not saved' }
}



/** The drives' step: write caching, write-cache flushing and weekly optimize together (one Windows prompt), named for what this PC has. */
function driveStep(drives: DrivesState | null): BestStep | null {
  const fixes = (['cache', 'flush', 'optimize'] as const).map(key => drives?.fixes.find(fix => fix.key === key)).filter(fix => fix != null)
  if (fixes.length === 0) return null
  const names = fixes.map(fix => fix.key === 'cache' ? 'write caching' : fix.key === 'flush' ? 'write-cache flushing' : 'weekly optimize')
  const listed = names.length === 1 ? `${names[0]} for your drives` : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
  const what = listed[0].toUpperCase() + listed.slice(1)
  return { key: 'drives', what: fixes.some(fix => fix.restart) ? `${what}, after a restart` : what, done: fixes.every(fix => fix.applied), run: async () => {
    const next = await host.systemDrives('all')
    return next.ok ? null : trimStop(next.message) || 'Not changed'
  } }
}

/** Runs the steps still to set, one after another; `onStep` hears which one runs. Each failed step's problem comes back by its key. */
export async function runBest(steps: BestStep[], onStep: (key: BestKey) => void): Promise<Partial<Record<BestKey, string>>> {
  const failed: Partial<Record<BestKey, string>> = {}
  for (const step of steps.filter(item => !item.done)) {
    onStep(step.key)
    try {
      const problem = await step.run()
      if (problem) failed[step.key] = problem
    } catch (e) {
      report(e, `BestSetup.${step.key}`)
      failed[step.key] = 'Not changed'
    }
  }
  return failed
}

/** A step's check: drawn in as it lands, simply there when it was already set. */
export function BestCheck({ landed }: { landed: boolean }) {
  return (
    <svg className={`exo-motion-check exo-best-check${landed ? ' is-held' : ' is-static'}`} viewBox="0 0 24 24" aria-hidden="true">
      <path d={CHECK_PATH} pathLength="1" />
    </svg>
  )
}

/**
 * PC › Best settings: everything Exo sets for this PC, each named, with a red X until it is set and a green check once it
 * is. Use best settings, on the menu's title line while any is left, runs them in turn, the one at work turning its
 * ring. The rest (display modes, BIOS, Windows settings) stay where they ask first. First run shows the same steps
 * (onboarding/PcStep.tsx).
 */
export function BestSetup({ profile, health, drives, screen, settings, onSave, onRead }: {
  profile: GpuProfile | null
  health: PcHealth | null
  drives: DrivesState | null
  screen: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  /** Reads the profile and the setup again, once the steps ran. */
  onRead: () => Promise<void>
}) {
  const [running, setRunning] = useState<BestKey | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The steps set by this press: their checks draw in.
  const [landed, setLanded] = useState<ReadonlySet<BestKey>>(() => new Set())

  const steps = bestSteps(profile, health, screen, settings, onSave, drives)

  const run = async () => {
    if (busy) return
    setBusy(true)
    setError(null)
    const failed = await runBest(steps, setRunning)
    setRunning(null)
    await onRead()
    setLanded(new Set(steps.filter(step => !step.done && !failed[step.key]).map(step => step.key)))
    const said = steps.filter(step => failed[step.key]).map(step => `${step.what}: ${failed[step.key]}`)
    setError(said.length ? `${said.join('. ')}. Try again.` : null)
    setBusy(false)
  }

  if (!profile || !health || steps.length === 0) return null
  return (
    <section className="exo-pc-best" aria-label="Steps">
      {steps.some(step => !step.done) ? (
        <SectionActions>
          <button type="button" className="exo-set-btn is-primary" disabled={busy} aria-busy={busy} onClick={() => void run()}>Use best settings</button>
        </SectionActions>
      ) : null}
      <ul className="exo-pc-best-steps">
        {steps.map(step => (
          <li key={step.key}>
            <span>{step.what}</span>
            {running === step.key ? <Spinner label={`Setting ${step.what}`} />
              : step.done ? <BestCheck landed={landed.has(step.key)} />
                : <span className="exo-best-miss" role="img" aria-label="Not set"><Close size={14} /></span>}
          </li>
        ))}
      </ul>
      {error ? <ErrorNote message={error} /> : null}
    </section>
  )
}

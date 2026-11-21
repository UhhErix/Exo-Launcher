import { host, type ExoAiState, type HdrState, type LauncherSettings, type TuningItem } from '../../lib/host'
import { report } from '../../lib/report'
import { formatSize, trimStop } from '../../lib/utils'
import type { PcState } from './usePcState'

type AutoTuneStageId = 'cooling' | 'graphics' | 'system' | 'bloat' | 'cleanup' | 'drivers'
type AutoTuneStageStatus = 'idle' | 'active' | 'done' | 'warn'

export type AutoTuneStageState = {
  id: AutoTuneStageId
  label: string
  status: AutoTuneStageStatus
  detail: string
}

export type BloatAppInfo = {
  id: string
  name: string
}

const isPlaybookItem = (item: TuningItem) => item.key !== 'vrr' && item.key !== 'mouseAccel' && !item.off && !item.blocked

function hasInterruptsWork(state: PcState): boolean {
  const interrupts = state.interrupts
  if (!interrupts || interrupts.restart) return false
  return Boolean(interrupts.reset || interrupts.devices.some(d => d.plan === 'change'))
}

function hasGraphicsWork(state: PcState, hdr: HdrState | null, settings: LauncherSettings | null): boolean {
  const profileTodo = state.profile?.status === 'ready' || state.profile?.status === 'changed'
  const screenTodo = (state.display?.displays ?? []).some(screen => !screen.isBest)
  const hdrTodo = Boolean(hdr?.supported && hdr.verdict !== 'weak' && settings?.hdrForGames !== true)
  const fgTodo = Boolean(settings?.frameGenEverywhereCard && settings.frameGenEverywhere !== true)
  return profileTodo || screenTodo || hdrTodo || fgTodo
}

function hasSystemWork(state: PcState): boolean {
  const sysTodo = (state.tuning?.items ?? []).some(i => isPlaybookItem(i) && !i.on)
  const netTodo = state.health?.items.find(i => i.key === 'network')?.applied === false
  const driveTodo = (state.drives?.fixes ?? []).some(f => !f.applied)
  return sysTodo || netTodo || driveTodo || hasInterruptsWork(state)
}

function stageReadySummary(
  state: PcState,
  exoAi: ExoAiState | null,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  bloatApps: readonly BloatAppInfo[],
): Record<AutoTuneStageId, string> {
  const powerDone = Boolean(state.health?.items.find(item => item.key === 'power')?.applied)
  const fanReady = Boolean(exoAi?.fans.supported)
  const graphicsPending = hasGraphicsWork(state, hdr, settings)
  const systemPending = hasSystemWork(state)
  const driverVersion = state.driver?.update ? state.driver.latest?.version : null
  const bloatCount = bloatApps.length
  const bloatSummary = bloatCount === 0
    ? 'No bloatware found'
    : `Remove ${bloatCount} preinstalled bloat app${bloatCount === 1 ? '' : 's'}`

  return {
    cooling: powerDone && fanReady
      ? 'Exo power plan, 0.5 ms timer & Autopilot active'
      : 'Exo power plan, 0.5 ms timer, standby purge & Autopilot',
    graphics: graphicsPending
      ? 'Driver profile, refresh rate, HDR & frame generation'
      : 'Graphics profile and display at best defaults',
    system: systemPending
      ? 'Windows 11 gaming tweaks, MSI interrupts & network'
      : 'Windows 11 tweaks, MSI interrupts & network optimal',
    bloat: bloatSummary,
    cleanup: 'Windows Update, browser, crash dump & launcher caches',
    drivers: driverVersion
      ? `Driver update ready (${driverVersion})`
      : 'Graphics & chipset drivers up to date',
  }
}

export function initialAutoTuneStages(
  state: PcState,
  exoAi: ExoAiState | null,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  bloatApps: readonly BloatAppInfo[] = [],
): AutoTuneStageState[] {
  const detail = stageReadySummary(state, exoAi, hdr, settings, bloatApps)
  return [
    { id: 'cooling', label: 'Power & Autopilot', status: 'idle', detail: detail.cooling },
    { id: 'graphics', label: 'Graphics & display', status: 'idle', detail: detail.graphics },
    { id: 'system', label: 'System, interrupts & network', status: 'idle', detail: detail.system },
    { id: 'bloat', label: 'Preinstalled apps', status: 'idle', detail: detail.bloat },
    { id: 'cleanup', label: 'Temporary caches', status: 'idle', detail: detail.cleanup },
    { id: 'drivers', label: 'Drivers & updates', status: 'idle', detail: detail.drivers },
  ]
}

export async function runAutoTuneStage(
  id: AutoTuneStageId,
  state: PcState,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>,
  onRefresh: () => Promise<void>,
  bloatApps: readonly BloatAppInfo[] = [],
): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  try {
    if (id === 'cooling') return await runCoolingStage(state)
    if (id === 'graphics') return await runGraphicsStage(state, hdr, settings, onSave)
    if (id === 'system') return await runSystemStage(state)
    if (id === 'bloat') return await runBloatStage(bloatApps)
    if (id === 'cleanup') return await runCleanupStage()
    return await runDriversStage(state, onRefresh)
  } catch (e) {
    report(e, `AutoTuneOverlay.${id}`)
    return { status: 'warn', detail: 'Step could not finish' }
  }
}

async function runCoolingStage(state: PcState): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  const ai = await host.exoAiSet({ runNow: true })
  const power = state.health?.items.find(item => item.key === 'power')
  if (power && !power.applied) {
    const hp = await host.systemHealth('apply')
    if (!hp.ok && hp.message) return { status: 'warn', detail: trimStop(hp.message) }
  }
  if (!ai.ok && ai.message) return { status: 'warn', detail: trimStop(ai.message) }
  return {
    status: 'done',
    detail: ai.fans.supported ? 'Exo power plan, Autopilot & automatic GPU fans active' : 'Exo power plan & Autopilot active',
  }
}

async function applyScreenModes(state: PcState, problems: string[]) {
  for (const screen of state.display?.displays ?? []) {
    if (screen.isBest) continue
    const next = await host.gpuDisplay('set', { display: screen.id, best: true })
    if (!next.ok && next.message) problems.push(trimStop(next.message))
  }
}

async function applyGraphicPrefs(
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>,
  problems: string[],
) {
  if (hdr?.supported && hdr.verdict !== 'weak' && settings?.hdrForGames !== true) {
    if (!await onSave({ hdrForGames: true })) problems.push('HDR preference not saved')
  }
  if (settings?.frameGenEverywhereCard && settings.frameGenEverywhere !== true) {
    if (!await onSave({ frameGenEverywhere: true })) problems.push('Frame generation setting not saved')
  }
}

async function runGraphicsStage(
  state: PcState,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>,
): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  const problems: string[] = []
  if (state.profile?.ok && (state.profile.status === 'ready' || state.profile.status === 'changed')) {
    const next = await host.gpuProfile('apply')
    if (!next.ok && next.message) problems.push(trimStop(next.message))
  }
  await applyScreenModes(state, problems)
  await applyGraphicPrefs(hdr, settings, onSave, problems)
  return problems.length > 0
    ? { status: 'warn', detail: problems.join(' · ') }
    : { status: 'done', detail: 'Driver profile, best refresh & output scaling applied' }
}

async function runSystemStep<T extends { ok: boolean; message?: string | null }>(
  run: () => Promise<T>,
  problems: string[],
  hasRestart: (res: T) => boolean,
): Promise<boolean> {
  const next = await run()
  if (!next.ok && next.message) problems.push(trimStop(next.message))
  return hasRestart(next)
}

async function runSystemStage(state: PcState): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  const problems: string[] = []
  let needsRestart = false
  if (state.tuning?.items.some(i => isPlaybookItem(i) && !i.on)) {
    needsRestart = await runSystemStep(() => host.systemTuning('playbook'), problems, res => res.restart) || needsRestart
  }
  if (hasInterruptsWork(state)) {
    const action = state.interrupts?.reset ? 'reapply' : 'apply'
    needsRestart = await runSystemStep(() => host.systemInterrupts(action), problems, res => res.restart) || needsRestart
  }
  if (state.health?.items.find(i => i.key === 'network')?.applied === false) {
    await runSystemStep(() => host.systemHealth('network'), problems, () => false)
  }
  if (state.drives?.fixes.some(f => !f.applied)) {
    needsRestart = await runSystemStep(() => host.systemDrives('all'), problems, res => res.fixes.some(f => f.restart)) || needsRestart
  }
  if (problems.length > 0) return { status: 'warn', detail: problems.join(' · ') }
  return {
    status: 'done',
    detail: needsRestart
      ? 'Full deep Windows 11 playbook, interrupts & network applied (restart to finish)'
      : 'Full deep Windows 11 playbook, interrupts, services & network optimal',
  }
}

async function runBloatStage(
  bloatApps: readonly BloatAppInfo[],
): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  if (bloatApps.length === 0) return { status: 'done', detail: 'No bloat Store apps installed' }
  const plan = await host.appsPackagesPlan(bloatApps.map(b => b.id), false)
  if (!plan.ok || !plan.token) {
    return { status: 'done', detail: plan.message ? trimStop(plan.message) : 'Nothing to remove' }
  }
  const removal = await host.appsPackagesRemove(plan.token)
  if (!removal.ok && removal.removed === 0) {
    return { status: 'warn', detail: removal.message ? trimStop(removal.message) : 'Bloat removal could not finish' }
  }
  return {
    status: 'done',
    detail: `${removal.removed} bloat app${removal.removed === 1 ? '' : 's'} removed (restore anytime in Apps)`,
  }
}

async function runCleanupStage(): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  const scan = await host.junkScan()
  const ids = scan.items.filter(item => item.id !== 'bin' && item.bytes > 0).map(item => item.id)
  if (ids.length === 0) return { status: 'done', detail: 'Caches already clean (cookies & logins kept)' }
  const clean = await host.junkClean(ids)
  if (!clean.ok) return { status: 'warn', detail: clean.message ? trimStop(clean.message) : 'Cleanup could not finish' }
  const freed = clean.bytes > 0 ? `${formatSize(clean.bytes)} freed` : 'Temp and browser caches checked'
  if (clean.message) {
    const suffix = clean.bytes > 0 ? 'in-use files skipped (cookies & logins kept)' : 'caches clean · in-use files skipped'
    return { status: 'done', detail: `${freed} · ${suffix}` }
  }
  return { status: 'done', detail: `${freed} across ${clean.files} files (cookies & logins kept)` }
}

async function runDriversStage(state: PcState, onRefresh: () => Promise<void>): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  await onRefresh()
  const chipset = state.health?.items.find(item => item.key === 'chipset')
  const hasGpuUpdate = !!(state.driver?.update && state.driver.latest?.version)
  const hasChipsetUpdate = !!(chipset && !chipset.applied && !chipset.current && !chipset.held)
  if (hasGpuUpdate || hasChipsetUpdate) {
    return { status: 'done', detail: 'Driver updates ready in Updates' }
  }
  return { status: 'done', detail: 'Graphics & chipset drivers up to date' }
}

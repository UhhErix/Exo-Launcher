import { host, type ExoAiState, type HdrState, type LauncherSettings } from '../../lib/host'
import { report } from '../../lib/report'
import { formatSize, trimStop } from '../../lib/utils'
import type { PcState } from './PcRoom'

export type AutoTuneStageId = 'cooling' | 'graphics' | 'system' | 'cleanup' | 'drivers'
export type AutoTuneStageStatus = 'idle' | 'active' | 'done' | 'warn'
export type AutoTuneStageState = {
  id: AutoTuneStageId
  label: string
  status: AutoTuneStageStatus
  detail: string
}

function stageReadySummary(
  state: PcState,
  exoAi: ExoAiState | null,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
): Record<AutoTuneStageId, string> {
  const powerDone = Boolean(state.health?.items.find(item => item.key === 'power')?.applied)
  const fanReady = Boolean(exoAi?.fans.supported)
  const graphicsPending = hasGraphicsWork(state, hdr, settings)
  const systemPending = hasSystemWork(state)
  const driverVersion = state.driver?.update ? state.driver.latest?.version : null
  return {
    cooling: powerDone && fanReady
      ? 'Exo power plan & automatic GPU fan curve active'
      : 'Apply power plan, Autopilot & automatic NVML GPU fan curve',
    graphics: graphicsPending
      ? 'Optimize driver profile, best screen mode, HDR & frame generation'
      : 'Graphics profile and display output at optimal defaults',
    system: systemPending
      ? 'Apply NVIDIA VRR LFC multiplier, reversible ad privacy, network & drive health'
      : 'Windows defaults, NVIDIA VRR LFC & network already optimal',
    cleanup: 'Clear disposable browser, launcher & Windows caches (keeps cookies & logins)',
    drivers: driverVersion
      ? `Update ready (${driverVersion}) in PC Updates`
      : 'Check graphics & chipset driver status',
  }
}

function hasGraphicsWork(state: PcState, hdr: HdrState | null, settings: LauncherSettings | null): boolean {
  const profileTodo = state.profile?.status === 'ready' || state.profile?.status === 'changed'
  const screenTodo = (state.display?.displays ?? []).some(screen => !screen.isBest)
  const hdrTodo = Boolean(hdr?.supported && hdr.verdict !== 'weak' && settings?.hdrForGames !== true)
  const fgTodo = Boolean(settings?.frameGenEverywhereCard && settings.frameGenEverywhere !== true)
  return profileTodo || screenTodo || hdrTodo || fgTodo
}

function hasSystemWork(state: PcState): boolean {
  const sysTodo = (state.tuning?.items ?? []).some(i => !i.optIn && !i.off && !i.blocked && !i.on)
  const netTodo = state.health?.items.find(i => i.key === 'network')?.applied === false
  const driveTodo = (state.drives?.fixes ?? []).some(f => !f.applied)
  return sysTodo || netTodo || driveTodo
}

export function initialAutoTuneStages(
  state: PcState,
  exoAi: ExoAiState | null,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
): AutoTuneStageState[] {
  const detail = stageReadySummary(state, exoAi, hdr, settings)
  return [
    { id: 'cooling', label: 'Cooling, power & Exo AI', status: 'idle', detail: detail.cooling },
    { id: 'graphics', label: 'Graphics, display & frame delivery', status: 'idle', detail: detail.graphics },
    { id: 'system', label: 'Windows, privacy, VRR LFC & network', status: 'idle', detail: detail.system },
    { id: 'cleanup', label: 'Browser, launcher & temp caches', status: 'idle', detail: detail.cleanup },
    { id: 'drivers', label: 'Graphics & chipset drivers', status: 'idle', detail: detail.drivers },
  ]
}

export async function runAutoTuneStage(
  id: AutoTuneStageId,
  state: PcState,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>,
  onRefresh: () => Promise<void>,
): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  try {
    if (id === 'cooling') return await runCoolingStage(state)
    if (id === 'graphics') return await runGraphicsStage(state, hdr, settings, onSave)
    if (id === 'system') return await runSystemStage(state)
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
    detail: ai.fans.supported ? 'Exo power plan, Autopilot & automatic NVML fans active' : 'Exo power plan & Autopilot active',
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

async function runSystemStage(state: PcState): Promise<{ status: AutoTuneStageStatus; detail: string }> {
  const problems: string[] = []
  let needsRestart = false
  const pendingTuning = state.tuning?.items.some(i => !i.optIn && !i.off && !i.blocked && !i.on) ?? false
  if (pendingTuning) {
    const next = await host.systemTuning('apply')
    if (next.restart) needsRestart = true
    if (!next.ok && next.message) problems.push(trimStop(next.message))
  }
  const network = state.health?.items.find(i => i.key === 'network')
  if (network && !network.applied) {
    const next = await host.systemHealth('network')
    if (!next.ok && next.message) problems.push(trimStop(next.message))
  }
  if (state.drives?.fixes.some(f => !f.applied)) {
    const next = await host.systemDrives('all')
    if (next.fixes.some(f => f.restart)) needsRestart = true
    if (!next.ok && next.message) problems.push(trimStop(next.message))
  }
  if (problems.length > 0) return { status: 'warn', detail: problems.join(' · ') }
  return {
    status: 'done',
    detail: needsRestart ? 'Windows privacy, VRR LFC & network applied (restart to finish)' : 'Windows privacy, VRR LFC, network & drives optimal',
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
    return { status: 'done', detail: 'Driver updates available in PC Updates' }
  }
  return { status: 'done', detail: 'Graphics & chipset drivers up to date' }
}

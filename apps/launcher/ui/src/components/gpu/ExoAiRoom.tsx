import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Alert, Bolt, Check } from '../../brand/icons'
import {
  host,
  type AutoTune,
  type CompressList,
  type DrivesState,
  type ExoAiState,
  type Game,
  type GpuProfile,
  type HdrState,
  type LauncherSettings,
  type PcHealth,
  type Tuning,
} from '../../lib/host'
import { useHdrState } from '../../lib/hdr'
import { pageGet, pageSet } from '../../lib/pageStore'
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { ShadersBoard } from '../shaders/ShadersBoard'
import { Spinner } from '../ui/Spinner'
import { useAutopilot } from './Autopilot'
import { AutoTuneRows, useTuneReads } from './AutoTune'
import { AutoTuneCinema } from './AutoTuneCinema'
import { bestSteps } from './BestSetup'
import { useCompressList } from './CompressGames'
import { DriversSection } from './DriversSection'
import { GpuProcessesSection } from './GpuProcessesSection'
import { useExoAi } from './ExoAiPanel'
import { useAppsSummary } from './PcApps'
import { ThisPc, type PcPartMenu } from './PcOverview'
import { PcPartMenus, StorageChip } from './PcPartMenus'
import { SectionSlot } from './SectionHead'
import { StorageRoom } from './StorageRoom'
import { SetupRows } from './ThisPcTab'
import type { PcLink } from './pcSections'
import { usePcState, type PcState } from './usePcState'
import {
  initialAutoTuneStages,
  runAutoTuneStage,
  type AutoTuneStageState,
  type BloatAppInfo,
} from './autoTuneStages'
import '../../styles/pc-room.css'
import '../../styles/auto-tune-overlay.css'
import '../../styles/exo-ai-room.css'

const AUTOTUNE_DONE_KEY = 'exo.autoTune.completedAt'

function readCompletedAt(): number | null {
  try {
    const raw = pageGet(AUTOTUNE_DONE_KEY)
    const parsed = raw ? Number(raw) : NaN
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null
  } catch {
    return null
  }
}

function writeCompletedAt(now: number) {
  try { pageSet(AUTOTUNE_DONE_KEY, String(now)) } catch { return }
}

function countPendingAutoTune(
  state: PcState,
  hdr: HdrState | null,
  settings: LauncherSettings | null,
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean> = async () => false,
): number | null {
  if (!state.profile || !state.health) return null
  const steps = bestSteps(state.profile, state.health, hdr, settings, onSave, state.drives, state.tuning)
    .filter(s => !s.done).length
  const screens = state.display?.displays.filter(screen => !screen.isBest).length ?? 0
  return steps + screens
}

function anyRestartRequired(stages: readonly AutoTuneStageState[], state: PcState): boolean {
  if (stages.some(s => s.detail.toLowerCase().includes('restart'))) return true
  if (state.interrupts?.restart) return true
  return Boolean(state.tuning?.items.some(item => item.restart))
}

function StageMarker({ status, label }: { status: AutoTuneStageState['status']; label: string }) {
  if (status === 'active') return <span className="exo-at-stage-marker"><Spinner label={label} /></span>
  if (status === 'done') return <span className="exo-at-stage-marker"><Check size={15} /></span>
  if (status === 'warn') return <span className="exo-at-stage-marker"><Alert size={15} /></span>
  return <span className="exo-at-stage-marker"><span className="exo-at-stage-dot" /></span>
}

function LiveHeaderStatus({ exoAi, tune, autopilotOn }: { exoAi: ExoAiState | null; tune: AutoTune | null; autopilotOn: boolean }) {
  const gaming = exoAi?.gaming.active ?? false
  const testing = tune?.parts.some(part => part.state === 'testing') ?? false
  let text = autopilotOn ? 'Autopilot active' : 'Autopilot paused'
  if (gaming) text = exoAi?.gaming.gameTitle ? `Boosting ${exoAi.gaming.gameTitle}` : 'Boosting game'
  else if (testing) text = 'Calibrating hardware'

  return (
    <span className={cn('exo-ai-live', (gaming || testing) && 'is-active')}>
      <span className="exo-ai-live-dot" aria-hidden="true" />
      <span>{text}</span>
    </span>
  )
}

function PlaybookBadge({
  running,
  complete,
  pending,
}: {
  running: boolean
  complete: boolean
  pending: number | null
}) {
  if (running) return <span className="exo-ai-badge is-live">Optimizing…</span>
  if (complete) return <span className="exo-ai-badge is-good">Auto Tune complete</span>
  if (pending != null && pending > 0) return <span className="exo-ai-badge is-pending">{pending} ready</span>
  return null
}

function RightSection({
  id,
  title,
  children,
}: {
  id: 'drivers' | 'gpu-usage' | 'shaders'
  title: string
  children: ReactNode
}) {
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  return (
    <section className={cn('exo-system-panel exo-pc-section', `is-${id}`)} data-pc-part={id} aria-labelledby={`exo-ai-${id}-title`}>
      <header className="exo-pc-section-head">
        <h2 id={`exo-ai-${id}-title`}>{title}</h2>
        <div ref={setSlot} className="exo-pc-section-actions" />
      </header>
      <SectionSlot.Provider value={slot}>
        <div className="exo-pc-pane">{children}</div>
      </SectionSlot.Provider>
    </section>
  )
}

function useAutoTunePipeline({
  active,
  state,
  exoAi,
  hdr,
  settings,
  onSave,
  refreshAll,
}: {
  active: boolean
  state: PcState
  exoAi: ExoAiState | null
  hdr: HdrState | null
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  refreshAll: () => Promise<void>
}) {
  const abortedRef = useRef(false)
  const [running, setRunning] = useState(false)
  const [finished, setFinished] = useState(false)
  const [completedAt, setCompletedAt] = useState<number | null>(() => readCompletedAt())
  const [bloatApps, setBloatApps] = useState<BloatAppInfo[]>([])
  const [stages, setStages] = useState<AutoTuneStageState[]>(() =>
    initialAutoTuneStages(state, exoAi, hdr, settings),
  )

  useEffect(() => {
    if (!active) return
    let alive = true
    host.appsPackages().then(
      res => {
        if (!alive || !res.ok) return
        setBloatApps(res.items.filter(item => item.forMe && item.bloat).map(item => ({ id: item.id, name: item.name })))
      },
      (e: unknown) => report(e, 'ExoAiRoom.packages', { level: 'warn' }),
    )
    return () => { alive = false }
  }, [active])

  useEffect(() => {
    if (!active || running || finished) return
    setStages(initialAutoTuneStages(state, exoAi, hdr, settings, bloatApps))
  }, [active, running, finished, state, exoAi, hdr, settings, bloatApps])

  useEffect(() => () => { abortedRef.current = true }, [])

  const startPipeline = useCallback(async () => {
    if (running) return
    abortedRef.current = false
    setRunning(true)
    setFinished(false)
    const initial = initialAutoTuneStages(state, exoAi, hdr, settings, bloatApps)
    setStages(initial)

    let warned = false
    for (const item of initial) {
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: 'active' } : s))
      const result = await runAutoTuneStage(item.id, state, hdr, settings, onSave, refreshAll, bloatApps)
      if (result.status === 'warn') warned = true
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: result.status, detail: result.detail } : s))
    }

    if (!abortedRef.current) {
      setRunning(false)
      setFinished(true)
      if (!warned) {
        const now = Date.now()
        writeCompletedAt(now)
        setCompletedAt(now)
      }
    }
  }, [running, state, exoAi, hdr, settings, bloatApps, onSave, refreshAll])

  return { stages, running, finished, completedAt, setFinished, startPipeline }
}

function useExoAiJump({
  active,
  jump,
  vendor,
  room,
  onStorage,
  onMenu,
}: {
  active: boolean
  jump: { tab: PcLink } | null
  vendor: GpuProfile['vendor'] | null
  room: React.RefObject<HTMLElement | null>
  onStorage: () => void
  onMenu: (menu: PcPartMenu) => void
}) {
  const handled = useRef<typeof jump>(null)
  useLayoutEffect(() => {
    if (!jump) { handled.current = null; return }
    const scroller = room.current
    if (!active || handled.current === jump || !scroller) return
    if (jump.tab === 'control' && !vendor) return
    handled.current = jump
    if (jump.tab === 'drives' || jump.tab === 'compress') { onStorage(); return }
    const opens: Partial<Record<PcLink, PcPartMenu>> = {
      graphics: 'graphics',
      upscalers: 'graphics',
      control: 'graphics',
      display: 'display',
      board: 'board',
    }
    if (jump.tab === 'control' && vendor && vendor !== 'nvidia') {
      host.gpuControlPanel().catch((e: unknown) => report(e, 'ExoAiRoom.controlPanelLink'))
      return
    }
    const wanted = opens[jump.tab]
    if (wanted) {
      scroller.querySelector<HTMLElement>(`[data-pc-menu="${wanted}"]`)?.focus({ preventScroll: true })
      onMenu(wanted)
      return
    }
    if (jump.tab === 'overview' || jump.tab === 'performance' || jump.tab === 'autopilot' || jump.tab === 'network') {
      scroller.scrollTop = 0
      return
    }
    scroller.querySelector(`[data-pc-part="${jump.tab}"]`)?.scrollIntoView({ block: 'start' })
  }, [active, jump, vendor, room, onStorage, onMenu])
}

function OverviewHealthRows({
  active,
  health,
  onHealth,
}: {
  active: boolean
  health: PcHealth | null
  onHealth: (next: PcHealth) => void
}) {
  const show = health?.items.some(
    item => !item.applied && !item.current && item.key !== 'power' && item.key !== 'chipset' && !item.key.startsWith('net'),
  )
  if (!show) return null
  return (
    <div className="exo-ai-health-rows">
      <SetupRows place="needs" active={active} health={health} onHealth={onHealth} />
    </div>
  )
}

export function ExoAiRoom({
  active,
  jump = null,
  games,
  settings,
  onSave,
}: {
  active: boolean
  jump?: { tab: PcLink } | null
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const room = useRef<HTMLElement>(null)
  const { state, put, read } = usePcState(active)
  const hdr = useHdrState(active)
  const apps = useAppsSummary(active, 0)
  const [menu, setMenu] = useState<PcPartMenu | null>(null)
  const [storageOpen, setStorageOpen] = useState(false)

  const setHealth = useCallback((next: PcHealth) => put('health', next), [put])
  const setInterrupts = useCallback((next: PcState['interrupts']) => put('interrupts', next), [put])
  const setTuning = useCallback((next: Tuning) => put('tuning', next), [put])
  const setProfile = useCallback((next: GpuProfile) => put('profile', next), [put])
  const setDriver = useCallback((next: PcState['driver']) => put('driver', next), [put])
  const setDisplay = useCallback((next: PcState['display']) => put('display', next), [put])
  const setDrives = useCallback((next: DrivesState) => put('drives', next), [put])
  const setTune = useCallback((next: AutoTune) => put('tune', next), [put])

  useTuneReads(active, state.tune, active, setTune)
  const autopilot = useAutopilot(active, menu === 'board')
  const exoAi = useExoAi(active, active)
  const compress = useCompressList(active)

  const openStorage = useCallback(() => {
    setMenu(null)
    setStorageOpen(true)
  }, [])

  useEffect(() => {
    if (!active) {
      setMenu(null)
      setStorageOpen(false)
    }
  }, [active])

  useExoAiJump({
    active,
    jump,
    vendor: state.profile?.vendor ?? null,
    room,
    onStorage: openStorage,
    onMenu: setMenu,
  })

  const refreshAll = useCallback(async () => {
    await read()
    exoAi.refresh()
  }, [read, exoAi])

  const pipeline = useAutoTunePipeline({
    active,
    state,
    exoAi: exoAi.state,
    hdr,
    settings,
    onSave,
    refreshAll,
  })

  const pending = useMemo(
    () => countPendingAutoTune(state, hdr, settings, onSave),
    [state, hdr, settings, onSave],
  )

  const isComplete = !pipeline.running && (
    pipeline.completedAt !== null
    || (pending === 0 && state.profile !== null && state.health !== null)
  )

  const needsRestart = anyRestartRequired(pipeline.stages, state)
  const compressList: CompressList | null = compress.list
  const hasHardwareParts = (state.tune?.parts.length ?? 0) > 0

  return (
    <main ref={room} className="exo-ai-room" aria-label="Exo AI">
      <div className="exo-ai-shell">
        <header className="exo-ai-head">
          <div className="exo-ai-title-row">
            <h1 className="exo-ai-title">Exo AI</h1>
            <LiveHeaderStatus exoAi={exoAi.state} tune={state.tune} autopilotOn={autopilot.state?.on ?? true} />
          </div>
          <div className="exo-ai-head-actions">
            <StorageChip drives={state.drives} list={compressList} onOpen={openStorage} />
            {isComplete ? (
              <div className="exo-ai-complete-group">
                <span className="exo-ai-complete">
                  <Check size={15} />
                  Auto Tune complete
                </span>
                <button
                  type="button"
                  className="exo-set-btn"
                  disabled={pipeline.running}
                  onClick={() => void pipeline.startPipeline()}
                >
                  Run again
                </button>
              </div>
            ) : (
              <button
                type="button"
                className="exo-at-run"
                disabled={pipeline.running}
                onClick={() => void pipeline.startPipeline()}
              >
                {pipeline.running ? <Spinner label="Optimizing PC" /> : <Bolt size={15} />}
                {pipeline.running ? 'Optimizing PC' : 'Run Auto Tune'}
              </button>
            )}
          </div>
        </header>

        <div className="exo-ai-grid">
          <div className="exo-ai-col">
            <section className="exo-system-panel exo-ai-panel" aria-labelledby="exo-ai-playbook-title">
              <header className="exo-ai-panel-head">
                <h2 id="exo-ai-playbook-title">Auto Tune</h2>
                <PlaybookBadge running={pipeline.running} complete={isComplete} pending={pending} />
              </header>
              <ul className="exo-at-stages">
                {pipeline.stages.map(stage => {
                  const stageStatus = isComplete && stage.status === 'idle' ? 'done' : stage.status
                  return (
                    <li
                      key={stage.id}
                      data-status={stageStatus}
                      className={cn(
                        'exo-at-stage',
                        stageStatus === 'active' && 'is-active',
                        stageStatus === 'done' && 'is-done',
                        stageStatus === 'warn' && 'is-warn',
                      )}
                    >
                      <StageMarker status={stageStatus} label={stage.label} />
                      <div className="exo-at-stage-copy">
                        <span className="exo-at-stage-title">{stage.label}</span>
                        <span className="exo-at-stage-detail">{stage.detail}</span>
                      </div>
                    </li>
                  )
                })}
              </ul>
              {hasHardwareParts ? (
                <div className="exo-ai-hw-wrap">
                  <AutoTuneRows tune={state.tune} profile={state.profile} about={state.about} onTune={setTune} />
                </div>
              ) : null}
            </section>

            <section className="exo-system-panel exo-ai-specs" data-pc-part="overview" aria-label="This PC">
              <ThisPc state={state} chips={null} onMenu={setMenu} />
              <OverviewHealthRows active={active} health={state.health} onHealth={setHealth} />
            </section>
          </div>

          <div className="exo-ai-col is-right">
            <RightSection id="drivers" title="Updates">
              <DriversSection
                active={active}
                driver={state.driver}
                onDriver={setDriver}
                health={state.health}
                onHealth={setHealth}
                about={state.about}
                runtimes={apps?.runtimes ?? null}
              />
            </RightSection>
            <RightSection id="gpu-usage" title="GPU activity">
              <GpuProcessesSection active={active} />
            </RightSection>
            <RightSection id="shaders" title="Shaders">
              <ShadersBoard active={active} games={games} />
            </RightSection>
          </div>
        </div>
      </div>

      {pipeline.running || pipeline.finished ? (
        <AutoTuneCinema
          stages={pipeline.stages}
          running={pipeline.running}
          finished={pipeline.finished}
          needsRestart={needsRestart}
          onExit={() => pipeline.setFinished(false)}
          onRunAgain={() => void pipeline.startPipeline()}
        />
      ) : null}

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

      <PcPartMenus
        active={active}
        menu={menu}
        onClose={() => setMenu(null)}
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
        onRead={refreshAll}
      />
    </main>
  )
}

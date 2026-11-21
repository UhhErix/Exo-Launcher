import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
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
import { report } from '../../lib/report'
import { cn } from '../../lib/utils'
import { Spinner } from '../ui/Spinner'
import { useAutopilot, AutopilotControls, AutopilotReports } from './Autopilot'
import { AutoTuneRows, useTuneReads } from './AutoTune'
import { AutoTuneCinema } from './AutoTuneCinema'
import { bestSteps, BestSetup } from './BestSetup'
import { useCompressList } from './CompressGames'
import { ExoAiFooter, useExoAi } from './ExoAiPanel'
import { InterruptRows } from './InterruptRows'
import { NetworkGroup } from './NetworkGroup'
import { ThisPc, type PcPartMenu } from './PcOverview'
import { PcPartMenus, StorageChip } from './PcPartMenus'
import { SectionSlot } from './SectionHead'
import { StorageRoom } from './StorageRoom'
import { SystemGroup } from './SystemGroup'
import { SetupRows } from './ThisPcTab'
import { usePcState, type PcState } from './usePcState'
import {
  initialAutoTuneStages,
  runAutoTuneStage,
  type AutoTuneStageState,
  type BloatAppInfo,
} from './autoTuneStages'
import '../../styles/auto-tune-overlay.css'
import '../../styles/exo-ai-room.css'

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

function hardwareLine(state: PcState): string {
  const gpu = state.profile?.gpu || state.about?.graphics || state.driver?.card
  const cpu = state.about?.processor
  const ram = state.about?.memoryGb ? `${state.about.memoryGb} GB RAM` : null
  return [gpu, cpu, ram].filter(Boolean).join(' · ') || 'PC optimization & hardware tuning'
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

function AiPanel({
  id,
  title,
  badge,
  className,
  children,
}: {
  id: string
  title: string
  badge?: ReactNode
  className?: string
  children: ReactNode
}) {
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  return (
    <section className={cn('exo-system-panel exo-ai-panel', className)} aria-labelledby={id}>
      <header className="exo-ai-panel-head">
        <h2 id={id}>{title}</h2>
        <div className="exo-ai-panel-right">
          {badge}
          <div ref={setSlot} className="exo-pc-section-actions" />
        </div>
      </header>
      <SectionSlot.Provider value={slot}>
        <div className="exo-ai-panel-body">{children}</div>
      </SectionSlot.Provider>
    </section>
  )
}

function PlaybookBadge({
  running,
  pending,
}: {
  running: boolean
  pending: number | null
}) {
  if (running) return <span className="exo-ai-badge is-live">Optimizing…</span>
  if (pending != null && pending > 0) {
    return <span className="exo-ai-badge is-pending">{pending} ready</span>
  }
  if (pending === 0) return <span className="exo-ai-badge is-good">All set</span>
  return null
}

function LiveHeaderStatus({ exoAi, tune }: { exoAi: ExoAiState | null; tune: AutoTune | null }) {
  const gaming = exoAi?.gaming.active ?? false
  const testing = tune?.parts.some(part => part.state === 'testing') ?? false
  let text = 'Active'
  if (gaming) text = exoAi?.gaming.gameTitle ? `Boosting ${exoAi.gaming.gameTitle}` : 'Boosting game'
  else if (testing) text = 'Hardware calibration active'

  return (
    <span className={cn('exo-ai-live', (gaming || testing) && 'is-active')}>
      <span className="exo-ai-live-dot" aria-hidden="true" />
      <span>{text}</span>
    </span>
  )
}

function AutoTunePlaybookPanel({
  stages,
  running,
  pending,
  exoAi,
}: {
  stages: readonly AutoTuneStageState[]
  running: boolean
  pending: number | null
  exoAi: ExoAiState | null
}) {
  return (
    <AiPanel
      id="exo-ai-playbook-title"
      title="Auto Tune"
      badge={<PlaybookBadge running={running} pending={pending} />}
    >
      <ul className="exo-at-stages">
        {stages.map(stage => (
          <li
            key={stage.id}
            data-status={stage.status}
            className={cn(
              'exo-at-stage',
              stage.status === 'active' && 'is-active',
              stage.status === 'done' && 'is-done',
              stage.status === 'warn' && 'is-warn',
            )}
          >
            <StageMarker status={stage.status} label={stage.label} />
            <div className="exo-at-stage-copy">
              <span className="exo-at-stage-title">{stage.label}</span>
              <span className="exo-at-stage-detail">{stage.detail}</span>
            </div>
          </li>
        ))}
      </ul>
      <ExoAiFooter exoAi={exoAi} />
    </AiPanel>
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

    for (const item of initial) {
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: 'active' } : s))
      const result = await runAutoTuneStage(item.id, state, hdr, settings, onSave, refreshAll, bloatApps)
      if (abortedRef.current) break
      setStages(current => current.map(s => s.id === item.id ? { ...s, status: result.status, detail: result.detail } : s))
    }

    if (!abortedRef.current) {
      setRunning(false)
      setFinished(true)
    }
  }, [running, state, exoAi, hdr, settings, bloatApps, onSave, refreshAll])

  return { stages, running, finished, setFinished, startPipeline }
}

export function ExoAiRoom({
  active,
  games,
  settings,
  onSave,
}: {
  active: boolean
  games: readonly Game[]
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  const { state, put, read } = usePcState(active)
  const hdr = useHdrState(active)
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
  const autopilot = useAutopilot(active, active)
  const exoAi = useExoAi(active, active)
  const compress = useCompressList(active)

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

  const needsRestart = anyRestartRequired(pipeline.stages, state)
  const compressList: CompressList | null = compress.list

  return (
    <main className="exo-ai-room" aria-label="Exo AI">
      <div className="exo-ai-shell">
        <header className="exo-ai-head">
          <div className="exo-ai-head-copy">
            <div className="exo-ai-title-row">
              <h1 className="exo-ai-title">Exo AI</h1>
              <LiveHeaderStatus exoAi={exoAi.state} tune={state.tune} />
            </div>
            <p className="exo-ai-sub">{hardwareLine(state)}</p>
          </div>
          <div className="exo-ai-head-actions">
            <StorageChip drives={state.drives} list={compressList} onOpen={() => setStorageOpen(true)} />
            <button
              type="button"
              className="exo-at-run"
              disabled={pipeline.running}
              onClick={() => void pipeline.startPipeline()}
            >
              {pipeline.running ? <Spinner label="Optimizing PC" /> : <Bolt size={15} />}
              {pipeline.running ? 'Optimizing PC' : 'Run Auto Tune'}
            </button>
          </div>
        </header>

        <div className="exo-ai-grid">
          <div className="exo-ai-col">
            <AutoTunePlaybookPanel
              stages={pipeline.stages}
              running={pipeline.running}
              pending={pending}
              exoAi={exoAi.state}
            />

            <AiPanel id="exo-ai-hw-title" title="Overclock & undervolt">
              <AutoTuneRows tune={state.tune} profile={state.profile} about={state.about} onTune={setTune} />
            </AiPanel>

            <AiPanel id="exo-ai-autopilot-title" title="Autopilot">
              <AutopilotControls state={autopilot.state} onState={autopilot.setState} />
              <AutopilotReports state={autopilot.state} />
            </AiPanel>
          </div>

          <div className="exo-ai-col">
            <section className="exo-system-panel exo-ai-specs" aria-label="PC specifications">
              <ThisPc state={state} chips={null} onMenu={setMenu} />
            </section>

            <AiPanel id="exo-ai-tuning-title" title="System tuning">
              <BestSetup
                profile={state.profile}
                health={state.health}
                tuning={state.tuning}
                drives={state.drives}
                screen={hdr}
                settings={settings}
                onSave={onSave}
                onRead={refreshAll}
              />
              <SetupRows place="needs" active={active} health={state.health} onHealth={setHealth} />
              <InterruptRows place="needs" state={state.interrupts} onState={setInterrupts} />
              <SetupRows place="section" active={active} health={state.health} onHealth={setHealth} />
              <InterruptRows place="section" state={state.interrupts} onState={setInterrupts} />
              <SystemGroup tuning={state.tuning} onTuning={setTuning} />
              <NetworkGroup health={state.health} onHealth={setHealth} />
            </AiPanel>
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

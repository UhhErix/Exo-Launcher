import { useEffect, useState } from 'react'
import { host, onHostEvent, type GameSettingsGroup, type GameSettingsState, type KeepStatus, type MinecraftInstanceState } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { GameLine, GameReading, type LineWord } from './GameLine'
import { sheetMemory } from './useGameDetails'
import { useSelectionGuard } from './useSelectionGuard'

/** What the line is doing or failed to do: setting Exo's values (on) or putting the player's back (off). */
type Work = { on: boolean; failed: boolean }

const settingsRead = sheetMemory<GameSettingsState>()

const WINDOW_MODES = [{ value: 'borderless', label: 'Borderless' }, { value: 'fullscreen', label: 'Fullscreen' }] as const

/**
 * The game page's Optimize: one line, like Shaders, for a game Exo has best settings for (Modern Warfare 4 first). A
 * press writes Exo's values for this PC into the game's own settings file (game.settings), and the line says Applied
 * in green once all it asked for was found and is in place (Partly optimized when the file lacks some of it, Settings not
 * found when it holds none); a press then, once confirmed, puts back each value Optimize changed. Nothing
 * changes while the game runs or before it has written its file, and the line says which. Once pressed it stays on:
 * after a game update resets the file, Exo writes it again as the game closes, and the line says Applied again after an
 * update until the game next runs. A game whose window mode Exo can set has its choice at the row's edge, and picking one
 * applies Optimize with that mode, which Exo then holds whenever the game is closed. The line is always there, like every
 * line on the page: a shining word while the game is read (the last read shows at once while it is read again), and Not
 * supported in this game (or its anti-cheat's sentence) when Exo has no best settings for it.
 */
export function OptimizeLine({ gameId }: { gameId: string }) {
  const [state, setState] = useState<GameSettingsState | null>(() => settingsRead.get(gameId))
  const [working, setWorking] = useState(false)
  const [work, setWork] = useState<Work | null>(null)
  const [kept, setKept] = useState<Kept>(null)
  const [expanded, setExpanded] = useState(false)
  const [modError, setModError] = useState<string | null>(null)
  const guard = useSelectionGuard(gameId)

  useEffect(() => {
    let live = true
    const load = () => host.gameSettings(gameId)
      .then(next => {
        if (!live) return
        if (next.ok) settingsRead.set(gameId, next)
        setState(next)
      })
      .catch(e => { report(e, 'OptimizeLine.load'); if (live) setState(current => current ?? { ok: false }) })
      // What Exo has seen of its values since: put back after an update, the player's own changes kept, or some the game won't keep.
      .then(() => host.keepStatus())
      .then(status => { if (live && status) setKept(status.games[gameId] ?? null) })
      .catch(e => report(e, 'OptimizeLine.kept'))
    setState(settingsRead.get(gameId))
    setKept(null)
    setWork(null)
    setExpanded(false)
    setModError(null)
    void load()
    // Starting or closing the game opens or closes the line; Exo writing it again after an update turns it back to Applied.
    const offRun = onHostEvent('game.runState', event => { if (event.id === gameId) void load() })
    const offAgain = onHostEvent('game.optimizedAgain', () => void load())
    const offHeld = onHostEvent('game.optimizeHeld', () => void load())
    return () => { live = false; offRun(); offAgain(); offHeld() }
  }, [gameId])

  if (!state) return null
  if (!state.ok) return <GameReading name="Optimize" word={{ text: 'Could not check', bad: true }} />
  if (!state.supported) return null
  const applied = state.standing === 'applied' || state.standing === 'partly'
  const applySettings = (payload: {
    group?: string
    key?: string
    all?: boolean
    on?: boolean
    mode?: string
    smoothMotion?: boolean
    jvm?: boolean
    modsAction?: 'install' | 'undo'
    instanceId?: string
  }, nextOn: boolean, context: string) => {
    setWorking(true)
    setModError(null)
    setWork({ on: nextOn, failed: false })
    host.gameSettings(gameId, payload)
      .then(guard((answer: GameSettingsState) => {
        if (!answer.ok) {
          setWork({ on: nextOn, failed: true })
          if (payload.modsAction) setModError(answer.message ?? 'Could not update performance mods.')
          return
        }
        settingsRead.set(gameId, answer)
        setState(answer)
      }))
      .catch(e => {
        report(e, context)
        setWork({ on: nextOn, failed: true })
        if (payload.modsAction) setModError('Could not update performance mods.')
      })
      .finally(() => setWorking(false))
  }
  const run = (on: boolean, mode?: string) =>
    applySettings(mode ? { mode } : { all: true, on }, on, on ? 'OptimizeLine.apply' : 'OptimizeLine.putBack')
  const toggleSmoothMotion = (smoothMotion: boolean) => {
    const payload = smoothMotion ? { smoothMotion: true, all: true, on: true }
      : applied ? { smoothMotion: false, all: true, on: true } : { smoothMotion: false }
    applySettings(payload, smoothMotion || applied, 'OptimizeLine.smoothMotion')
  }
  const toggleGroup = (groupId: string, nextOn: boolean) =>
    applySettings({ group: groupId, on: nextOn }, nextOn, 'OptimizeLine.group')
  const toggleKey = (key: string, nextOn: boolean) =>
    applySettings({ key, on: nextOn }, nextOn, 'OptimizeLine.key')
  const toggleJvm = (instanceId: string, nextOn: boolean) =>
    applySettings({ jvm: nextOn, instanceId }, nextOn, 'OptimizeLine.jvm')
  const runMods = (instanceId: string, modsAction: 'install' | 'undo') =>
    applySettings({ modsAction, instanceId }, modsAction === 'install', 'OptimizeLine.mods')

  const isDisabled = working || state.running === true

  return (
    <>
      <GameLine name="Optimize" word={wordFor(state, working, work, kept)} state={state.standing === 'applied' ? 'applied' : state.running ? 'running' : 'ready'} action
        disabled={isDisabled || state.standing === 'unfound'} confirm={applied ? 'Put back your settings?' : undefined} onOpen={() => run(!applied)}
        control={state.windowMode ? <Choice label="Window mode" value={state.windowMode} choices={WINDOW_MODES} disabled={isDisabled} onChange={mode => run(true, mode)} /> : null} />
      {state.smoothMotion != null && (
        <GameReading name="Smooth Motion" word={state.smoothMotion ? { text: 'On (without in-game DLSS-G)', good: true } : { text: 'Off' }}>
          <Switch checked={state.smoothMotion} label="Smooth Motion" disabled={isDisabled} onChange={toggleSmoothMotion} />
        </GameReading>
      )}
      <OptimizeExtras
        state={state}
        expanded={expanded}
        onToggleExpanded={() => setExpanded(open => !open)}
        disabled={isDisabled}
        modError={modError}
        onToggleGroup={toggleGroup}
        onToggleKey={toggleKey}
        onToggleJvm={toggleJvm}
        onRunMods={runMods}
      />
    </>
  )
}

function OptimizeExtras({
  state,
  expanded,
  onToggleExpanded,
  disabled,
  modError,
  onToggleGroup,
  onToggleKey,
  onToggleJvm,
  onRunMods,
}: {
  state: GameSettingsState
  expanded: boolean
  onToggleExpanded: () => void
  disabled: boolean
  modError: string | null
  onToggleGroup: (groupId: string, nextOn: boolean) => void
  onToggleKey: (key: string, nextOn: boolean) => void
  onToggleJvm: (instanceId: string, nextOn: boolean) => void
  onRunMods: (instanceId: string, modsAction: 'install' | 'undo') => void
}) {
  const groups = state.groups ?? []
  const advice = state.advice ?? []
  const mcInstances = state.minecraft?.instances ?? []
  const onCount = groups.filter(g => g.on).length

  return (
    <>
      {groups.length > 0 && (
        <GameReading name="Profile settings" word={{ text: `${onCount} of ${groups.length} groups in place` }}>
          <button type="button" className="exo-set-btn is-compact" aria-expanded={expanded} onClick={onToggleExpanded}>
            {expanded ? 'Hide changes' : 'Show changes'}
          </button>
        </GameReading>
      )}
      {expanded && <OptimizeGroups groups={groups} disabled={disabled} onToggleGroup={onToggleGroup} onToggleKey={onToggleKey} />}
      {mcInstances.length > 0 && (
        <MinecraftInstancesRows
          instances={mcInstances}
          heapGb={state.minecraft?.heapGb ?? 4}
          disabled={disabled}
          onToggleJvm={onToggleJvm}
          onRunMods={onRunMods}
        />
      )}
      {modError && <GameReading name="Performance mods" word={{ text: modError, bad: true }} />}
      {advice.map((note, idx) => (
        <GameReading key={idx} name={idx === 0 ? 'Notes' : 'Note'} word={{ text: note }} />
      ))}
    </>
  )
}

function OptimizeGroups({
  groups,
  disabled,
  onToggleGroup,
  onToggleKey,
}: {
  groups: GameSettingsGroup[]
  disabled: boolean
  onToggleGroup: (groupId: string, nextOn: boolean) => void
  onToggleKey: (key: string, nextOn: boolean) => void
}) {
  return (
    <>
      {groups.map(group => (
        <div key={group.id} className="exo-game-opt-group">
          <GameReading name={group.label} word={group.on ? { text: group.exo ? 'Set by Exo' : 'Already set', good: true } : { text: 'Not set' }}>
            <Switch checked={group.on} label={group.label} disabled={disabled} onChange={nextOn => onToggleGroup(group.id, nextOn)} />
          </GameReading>
          {(group.changes ?? []).map(change => (
            <div key={change.key} className="exo-game-opt-change">
              <span className="exo-game-opt-change-copy">
                <span className="exo-game-opt-change-key">{change.key}</span>
                <span className="exo-game-opt-change-val">
                  {change.applied ? change.target : `${change.current} → ${change.target}`}
                </span>
              </span>
              <Switch checked={change.applied} label={`Keep ${change.key}`} disabled={disabled} onChange={nextOn => onToggleKey(change.key, nextOn)} />
            </div>
          ))}
        </div>
      ))}
    </>
  )
}

function MinecraftInstancesRows({
  instances,
  heapGb,
  disabled,
  onToggleJvm,
  onRunMods,
}: {
  instances: MinecraftInstanceState[]
  heapGb: number
  disabled: boolean
  onToggleJvm: (instanceId: string, nextOn: boolean) => void
  onRunMods: (instanceId: string, modsAction: 'install' | 'undo') => void
}) {
  return (
    <>
      {instances.map(instance => {
        const hasMods = instance.modsInstalled.length > 0
        const subtitle = `${instance.launcher} · ${instance.loader}${instance.gameVersion ? ` ${instance.gameVersion}` : ''} · ${heapGb} GB G1GC`
        const modWord = hasMods
          ? { text: `${subtitle} · Mods installed (${instance.modsInstalled.join(', ')})`, good: true }
          : instance.isFabric
            ? { text: subtitle }
            : { text: `${subtitle} · Fabric required for Sodium, Lithium and FerriteCore` }
        return (
          <GameReading key={instance.id} name={instance.name} word={modWord}>
            <div className="exo-game-opt-actions">
              <Switch checked={instance.jvmApplied} label={`JVM flags for ${instance.name}`} disabled={disabled} onChange={nextOn => onToggleJvm(instance.id, nextOn)} />
              {instance.isFabric && instance.canInstallMods && (
                <button
                  type="button"
                  className={`exo-set-btn is-compact${hasMods ? ' is-danger' : ''}`}
                  disabled={disabled}
                  onClick={() => onRunMods(instance.id, hasMods ? 'undo' : 'install')}
                >
                  {hasMods ? 'Remove performance mods' : 'Install performance mods'}
                </button>
              )}
            </div>
          </GameReading>
        )
      })}
    </>
  )
}

type Kept = KeepStatus['games'][string] | null

function wordFor(state: GameSettingsState, working: boolean, work: Work | null, kept: Kept): LineWord {
  if (working) return { text: work?.on === false ? 'Putting back' : 'Optimizing', working: true }
  if (work?.failed) return { text: work.on ? 'Could not optimize' : 'Could not put back', bad: true }
  if (state.running) return { text: `Close ${state.title ?? 'the game'} first` }
  if (!state.found) return { text: state.waiting ? 'Applies after you play once' : 'Not optimized' }
  if (state.standing === 'unfound') return { text: 'Settings not found' }
  if (state.standing === 'partly') return { text: 'Partly optimized' }
  if (state.standing !== 'applied') return { text: 'Not optimized' }
  if (kept === 'wontKeepSome') return { text: 'The game won’t keep some settings' }
  if (kept === 'yourChangesKept') return { text: 'Applied, your changes kept', good: true }
  return { text: state.again || kept === 'putBackAfterUpdate' ? 'Applied again after an update' : 'Applied', good: true }
}

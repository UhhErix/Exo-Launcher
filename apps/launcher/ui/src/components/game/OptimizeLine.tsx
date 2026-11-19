import { useEffect, useState } from 'react'
import { host, onHostEvent, type GameSettingsState, type KeepStatus } from '../../lib/host'
import { report } from '../../lib/report'
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
 * applies Optimize with that mode, which Exo then holds whenever the game is closed. Nothing shows until the game is read (as Upscalers: most games have no Optimize, so
 * a line shown while reading would vanish); the last read shows at once while it is read again.
 */
export function OptimizeLine({ gameId }: { gameId: string }) {
  const [state, setState] = useState<GameSettingsState | null>(() => settingsRead.get(gameId))
  const [working, setWorking] = useState(false)
  const [work, setWork] = useState<Work | null>(null)
  const [kept, setKept] = useState<Kept>(null)
  const guard = useSelectionGuard(gameId)

  useEffect(() => {
    let live = true
    const load = () => host.gameSettings(gameId)
      .then(next => {
        if (!live) return
        if (next.ok) settingsRead.set(gameId, next)
        setState(next)
      })
      .catch(e => report(e, 'OptimizeLine.load'))
      // What Exo has seen of its values since: put back after an update, the player's own changes kept, or some the game won't keep.
      .then(() => host.keepStatus())
      .then(status => { if (live && status) setKept(status.games[gameId] ?? null) })
      .catch(e => report(e, 'OptimizeLine.kept'))
    setState(settingsRead.get(gameId))
    setKept(null)
    setWork(null)
    void load()
    // Starting or closing the game opens or closes the line; Exo writing it again after an update turns it back to Applied.
    const offRun = onHostEvent('game.runState', event => { if (event.id === gameId) void load() })
    const offAgain = onHostEvent('game.optimizedAgain', () => void load())
    const offHeld = onHostEvent('game.optimizeHeld', () => void load())
    return () => { live = false; offRun(); offAgain(); offHeld() }
  }, [gameId])

  if (state?.ok && !state.supported && state.note) return <GameReading name="Optimize" word={{ text: state.note }} />
  if (!state?.ok || !state.supported) return null
  const applied = state.standing === 'applied' || state.standing === 'partly'
  const run = (on: boolean, mode?: string) => {
    setWorking(true)
    setWork({ on, failed: false })
    host.gameSettings(gameId, mode ? { mode } : { all: true, on })
      .then(guard((answer: GameSettingsState) => {
        if (!answer.ok) { setWork({ on, failed: true }); return }
        settingsRead.set(gameId, answer)
        setState(answer)
      }))
      .catch(e => { report(e, on ? 'OptimizeLine.apply' : 'OptimizeLine.putBack'); setWork({ on, failed: true }) })
      .finally(() => setWorking(false))
  }
  return (
    <GameLine name="Optimize" word={wordFor(state, working, work, kept)} state={state.standing === 'applied' ? 'applied' : state.running ? 'running' : 'ready'} action
      disabled={working || state.running === true || state.standing === 'unfound'} confirm={applied ? 'Put back your settings?' : undefined} onOpen={() => run(!applied)}
      control={state.windowMode ? <Choice label="Window mode" value={state.windowMode} choices={WINDOW_MODES} disabled={working || state.running === true} onChange={mode => run(true, mode)} /> : null} />
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

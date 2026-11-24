import { useEffect, useState } from 'react'
import { host, type FrameGenMod, type GpuGame, type IntelFrameGen } from '../../lib/host'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Switch } from '../Switch'
import { frameGenWord } from './frameGenWord'
import { GameReading, type LineWord } from './GameLine'
import { sheetMemory } from './useGameDetails'

const FRAME_GEN = [
  { value: 'game', label: 'Game’s choice' },
  { value: '2x', label: '2x' },
  { value: '3x', label: '3x' },
  { value: '4x', label: '4x' },
] as const

const gpuRead = sheetMemory<GpuGame>()

/**
 * Game sheet > the driver's frame generation for this one game, one line like Upscalers and Shaders. On a Radeon,
 * Fluid Motion Frames as Exo decides it for the game on this PC (off for competitive games, games with frame generation
 * of their own and games this PC can't run near 60 fps; Helpers/FluidMotionFrames.cs), a reading with nothing to set:
 * Exo sets it as the game starts and puts it back once no game runs. It adds frames only while AMD Software runs, so
 * the line says so when it doesn't. On an Intel GPU, XeSS frame generation (named so beside the mod's Frame generation,
 * FrameGenModRow): the game's own choice by default, or 2x, 3x
 * or 4x, which Exo sets in Intel's driver just before the game starts and puts back once no game runs. Shows only when
 * the GPU's driver has it. The game's last read shows at once while it is read again.
 */
export function FrameGenRow({ gameId }: { gameId: string }) {
  const [game, setGame] = useState<GpuGame | null>(() => gpuRead.get(gameId))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    setGame(gpuRead.get(gameId))
    host.gpuGame(gameId).then(answer => {
      if (!live) return
      if (answer.ok) gpuRead.set(gameId, answer)
      setGame(answer)
    }).catch(e => report(e, 'FrameGenRow.load'))
    return () => { live = false }
  }, [gameId])

  if (!game?.ok || !game.supported) return null
  if (game.vendor !== 'intel') {
    const word: LineWord = !game.afmf ? { text: 'Off' } : game.amdSoftware ? { text: 'On' } : { text: 'AMD Software isn’t running', bad: true }
    return <GameReading name="Fluid Motion Frames" word={word} />
  }
  const pick = (frameGen: IntelFrameGen) => {
    setBusy(true)
    setError(null)
    host.gpuGame(gameId, { frameGen })
      .then(answer => {
        if (!answer.ok) { setError('Could not save. Try again.'); return }
        gpuRead.set(gameId, answer)
        setGame(answer)
      })
      .catch(e => { report(e, 'FrameGenRow.set'); setError('Could not save. Try again.') })
      .finally(() => setBusy(false))
  }
  return (
    <GameReading name="XeSS frame generation" word={error ? { text: error, bad: true } : null}>
      <Choice label="XeSS frame generation" value={game.frameGen ?? 'game'} choices={FRAME_GEN} disabled={busy} onChange={value => pick(value as IntelFrameGen)} />
    </GameReading>
  )
}

const modRead = sheetMemory<FrameGenMod>()

/**
 * Game sheet > Frame generation. Exo picks it from the card and from what the game already has, in the background
 * when Best settings has frame generation on. An RTX 20 or 30 with DLSS frame generation gets DLSS where Exo can offer
 * it; every other card a mod helps, and an RTX 20 or 30 where DLSS can't go, gets AMD FidelityFX. An RTX 40 or 50 is
 * left on NVIDIA's own. A game with anti-cheat never gets it. The line is always there: a shining word while it is read
 * (the last read shows at once while it is read again), Built into the game when the game's own runs on this card (an
 * RTX 40 or 50 included), and Not supported in this game only when the game has none and Exo has nothing to offer.
 */
const LOSSLESS_STORE_URL = 'https://store.steampowered.com/app/993090/Lossless_Scaling/'

export function FrameGenModRow({ gameId }: { gameId: string }) {
  const [state, setState] = useState<FrameGenMod | null>(() => modRead.get(gameId))
  const [busy, setBusy] = useState<'on' | 'off' | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    setState(modRead.get(gameId))
    setBusy(null)
    setError(null)
    host.dlssFrameGen(gameId).then(answer => {
      if (!live) return
      modRead.set(gameId, answer)
      setState(answer)
    }).catch(e => { report(e, 'FrameGenModRow.load'); if (live) setState({ ok: false, state: 'hidden' }) })
    return () => { live = false }
  }, [gameId])

  if (!state) return null
  if (!state.ok) return <GameReading name="Frame generation" word={{ text: 'Could not check', bad: true }} />
  if (state.state === 'hidden') return null

  const toggle = (enabled: boolean) => {
    setBusy(enabled ? 'on' : 'off')
    setError(null)
    host.dlssFrameGenSet(gameId, enabled)
      .then(answer => {
        if (!answer.ok) {
          setError((answer.message ?? 'Could not update. Try again.').replace(/'/g, '’'))
          if (answer.state && answer.state !== 'hidden') {
            modRead.set(gameId, { ...answer, ok: true })
            setState({ ...answer, ok: true })
          }
          return
        }
        modRead.set(gameId, answer)
        setState(answer)
      })
      .catch(e => { report(e, 'FrameGenModRow.set'); setError('Could not update. Try again.') })
      .finally(() => setBusy(null))
  }

  const word: LineWord | null = busy
    ? { text: busy === 'on' ? 'Installing' : 'Turning off', working: true }
    : error
      ? { text: error, bad: true }
      : frameGenWord(state)

  const needsLossless = state.path === 'lossless' && state.lossless && !state.lossless.installed
  const storeUrl = state.lossless?.storeUrl || LOSSLESS_STORE_URL

  return (
    <GameReading name="Frame generation" word={word}>
      {needsLossless ? (
        <button type="button" className="exo-set-btn is-compact" onClick={() => void host.openUrl(storeUrl).catch(e => report(e, 'FrameGenModRow.lossless'))}>
          Get Lossless Scaling
        </button>
      ) : state.canToggle ? (
        <Switch checked={state.state === 'on'} disabled={busy !== null} label="Frame generation" onChange={toggle} />
      ) : null}
    </GameReading>
  )
}

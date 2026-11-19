import { useEffect, useState } from 'react'
import { host, type HdrGame } from '../../lib/host'
import { report } from '../../lib/report'
import { useHdrState } from '../../lib/hdr'
import { Choice } from '../settings/Choice'
import { Spinner } from '../ui/Spinner'
import { GameReading, type LineWord } from './GameLine'
import { sheetMemory } from './useGameDetails'

const MODES = [
  { value: 'auto', label: 'Auto' },
  { value: 'on', label: 'Always' },
  { value: 'off', label: 'Never' },
] as const

const hdrRead = sheetMemory<HdrGame>()

/**
 * Game sheet > HDR, one line like Upscalers and Shaders: its Auto, Always or Never choice for System > HDR > HDR for HDR games at the right edge. While that is off nothing
 * switches, so the line says so and offers to turn it on in the choice's place. On a screen that can't show HDR the line
 * says so, with no choice. The game's last read shows at once while it is read again.
 */
export function HdrRow({ gameId, hdrForGames, onHdrForGames }: {
  gameId: string
  /** System > HDR > HDR for HDR games. */
  hdrForGames: boolean
  /** Turns it on; false when the save failed. */
  onHdrForGames: () => Promise<boolean>
}) {
  const screen = useHdrState()
  const [game, setGame] = useState<HdrGame | null>(() => hdrRead.get(gameId))
  const [busy, setBusy] = useState<'mode' | 'on' | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!screen?.supported) return
    let live = true
    setGame(hdrRead.get(gameId))
    host.hdrGame(gameId).then(answer => {
      if (!live) return
      if (answer.ok) hdrRead.set(gameId, answer)
      setGame(answer)
    }).catch(e => { report(e, 'HdrRow.load'); if (live) setGame(current => current ?? { ok: false }) })
    return () => { live = false }
  }, [gameId, screen?.supported])

  // Always a line, like every other: shining while read, and said plainly when this screen has no HDR.
  if (!screen || (screen.supported && !game)) return <GameReading name="HDR" word={null} waiting />
  if (!screen.supported) return <GameReading name="HDR" word={{ text: 'This screen can’t show HDR' }} />
  if (!game?.ok) return <GameReading name="HDR" word={{ text: 'Could not check', bad: true }} />
  const mode = game.mode ?? 'auto'
  const pick = (next: string) => {
    setBusy('mode')
    setError(null)
    host.hdrGame(gameId, next)
      .then(answer => {
        if (!answer.ok) { setError('Could not save. Try again.'); return }
        hdrRead.set(gameId, answer)
        setGame(answer)
      })
      .catch(e => { report(e, 'HdrRow.set'); setError('Could not save. Try again.') })
      .finally(() => setBusy(null))
  }
  const turnOn = () => {
    setBusy('on')
    setError(null)
    void onHdrForGames().then(ok => { if (!ok) setError('Could not save. Try again.') }).finally(() => setBusy(null))
  }
  const off = !hdrForGames && mode !== 'off'
  // Why Exo judges the game as it does, in plain words; with HDR for HDR games off, nothing switches whatever the verdict.
  const why = game.reason ?? ''
  const word: LineWord | null = error
    ? { text: error, bad: true }
    : off ? { text: [why, 'HDR for HDR games is off.'].filter(Boolean).join(' ') }
    : why ? { text: why } : null
  return (
    <GameReading name="HDR" word={word}>
      {off ? (
        <button type="button" className="exo-set-btn is-compact" disabled={busy !== null} onClick={turnOn}>
          {busy === 'on' ? <Spinner label="Turning on HDR for HDR games" /> : null}Turn on
        </button>
      ) : (
        <Choice label="HDR while playing" value={mode} choices={MODES} disabled={busy !== null} onChange={pick} />
      )}
    </GameReading>
  )
}

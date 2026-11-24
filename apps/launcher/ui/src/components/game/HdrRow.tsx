import { useEffect, useState } from 'react'
import { host, type HdrGame } from '../../lib/host'
import { report } from '../../lib/report'
import { useHdrState } from '../../lib/hdr'
import { Choice } from '../settings/Choice'
import { ActionButton } from '../ui/ConfirmButton'
import { GameReading, type LineWord } from './GameLine'
import { sheetMemory } from './useGameDetails'

const MODES = [
  { value: 'auto', label: 'Auto' },
  { value: 'on', label: 'Always' },
  { value: 'off', label: 'Never' },
] as const

const hdrRead = sheetMemory<HdrGame>()

function buildHdrWord(game: HdrGame, hdrForGames: boolean, mode: string, error: string | null): LineWord | null {
  if (error) return { text: error, bad: true }
  const autoHdrTag = game.autoHdr && game.autoHdr.effective !== 'default'
    ? `Windows Auto HDR: ${game.autoHdr.effective}`
    : ''
  const why = [game.reason ?? '', autoHdrTag].filter(Boolean).join(' · ')
  if (!hdrForGames && mode !== 'off') return { text: why || 'Off' }
  return why ? { text: why } : null
}

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
  const [busy, setBusy] = useState(false)
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

  if (!screen || !screen.supported || !game) return null
  if (!game.ok) return <GameReading name="HDR" word={{ text: 'Could not check', bad: true }} />

  const set = (next: string) => {
    setBusy(true)
    setError(null)
    host.hdrGame(gameId, next)
      .then(answer => {
        if (!answer.ok) { setError('Could not save. Try again.'); return }
        hdrRead.set(gameId, answer)
        setGame(answer)
      })
      .catch(e => { report(e, 'HdrRow.set'); setError('Could not save. Try again.') })
      .finally(() => setBusy(false))
  }

  const mode = game.mode ?? 'auto'
  const off = !hdrForGames && mode !== 'off'
  const word = buildHdrWord(game, hdrForGames, mode, error)
  return (
    <GameReading name="HDR" word={word}>
      {off ? (
        <ActionButton className="exo-set-btn is-compact" why="none" context="HdrRow.hdrForGames" fallback="Could not save. Try again."
          onPress={() => { setError(null); return onHdrForGames() }} onFailed={failure => setError(failure.text)}>Turn on</ActionButton>
      ) : (
        <Choice label="HDR while playing" value={mode} choices={MODES} disabled={busy} onChange={set} />
      )}
    </GameReading>
  )
}

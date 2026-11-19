import { useEffect, useRef, useState } from 'react'
import { withHold, type GameHold } from '../../lib/gameHold'

/**
 * Which games Play or Stop is holding (lib/gameHold.ts). The ref is what a second press meets at once; the state draws it.
 * `releaseAfter` lets a hold run on a moment after the host answered, so the pill does not flicker back to Play at once.
 */
export function useGameHolds() {
  const [holds, setHolds] = useState<ReadonlyMap<string, GameHold>>(new Map())
  const ref = useRef(holds)
  const timers = useRef(new Map<string, number>())
  const hold = (gameId: string, held: GameHold | null) => {
    ref.current = withHold(ref.current, gameId, held)
    setHolds(ref.current)
  }
  const cancelRelease = (gameId: string) => {
    const timer = timers.current.get(gameId)
    if (timer === undefined) return
    window.clearTimeout(timer)
    timers.current.delete(gameId)
  }
  const releaseAfter = (gameId: string, ms: number) => {
    cancelRelease(gameId)
    timers.current.set(gameId, window.setTimeout(() => {
      timers.current.delete(gameId)
      hold(gameId, null)
    }, ms))
  }
  useEffect(() => {
    const live = timers.current
    return () => { for (const timer of live.values()) window.clearTimeout(timer) }
  }, [])
  return { holds, holdsGame: (game?: { id: string } | null) => !!game && holds.has(game.id), heldNow: (gameId: string) => ref.current.get(gameId), hold, releaseAfter, cancelRelease }
}

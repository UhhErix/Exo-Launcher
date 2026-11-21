import { useEffect, useState } from 'react'
import type { Game, InstallProgress } from '../../lib/host'
import { statusBelongsToSelection } from '../../lib/library'
import { GamePage, type GamePageProps } from '../GamePage'

const STREAK_MS = 500
const STREAK_REACH = 16

let lastPoint = { x: -1, y: -1 }
if (typeof window !== 'undefined') window.addEventListener('pointerdown', event => { lastPoint = { x: event.clientX, y: event.clientY } }, { capture: true, passive: true })

export type GameSheetProps = Omit<GamePageProps, 'selected' | 'statusMsg' | 'onStatus' | 'onBack'> & {
  onClose: () => void
  game: Game | null
  onStatusFor: (gameId: string, message: string | null, sticky?: boolean) => void
  open: boolean
  instant: boolean
  openedAt: number
  statusMsg: string | null
  statusGameId: string | null
  progress: InstallProgress | null
}

export function GameSheet({ game: current, open, instant, openedAt, statusMsg, statusGameId, onClose, onStatusFor, ...page }: GameSheetProps) {
  const [last, setLast] = useState(current)
  if (current && current !== last) setLast(current)
  const game = current ?? last

  useEffect(() => {
    if (!open || instant) return
    let lastTime = openedAt
    let at = lastPoint
    let holding = false
    let over = false
    const down = (event: Event) => {
      const now = performance.now()
      const { clientX: x, clientY: y } = event as PointerEvent
      holding = !over && now - lastTime < STREAK_MS && Math.hypot(x - at.x, y - at.y) <= STREAK_REACH
      if (!holding) { over = true; return }
      lastTime = now
      at = { x, y }
      event.preventDefault()
      event.stopPropagation()
    }
    const rest = (event: Event) => {
      if (!holding) return
      event.preventDefault()
      event.stopPropagation()
      if (event.type === 'click') holding = false
    }
    const events = ['pointerup', 'mousedown', 'mouseup', 'click', 'dblclick'] as const
    window.addEventListener('pointerdown', down, true)
    for (const name of events) window.addEventListener(name, rest, true)
    return () => {
      window.removeEventListener('pointerdown', down, true)
      for (const name of events) window.removeEventListener(name, rest, true)
    }
  }, [open, instant, openedAt])

  if (!open || !game) return null

  return (
    <div className="exo-game-room" role="region" aria-label={game.title} data-log-area="Library">
      <GamePage
        {...page}
        selected={game}
        statusMsg={statusBelongsToSelection(statusGameId, game) ? statusMsg : null}
        onStatus={(message, sticky) => onStatusFor(game.id, message, sticky)}
        onBack={onClose}
      />
    </div>
  )
}

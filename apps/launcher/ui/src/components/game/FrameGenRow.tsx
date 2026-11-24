import { useEffect, useId, useState } from 'react'
import { ChevronDown } from '../../brand/icons'
import { host, type GameFrameGen, type IntelFrameGen } from '../../lib/host'
import { report } from '../../lib/report'
import { Menu } from '../ui/Menu'
import { buildFrameGenView } from './frameGenWord'
import { GameReading } from './GameLine'
import { sheetMemory } from './useGameDetails'

const frameGenRead = sheetMemory<GameFrameGen>()

export function FrameGenRow({ gameId, initialState }: { gameId: string; initialState?: GameFrameGen | null }) {
  const [state, setState] = useState<GameFrameGen | null>(() => initialState !== undefined ? initialState : frameGenRead.get(gameId))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const anchor = `--exo-fg-${useId().replace(/[^a-zA-Z0-9]/g, '')}` as const

  useEffect(() => {
    if (initialState !== undefined) return
    let live = true
    setState(frameGenRead.get(gameId))
    setError(null)
    host.gameFrameGen(gameId)
      .then(answer => {
        if (!live) return
        if (answer.ok) frameGenRead.set(gameId, answer)
        setState(answer)
      })
      .catch(e => {
        report(e, 'FrameGenRow.load')
        if (live) {
          setState({
            ok: false,
            bestRouteId: 'unavailable',
            selectedRouteId: 'unavailable',
            state: 'unread',
            word: 'Could not check',
            good: false,
            bad: true,
            routes: [],
          })
        }
      })
    return () => { live = false }
  }, [gameId, initialState])

  const view = buildFrameGenView(state, error)

  const submitChange = (change: { route?: string; xessOverride?: IntelFrameGen }) => {
    setBusy(true)
    setError(null)
    host.gameFrameGen(gameId, change)
      .then(answer => {
        if (answer.routes.length > 0) {
          frameGenRead.set(gameId, answer)
          setState(answer)
        }
        if (!answer.ok) setError(answer.message ?? 'Could not save. Try again.')
      })
      .catch(e => {
        report(e, 'FrameGenRow.set')
        setError('Could not save. Try again.')
      })
      .finally(() => setBusy(false))
  }

  const handleAction = (id: string) => {
    if (busy) return
    if (id.startsWith('info:')) return
    if (id.startsWith('xess:')) {
      submitChange({ xessOverride: id.slice(5) as IntelFrameGen })
      return
    }
    const routeId = id.startsWith('route:') ? id.slice(6) : id
    submitChange({ route: routeId })
  }

  return (
    <GameReading name="Frame generation" word={view.word} waiting={view.waiting}>
      {view.triggerLabel ? (
        <Menu
          label="Frame generation route"
          anchor={anchor}
          offset={4}
          triggerClassName="exo-shelf-sort-trigger exo-graphics-menu"
          trigger={<>{view.triggerLabel}<ChevronDown size={12} /></>}
          items={view.items}
          checked={view.checkedId}
          disabled={busy}
          onAction={handleAction}
        />
      ) : null}
    </GameReading>
  )
}

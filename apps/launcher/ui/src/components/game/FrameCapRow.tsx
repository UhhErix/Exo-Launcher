import { useEffect, useId, useState } from 'react'
import { ChevronDown } from '../../brand/icons'
import { host, onHostEvent, type GameFrameCap } from '../../lib/host'
import { report } from '../../lib/report'
import { Menu } from '../ui/Menu'
import { buildFrameCapView } from './frameCapWord'
import { GameReading } from './GameLine'
import { sheetMemory } from './useGameDetails'

const frameCapRead = sheetMemory<GameFrameCap>()

export function FrameCapRow({ gameId, initialState }: { gameId: string; initialState?: GameFrameCap | null }) {
  const [state, setState] = useState<GameFrameCap | null>(() => initialState !== undefined ? initialState : frameCapRead.get(gameId))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const anchor = `--exo-fc-${useId().replace(/[^a-zA-Z0-9]/g, '')}` as const

  useEffect(() => {
    if (initialState !== undefined) return
    let live = true
    setState(frameCapRead.get(gameId))
    setError(null)
    host.gameFrameCap(gameId)
      .then(answer => {
        if (!live) return
        if (answer.ok) frameCapRead.set(gameId, answer)
        setState(answer)
      })
      .catch(e => {
        report(e, 'FrameCapRow.load')
        if (live) {
          setError('Could not check frame cap')
        }
      })
    return () => { live = false }
  }, [gameId, initialState])

  useEffect(() => {
    return onHostEvent('game.capLab', ab => {
      if (ab.gameId && ab.gameId.toLowerCase() !== gameId.toLowerCase()) return
      setState(prev => {
        if (!prev) return prev
        const next: GameFrameCap = { ...prev, ab }
        frameCapRead.set(gameId, next)
        return next
      })
    })
  }, [gameId])

  const view = buildFrameCapView(state, error)

  const handleAction = (id: string) => {
    if (busy || id.startsWith('info:')) return
    setBusy(true)
    setError(null)

    if (id === 'lab:cancel') {
      host.capLabCancel(gameId)
        .then(res => {
          if (res.ab) setState(prev => prev ? { ...prev, ab: res.ab } : prev)
          if (!res.ok) setError(res.message ?? 'Could not cancel A/B test.')
        })
        .catch(e => {
          report(e, 'FrameCapRow.capLabCancel')
          setError('Could not cancel A/B test.')
        })
        .finally(() => setBusy(false))
      return
    }

    if (id.startsWith('lab:run:')) {
      const parts = id.split(':')
      const capA = Number(parts[2]) || undefined
      const capB = Number(parts[3]) || undefined
      host.capLabRun(gameId, capA, capB)
        .then(res => {
          if (res.ab) setState(prev => prev ? { ...prev, ab: res.ab } : prev)
          if (!res.ok) setError(res.message ?? 'Could not start A/B test.')
        })
        .catch(e => {
          report(e, 'FrameCapRow.capLabRun')
          setError('Could not start A/B test.')
        })
        .finally(() => setBusy(false))
      return
    }

    const change = id.startsWith('tier:')
      ? { tier: id.slice(5) }
      : id.startsWith('refresh:')
        ? { tier: 'refresh', capFps: Number(id.slice(8)) }
        : id.startsWith('backend:')
          ? { backend: id.slice(8), tier: state?.selectedTier && state.selectedTier !== 'off' ? state.selectedTier : 'max' }
          : null

    if (!change) {
      setBusy(false)
      return
    }

    host.gameFrameCap(gameId, change)
      .then(answer => {
        if (answer.tiers.length > 0) {
          frameCapRead.set(gameId, answer)
          setState(answer)
        }
        if (!answer.ok) setError(answer.message ?? 'Could not save frame cap.')
      })
      .catch(e => {
        report(e, 'FrameCapRow.set')
        setError('Could not save frame cap.')
      })
      .finally(() => setBusy(false))
  }

  return (
    <GameReading name="Frame cap" word={view.word} waiting={view.waiting}>
      {view.triggerLabel && (
        <Menu
          label="Frame cap"
          anchor={anchor}
          offset={4}
          triggerClassName="exo-shelf-sort-trigger exo-graphics-menu"
          trigger={<>{view.triggerLabel}<ChevronDown size={12} /></>}
          items={view.items}
          checked={view.checkedId}
          disabled={busy}
          onAction={handleAction}
        />
      )}
    </GameReading>
  )
}

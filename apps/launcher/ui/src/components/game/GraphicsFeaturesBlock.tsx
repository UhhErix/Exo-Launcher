import { useEffect, useState } from 'react'
import { host, onHostEvent, type GameGraphicsFeaturesState, type GraphicsFeatureId, type GraphicsFeatureRow } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { ConfirmButton } from '../ui/ConfirmButton'
import { GameReading } from './GameLine'
import { graphicsFeatureWord } from './graphicsFeaturesWord'
import { sheetMemory } from './useGameDetails'
import { useSelectionGuard } from './useSelectionGuard'

const featuresRead = sheetMemory<GameGraphicsFeaturesState>()

const FALLBACK_ROWS: GraphicsFeatureRow[] = [
  { id: 'upscalers', name: 'Upscalers', state: 'off', route: 'Checking', routeKind: 'info', canToggle: false, needsAntiCheatConfirm: false },
  { id: 'reflex', name: 'Reflex & low latency', state: 'off', route: 'Checking', routeKind: 'info', canToggle: false, needsAntiCheatConfirm: false },
  { id: 'rayTracing', name: 'Ray tracing', state: 'off', route: 'Checking', routeKind: 'info', canToggle: false, needsAntiCheatConfirm: false },
  { id: 'pathTracing', name: 'Path tracing', state: 'off', route: 'Checking', routeKind: 'info', canToggle: false, needsAntiCheatConfirm: false },
  { id: 'hiddenSettings', name: 'Hidden settings', state: 'off', route: 'Checking', routeKind: 'info', canToggle: false, needsAntiCheatConfirm: false },
]

export function GraphicsFeaturesBlock({ gameId, gameTitle }: { gameId: string; gameTitle: string }) {
  const [state, setState] = useState<GameGraphicsFeaturesState | null>(() => featuresRead.get(gameId))
  const [busyId, setBusyId] = useState<GraphicsFeatureId | null>(null)
  const [errors, setErrors] = useState<Partial<Record<GraphicsFeatureId, string>>>({})
  const guard = useSelectionGuard(gameId)

  useEffect(() => {
    let live = true
    const load = () => host.gameGraphicsFeatures(gameId)
      .then(next => {
        if (!live) return
        if (next.ok) featuresRead.set(gameId, next)
        setState(next)
      })
      .catch(e => {
        report(e, 'GraphicsFeaturesBlock.load')
        if (live) setState(current => current ?? { ok: false, message: 'Could not check graphics features.' })
      })

    setState(featuresRead.get(gameId))
    setBusyId(null)
    setErrors({})
    void load()
    const offRun = onHostEvent('game.runState', event => { if (event.id === gameId) void load() })
    return () => { live = false; offRun() }
  }, [gameId])

  const applyFeature = (feature: GraphicsFeatureId, on: boolean, acceptAntiCheatRisk = false) => {
    setBusyId(feature)
    setErrors(prev => ({ ...prev, [feature]: undefined }))
    host.gameGraphicsFeaturesSet(gameId, feature, on, acceptAntiCheatRisk)
      .then(guard((next: GameGraphicsFeaturesState) => {
        if (!next.ok) {
          setErrors(prev => ({ ...prev, [feature]: next.message ?? 'Could not save. Try again.' }))
          if (next.features && next.features.length > 0) setState(next)
          return
        }
        featuresRead.set(gameId, next)
        setState(next)
      }))
      .catch(e => {
        report(e, 'GraphicsFeaturesBlock.set')
        setErrors(prev => ({ ...prev, [feature]: 'Could not save. Try again.' }))
      })
      .finally(() => setBusyId(current => current === feature ? null : current))
  }

  const rows = state?.features && state.features.length > 0 ? state.features : FALLBACK_ROWS
  const waiting = !state
  const running = state?.running === true

  return (
    <section className="exo-glass is-panel exo-game-panel" aria-labelledby="exo-game-graphics-features-title">
      <header className="exo-game-panel-head">
        <h2 id="exo-game-graphics-features-title">Graphics features</h2>
      </header>
      <div className="exo-game-lines">
        {rows.map(row => {
          const isBusy = busyId === row.id
          const error = errors[row.id] ?? null
          const word = waiting ? null : graphicsFeatureWord(row, isBusy, error)
          const disabled = waiting || running || busyId !== null
          const needsConfirm = row.canToggle && row.state !== 'on' && row.needsAntiCheatConfirm
          const confirmQuestion = row.antiCheatWarning ?? `${gameTitle} runs in its official offline mode without anti-cheat. Mods load only in that offline mode.`

          return (
            <GameReading key={row.id} name={row.name} word={word} waiting={waiting}>
              {row.canToggle ? (
                needsConfirm ? (
                  <ConfirmButton
                    className="exo-set-btn is-compact"
                    disabled={disabled}
                    question={confirmQuestion}
                    onConfirm={() => applyFeature(row.id, true, true)}
                  >
                    Turn on
                  </ConfirmButton>
                ) : (
                  <Switch
                    checked={row.state === 'on'}
                    disabled={disabled}
                    label={row.name}
                    onChange={checked => applyFeature(row.id, checked, false)}
                  />
                )
              ) : null}
            </GameReading>
          )
        })}
      </div>
    </section>
  )
}

import { useEffect, useState } from 'react'
import { Network } from '../../brand/icons'
import { host, type QosGame, type QosState } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Switch } from '../Switch'
import { Row } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

export function QosGroup() {
  const [state, setState] = useState<QosState | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ gameId: string; text: string } | null>(null)

  useEffect(() => {
    let alive = true
    host.systemQos().then(
      next => { if (alive) setState(next) },
      (e: unknown) => report(e, 'QosGroup.read', { level: 'warn' }),
    )
    return () => { alive = false }
  }, [])

  if (!state || state.games.length === 0) return null

  const toggle = async (game: QosGame, on: boolean) => {
    if (busy) return
    setBusy(game.gameId)
    setError(null)
    try {
      const next = await host.systemQos(on ? 'on' : 'off', game.gameId)
      setState(next)
      if (!next.ok) setError({ gameId: game.gameId, text: trimStop(next.message) || 'Not changed. Try again.' })
    } catch (e) {
      report(e, `QosGroup.${game.gameId}`)
      setError({ gameId: game.gameId, text: 'Not changed. Try again.' })
    }
    setBusy(null)
  }

  return (
    <section className="exo-pc-menu-rows" aria-labelledby="pc-best-qos">
      <h3 id="pc-best-qos" className="exo-pc-best-group">Per-game QoS (DSCP 46)</h3>
      <p className="exo-pc-best-note">{state.note}</p>
      {state.games.map(game => {
        const id = `pc-best-qos-${game.gameId}`
        return (
          <Row
            key={game.gameId}
            label={game.title}
            labelId={id}
            icon={<Network size={16} />}
            detail={game.enabled ? `DSCP 46 active for ${game.exeName}` : `Opt-in DSCP 46 policy for ${game.exeName}`}
            error={error?.gameId === game.gameId ? error.text : null}
          >
            {busy === game.gameId ? <Spinner label={`Updating QoS for ${game.title}`} /> : null}
            <Switch
              checked={game.enabled}
              disabled={!!busy}
              labelledBy={id}
              onChange={on => void toggle(game, on)}
            />
          </Row>
        )
      })}
    </section>
  )
}

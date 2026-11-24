import { useEffect, useState } from 'react'
import { EyeOff, Monitor } from '../../brand/icons'
import { host, type OverlayAdvisorItem, type OverlayAdvisorState } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

export function OverlayAdvisorGroup() {
  const [state, setState] = useState<OverlayAdvisorState | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ id: string; text: string } | null>(null)

  useEffect(() => {
    let alive = true
    host.systemOverlays().then(
      next => { if (alive) setState(next) },
      (e: unknown) => report(e, 'OverlayAdvisorGroup.read', { level: 'warn' }),
    )
    return () => { alive = false }
  }, [])

  if (!state || state.items.length === 0) return null

  const act = async (item: OverlayAdvisorItem, action: 'off' | 'restore') => {
    if (busy) return
    setBusy(item.id)
    setError(null)
    try {
      const next = await host.systemOverlays(action, item.id)
      setState(next)
      if (!next.ok) setError({ id: item.id, text: trimStop(next.message) || 'Not changed. Try again.' })
    } catch (e) {
      report(e, `OverlayAdvisorGroup.${item.id}`)
      setError({ id: item.id, text: 'Not changed. Try again.' })
    }
    setBusy(null)
  }

  return (
    <section className="exo-pc-menu-rows" aria-labelledby="pc-best-overlays">
      <h3 id="pc-best-overlays" className="exo-pc-best-group">Overlays</h3>
      {state.items.map(item => {
        const detail = [item.cost, item.detail, item.manualSteps].filter(Boolean).join(' · ')
        return (
          <Row
            key={item.id}
            label={item.name}
            icon={item.enabled ? <Monitor size={16} /> : <EyeOff size={16} />}
            detail={detail}
            error={error?.id === item.id ? error.text : null}
          >
            {busy === item.id ? <Spinner label={`Changing ${item.name}`} /> : null}
            {item.enabled && item.canToggle ? (
              <button
                type="button"
                className="exo-set-btn is-compact"
                disabled={!!busy}
                onClick={() => void act(item, 'off')}
              >
                Turn off
              </button>
            ) : null}
            {!item.enabled && item.canUndo ? (
              <button
                type="button"
                className="exo-set-btn is-compact"
                disabled={!!busy}
                onClick={() => void act(item, 'restore')}
              >
                Put back
              </button>
            ) : null}
            <State on={!item.enabled}>{item.enabled ? 'On' : item.status === 'unknown' ? 'Check' : 'Off'}</State>
          </Row>
        )
      })}
    </section>
  )
}

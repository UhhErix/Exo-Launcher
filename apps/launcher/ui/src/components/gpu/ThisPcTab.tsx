import { useEffect, useState } from 'react'
import { host, type PcHealth, type PcHealthItem } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

type HealthAction = 'chipset' | 'gpusched' | 'gpupref' | 'gpupref-undo'
const ROW: Record<HealthAction, PcHealthItem['key']> = {
  chipset: 'chipset', gpusched: 'gpusched', gpupref: 'gpupref', 'gpupref-undo': 'gpupref',
}

/** Best settings sets the network; the adapter is a part of This PC. */
const NETWORK: readonly PcHealthItem['key'][] = ['network', 'netlink']

/** Where a row of the PC room goes: Needs you (something to do), or its own section (what Exo set, with its Undo). */
export type PcPlace = 'needs' | 'section'

/**
 * The PC's setup. Needs: what is left to do (RAM below its rated speed, Resizable BAR off, an old GPU scheduling off,
 * games on the slow card; Exo's power plan is a step of Best settings). Section: the fast card once Exo set it, with its Undo. Drivers: the
 * chipset driver while old, installing or waiting for its restart (read every 5 s while it installs and Exo is on
 * screen), by name alone until it installs: versions are not shown. What is fine, or held back with nothing to press, shows nothing.
 */
export function SetupRows({ place, active, health, onHealth }: {
  place: PcPlace | 'drivers'
  active: boolean
  health: PcHealth | null
  onHealth: (next: PcHealth) => void
}) {
  const [busy, setBusy] = useState<HealthAction | null>(null)
  const [error, setError] = useState<{ key: PcHealthItem['key']; text: string } | null>(null)

  const installing = place === 'drivers' && health?.items.some(item => item.busy) === true
  useEffect(() => {
    if (!active || !installing) return
    const timer = window.setInterval(() => {
      if (document.hidden) return
      host.systemHealth().then(onHealth, (e: unknown) => report(e, 'SetupRows.state'))
    }, 5000)
    return () => window.clearInterval(timer)
  }, [active, installing, onHealth])

  const act = async (action: HealthAction) => {
    if (busy) return
    setBusy(action)
    setError(null)
    try {
      const next = await host.systemHealth(action)
      onHealth(next)
      if (!next.ok) setError({ key: ROW[action], text: trimStop(next.message) || 'Not changed. Try again.' })
    } catch (e) {
      report(e, `SetupRows.${action}`)
      setError({ key: ROW[action], text: 'Not changed. Try again.' })
    }
    setBusy(null)
  }

  if (!health) return null
  const items = health.items.filter(item => !NETWORK.includes(item.key) && (item.key === 'chipset') === (place === 'drivers') && (
    place === 'drivers' ? !item.current && !item.held
      : place === 'needs' ? !item.applied && !item.current && item.key !== 'power'
        : item.key !== 'power' && item.applied && item.canUndo))
  return (
    <>
      {items.map(item => (
        <Row key={item.key} label={item.label} detail={item.key === 'chipset' && !item.busy && !item.applied ? undefined : item.detail} error={error?.key === item.key ? error.text : null}>
          <Action item={item} busy={busy} onAct={action => void act(action)} />
        </Row>
      ))}
    </>
  )
}

/** What each action is doing while it runs, for its ring. */
const WORKING: Record<HealthAction, string> = {
  chipset: 'Updating the chipset driver',
  gpusched: 'Opening Settings', gpupref: 'Setting games to the fast card', 'gpupref-undo': 'Putting the graphics card choice back',
}

/**
 * What a row's button does: update the chipset driver (its maker's page after a
 * failed install), open Windows' Graphics settings. RAM and Resizable BAR are set in the BIOS. The one pressed keeps its words
 * and carries the ring while it works; the chipset's Update carries it for as long as the install runs.
 */
function Action({ item, busy, onAct }: { item: PcHealthItem; busy: HealthAction | null; onAct: (action: HealthAction) => void }) {
  const button = (action: HealthAction, words: string, primary = false, working = busy === action) => (
    <button type="button" className={`exo-set-btn${primary ? ' is-primary' : ''}`} disabled={!!busy || working} aria-busy={working}
      onClick={() => onAct(action)}>
      {working ? <Spinner label={WORKING[action]} /> : null}{words}
    </button>
  )
  if (item.key === 'chipset') {
    if (item.held || item.current) return null
    if (item.busy) return button('chipset', 'Update', false, true)
    if (item.url) {
      const url = item.url
      return <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(url)}>Update</button>
    }
    // Applied: installed, waiting for a restart (Exo never restarts the PC).
    return item.applied ? null : button('chipset', 'Update')
  }
  if (item.key === 'gpupref') {
    return item.applied
      ? item.canUndo ? button('gpupref-undo', 'Undo') : null
      : button('gpupref', 'Use the fast card', true)
  }
  if (item.key === 'gpusched') return button(item.key, 'Open Settings')
  return null
}

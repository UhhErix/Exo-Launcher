import { useEffect, useState } from 'react'
import { host, type PcHealth, type PcHealthItem } from '../../lib/host'
import { report } from '../../lib/report'
import type { PlainError } from '../../lib/errorWords'
import { trimStop } from '../../lib/utils'
import { driverInstallPhase, driverInstallStarted, driverInstallStopped } from '../shell/DriverInstallOverlay'
import { ActionButton } from '../ui/ConfirmButton'
import { Row } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

type HealthAction = 'chipset' | 'gpusched' | 'winupdate' | 'gpupref' | 'gpupref-undo'
const ROW: Record<HealthAction, PcHealthItem['key']> = {
  chipset: 'chipset', gpusched: 'gpusched', winupdate: 'ryzenbranch', gpupref: 'gpupref', 'gpupref-undo': 'gpupref',
}

/** Best settings sets the network and has its items (NetworkGroup.tsx); the adapter is a part of This PC. */
const NETWORK: readonly PcHealthItem['key'][] = ['network', 'net-adapter', 'net-dns', 'net-tcp', 'netlink']

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

  // The pressed button shows the work (ActionButton); the rows take the answer after its check, or at once on a refusal.
  const act = async (action: HealthAction) => {
    setError(null)
    if (action === 'chipset') {
      const chip = health?.items.find(item => item.key === 'chipset')
      driverInstallStarted(chip?.label || 'CPU & chipset drivers', '', null, 'chipset')
    }
    try {
      const next = await host.systemHealth(action)
      if (!next.ok) onHealth(next)
      if (action === 'chipset') {
        const chip = next.items.find(item => item.key === 'chipset')
        if (!next.ok) driverInstallPhase('failed', { message: trimStop(next.message) || 'The chipset driver didn’t update.' })
        else if (chip?.busy) driverInstallPhase('installing', { name: chip.label })
        else if (chip?.applied || chip?.current) driverInstallPhase('done', { name: chip.label })
        else driverInstallStopped()
      }
      return next
    } catch (e) {
      if (action === 'chipset') driverInstallPhase('failed', { message: 'The chipset driver didn’t update.' })
      throw e
    }
  }
  const press = (action: HealthAction): Press => ({
    onPress: () => act(action), onDone: next => onHealth(next as PcHealth), onFailed: failure => setError({ key: ROW[action], text: failure.text }),
  })

  if (!health) return null
  const items = health.items.filter(item => !NETWORK.includes(item.key) && (item.key === 'chipset') === (place === 'drivers') && (
    place === 'drivers' ? !item.current && !item.held
      : place === 'needs' ? !item.applied && !item.current && item.key !== 'power'
        : item.key !== 'power' && item.applied && item.canUndo))
  return (
    <>
      {items.map(item => (
        <Row key={item.key} label={item.label} detail={item.key === 'chipset' && !item.busy && !item.applied ? undefined : item.detail} error={error?.key === item.key ? error.text : null}>
          <Action item={item} press={press} />
        </Row>
      ))}
    </>
  )
}

type Press = { onPress: () => Promise<unknown>; onDone: (next: unknown) => void; onFailed: (failure: PlainError) => void }

/**
 * What a row's button does: update the chipset driver (its maker's page after a
 * failed install), open Windows' Graphics settings. RAM and Resizable BAR are set in the BIOS. The one pressed shows its
 * own work (ActionButton: the ring, then its check, or Bad with the reason under the row); the chipset's Update carries
 * the ring for as long as the install runs.
 */
function Action({ item, press }: { item: PcHealthItem; press: (action: HealthAction) => Press }) {
  const button = (action: HealthAction, words: string, primary = false) => (
    <ActionButton className={`exo-set-btn${primary ? ' is-primary' : ''}`} why="none" timeoutMs={null} context={`SetupRows.${action}`}
      fallback="Not changed. Try again." {...press(action)}>{words}</ActionButton>
  )
  if (item.key === 'chipset') {
    if (item.held || item.current) return null
    if (item.busy) return <button type="button" className="exo-set-btn" disabled aria-busy><Spinner label="Updating the chipset driver" />Update</button>
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
  if (item.key === 'cpumicrocode' && item.url) {
    const url = item.url
    return <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(url)}>BIOS page</button>
  }
  if (item.key === 'ryzenbranch') return button('winupdate', 'Windows Update')
  if (item.key === 'gpusched') return button(item.key, 'Open Settings')
  return null
}

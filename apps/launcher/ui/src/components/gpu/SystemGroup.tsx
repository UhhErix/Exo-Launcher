import { useState, type ReactNode } from 'react'
import { Clock, Cpu, GraphicsCard, Lock, Memory, Mouse, Server } from '../../brand/icons'
import { host, type Tuning, type TuningItem, type TuningKey } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Switch } from '../Switch'
import { Row } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'
import { systemItemCanPutBack } from './systemGroupModel'

const ICONS: Record<TuningKey, ReactNode> = {
  timer: <Clock size={16} />,
  scheduling: <Cpu size={16} />,
  pagefile: <Memory size={16} />,
  cache: <Memory size={16} />,
  captureService: <Server size={16} />,
  services: <Server size={16} />,
  integrity: <Lock size={16} />,
  vrr: <GraphicsCard size={16} />,
  mouseAccel: <Mouse size={16} />,
}

/**
 * Best settings › System (SystemTuning.cs): each item with its own switch (on: set now, in one Windows prompt with a restore
 * point first; off: put back exactly as it was). Use best settings sets only the Windows defaults a tweak changed; Services
 * and Memory Integrity are set here only, and Memory Integrity asks once more, since it lowers the PC's protection. A change
 * that waits on a restart says so.
 */
export function SystemGroup({ tuning, onTuning }: { tuning: Tuning | null; onTuning: (next: Tuning) => void }) {
  const [busy, setBusy] = useState<TuningKey | null>(null)
  const [error, setError] = useState<{ key: TuningKey; text: string } | null>(null)
  if (!tuning || tuning.items.length === 0) return null

  const run = async (item: TuningItem, on: boolean) => {
    if (busy) return
    setBusy(item.key)
    setError(null)
    try {
      const next = await host.systemTuning(on ? 'on' : 'off', item.key)
      onTuning(next)
      if (!next.ok) setError({ key: item.key, text: trimStop(next.message) || 'Not changed. Try again.' })
    } catch (e) {
      report(e, `SystemGroup.${item.key}`)
      setError({ key: item.key, text: 'Not changed. Try again.' })
    }
    setBusy(null)
  }

  return (
    <section className="exo-pc-menu-rows" aria-labelledby="pc-best-system">
      <h3 id="pc-best-system" className="exo-pc-best-group">System</h3>
      {tuning.items.map(item => {
        const id = `pc-best-sys-${item.key}`
        const detail = item.off ? 'Off: Use best settings leaves it as it is.' : item.optIn && !item.on && !item.blocked && item.key !== 'integrity' ? `${item.detail} Only this switch sets it.` : item.detail
        return (
          <Row key={item.key} label={item.label} labelId={id} icon={ICONS[item.key]} detail={detail}
            error={error?.key === item.key ? error.text : null}>
            {busy === item.key ? <Spinner label={`Changing ${item.label}`} /> : null}
            {systemItemCanPutBack(item) ? (
              <button type="button" className="exo-set-btn is-compact" disabled={!!busy} onClick={() => void run(item, false)}>Put back</button>
            ) : null}
            {item.key === 'integrity' && !item.on ? (
              <ConfirmButton className="exo-set-btn is-danger" danger disabled={!!busy || !!item.blocked} question="Turn it off?" onConfirm={() => void run(item, true)}>Turn off</ConfirmButton>
            ) : (
              <Switch checked={item.on} disabled={!!busy || (!item.on && !!item.blocked)} labelledBy={id}
                onChange={on => void run(item, on)} />
            )}
          </Row>
        )
      })}
      {tuning.restart ? <p className="exo-pc-best-note">Restart Windows to finish.</p> : null}
    </section>
  )
}

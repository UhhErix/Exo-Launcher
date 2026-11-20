import { useState, type ReactNode } from 'react'
import { Clock, Cpu, GraphicsCard, Lock, Memory, Mouse, Performance, Server } from '../../brand/icons'
import { host, type LatencyCheck, type LatencySample, type Tuning, type TuningItem, type TuningKey } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Switch } from '../Switch'
import { Row } from '../settings/parts'
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
 * that waits on a restart says so. Latency check measures, read-only, how long drivers hold the processor (ten seconds,
 * one Windows prompt), against the check before.
 */
export function SystemGroup({ tuning, onTuning }: { tuning: Tuning | null; onTuning: (next: Tuning) => void }) {
  const [busy, setBusy] = useState<TuningKey | 'latency' | null>(null)
  const [error, setError] = useState<{ key: TuningKey | 'latency'; text: string } | null>(null)
  if (!tuning) return null
  const visibleItems = tuning.items.filter(item =>
    item.key !== 'integrity' && item.key !== 'mouseAccel' && (item.key !== 'vrr' || item.canUndo),
  )

  const call = async (key: TuningKey | 'latency', ask: () => Promise<Tuning>) => {
    if (busy) return
    setBusy(key)
    setError(null)
    try {
      const next = await ask()
      onTuning(next)
      if (!next.ok) setError({ key, text: trimStop(next.message) || (key === 'latency' ? 'Not measured. Try again.' : 'Not changed. Try again.') })
    } catch (e) {
      report(e, `SystemGroup.${key}`)
      setError({ key, text: key === 'latency' ? 'Not measured. Try again.' : 'Not changed. Try again.' })
    }
    setBusy(null)
  }
  const run = (item: TuningItem, on: boolean) => call(item.key, () => host.systemTuning(on ? 'on' : 'off', item.key))

  return (
    <section className="exo-pc-menu-rows" aria-labelledby="pc-best-system">
      <h3 id="pc-best-system" className="exo-pc-best-group">System</h3>
      {visibleItems.map(item => {
        const id = `pc-best-sys-${item.key}`
        const detail = item.off ? 'Off: Use best settings leaves it as it is.' : item.optIn && !item.on && !item.blocked ? `${item.detail} Only this switch sets it.` : item.detail
        return (
          <Row key={item.key} label={item.label} labelId={id} icon={ICONS[item.key]} detail={detail}
            error={error?.key === item.key ? error.text : null}>
            {busy === item.key ? <Spinner label={`Changing ${item.label}`} /> : null}
            {systemItemCanPutBack(item) ? (
              <button type="button" className="exo-set-btn is-compact" disabled={!!busy} onClick={() => void run(item, false)}>Put back</button>
            ) : null}
            {item.key === 'vrr' ? null : (
              <Switch checked={item.on} disabled={!!busy || (!item.on && !!item.blocked)} labelledBy={id}
                onChange={on => void run(item, on)} />
            )}
          </Row>
        )
      })}
      <Row label="Latency check" icon={<Performance size={16} />} detail={<LatencyWords check={tuning.latency ?? null} />}
        error={error?.key === 'latency' ? error.text : null}>
        <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'latency'}
          onClick={() => void call('latency', () => host.systemTuning('latency'))}>
          {busy === 'latency' ? <Spinner label="Measuring for ten seconds" /> : null}Check
        </button>
      </Row>
      {tuning.restart ? <p className="exo-pc-best-note">Restart Windows to finish.</p> : null}
    </section>
  )
}

/** The check in words: the longest runs and their drivers, then what they mean (and how they compare with the check before). */
function LatencyWords({ check }: { check: LatencyCheck | null }) {
  if (!check) return <>How long drivers hold the processor, measured for ten seconds. Changes nothing.</>
  return <>{sample(check.after)}. {check.said}</>
}

/** "Longest DPC 318 µs (nvlddmkm.sys), interrupt 41 µs (USBXHCI.SYS)". */
function sample(s: LatencySample): string {
  const dpc = `Longest DPC ${s.dpcUs} µs${s.dpcDriver ? ` (${s.dpcDriver})` : ''}`
  return s.isrUs > 0 ? `${dpc}, interrupt ${s.isrUs} µs${s.isrDriver ? ` (${s.isrDriver})` : ''}` : dpc
}

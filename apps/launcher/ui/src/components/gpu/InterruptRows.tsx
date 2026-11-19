import { useState } from 'react'
import { host, type InterruptDevice, type Interrupts } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import type { PcPlace } from './ThisPcTab'

const MODE: Record<InterruptDevice['mode'], string> = { msi: 'MSI mode', line: 'Shared line', none: 'Not running' }
/** A device's plan as a state beside its kind; why it is left alone is the host's reason, not shown. */
const PLAN: Record<InterruptDevice['plan'], string | null> = { change: null, done: null, keep: 'Left alone', skip: 'Left alone', restart: 'After a restart' }

/**
 * PC › Interrupts: one row that says whether every device that can use MSI mode does, with Optimize interrupts (one
 * permission prompt, then a restart Exo never does itself), Undo all, and after the restart what took. Devices lists
 * each one, its interrupt now and what Exo does to it, with Revert on the ones Exo switched. Devices still to switch put
 * the row in Needs you; once switched it stays in Performance for its Undo all and its restart; all on MSI with nothing
 * of Exo's to undo, it shows nothing. A driver update that reset them, where Windows would ask to set them again, is
 * one row in Needs you, Switch back.
 */
export function InterruptRows({ place, state, onState }: { place: PcPlace; state: Interrupts | null; onState: (next: Interrupts) => void }) {
  // What is being written, while it is: Optimize, Undo all, or one device's Revert (its id).
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState(false)

  const act = async (action: 'apply' | 'undo' | 'revert' | 'reapply', device?: string) => {
    if (busy) return
    setBusy(device ?? action)
    setError(null)
    try {
      const next = await host.systemInterrupts(action, device)
      onState(next)
      if (!next.ok) setError(trimStop(next.message) || 'Not changed. Try again.')
    } catch (e) {
      report(e, `InterruptRows.${action}`)
      setError('Not changed. Try again.')
    }
    setBusy(null)
  }

  if (!state || state.devices.length === 0) return null
  // A driver update put Windows' interrupt values back, and setting them again needs its permission: one row, one press.
  if (state.reset) {
    return place === 'needs' ? (
      <Row label="A driver update reset your interrupts" error={error}>
        <button type="button" className="exo-set-btn is-primary" disabled={!!busy} aria-busy={busy === 'reapply'} onClick={() => void act('reapply')}>
          {busy === 'reapply' ? <Spinner label="Switching your interrupts back" /> : null}Switch back
        </button>
      </Row>
    ) : null
  }
  const needs = state.devices.some(d => d.plan === 'change') && !state.restart
  if (place === 'needs' ? !needs : needs || (!state.restart && !state.canUndo)) return null
  const detail = interruptDetail(state)
  return (
    <>
      <Row label="Interrupts (MSI mode)" detail={detail} error={error}>
        {state.restart ? <State on>Restart to finish</State> : null}
        {needs
          ? <button type="button" className="exo-set-btn is-primary" disabled={!!busy} aria-busy={busy === 'apply'} onClick={() => void act('apply')}>
            {busy === 'apply' ? <Spinner label="Switching devices to MSI mode" /> : null}Optimize interrupts
          </button>
          : null}
        {state.canUndo
          ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => void act('undo')}>
            {busy === 'undo' ? <Spinner label="Putting every device's interrupt back" /> : null}Undo all
          </button>
          : null}
        <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Hide devices' : 'Devices'}</button>
      </Row>
      {open && state.devices.map(device => (
        <Row key={device.id} label={device.name} detail={[`${device.kind[0].toUpperCase()}${device.kind.slice(1)}`, PLAN[device.plan]].filter(Boolean).join(' · ')}>
          {device.changed
            ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === device.id} onClick={() => void act('revert', device.id)}>
              {busy === device.id ? <Spinner label={`Putting ${device.name}'s interrupt back`} /> : null}Revert
            </button>
            : <State on={device.mode === 'msi'}>{MODE[device.mode]}</State>}
        </Row>
      ))}
    </>
  )
}

/** What took after the restart, else how many devices still share a line; the state beside it says the rest. */
function interruptDetail(state: Interrupts): string | undefined {
  if (state.restart) return undefined
  // A device on MSI that Optimize still changes takes high priority (the graphics card and USB controllers).
  const sharing = state.devices.filter(d => d.plan === 'change' && d.mode !== 'msi').length
  return state.result || (sharing > 0 ? `${sharing === 1 ? 'A device shares' : `${sharing} devices share`} an interrupt line` : undefined)
}

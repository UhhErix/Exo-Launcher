import { useState } from 'react'
import { host, type InterruptDevice, type Interrupts } from '../../lib/host'
import { Row, State } from '../settings/parts'
import { ActionButton } from '../ui/ConfirmButton'
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
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState(false)

  // Each button shows its own work (ActionButton): the ring while Windows' prompt and the write run, then its check, and
  // then the rows take the new state; a refusal says why under the row. Behind Windows' prompt, so no time limit of its own.
  const act = (action: 'apply' | 'undo' | 'revert' | 'reapply', device?: string) => async () => {
    setError(null)
    const next = await host.systemInterrupts(action, device)
    if (!next.ok) onState(next)
    return next
  }
  const button = (action: 'apply' | 'undo' | 'revert' | 'reapply', words: string, primary = false, device?: string) => (
    <ActionButton className={primary ? 'exo-set-btn is-primary' : 'exo-set-btn'} timeoutMs={null} why="none" context={`InterruptRows.${action}`}
      fallback="Not changed. Try again." onPress={act(action, device)} onFailed={failure => setError(failure.text)}
      onDone={next => onState(next as Interrupts)}>{words}</ActionButton>
  )

  if (!state || state.devices.length === 0) return null
  // A driver update put Windows' interrupt values back, and setting them again needs its permission: one row, one press.
  if (state.reset) {
    return place === 'needs' ? (
      <Row label="A driver update reset your interrupts" error={error}>
        {button('reapply', 'Switch back', true)}
      </Row>
    ) : null
  }
  const needs = state.devices.some(d => d.plan === 'change') && !state.restart
  if (place === 'needs' ? !needs : needs || (!state.restart && !state.canUndo)) return null
  const detail = interruptDetail(state)
  return (
    <>
      <Row label="Hardware interrupts" detail={detail} error={error}>
        {state.restart ? <State on>Restart to finish</State> : null}
        {needs ? button('apply', 'Optimize', true) : null}
        {state.canUndo ? button('undo', 'Undo') : null}
        <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Hide' : 'Devices'}</button>
      </Row>
      {open && state.devices.map(device => (
        <Row key={device.id} label={device.name} detail={[`${device.kind[0].toUpperCase()}${device.kind.slice(1)}`, PLAN[device.plan]].filter(Boolean).join(' · ')}>
          {device.changed ? button('revert', 'Undo', false, device.id) : <State on={device.mode === 'msi'}>{MODE[device.mode]}</State>}
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

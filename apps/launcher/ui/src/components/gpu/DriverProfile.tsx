import { useState } from 'react'
import type { GpuProfile, IntelLowLatency } from '../../lib/host'
import { trimStop } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { ActionButton } from '../ui/ConfirmButton'
import type { PlainError } from '../../lib/errorWords'

const LOW_LATENCY = [
  { value: 'on', label: 'On' },
  { value: 'boost', label: 'On with boost' },
  { value: 'off', label: 'Off' },
] as const

export const PROFILE_ROW = 'Exo’s driver profile'

/** The latency choice beside Use: an Intel GPU's Low latency mode. */
export type LatencyChoice = { lowLatency?: IntelLowLatency }

/**
 * PC › Graphics › Driver and profile on a Radeon or an Intel GPU: Exo's profile through the GPU's own driver (a Radeon's
 * with Anti-Lag always on, no choice; an Intel GPU's with its Low latency mode beside Use, On by default), and Undo while
 * Exo's backup is there.
 */
export function DriverProfile({ profile, error, onAct, onSettled, onFailed }: {
  profile: GpuProfile
  error: string | null
  onAct: (action: 'apply' | 'undo', latency: LatencyChoice) => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (failure: PlainError) => void
}) {
  const intel = profile.vendor === 'intel'
  const saved = profile.lowLatency ?? 'on'
  const [choice, setChoice] = useState<string | null>(null)
  const latency = choice ?? saved
  const changed = intel && latency !== saved
  const act = (action: 'apply' | 'undo') => {
    setChoice(null)
    return onAct(action, intel ? { lowLatency: latency as IntelLowLatency } : {})
  }
  return (
    <Row label={PROFILE_ROW} detail={changed ? null : changedLine(profile)}
      error={error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer. Try again.')}>
      {profile.status === 'applied' && !changed ? <State on>Applied</State> : null}
      {intel ? <Choice label="Low latency" value={latency} choices={LOW_LATENCY} onChange={setChoice} /> : null}
      <ProfileButtons apply={applyWords(profile, changed)} undo={!!profile.canUndo} onAct={act} onSettled={onSettled} onFailed={onFailed} />
    </Row>
  )
}

/**
 * Exo's profile's Use (or Re-apply), named by `apply`, and Undo while Exo's backup is there. Each shows its own work
 * (ActionButton): the ring while the driver is written, then its check, and then the row takes the driver's answer.
 */
export function ProfileButtons({ apply, undo, onAct, onSettled, onFailed }: {
  apply: string | null
  undo: boolean
  onAct: (action: 'apply' | 'undo') => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (failure: PlainError) => void
}) {
  const press = (action: 'apply' | 'undo') => ({
    onPress: () => onAct(action), onDone: onSettled, onFailed, why: 'none' as const, context: `ProfileRows.${action}`, fallback: 'Not changed. Try again.',
  })
  return (
    <>
      {apply ? <ActionButton className="exo-set-btn is-primary" {...press('apply')}>{apply}</ActionButton> : null}
      {undo ? <ActionButton className="exo-set-btn" {...press('undo')}>Undo</ActionButton> : null}
    </>
  )
}

/** What Use says (Re-apply for what changed since), or null with nothing to apply. The profile is taken only after the button's check, so it stays while it writes. */
function applyWords(profile: GpuProfile, changed: boolean) {
  if (profile.status !== 'ready' && profile.status !== 'changed' && !changed) return null
  return profile.status === 'changed' && !changed ? 'Re-apply' : 'Use'
}

/** What changed since Exo's profile was applied, on its row. */
export function changedLine(profile: GpuProfile) {
  if (profile.status !== 'changed') return null
  return profile.driverUpdated ? 'Driver changed some settings' : 'Some settings changed'
}

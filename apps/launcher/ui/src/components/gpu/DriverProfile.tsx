import { useState } from 'react'
import type { GpuProfile, IntelLowLatency } from '../../lib/host'
import { trimStop } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { gpuOffers } from '../../lib/canUse'
import { Spinner } from '../ui/Spinner'

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
export function DriverProfile({ profile, busy, error, onAct }: {
  profile: GpuProfile
  /** What is being written, while it is. */
  busy: 'apply' | 'undo' | null
  error: string | null
  onAct: (action: 'apply' | 'undo', latency: LatencyChoice) => void
}) {
  const intel = gpuOffers(profile.vendor).lowLatency
  const saved = profile.lowLatency ?? 'on'
  const [choice, setChoice] = useState<string | null>(null)
  const latency = choice ?? saved
  const changed = intel && latency !== saved
  const act = (action: 'apply' | 'undo') => {
    setChoice(null)
    onAct(action, intel ? { lowLatency: latency as IntelLowLatency } : {})
  }
  return (
    <Row label={PROFILE_ROW} detail={changed ? null : changedLine(profile)}
      error={error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer. Try again.')}>
      {profile.status === 'applied' && !changed ? <State on>Applied</State> : null}
      {intel ? <Choice label="Low latency" value={latency} choices={LOW_LATENCY} disabled={!!busy} onChange={setChoice} /> : null}
      <ProfileButtons apply={applyWords(profile, changed, busy === 'apply')} undo={!!profile.canUndo} busy={busy} onAct={act} />
    </Row>
  )
}

/**
 * Exo's profile's Use (or Re-apply), named by `apply`, and Undo while Exo's backup is there. The one pressed keeps its
 * words and carries the bar while the driver is written.
 */
export function ProfileButtons({ apply, undo, busy, onAct }: {
  apply: string | null
  undo: boolean
  busy: 'apply' | 'undo' | null
  onAct: (action: 'apply' | 'undo') => void
}) {
  return (
    <>
      {apply
        ? <button type="button" className="exo-set-btn is-primary" disabled={!!busy} aria-busy={busy === 'apply'} onClick={() => onAct('apply')}>
          {busy === 'apply' ? <Spinner label="Writing Exo’s profile to the driver" /> : null}{apply}
        </button>
        : null}
      {undo
        ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => onAct('undo')}>
          {busy === 'undo' ? <Spinner label="Putting your driver settings back" /> : null}Undo
        </button>
        : null}
    </>
  )
}

/** What Use says (Re-apply for what changed since), or null with nothing to apply; kept while it writes, as pressing it put the latency choice back. */
function applyWords(profile: GpuProfile, changed: boolean, writing: boolean) {
  if (profile.status !== 'ready' && profile.status !== 'changed' && !changed && !writing) return null
  return profile.status === 'changed' && !changed ? 'Re-apply' : 'Use'
}

/** What changed since Exo's profile was applied, on its row. */
export function changedLine(profile: GpuProfile) {
  if (profile.status !== 'changed') return null
  return profile.driverUpdated ? 'Driver changed some settings' : 'Some settings changed'
}

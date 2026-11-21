import { useState } from 'react'
import { host, type GpuProfile } from '../../lib/host'
import { trimStop } from '../../lib/utils'
import { changedLine, DriverProfile, PROFILE_ROW, ProfileButtons, type LatencyChoice } from './DriverProfile'
import { Row, State } from '../settings/parts'
import { Switch } from '../Switch'
import type { PlainError } from '../../lib/errorWords'
import type { PcPlace } from './ThisPcTab'
import { gpuOffers } from '../../lib/canUse'
import { Afmf3Row } from './Afmf3Row'

/** Exo's profile still to use or to put back (a driver update moved a setting): the card's row goes in Needs you. */
export function profileNeedsYou(profile: GpuProfile | null): boolean {
  return !!profile?.ok && !!profile.vendor && profile.status !== 'manual' && (profile.status === 'ready' || profile.status === 'changed')
}

/**
 * PC › Graphics, the driver profile: Exo's profile for this PC's card, one press to use it and Undo. The room reads it
 * again when Exo's window comes back, so a driver update that moved a setting shows with Re-apply, in Best settings until
 * it is. Under it a Profile Inspector export can be imported to the global profile instead, previewed before anything is
 * written. A Radeon and an Intel GPU get their own profile through their driver; one Exo can't reach shows what to set by
 * hand.
 */
export function ProfileRows({ place, profile, onProfile }: {
  place: PcPlace
  profile: GpuProfile | null
  onProfile: (next: GpuProfile) => void
}) {
  const [error, setError] = useState<string | null>(null)

  // The pressed button shows the work (ProfileButtons); the row takes the driver's answer after its check, or at once on a refusal.
  const act = async (action: 'apply' | 'undo', latency?: LatencyChoice) => {
    setError(null)
    const next = await host.gpuProfile(action, latency)
    if (!next.ok) onProfile(next)
    return next
  }
  const settle = (next: unknown) => onProfile(next as GpuProfile)
  const failed = (failure: PlainError) => setError(failure.text)

  if (!profile || (profile.status === 'none' && profile.ok)) return null
  const needs = profileNeedsYou(profile)
  const nvidia = profile.vendor === 'nvidia'
  const tuned = (profile.vendor === 'amd' || profile.vendor === 'intel') && profile.status !== 'manual'

  // The card's own row: in Needs you while Exo's profile isn't on it, else here with its Undo and its choice.
  const card = tuned
    ? <DriverProfile profile={profile} error={error} onAct={act} onSettled={settle} onFailed={failed} />
    : nvidia
      ? <NvidiaRow profile={profile} error={error} onAct={(action, opts) => act(action, opts)} onSettled={settle} onFailed={failed} />
      : null
  if (place === 'needs') return needs ? card : null
  if (!tuned && !nvidia) return <ManualProfile profile={profile} />

  return (
    <>
      {needs ? null : card}
      {nvidia ? <NvidiaDetails profile={profile} /> : null}
      {gpuOffers(profile.vendor).afmf3 ? <Afmf3Row /> : null}
    </>
  )
}

function nvidiaApplyLabel(status: GpuProfile['status']): 'Use' | 'Re-apply' | null {
  if (status === 'ready') return 'Use'
  return status === 'changed' ? 'Re-apply' : null
}

function NvidiaRow({ profile, error, onAct, onSettled, onFailed }: {
  profile: GpuProfile
  error: string | null
  onAct: (action: 'apply' | 'undo', opts?: LatencyChoice) => Promise<unknown>
  onSettled: (next: unknown) => void
  onFailed: (failure: PlainError) => void
}) {
  const [vrrBusy, setVrrBusy] = useState(false)
  const vrr = profile.vrrEnabled ?? profile.screen.vrr
  const apply = nvidiaApplyLabel(profile.status)
  const showApplied = profile.status === 'applied'
  const rowError = error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer. Try again.')

  const act = (action: 'apply' | 'undo') =>
    onAct(action, action === 'apply' ? { lowLatency: 'on', ...(profile.screen.vrr ? { vrr } : {}) } : undefined)

  const toggleVrr = async (next: boolean) => {
    if (vrrBusy) return
    setVrrBusy(true)
    try {
      const res = await onAct('apply', { lowLatency: 'on', vrr: next })
      if ((res as GpuProfile).ok) onSettled(res)
    } catch (e) {
      onFailed({ text: trimStop(e instanceof Error ? e.message : String(e)) || 'Not changed. Try again.', detail: null })
    } finally {
      setVrrBusy(false)
    }
  }

  return (
    <Row label={PROFILE_ROW} detail={changedLine(profile)} error={rowError}>
      {showApplied ? <State on>Applied</State> : null}
      {profile.screen.vrr ? (
        <Switch label="G-SYNC" checked={vrr} disabled={vrrBusy} onChange={next => void toggleVrr(next)} />
      ) : null}
      {(apply || profile.canUndo || showApplied)
        ? <ProfileButtons apply={apply} undo={!!profile.canUndo} onAct={act} onSettled={onSettled} onFailed={onFailed} />
        : null}
    </Row>
  )
}

/** Resizable BAR status, feature states, skipped warning, and < 50 GB shader cache note. */
function NvidiaDetails({ profile }: { profile: GpuProfile }) {
  const features = profile.features ?? []
  const lowFreeSpace = typeof profile.cacheFreeGb === 'number' && profile.cacheFreeGb < 50
  if (features.length === 0 && profile.rebar == null && !profile.skipped && !lowFreeSpace) return null
  return (
    <>
      {profile.rebar != null && (
        <Row label="Resizable BAR">
          <State on={profile.rebar}>{profile.rebar ? 'On' : 'Off'}</State>
        </Row>
      )}
      {features.map(feature => (
        <Row key={feature.key} label={feature.label}>
          <State on={feature.state === 'on'}>
            {feature.state === 'on' ? 'On' : feature.state === 'unavailable' ? 'Unavailable on this driver' : 'Off'}
          </State>
        </Row>
      ))}
      {lowFreeSpace && (
        <p className="exo-graphics-import-extra">
          Shader cache stays at driver default because under 50 GB is free on the cache drive ({profile.cacheFreeGb} GB free).
        </p>
      )}
      {profile.skipped && (
        <p className="exo-graphics-import-extra">
          Some driver settings were skipped because this driver version did not list them.
        </p>
      )}
    </>
  )
}

/** A Radeon or an Intel GPU Exo can't reach: what to set by hand, next to what their driver holds. */
function ManualProfile({ profile }: { profile: GpuProfile }) {
  const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()
  return (
    <>
      {profile.message && <p className="exo-hub-sub">{profile.message}</p>}
      {profile.items.map(item => (
        <Row key={item.key} label={item.label}>
          <State on={!!item.current && same(item.current, item.value)}>
            {item.value}{item.current && !same(item.current, item.value) ? ` (now ${item.current})` : ''}
          </State>
        </Row>
      ))}
    </>
  )
}

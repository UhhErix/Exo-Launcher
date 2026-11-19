import { useEffect, useRef, useState } from 'react'
import { host, type GpuImportAnswer, type GpuProfile } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { changedLine, DriverProfile, PROFILE_ROW, ProfileButtons, type LatencyChoice } from './DriverProfile'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { Unfold } from '../ui/Unfold'
import type { PlainError } from '../../lib/errorWords'
import type { PcPlace } from './ThisPcTab'
import { gpuOffers } from '../../lib/canUse'
import { Afmf3Row } from './Afmf3Row'

const NVIDIA_LATENCY = [
  { value: 'off', label: 'Off' },
  { value: 'on', label: 'On' },
  { value: 'ultra', label: 'Ultra' },
] as const

const NVIDIA_GSYNC = [
  { value: 'on', label: 'G-SYNC on' },
  { value: 'off', label: 'G-SYNC off' },
] as const

type Preview = NonNullable<GpuImportAnswer['preview']>

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
      {gpuOffers(profile.vendor).profileImport ? <ImportRow /> : null}
      {gpuOffers(profile.vendor).afmf3 ? <Afmf3Row /> : null}
    </>
  )
}

/** A GeForce's row: Exo's profile, Low Latency and G-SYNC choices, and Apply/Undo controls. */
function nvidiaApplyLabel(status: GpuProfile['status'], dirty: boolean): 'Use' | 'Re-apply' | null {
  if (dirty) return status === 'changed' ? 'Re-apply' : 'Use'
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
  const savedLatency = (profile.lowLatency === 'off' || profile.lowLatency === 'ultra' ? profile.lowLatency : 'on') as 'off' | 'on' | 'ultra'
  const savedVrr = profile.vrrEnabled ?? profile.screen.vrr
  const [latencyChoice, setLatencyChoice] = useState<'off' | 'on' | 'ultra' | null>(null)
  const [vrrChoice, setVrrChoice] = useState<boolean | null>(null)

  const latency = latencyChoice ?? savedLatency
  const vrr = vrrChoice ?? savedVrr
  const dirty = latency !== savedLatency || (profile.screen.vrr && vrr !== savedVrr)
  const apply = nvidiaApplyLabel(profile.status, dirty)
  const showApplied = profile.status === 'applied' && !dirty
  const rowError = error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer. Try again.')

  const act = (action: 'apply' | 'undo') => {
    setLatencyChoice(null)
    setVrrChoice(null)
    const opts = profile.screen.vrr ? { lowLatency: latency, vrr } : { lowLatency: latency }
    return onAct(action, action === 'apply' ? opts : undefined)
  }

  return (
    <Row label={PROFILE_ROW} detail={dirty ? null : changedLine(profile)} error={rowError}>
      {showApplied ? <State on>Applied</State> : null}
      <Choice label="Low latency" value={latency} choices={NVIDIA_LATENCY} onChange={v => setLatencyChoice(v as 'off' | 'on' | 'ultra')} />
      {profile.screen.vrr ? (
        <Choice label="G-SYNC" value={vrr ? 'on' : 'off'} choices={NVIDIA_GSYNC} onChange={v => setVrrChoice(v === 'on')} />
      ) : null}
      {(apply || profile.canUndo || showApplied)
        ? <ProfileButtons apply={apply} undo={!!profile.canUndo} onAct={act} onSettled={onSettled} onFailed={onFailed} />
        : null}
    </Row>
  )
}

/** Per-setting NVIDIA breakdown, Resizable BAR status, feature states, skipped warning, and < 50 GB shader cache note. */
function NvidiaDetails({ profile }: { profile: GpuProfile }) {
  const features = profile.features ?? []
  const details = profile.details ?? []
  const lowFreeSpace = typeof profile.cacheFreeGb === 'number' && profile.cacheFreeGb < 50
  if (details.length === 0 && features.length === 0 && profile.rebar == null && !profile.skipped && !lowFreeSpace) return null
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
      {details.length > 0 && (
        <ul className="exo-graphics-import-list" aria-label="NVIDIA profile settings">
          {details.map(item => (
            <li key={item.key}>
              <span>{item.label}</span>
              <span className="exo-graphics-import-value">
                {item.value}
                {item.current && item.current !== item.value ? ` (now ${item.current})` : ''}
              </span>
            </li>
          ))}
        </ul>
      )}
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

/**
 * Import profile: a Profile Inspector export of the global profile, read into a list first; nothing is written until
 * Apply, and Undo puts back what it replaced. The list opens in place under the row, where Import was pressed.
 */
function ImportRow() {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [undo, setUndo] = useState<GpuImportAnswer['undo']>(null)
  const importButton = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    let live = true
    host.gpuImport('state').then(answer => { if (live) setUndo(answer.undo ?? null) }, () => undefined)
    return () => { live = false }
  }, [])

  const run = async (what: 'pick' | 'apply' | 'undo') => {
    if (busy) return
    setBusy(what)
    setError(null)
    try {
      const answer = await host.gpuImport(what)
      setUndo(answer.undo ?? null)
      if (answer.ok && answer.preview) setPreview(answer.preview)
      if (!answer.ok && !answer.cancelled) setError(trimStop(answer.message) || 'Not changed. Try again.')
    } catch (e) {
      report(e, `ProfileRows.import.${what}`)
      setError('Not changed. Try again.')
    }
    setBusy(null)
  }
  const cancel = () => {
    setPreview(null)
    importButton.current?.focus({ preventScroll: true })
    void host.gpuImport('cancel').catch(() => undefined)
  }

  return (
    <>
      <Row
        label={undo?.profile ? `Imported “${undo.profile}”` : undo ? 'Imported profile' : 'Profile Inspector file'}
        error={error}
      >
        {/* Undo import carries the ring while it puts the profile back; Import… while the file is read and, after Apply, written. */}
        {undo && (
          <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => void run('undo')}>
            {busy === 'undo' ? <Spinner label="Putting back the settings the import replaced" /> : null}Undo import
          </button>
        )}
        <button ref={importButton} type="button" className="exo-set-btn" disabled={!!preview || !!busy} aria-busy={busy === 'pick' || busy === 'apply'}
          aria-expanded={!!preview} onClick={() => void run('pick')}>
          {busy === 'pick' || busy === 'apply'
            ? <Spinner label={busy === 'pick' ? 'Reading the profile file' : 'Writing the imported profile to the driver'} />
            : null}
          Import…
        </button>
      </Row>
      <Unfold open={!!preview} className="exo-graphics-import" onEscape={cancel}>
        {preview ? <ImportPreview preview={preview} busy={!!busy} onCancel={cancel} onApply={() => { setPreview(null); void run('apply') }} /> : null}
      </Unfold>
    </>
  )
}

/**
 * What the file would change, in plain words, before anything is written: the profile's name and each setting a line,
 * then Cancel (focused as it opens, so Enter alone never applies it) and Apply to every game.
 */
function ImportPreview({ preview, busy, onCancel, onApply }: {
  preview: Preview
  busy: boolean
  onCancel: () => void
  onApply: () => void
}) {
  const cancelButton = useRef<HTMLButtonElement>(null)
  useEffect(() => cancelButton.current?.focus({ preventScroll: true }), [])
  const extra = [
    preview.unknown > 0 ? `${preview.unknown} more Exo has no name for` : null,
    preview.leftOut > 0 ? `${preview.leftOut} left out` : null,
  ].filter(Boolean).join(', ')
  return (
    <>
      <ul className="exo-graphics-import-list" aria-label="What the import changes">
        {preview.profile ? <li><span>Profile</span><span className="exo-graphics-import-value">{preview.profile}</span></li> : null}
        {preview.items.map((item, index) => (
          <li key={index}><span>{item.label}</span><span className="exo-graphics-import-value">{item.value}</span></li>
        ))}
      </ul>
      {extra && <p className="exo-graphics-import-extra">{extra}</p>}
      <div className="exo-graphics-import-actions">
        <button ref={cancelButton} type="button" className="exo-set-btn" onClick={onCancel}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={onApply}>Apply to every game</button>
      </div>
    </>
  )
}

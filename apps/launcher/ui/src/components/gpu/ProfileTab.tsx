import { useEffect, useRef, useState } from 'react'
import { host, type GpuImportAnswer, type GpuProfile } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { changedLine, DriverProfile, PROFILE_ROW, ProfileButtons, type LatencyChoice } from './DriverProfile'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { Unfold } from '../ui/Unfold'
import type { PcPlace } from './ThisPcTab'
import { Afmf3Row } from './Afmf3Row'

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
  const [busy, setBusy] = useState<'apply' | 'undo' | null>(null)
  const [error, setError] = useState<string | null>(null)

  const act = async (action: 'apply' | 'undo', latency?: LatencyChoice) => {
    if (busy) return
    setBusy(action)
    setError(null)
    try {
      const next = await host.gpuProfile(action, latency)
      onProfile(next)
      if (!next.ok) setError(trimStop(next.message) || 'Not changed. Try again.')
    } catch (e) {
      report(e, `ProfileRows.${action}`)
      setError('Not changed. Try again.')
    }
    setBusy(null)
  }

  if (!profile || (profile.status === 'none' && profile.ok)) return null
  const needs = profileNeedsYou(profile)
  const nvidia = profile.vendor === 'nvidia'
  const tuned = (profile.vendor === 'amd' || profile.vendor === 'intel') && profile.status !== 'manual'

  // The card's own row: in Needs you while Exo's profile isn't on it, else here with its Undo and its choice.
  const card = tuned
    ? <DriverProfile profile={profile} busy={busy} error={error} onAct={(action, latency) => void act(action, latency)} />
    : nvidia
      ? <NvidiaRow profile={profile} busy={busy} error={error} onAct={action => void act(action)} />
      : null
  if (place === 'needs') return needs ? card : null
  if (!tuned && !nvidia) return <ManualProfile profile={profile} />

  return (
    <>
      {needs ? null : card}
      {nvidia ? <ImportRow /> : null}
      {profile.vendor === 'amd' ? <Afmf3Row /> : null}
    </>
  )
}

/** A GeForce's row: Exo's profile, what changed since it was applied, and whether it is. */
function NvidiaRow({ profile, busy, error, onAct }: {
  profile: GpuProfile
  busy: 'apply' | 'undo' | null
  error: string | null
  onAct: (action: 'apply' | 'undo') => void
}) {
  return (
    <Row label={PROFILE_ROW} detail={changedLine(profile)}
      error={error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer. Try again.')}>
      {profile.status === 'applied' ? <State on>Applied</State> : null}
      <ProfileAction profile={profile} busy={busy} onAct={onAct} />
    </Row>
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

/** Use (or switch to) the picked variant, Re-apply what changed since, and Undo while Exo's backup is there. */
function ProfileAction({ profile, busy, onAct }: { profile: GpuProfile; busy: 'apply' | 'undo' | null; onAct: (action: 'apply' | 'undo') => void }) {
  const apply = profile.status === 'ready' ? 'Use' : profile.status === 'changed' ? 'Re-apply' : null
  if (!apply && profile.status !== 'applied') return null
  return <ProfileButtons apply={apply} undo={!!profile.canUndo} busy={busy} onAct={onAct} />
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

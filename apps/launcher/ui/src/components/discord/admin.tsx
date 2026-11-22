import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { useLayer } from '../../lib/layer'
import { report } from '../../lib/report'
import { Spinner } from '../ui/Spinner'
import { Dialog } from '../ui/Dialog'
import { CopyLogs, ErrorNote } from '../ui/ErrorNote'
import { ConfirmLabel, useConfirmInPlace } from '../ui/ConfirmButton'
import { cn } from '../../lib/utils'
import { errorText } from '../../lib/errorWords'
import { SUCCESS_HOLD_MS } from '../../lib/actionState'
import { CHECK_PATH } from '../../brand/icons'

/** Discord wants the person to verify first: the check's id (the ticket stays in Exo's host) and the ways Discord offers. */
export type MfaAsk = { id: string; methods: string[] }

/** One discord.admin reply: data is Discord's own (or Exo's summary for the person menu and Edit Channel). */
export type AdminReply<T> = { ok: boolean; message?: string | null; data?: T | null; mfa?: MfaAsk | null }

type MfaShow = (ask: MfaAsk, done: (reply: AdminReply<unknown>) => void) => void
let mfaShow: MfaShow | null = null

/** The room's verify dialog (MfaHost) takes Discord's checks while it is mounted. */
export function onMfaAsk(show: MfaShow) {
  mfaShow = show
  return () => { if (mfaShow === show) mfaShow = null }
}

export async function admin<T = unknown>(action: string, params: Record<string, unknown>): Promise<AdminReply<T>> {
  try {
    const reply = await host.discordAdmin(action, params) as AdminReply<T>
    // Discord's "it's really you" check: its dialog, then the same request's own answer (or the cancel).
    const show = mfaShow
    if (reply.mfa && show) return await new Promise<AdminReply<unknown>>(resolve => show(reply.mfa!, resolve)) as AdminReply<T>
    return reply
  } catch (error) {
    report(error, 'discord.admin.' + action)
    return { ok: false, message: 'Discord did not answer. Try again.' }
  }
}

/** One of discord.admin's reads, kept while open; reload asks Discord again, set changes it in place. */
export function useAdmin<T>(action: string, params: Record<string, unknown>) {
  const key = JSON.stringify(params)
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let live = true
    void admin<T>(action, JSON.parse(key) as Record<string, unknown>).then(result => {
      if (!live) return
      if (result.ok) { setData(result.data ?? null); setError(null) } else setError(errorText(result.message, 'Discord didn’t answer.'))
    })
    return () => { live = false }
  }, [action, key, tick])
  return { data, error, set: setData, reload: useCallback(() => setTick(t => t + 1), []) }
}

/** Lets the host drop a picture a save didn't take, so it stops counting toward the upload limit. */
export function dropUpload(uploadId: string | undefined) {
  if (uploadId) void host.discordUpload({ uploadId, cancel: true }).catch(() => { })
}

/** A read that failed: why, and Try Again. A page keeps showing what it had. */
export function ReadError({ error, onRetry }: { error: string | null; onRetry: () => void }) {
  if (!error) return null
  return <p className="exo-dc-note is-bad" role="alert">{error} <button type="button" className="exo-dc-tab is-raised" onClick={onRetry}>Try again</button> <CopyLogs error={error} /></p>
}

/** The page's layers (lib/layer.ts): kept here too, where the Discord room has always found it. */
export { useLayer }

type Unsaved = { mark: (dirty: boolean) => void; nudge: number; guard: (leave: () => void) => void }
const UnsavedContext = createContext<Unsaved | null>(null)
export const UnsavedProvider = UnsavedContext.Provider

/** An overlay's unsaved changes, Discord's way: leaving while its save bar shows shakes the bar red instead. */
export function useUnsaved(): Unsaved {
  // How many save bars show: a one-column sheet (Edit channel) can hold more than one.
  const dirty = useRef(0)
  const [nudge, setNudge] = useState(0)
  const mark = useCallback((on: boolean) => { dirty.current = Math.max(0, dirty.current + (on ? 1 : -1)); if (!dirty.current) setNudge(0) }, [])
  const guard = useCallback((leave: () => void) => { if (dirty.current) setNudge(n => n + 1); else leave() }, [])
  return useMemo(() => ({ mark, nudge, guard }), [mark, nudge, guard])
}

/** Leave this page only when nothing is unsaved (else the save bar shakes). */
export function useGuard() {
  return useContext(UnsavedContext)?.guard ?? ((leave: () => void) => leave())
}

/** Choices to pick any of, as chips: AutoMod's exemptions, an onboarding answer's channels and roles. */
export function PickChips({ label, ids, options, onChange }: {
  label: string
  ids: string[]
  options: { id: string; name: string; color?: string }[]
  onChange: (ids: string[]) => void
}) {
  return (
    <section className="exo-dc-perm-group" aria-label={label}>
      <h3>{label}</h3>
      <div className="exo-dc-chip-list">
        {options.map(option => (
          <button key={option.id} type="button" className="exo-shelf-chip" aria-pressed={ids.includes(option.id)}
            onClick={() => onChange(ids.includes(option.id) ? ids.filter(id => id !== option.id) : [...ids, option.id])}>
            {option.color ? <span className="exo-dc-role-dot" style={{ background: option.color }} aria-hidden="true" /> : null}
            {option.name}
          </button>
        ))}
      </div>
    </section>
  )
}

/** Discord's bar for unsaved changes: Reset and Save Changes, with the error under it when Discord refused. */
/**
 * Discord's unsaved-changes bar. It says how a save went in itself: the ring on Save while it works; on success the
 * bar stays a beat (SUCCESS_HOLD_MS) saying so with the check drawn where Save was, then goes; a refusal turns Save
 * Bad, shakes it once, and the bar's words say why (errorText), Save then trying again.
 */
export function SaveBar({ shown, busy, error, onReset, onSave }: { shown: boolean; busy: boolean; error: string | null; onReset: () => void; onSave: () => void }) {
  const unsaved = useContext(UnsavedContext)
  const mark = unsaved?.mark
  useEffect(() => {
    if (!shown || !mark) return
    mark(true)
    return () => mark(false)
  }, [mark, shown])
  // A save that ended without an error while the bar was up: held as done for a beat, even once nothing is unsaved.
  const [saved, setSaved] = useState(false)
  const [wasBusy, setWasBusy] = useState(busy)
  if (busy !== wasBusy) {
    setWasBusy(busy)
    if (!busy && !error) setSaved(true)
    if (busy) setSaved(false)
  }
  useEffect(() => {
    if (!saved) return
    const timer = window.setTimeout(() => setSaved(false), SUCCESS_HOLD_MS)
    return () => window.clearTimeout(timer)
  }, [saved])
  const done = saved && !shown
  if (!shown && !done) return null
  const nudge = unsaved?.nudge ?? 0
  const failed = !busy && !!error
  // A new key each nudge plays the shake again.
  return (
    <div key={nudge} className={`exo-dc-savebar exo-glass${nudge ? ' is-nudged' : ''}`} role="status">
      <span className={failed ? 'exo-dc-savebar-why' : undefined}>{done ? 'Changes saved' : error ? errorText(error, 'Discord didn’t save that. Try again.') : 'Unsaved changes'}</span>
      {done ? (
        <span className="exo-action is-success exo-dc-savebar-done" aria-hidden="true">
          <svg className="exo-action-check" width="18" height="18" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>
        </span>
      ) : (
        <>
          <button type="button" className="exo-dc-tab" disabled={busy} onClick={onReset}>Reset</button>
          <button type="button" key={failed ? 'failed' : 'save'} className={cn('exo-set-btn is-primary', failed && 'exo-action is-error')} disabled={busy} onClick={onSave}>
            {busy ? <Spinner label="Saving changes" /> : null}Save changes
          </button>
        </>
      )}
    </div>
  )
}

/** A question before something that can't be taken back, in Discord's room: Exo's one popover (ui/Dialog.tsx), hung
 * from the control that asked and drawn in the room so its words keep Discord's colors. Cancel (focused: Enter alone
 * never confirms), then the action, in Bad when it destroys. */
export function Confirm({ title, children, action, danger = true, busy, disabled, error, form, onConfirm, onClose }: {
  title: string
  children?: React.ReactNode
  action: string
  danger?: boolean
  /** The action is running: its ring shows before its words. */
  busy?: boolean
  /** The action can't be taken yet (a field still empty); nothing is running. */
  disabled?: boolean
  error?: string | null
  /** Its body holds fields (a name, a reason). */
  form?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Dialog title={title} form={form} portal={false} onClose={onClose} actions={
      <>
        <button type="button" className="exo-set-btn" data-autofocus onClick={onClose}>Cancel</button>
        <button type="button" className={`exo-set-btn ${danger ? 'is-danger' : 'is-primary'}`} disabled={busy || disabled} onClick={onConfirm}>
          {busy ? <Spinner label={action} /> : null}{action}
        </button>
      </>
    }>
      {children}
      {error ? <ErrorNote className="exo-dialog-error" message={error} /> : null}
    </Dialog>
  )
}

const STRANGER = 'No shared server. Accept?'

/**
 * Discord's ask before accepting a request from someone you share no server with (it refuses the plain accept, 80013),
 * asked in place on the Accept control (DESIGN.md › Confirm): `refused()` when Discord refuses, and the control arms once
 * its work is over (`busy` false); a second press accepts anyway. Spread `ref` and `className` on the control, press it
 * through `press` while `armed`, and show its words through `label`.
 */
export function useStrangerAsk(onConfirm: () => void, busy: boolean) {
  const ask = useConfirmInPlace<HTMLButtonElement>(STRANGER, onConfirm)
  const refused = useRef(false)
  const { arm } = ask
  useEffect(() => { if (refused.current && !busy) { refused.current = false; arm() } }, [busy, arm])
  return {
    ref: ask.ref,
    armed: ask.armed,
    className: cn('exo-confirm', ask.className),
    press: ask.press,
    refused: () => { refused.current = true },
    label: (words: React.ReactNode) => <ConfirmLabel armed={ask.armed} question={STRANGER}>{words}</ConfirmLabel>,
  }
}

/** Opens Windows' file picker for one file of these types. */
export function pickFile(accept: string): Promise<File | null> {
  return new Promise(resolve => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = accept
    input.onchange = () => resolve(input.files?.[0] ?? null)
    input.oncancel = () => resolve(null)
    input.click()
  })
}

/** A picture or a file of yours, as a data URL to show before it's saved. */
export function previewOf(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

/** A Discord color number as CSS (the muted gray for no color). */
export const colorOf = (color: number | null | undefined) => color ? `#${color.toString(16).padStart(6, '0')}` : 'var(--dc-muted)'

/** A name as Discord shows people: server nickname, else display name, else username. */
export const personName = (user: { username: string; global_name?: string | null } | null | undefined, nick?: string | null) =>
  nick || user?.global_name || user?.username || 'Someone'

/** "3 days", "5 hours": how long until a time, for invite expiry. */
export function untilLabel(iso: string | null | undefined) {
  if (!iso) return 'Never'
  const ms = Date.parse(iso) - Date.now()
  if (ms <= 0) return 'Expired'
  const minutes = Math.round(ms / 60_000)
  if (minutes < 60) return `${minutes} min`
  const hours = Math.round(minutes / 60)
  return hours < 48 ? `${hours} hours` : `${Math.round(hours / 24)} days`
}

/** A settings sheet's pages down its left, as the app's own Settings has them: group names, then the pages. */
export function SheetNav<T extends string>({ title, groups, current, onPick }: {
  title: string
  groups: { heading: string | null; items: { id: T; label: string }[] }[]
  current: T
  onPick: (id: T) => void
}) {
  return (
    <nav className="exo-settings-nav exo-dc-sheet-nav" aria-label={title}>
      <h2 className="exo-settings-nav-title">{title}</h2>
      {groups.filter(group => group.items.length > 0).map(group => (
        <div key={group.heading ?? group.items[0].id} className="exo-dc-sheet-group">
          {group.heading ? <span className="exo-dc-sheet-label">{group.heading}</span> : null}
          <ul className="exo-settings-nav-pages">
            {group.items.map(item => (
              <li key={item.id}><button type="button" aria-current={item.id === current ? 'page' : undefined} onClick={() => onPick(item.id)}>{item.label}</button></li>
            ))}
          </ul>
        </div>
      ))}
    </nav>
  )
}

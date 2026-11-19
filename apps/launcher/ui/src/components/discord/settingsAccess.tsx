import { RowsWait } from './waits'
import { useEffect, useState } from 'react'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Dialog as ExoDialog } from '../ui/Dialog'
import { Menu, useChoiceMenu } from '../ui/Menu'
import { admin, ReadError, SaveBar, useAdmin, type AdminReply } from './admin'
import { Row } from '../settings/parts'
import { Icons } from './icons'
import { LANGUAGES } from './settings/AppPages'
import type { PageGuild } from './settingsPages'
import { ErrorNote } from '../ui/ErrorNote'

/**
 * Discord's Access, as its web client draws and saves it: how people join (Invite only, Apply to join, Discoverable),
 * whether the server is age-restricted, and each way's own part: the rules members agree to, the application's
 * questions, or Server Discovery's checklist and listing. Changes wait for Save changes, as in Discord.
 */

type Kind = 'TERMS' | 'TEXT_INPUT' | 'PARAGRAPH' | 'MULTIPLE_CHOICE'
type FormField = { field_type: Kind; label: string; values?: string[]; choices?: string[]; required?: boolean }
type Mode = 'invite' | 'apply' | 'discoverable'
export type AccessGuild = PageGuild & { owner_configured_content_level?: number; description?: string | null; preferred_locale?: string | null }

const MODES: { id: Mode; label: string }[] = [
  { id: 'invite', label: 'Invite only' },
  { id: 'apply', label: 'Apply to join' },
  { id: 'discoverable', label: 'Discoverable' },
]
const KINDS: Record<Kind, string> = { TEXT_INPUT: 'Short answer', PARAGRAPH: 'Paragraph', MULTIPLE_CHOICE: 'Multiple choice', TERMS: 'Server rules' }
const TERMS_LABEL = 'Read and agree to the server rules'
const EXAMPLES = ['Do you play any of the same games as us?', 'How did you find us?', 'What makes you unique?']
// Discord's limits: 5 fields, 16 rules of 300, questions of 300, 8 choices of 150, 10 tags of 30.
const MAX_FIELDS = 5, MAX_RULES = 16, MAX_CHOICES = 8, MAX_TAGS = 10

const has = (guild: PageGuild, feature: string) => guild.features.includes(feature)
const clean = (list: string[]) => list.map(s => s.trim()).filter(Boolean)

export function Access({ guild, administrator, onSaved }: { guild: AccessGuild; administrator: boolean; onSaved: () => void }) {
  const form = useAdmin<{ form_fields?: FormField[] | null }>('verification', { guildId: guild.id })
  const profile = useAdmin<{ visibility?: number }>('profile', { guildId: guild.id })
  const pending = useAdmin<{ total: number }>('pending', { guildId: guild.id })
  const saved = savedState(guild, form.data?.form_fields ?? [], profile.data?.visibility)
  const [draft, setDraft] = useState<Partial<State>>({})
  const [listing, setListing] = useState<Listing | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const bulk = useChoiceMenu()
  // Whether this server meets Server Discovery's requirements (known once Discoverable is chosen).
  const [eligible, setEligible] = useState<boolean | null>(null)
  const now: State = { ...saved, ...draft }
  const set = (change: Partial<typeof now>) => { setError(null); setDraft(d => ({ ...d, ...change })) }
  const formDirty = ['mode', 'rules', 'gate', 'questions'].some(key => key in draft)
  const dirty = Object.keys(draft).length > 0 || listing !== null
  const waiting = pending.data?.total ?? 0

  async function save(bulkAction?: 'APPROVED' | 'REJECTED') {
    const why = problem(now, listing, eligible)
    if (why) { setError(why); return }
    // Leaving Apply to join with applications waiting: Discord has them accepted or rejected first (more than 200 can
    // only be rejected), asked as a menu from Save changes; Reject all asks in place.
    if (saved.mode === 'apply' && now.mode !== 'apply' && waiting > 0 && !bulkAction) {
      const section = `${waiting} ${waiting === 1 ? 'application' : 'applications'} waiting`
      const answer = await bulk.ask(section, [
        ...(waiting <= 200 ? [{ id: 'APPROVED', label: 'Accept all', section }] : []),
        { id: 'REJECTED', label: 'Reject all', section, danger: true, confirm: waiting === 1 ? 'Reject it?' : `Reject all ${waiting}?` },
      ])
      if (answer === 'APPROVED' || answer === 'REJECTED') void save(answer)
      return
    }
    setBusy(true); setError(null)
    for (const step of steps(guild, saved, now, formDirty, listing, bulkAction)) {
      const result = await step()
      if (!result.ok) { setBusy(false); setError(result.message ?? 'Discord did not save that.'); return }
    }
    setBusy(false); setDraft({}); setListing(null)
    form.reload(); profile.reload(); pending.reload(); onSaved()
  }

  const profileVisible = profile.data?.visibility === 1 || profile.data?.visibility === 3
  const blocked = (mode: Mode) => unavailable(mode, saved.mode, guild, administrator)
  return (
    <>
      <h1>Access</h1>
      <ReadError error={form.error} onRetry={form.reload} />
      <Row label="How people join">
        <div className="exo-shelf-chips" role="radiogroup" aria-label="How people join">
          {MODES.map(m => (
            <button key={m.id} type="button" role="radio" className="exo-shelf-chip" disabled={busy || !!blocked(m.id)} aria-checked={now.mode === m.id} aria-pressed={now.mode === m.id}
              onClick={() => set({ mode: m.id })}>{m.label}</button>
          ))}
        </div>
      </Row>
      {MODES.map(m => blocked(m.id)).filter(Boolean).slice(0, 1).map(why => <p key={why} className="exo-dc-note">{why}</p>)}
      <Row label="Age-restricted server">
        <Switch checked={now.age} disabled={busy || now.mode === 'discoverable'} label="Age-restricted server" onChange={age => set({ age })} />
      </Row>
      {form.data ? <Way guild={guild} now={now} busy={busy} waiting={waiting} profileVisible={profileVisible} listing={listing} onListing={setListing} onEligible={setEligible} set={set} /> : null}
      <SaveBar shown={dirty} busy={busy} error={error} onReset={() => { setDraft({}); setListing(null); setError(null) }} onSave={() => void save()} />
      {bulk.menu}
    </>
  )
}

type State = { mode: Mode; rules: string[]; gate: boolean; age: boolean; recruit: boolean; questions: FormField[] }

// The chosen way's own part: the application, or the rules (with Discovery's checklist or listing first).
function Way({ guild, now, busy, waiting, profileVisible, listing, onListing, onEligible, set }: {
  guild: AccessGuild; now: State; busy: boolean; waiting: number; profileVisible: boolean
  listing: Listing | null; onListing: (listing: Listing) => void; onEligible: (eligible: boolean) => void; set: (change: Partial<State>) => void
}) {
  if (now.mode === 'apply') return <Application questions={now.questions} waiting={waiting} disabled={busy} onChange={questions => set({ questions })}
    recruit={now.recruit} profileVisible={profileVisible} onRecruit={recruit => set({ recruit })} />
  return (
    <>
      {now.mode === 'discoverable' ? <Discovery guild={guild} listing={listing} onListing={onListing} onEligible={onEligible} /> : null}
      <Rules rules={now.rules} gate={now.gate} disabled={busy} onRules={rules => set({ rules })} onGate={gate => set({ gate })} />
    </>
  )
}

// What the server has now: its way to join (Discoverable, Apply to join, else Invite only), its rules and form, the
// content level and whether people can apply from its profile.
function savedState(guild: AccessGuild, fields: FormField[], visibility: number | undefined): State {
  return {
    mode: has(guild, 'DISCOVERABLE') ? 'discoverable' : has(guild, 'MEMBER_VERIFICATION_MANUAL_APPROVAL') ? 'apply' : 'invite',
    rules: fields.find(f => f.field_type === 'TERMS')?.values ?? [],
    gate: has(guild, 'MEMBER_VERIFICATION_GATE_ENABLED'),
    age: guild.owner_configured_content_level === 3,
    recruit: visibility === 3,
    questions: fields,
  }
}

// Why a way to join can't be picked here, in Discord's words: Discovery needs Community, and only an administrator
// turns it on or off.
function unavailable(mode: Mode, saved: Mode, guild: AccessGuild, administrator: boolean) {
  if (mode === 'discoverable' && !has(guild, 'COMMUNITY')) return 'Enable Community to access Server Discovery.'
  if (administrator || (mode === 'discoverable') === (saved === 'discoverable')) return null
  return mode === 'discoverable' ? 'Only server administrators can turn on Server Discovery.' : 'Only server administrators can turn off Server Discovery.'
}

// Discord's checks before Access saves anything: the first that fails, in its words.
function problem(now: State, listing: Listing | null, eligible: boolean | null) {
  if (now.mode === 'discoverable' && eligible === false) return 'You cannot set your server to Discoverable because it does not meet requirements.'
  if (now.mode !== 'apply' && now.gate && clean(now.rules).length === 0) return 'You need to add one or more rules.'
  if (now.mode === 'apply' && !now.questions.some(f => f.field_type !== 'TERMS')) return 'Add a non-server rules question to enable Apply to join.'
  if (now.mode === 'discoverable' && listing && (!listing.description.trim() || !listing.category || !listing.tags.length)) return 'Fill in the required information to list on Discovery.'
  return null
}

// What Access saves, in Discord's order: the content level, leaving Discovery, the rules or the application, the
// Discovery listing, joining Discovery, Apply from profile.
function steps(guild: AccessGuild, saved: State, now: State, formDirty: boolean, listing: Listing | null, bulkAction?: string) {
  const guildId = guild.id
  const rules = clean(now.rules)
  const crossing = now.mode !== saved.mode && (now.mode === 'discoverable' || saved.mode === 'discoverable')
  const joinMode = () => admin('joinMode', { guildId, mode: now.mode, description: listing?.description ?? guild.description, locale: listing?.locale ?? guild.preferred_locale })
  const fields = now.mode === 'apply' ? now.questions : rules.length ? [{ field_type: 'TERMS', label: TERMS_LABEL, values: rules }] : []
  const list: (() => Promise<AdminReply<unknown>>)[] = []
  if (now.age !== saved.age) list.push(() => admin('saveGuild', { guildId, changes: { owner_configured_content_level: now.age ? 3 : 0 } }))
  if (crossing && saved.mode === 'discoverable') list.push(joinMode)
  if (formDirty) list.push(() => admin('saveForm', { guildId, bulkAction, fields, enabled: now.mode === 'apply' || now.gate }))
  if (listing && now.mode === 'discoverable') list.push(() => admin('saveDiscovery', { guildId, category: listing.category, keywords: listing.tags, emoji: listing.emoji }))
  if (crossing && now.mode === 'discoverable') list.push(joinMode)
  if (now.recruit !== saved.recruit) list.push(() => admin('saveProfile', { guildId, visibility: now.recruit ? 3 : 1 }))
  return list
}

// Invite only's (and Discoverable's) rules: on or off, and up to 16 of them.
function Rules({ rules, gate, disabled, onRules, onGate }: { rules: string[]; gate: boolean; disabled: boolean; onRules: (rules: string[]) => void; onGate: (on: boolean) => void }) {
  return (
    <section className="exo-dc-perm-group" aria-label="Server rules">
      <Row label="Server rules">
        <Switch checked={gate} disabled={disabled} label="Server rules" onChange={onGate} />
      </Row>
      {gate ? <RuleList rules={rules} disabled={disabled} onChange={onRules} /> : null}
    </section>
  )
}

function RuleList({ rules, disabled, onChange }: { rules: string[]; disabled: boolean; onChange: (rules: string[]) => void }) {
  return (
    <>
      <ol className="exo-dc-rule-list">
        {rules.map((rule, i) => (
          <li key={i}>
            <input className="exo-dc-modal-input" value={rule} maxLength={300} disabled={disabled} aria-label={`Rule ${i + 1}`} onChange={event => onChange(rules.map((r, j) => j === i ? event.target.value : r))} />
            <button type="button" className="exo-set-btn is-compact" disabled={disabled} onClick={() => onChange(rules.filter((_, j) => j !== i))}>Remove</button>
          </li>
        ))}
      </ol>
      {rules.length < MAX_RULES ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" disabled={disabled} onClick={() => onChange([...rules, ''])}>Add a rule</button></span> : null}
    </>
  )
}

// Apply to join's application: up to 5 questions (Server rules at most once), each edited in its dialog; Discord's
// example questions; and Apply from profile.
function Application({ questions, waiting, disabled, onChange, recruit, profileVisible, onRecruit }: {
  questions: FormField[]
  waiting: number
  disabled: boolean
  onChange: (questions: FormField[]) => void
  recruit: boolean
  profileVisible: boolean
  onRecruit: (on: boolean) => void
}) {
  const [editing, setEditing] = useState<{ index: number; field: FormField } | null>(null)
  // Editing while applications wait: Edit asks once, in place, since those keep the questions they were sent.
  const [warned, setWarned] = useState(false)
  const full = questions.length >= MAX_FIELDS
  const add = (kind: Kind) => setEditing({ index: -1, field: { field_type: kind, label: kind === 'TERMS' ? TERMS_LABEL : '', ...(kind === 'MULTIPLE_CHOICE' ? { choices: [''] } : kind === 'TERMS' ? { values: [''] } : {}), required: true } })
  const put = (field: FormField) => onChange(editing && editing.index >= 0 ? questions.map((q, i) => i === editing.index ? field : q) : [...questions, field])
  const move = (from: number, to: number) => { const next = [...questions]; next.splice(to, 0, ...next.splice(from, 1)); onChange(next) }
  const kinds = (Object.keys(KINDS) as Kind[]).filter(k => k !== 'TERMS' || !questions.some(q => q.field_type === 'TERMS'))
  return (
    <section className="exo-dc-perm-group" aria-label="Join application">
      <h3>Questions ({questions.length}/{MAX_FIELDS})</h3>
      <ol className="exo-dc-rule-list">
        {questions.map((q, i) => (
          <li key={i}>
            <span className="exo-dc-question">
              <strong>{q.field_type === 'TERMS' ? 'Read and agree to the server rules' : q.label}</strong>
              <span>{KINDS[q.field_type]}{detailOf(q)}</span>
            </span>
            {i > 0 ? <button type="button" className="exo-set-btn is-compact" disabled={disabled} onClick={() => move(i, i - 1)}>Move up</button> : null}
            {waiting > 0 && !warned ? (
              <ConfirmButton className="exo-set-btn is-compact" disabled={disabled} question="Edit? Pending ones keep the old"
                onConfirm={() => { setWarned(true); setEditing({ index: i, field: q }) }}>Edit</ConfirmButton>
            ) : <button type="button" className="exo-set-btn is-compact" disabled={disabled} onClick={() => setEditing({ index: i, field: q })}>Edit</button>}
            {questions.length > 1 ? <button type="button" className="exo-set-btn is-compact" disabled={disabled} onClick={() => onChange(questions.filter((_, j) => j !== i))}>Remove</button> : null}
          </li>
        ))}
      </ol>
      {full ? null : (
        <>
          <span className="exo-dc-settings-buttons">
            <Menu label="Add a question" anchor="--exo-dc-add-question" offset={4} triggerClassName="exo-set-btn" trigger="Add a question" disabled={disabled}
              items={kinds.map(k => ({ id: k, label: KINDS[k] }))} onAction={id => add(id as Kind)} />
          </span>
          <Row label="Example questions" stacked>
            <span className="exo-dc-settings-buttons">
              {EXAMPLES.map(text => <button key={text} type="button" className="exo-set-btn is-compact" disabled={disabled}
                onClick={() => onChange([...questions, { field_type: 'TEXT_INPUT', label: text, required: true }])}>{text}</button>)}
            </span>
          </Row>
        </>
      )}
      <Row label="Apply from profile" detail={profileVisible ? undefined : 'Needs a server profile people can see'}>
        <Switch checked={recruit} disabled={disabled || !profileVisible} label="Apply from profile" onChange={onRecruit} />
      </Row>
      {editing ? <QuestionDialog field={editing.field} onSave={field => { put(field); setEditing(null) }} onClose={() => setEditing(null)} /> : null}
    </section>
  )
}

// A question's count under its kind: a multiple choice's choices, the server rules' rules.
function detailOf(q: FormField) {
  const n = q.field_type === 'MULTIPLE_CHOICE' ? q.choices?.length ?? 0 : q.field_type === 'TERMS' ? q.values?.length ?? 0 : null
  if (n === null) return ''
  return ` · ${n} ${q.field_type === 'TERMS' ? (n === 1 ? 'rule' : 'rules') : (n === 1 ? 'choice' : 'choices')}`
}

// One question's dialog, Discord's: the question (300 letters, one line); a multiple choice's options (8 of 150); the
// server rules' list.
function QuestionDialog({ field, onSave, onClose }: { field: FormField; onSave: (field: FormField) => void; onClose: () => void }) {
  const [label, setLabel] = useState(field.label)
  const [list, setList] = useState(field.field_type === 'MULTIPLE_CHOICE' ? field.choices ?? [''] : field.values ?? [''])
  const [error, setError] = useState<string | null>(null)
  const kind = field.field_type
  function done() {
    const text = label.trim()
    if (kind !== 'TERMS' && !text) { setError('You must enter a question.'); return }
    if (kind === 'MULTIPLE_CHOICE' && clean(list).length === 0) { setError('You need at least two or more choices.'); return }
    if (kind === 'TERMS' && clean(list).length === 0) { setError('You need to add one or more rules.'); return }
    onSave(kind === 'MULTIPLE_CHOICE' ? { field_type: kind, label: text, choices: clean(list), required: true }
      : kind === 'TERMS' ? { field_type: kind, label: TERMS_LABEL, values: clean(list), required: true }
        : { field_type: kind, label: text, required: true })
  }
  const oneLine = (text: string, max: number) => text.replace(/\r\n|\n|\r/g, ' ').slice(0, max)
  return (
    <Dialog title={KINDS[kind]} onClose={onClose} actions={<button type="button" className="exo-set-btn is-primary" onClick={done}>Done</button>}>
      {kind === 'TERMS' ? <RuleList rules={list} disabled={false} onChange={setList} /> : (
        <input className="exo-dc-modal-input" value={label} autoFocus placeholder="Enter your question" aria-label="Question"
          onChange={event => { setError(null); setLabel(oneLine(event.target.value, 300)) }} onKeyDown={event => { if (event.key === 'Enter' && kind !== 'MULTIPLE_CHOICE') done() }} />
      )}
      {kind === 'MULTIPLE_CHOICE' ? (
        <>
          <ol className="exo-dc-rule-list">
            {list.map((choice, i) => (
              <li key={i}>
                <input className="exo-dc-modal-input" value={choice} placeholder={`Option ${i + 1}`} aria-label={`Option ${i + 1}`}
                  onChange={event => { setError(null); setList(list.map((c, j) => j === i ? oneLine(event.target.value, 150) : c)) }} />
                <button type="button" className="exo-set-btn is-compact" onClick={() => setList(list.length > 1 ? list.filter((_, j) => j !== i) : [''])}>Clear</button>
              </li>
            ))}
          </ol>
          {list.length < MAX_CHOICES ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" onClick={() => setList([...list, ''])}>Add an option</button></span> : null}
        </>
      ) : null}
      {error ? <ErrorNote className="exo-dialog-error" message={error} /> : null}
    </Dialog>
  )
}

/** A dialog of fields in Discord's settings: Exo's one dialog (ui/Dialog.tsx) drawn in the room, Cancel first, then its own buttons. */
export function Dialog({ title, children, actions, onClose }: { title: string; children: React.ReactNode; actions: React.ReactNode; onClose: () => void }) {
  return (
    <ExoDialog title={title} form portal={false} onClose={onClose} actions={<>
      <button type="button" className="exo-set-btn" onClick={onClose}>Cancel</button>
      {actions}
    </>}>{children}</ExoDialog>
  )
}

type Listing = { description: string; category: number | null; tags: string[]; emoji: boolean; locale: string }
type Requirements = {
  safe_environment?: boolean; size?: boolean; minimum_size?: number | null; age?: boolean; minimum_age?: number | null; healthy?: boolean; health_score_pending?: boolean
  engagement_healthy?: boolean; retention_healthy?: boolean; nsfw_properties?: Record<string, unknown> | null; protected?: boolean; sufficient?: boolean
}
type DiscoveryData = {
  requirements: Requirements | null
  metadata: { primary_category_id?: number | null; keywords?: string[] | null; emoji_discoverability_enabled?: boolean } | null
  categories: { id: number; name: string; is_primary?: boolean }[] | null
}

// Discoverable: Discovery's checklist while the server doesn't meet it; once it does, its listing (description,
// category, tags, emoji information and language).
function Discovery({ guild, listing, onListing, onEligible }: { guild: AccessGuild; listing: Listing | null; onListing: (listing: Listing) => void; onEligible: (eligible: boolean) => void }) {
  const data = useAdmin<DiscoveryData>('discovery', { guildId: guild.id })
  const req = data.data?.requirements
  const sufficient = req ? req.sufficient === true : null
  useEffect(() => { if (sufficient !== null) onEligible(sufficient) }, [sufficient, onEligible])
  if (data.error) return <ReadError error={data.error} onRetry={data.reload} />
  if (!data.data || !req) return <RowsWait label="Checking if your server passes requirements" />
  if (!req.sufficient) return <Checklist req={req} />
  const meta = data.data.metadata
  const value: Listing = listing ?? {
    description: guild.description ?? '', category: meta?.primary_category_id ?? null, tags: meta?.keywords ?? [],
    emoji: meta?.emoji_discoverability_enabled ?? false, locale: guild.preferred_locale ?? 'en-US',
  }
  const set = (change: Partial<Listing>) => onListing({ ...value, ...change })
  const categories = (data.data.categories ?? []).filter(c => c.is_primary !== false).map(c => ({ value: String(c.id), label: c.name }))
  return (
    <section className="exo-dc-perm-group" aria-label="Server Discovery">
      <h3>Server Discovery</h3>
      <Row label="Description" stacked>
        <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={2} maxLength={120} value={value.description} aria-label="Description" onChange={event => set({ description: event.target.value })} />
      </Row>
      <Row label="Category">
        <Choice label="Category" value={value.category === null ? '' : String(value.category)} choices={categories} onChange={id => set({ category: Number(id) })} />
      </Row>
      <Row label="Tags" stacked>
        <Tags tags={value.tags} onChange={tags => set({ tags })} />
      </Row>
      <Row label="Show emoji and sticker info when pressed">
        <Switch checked={value.emoji} label="Show emoji and sticker info when pressed" onChange={emoji => set({ emoji })} />
      </Row>
      <Row label="Server primary language">
        <Choice label="Server primary language" value={value.locale} choices={LANGUAGES} onChange={locale => set({ locale })} />
      </Row>
    </section>
  )
}

function Tags({ tags, onChange }: { tags: string[]; onChange: (tags: string[]) => void }) {
  const [text, setText] = useState('')
  const add = () => {
    const tag = text.trim().slice(0, 30)
    if (tag && tags.length < MAX_TAGS && !tags.some(t => t.toLowerCase() === tag.toLowerCase())) onChange([...tags, tag])
    setText('')
  }
  return (
    <span className="exo-dc-settings-buttons">
      {tags.map(tag => <button key={tag} type="button" className="exo-set-btn is-compact" aria-label={`Remove ${tag}`} onClick={() => onChange(tags.filter(t => t !== tag))}>{tag}<Icons.Remove size={12} /></button>)}
      {tags.length < MAX_TAGS ? <input className="exo-dc-modal-input" value={text} maxLength={30} placeholder="Add a tag" aria-label="Add a tag"
        onChange={event => setText(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); add() } }} onBlur={add} /> : null}
    </span>
  )
}

// Discord's checklist for Server Discovery, each requirement passed or failed in Discord's words.
function Checklist({ req }: { req: Requirements }) {
  return (
    <section className="exo-dc-perm-group" aria-label="Server Discovery requirements">
      <h3>Server Discovery requirements</h3>
      <ul className="exo-dc-rule-list">
        {checks(req).map(row => (
          <li key={row.name} className="exo-dc-check-row">
            <span className={`exo-dc-check-mark${row.pending ? '' : row.ok ? ' is-good' : ' is-bad'}`} aria-hidden="true">{row.pending ? <Icons.Pending size={16} /> : row.ok ? <Icons.Check size={16} /> : <Icons.Failed size={16} />}</span>
            <span className="exo-dc-question"><strong>{row.name}</strong>{row.detail ? <span>{row.detail}</span> : null}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

function checks(req: Requirements) {
  const min = (req.minimum_size ?? 1000).toLocaleString()
  const weeks = Math.ceil((req.minimum_age ?? 0) / 7)
  const content = !req.nsfw_properties || Object.keys(req.nsfw_properties).length === 0
  return [
    { ok: req.safe_environment, name: req.safe_environment ? 'Safe environment' : 'Flagged by Trust & Safety',
      detail: null },
    { ok: req.size, name: req.size ? `${min}+ members` : `Fewer than ${min} members`, detail: null },
    { ok: req.age, name: req.age ? 'Meets age requirement' : 'Server too young', detail: req.age ? null : `Needs to be ${weeks} ${weeks === 1 ? 'week' : 'weeks'} old` },
    activity(req),
    { ok: content, name: content ? 'No bad content' : 'Bad content found', detail: null },
    { ok: req.protected, name: req.protected ? '2FA requirement for moderation enabled' : 'Turn on Require 2FA for moderator actions in Safety setup', detail: null },
  ].map(row => ({ pending: false, ...row }))
}

// Discovery's activity requirement: waiting on Discord's weekly numbers, passed, or what failed.
function activity(req: Requirements) {
  if (req.health_score_pending) return {
    ok: false, pending: true, name: 'Waiting on server activity metrics',
    detail: req.size ? null : 'Needs 200 members first',
  }
  if (req.healthy) return { ok: true, name: 'Passed server activity requirements', detail: null }
  const why = [req.engagement_healthy ? null : 'Too few members visit and talk weekly.', req.retention_healthy ? null : 'Too few new members stay.']
  return { ok: false, name: 'Failed server activity requirements', detail: why.filter(Boolean).join(' ') || null }
}

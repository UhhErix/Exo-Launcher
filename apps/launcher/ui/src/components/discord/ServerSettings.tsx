import { RowsWait } from './waits'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import type { DiscordChannel, DiscordModeration } from '../../lib/discord'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { guildBannerUrl, requestDiscordSettings } from '../../lib/discord'
import { admin, dropUpload, SheetNav, SaveBar, pickFile, previewOf, ReadError, UnsavedProvider, useAdmin, useLayer, useUnsaved } from './admin'
import { Row } from '../settings/parts'
import { uploadFile } from './Chat'
import { Roles } from './settingsRoles'
import { Emoji, Stickers } from './settingsExpressions'
import { AuditLog, Bans, Integrations, Invites, type Incidents } from './settingsModeration'
import { AutoMod } from './settingsAutomod'
import { SettingsLayer } from './SettingsLayer'
import { BoostPerks, Community, CustomInvite, OnDiscord, ServerInsights, ServerTag, ServerTemplate, Soundboard, WelcomeScreen, Widget } from './settingsPages'
import { Access } from './settingsAccess'
import { Onboarding } from './settingsOnboarding'
import { Members } from './serverPages'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Unfold } from '../ui/Unfold'
import { Spinner } from '../ui/Spinner'
import { ErrorNote } from '../ui/ErrorNote'
import { NAV, type Section } from './serverSections'

export type Guild = {
  id: string; name: string; icon: string | null; banner: string | null; description: string | null; owner_id: string
  afk_channel_id: string | null; afk_timeout: number; system_channel_id: string | null; system_channel_flags: number
  verification_level: number; default_message_notifications: number; explicit_content_filter: number; mfa_level: number
  premium_tier: number; features: string[]; premium_subscription_count?: number; rules_channel_id?: string | null
  safety_alerts_channel_id?: string | null; public_updates_channel_id?: string | null; incidents_data?: Incidents
  owner_configured_content_level?: number; preferred_locale?: string | null; approximate_member_count?: number
}


/**
 * Discord's Server Settings in Exo's look: the sheet Exo's own Settings is (the sections down the left, the one open at the
 * right), each section only with the permission Discord shows it for, each read from Discord only once it is opened.
 * Members opens the server's Members page, as Discord does for a Community server; elsewhere it is a section here.
 * Unsaved changes hold the sheet: closing or Escape shakes the save bar instead.
 * The owner gets Delete Server at the bottom, as Discord offers it (with 2FA on their account when the server requires it).
 */
export function ServerSettings({ guildId, name, moderation, channels, start = 'profile', onClose, onMembers }: {
  guildId: string
  name: string
  moderation: DiscordModeration
  channels: DiscordChannel[]
  /** The section it opens at (the server menu's Server Settings submenu). */
  start?: Section
  onClose: () => void
  onMembers?: () => void
}) {
  const guild = useAdmin<Guild>('guild', { guildId })
  const unsaved = useUnsaved()
  const close = () => unsaved.guard(onClose)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, close)
  // Discord's own settings opened from here (Safety Setup's My Account) take the room's place.
  const leave = useRef(close)
  useEffect(() => { leave.current = close })
  useEffect(() => {
    const away = () => leave.current()
    window.addEventListener('exo-discord-settings', away)
    return () => window.removeEventListener('exo-discord-settings', away)
  }, [])
  const groups = NAV.map(group => ({ heading: group.heading, items: group.items.filter(item => item.shown(moderation, guild.data)) }))
  const sections = groups.flatMap(group => group.items)
  // One page at a time, as the app's Settings has them; a page the server does not show falls back to the first.
  const [chosen, setChosen] = useState<Section>(start)
  const page = sections.some(item => item.id === chosen) || !guild.data ? chosen : sections[0]?.id ?? 'profile'
  const body = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => { if (body.current) body.current.scrollTop = 0 }, [page])
  const props = { guildId, moderation, channels, guild: guild.data, onSaved: guild.reload }
  return (
    <UnsavedProvider value={unsaved}>
    <SettingsLayer onClose={close}>
    <div ref={box} className="exo-dc-settings is-paged" role="dialog" aria-modal="true" aria-label={`${name} settings`} tabIndex={-1}>
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <SheetNav title={name} groups={groups} current={page} onPick={id => unsaved.guard(() => setChosen(id))} />
      <div className="exo-settings-main">
        <button type="button" className="exo-props-close exo-settings-close" aria-label="Close server settings" onClick={close}><Close size={16} /></button>
        <div ref={body} className="exo-settings-scroll exo-dc-settings-body">
          <div className="exo-dc-settings-page">
            <section key={page} id={`dcg-${page}`} className="exo-dc-settings-section">
              {page === 'members' && onMembers ? (
                <>
                  <h1>Members</h1>
                  <Row label="Member list">
                    <button type="button" className="exo-set-btn" onClick={() => unsaved.guard(() => { onClose(); onMembers() })}>Open</button>
                  </Row>
                </>
              ) : <SectionPage section={page} {...props} error={guild.error} />}
            </section>
            {page === 'profile' && moderation.owner && guild.data && (guild.data.mfa_level !== 1 || moderation.mfa) ? (
              <section className="exo-dc-settings-section">
                <DeleteServer guildId={guildId} name={name} confirmByName={!moderation.mfa} guard={unsaved.guard} onDeleted={onClose} />
              </section>
            ) : null}
          </div>
        </div>
      </div>
    </div>
    </SettingsLayer>
    </UnsavedProvider>
  )
}

type PageProps = { guildId: string; moderation: DiscordModeration; channels: DiscordChannel[]; guild: Guild | null; onSaved: () => void }

// Each section's page; the server's own (Server Profile, Engagement, Safety Setup) are the Overview below.
const PAGES: Partial<Record<Section, (p: PageProps) => React.ReactNode>> = {
  roles: p => <Roles guildId={p.guildId} />,
  emoji: p => <Emoji guildId={p.guildId} tier={p.guild?.premium_tier ?? 0} />,
  stickers: p => <Stickers guildId={p.guildId} tier={p.guild?.premium_tier ?? 0} />,
  invites: p => <><Invites guildId={p.guildId} channels={p.channels} incidents={p.guild?.incidents_data ?? null} onChanged={p.onSaved} />{p.guild ? <CustomInvite guild={p.guild} /> : null}</>,
  integrations: p => <Integrations guildId={p.guildId} moderation={p.moderation} channels={p.channels} />,
  automod: p => <AutoMod guildId={p.guildId} channels={p.channels} />,
  audit: p => <AuditLog guildId={p.guildId} />,
  bans: p => <Bans guildId={p.guildId} />,
  members: p => <><h1>Members</h1><Members guildId={p.guildId} canPrune={p.moderation.prune} /></>,
  directory: p => <OnDiscord title="App directory" url={`https://discord.com/application-directory?guild_id=${p.guildId}`} action="Open app directory" />,
  subscriptions: p => <OnDiscord title="Server subscriptions" url={`https://discord.com/channels/${p.guildId}/role-subscriptions`} action="Set up in Discord" />,
  tag: p => p.guild && <ServerTag guild={p.guild} />,
  boost: p => p.guild && <BoostPerks guild={p.guild} />,
  soundboard: p => p.guild && <Soundboard guild={p.guild} />,
  access: p => p.guild && <Access guild={p.guild} administrator={p.moderation.administrator === true || p.moderation.owner} onSaved={p.onSaved} />,
  insights: p => p.guild && <ServerInsights guild={p.guild} />,
  template: p => p.guild && <ServerTemplate guild={p.guild} />,
  community: p => p.guild && <Community key={p.guild.rules_channel_id ?? ''} guild={p.guild} channels={p.channels} onSaved={p.onSaved} />,
  onboarding: p => p.guild && <Onboarding guild={p.guild} channels={p.channels} />,
  welcome: p => p.guild && <WelcomeScreen guild={p.guild} channels={p.channels} />,
}

function SectionPage({ section, error, ...props }: PageProps & { section: Section; error: string | null }) {
  const { guild, moderation, channels, onSaved } = props
  const page = PAGES[section]
  // A page that needs the server waits for it like the server's own pages.
  if (page && (guild || !['tag', 'boost', 'soundboard', 'access', 'template', 'community', 'onboarding', 'insights', 'welcome'].includes(section))) return page(props)
  // A failed read keeps what the page had, with Try Again.
  if (!guild) return error ? <ReadError error={error} onRetry={onSaved} /> : <RowsWait label="Loading the server" rows={6} />
  return (
    <>
      <Overview key={section} section={section} guild={guild} moderation={moderation} channels={channels} onSaved={onSaved} />
      {section === 'engagement' && moderation.manageGuild ? <Widget guild={guild} channels={channels} /> : null}
      <ReadError error={error} onRetry={onSaved} />
    </>
  )
}

type Draft = Partial<Pick<Guild, 'name' | 'description' | 'afk_channel_id' | 'afk_timeout' | 'system_channel_id' | 'system_channel_flags' | 'default_message_notifications'
  | 'verification_level' | 'explicit_content_filter'>>
type Picture = { file: File; preview: string } | null

// Server Profile, Engagement and Safety Setup: changes wait in a draft until Save Changes, as in Discord.
function Overview({ section, guild, moderation, channels, onSaved }: { section: Section; guild: Guild; moderation: DiscordModeration; channels: DiscordChannel[]; onSaved: () => void }) {
  const canEdit = moderation.manageGuild
  const [draft, setDraft] = useState<Draft>({})
  const [icon, setIcon] = useState<Picture | undefined>(undefined)
  const [banner, setBanner] = useState<Picture | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [nameError, setNameError] = useState<string | null>(null)
  const value = <K extends keyof Draft>(key: K) => (key in draft ? draft[key] : guild[key]) as Guild[K]
  const set = (change: Draft) => { setError(null); setNameError(null); setDraft(d => ({ ...d, ...change })) }
  const dirty = Object.keys(draft).length > 0 || icon !== undefined || banner !== undefined
  async function save() {
    // Discord's rule for a server name, said under Name rather than dropped.
    const name = draft.name?.trim()
    if (name !== undefined && (name.length < 2 || name.length > 100)) { setNameError('Use a server name of 2 to 100 characters.'); return }
    setBusy(true)
    const changes: Record<string, unknown> = { ...draft }
    // Pictures sent for a save that didn't happen are dropped, so they stop counting toward the upload limit.
    const sent: string[] = []
    for (const [key, picture] of [['icon', icon], ['banner', banner]] as const) {
      if (picture === undefined) continue
      if (picture === null) { changes[key] = null; continue }
      const uploaded = await uploadFile(picture.file)
      if (typeof uploaded !== 'string') { sent.forEach(dropUpload); setError(uploaded.message); setBusy(false); return }
      sent.push(uploaded)
      changes[key] = uploaded
    }
    const result = await admin('saveGuild', { guildId: guild.id, changes })
    setBusy(false)
    if (!result.ok) { sent.forEach(dropUpload); setError(result.message ?? 'Discord did not save that.'); return }
    setDraft({}); setIcon(undefined); setBanner(undefined); onSaved()
  }
  const reset = () => { setDraft({}); setIcon(undefined); setBanner(undefined); setError(null); setNameError(null) }
  return (
    <>
      {section === 'profile' ? <Profile guild={guild} canEdit={canEdit} value={value} set={set} nameError={nameError} icon={icon} setIcon={setIcon} banner={banner} setBanner={setBanner} /> : null}
      {section === 'engagement' ? <Engagement canEdit={canEdit} value={value} set={set} channels={channels} /> : null}
      {section === 'safety' ? <Safety guild={guild} moderation={moderation} channels={channels} value={value} set={set} onSaved={onSaved} /> : null}
      <SaveBar shown={dirty} busy={busy} error={error} onReset={reset} onSave={() => void save()} />
    </>
  )
}

type Value = <K extends keyof Draft>(key: K) => Guild[K]

function Profile({ guild, canEdit, value, set, nameError, icon, setIcon, banner, setBanner }: {
  guild: Guild
  canEdit: boolean
  value: Value
  set: (change: Draft) => void
  nameError: string | null
  icon: Picture | undefined
  setIcon: (picture: Picture | undefined) => void
  banner: Picture | undefined
  setBanner: (picture: Picture | undefined) => void
}) {
  const iconUrl = icon === undefined ? (guild.icon ? `https://cdn.discordapp.com/icons/${guild.id}/${guild.icon}.webp?size=256&quality=lossless` : null) : icon?.preview ?? null
  const bannerUrl = banner === undefined ? (guild.banner ? guildBannerUrl(guild.id, guild.banner, false) : null) : banner?.preview ?? null
  const choose = async (apply: (picture: Picture) => void) => {
    const file = await pickFile('image/png,image/jpeg,image/gif,image/webp')
    if (file) apply({ file, preview: await previewOf(file) })
  }
  return (
    <>
      <h1>Server profile</h1>
      <Row label="Name" error={nameError}>
        <input className="exo-dc-modal-input exo-dc-field" value={value('name')} maxLength={100} disabled={!canEdit} aria-label="Server name"
          onChange={event => set({ name: event.target.value })} />
      </Row>
      <Row label="Icon">
        <span className="exo-dc-picture">
          {iconUrl ? <img src={iconUrl} alt="" width={64} height={64} decoding="async" draggable={false} /> : <span className="exo-dc-picture-empty">{guild.name.slice(0, 1)}</span>}
          {canEdit ? <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose(setIcon)}>Change icon</button> : null}
          {canEdit && iconUrl ? <button type="button" className="exo-dc-tab" onClick={() => setIcon(null)}>Remove icon</button> : null}
        </span>
      </Row>
      {guild.features.includes('BANNER') ? (
        <Row label="Banner">
          <span className="exo-dc-picture is-banner">
            {bannerUrl ? <img src={bannerUrl} alt="" width={160} height={90} decoding="async" draggable={false} /> : null}
            {canEdit ? <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose(setBanner)}>Change banner</button> : null}
            {canEdit && bannerUrl ? <button type="button" className="exo-dc-tab" onClick={() => setBanner(null)}>Remove</button> : null}
          </span>
        </Row>
      ) : null}
      {guild.features.includes('COMMUNITY') ? (
        <Row label="Description" stacked>
          <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={3} maxLength={120} value={value('description') ?? ''} disabled={!canEdit} aria-label="Server description"
            onChange={event => set({ description: event.target.value || null })} />
        </Row>
      ) : null}
    </>
  )
}

const AFK = [{ value: '60', label: '1 minute' }, { value: '300', label: '5 minutes' }, { value: '900', label: '15 minutes' }, { value: '1800', label: '30 minutes' }, { value: '3600', label: '1 hour' }]
// System channel flags (Discord's SUPPRESS_* bits), each switch on while its bit is off.
const SYSTEM_FLAGS = [
  { bit: 1, label: 'Welcome new members' },
  { bit: 8, label: 'Suggest a sticker reply to welcomes' },
  { bit: 2, label: 'Announce Boosts' },
  { bit: 4, label: 'Server setup tips' },
]

function Engagement({ canEdit, value, set, channels }: { canEdit: boolean; value: Value; set: (change: Draft) => void; channels: DiscordChannel[] }) {
  const text = channels.filter(c => c.kind === 'text' && c.type === 0)
  const voice = channels.filter(c => c.kind === 'voice' && c.type === 2)
  const system = value('system_channel_id')
  const flags = value('system_channel_flags')
  const notify = value('default_message_notifications')
  return (
    <>
      <h1>Engagement</h1>
      <Row label="System messages channel">
        <Choice label="System messages channel" value={system ?? 'none'} disabled={!canEdit}
          choices={[{ value: 'none', label: 'No system messages' }, ...text.map(c => ({ value: c.id, label: `#${c.name}` }))]}
          onChange={id => set({ system_channel_id: id === 'none' ? null : id })} />
      </Row>
      {SYSTEM_FLAGS.map(flag => (
        <Row key={flag.bit} label={flag.label}>
          <Switch checked={(flags & flag.bit) === 0} disabled={!canEdit || !system} label={flag.label}
            onChange={on => set({ system_channel_flags: on ? flags & ~flag.bit : flags | flag.bit })} />
        </Row>
      ))}
      <Row label="Inactive channel">
        <Choice label="Inactive channel" value={value('afk_channel_id') ?? 'none'} disabled={!canEdit}
          choices={[{ value: 'none', label: 'No inactive channel' }, ...voice.map(c => ({ value: c.id, label: c.name }))]}
          onChange={id => set({ afk_channel_id: id === 'none' ? null : id })} />
      </Row>
      <Row label="Inactive timeout">
        <Choice label="Inactive timeout" value={String(value('afk_timeout'))} disabled={!canEdit || !value('afk_channel_id')} choices={AFK}
          onChange={seconds => set({ afk_timeout: Number(seconds) })} />
      </Row>
      <Row label="Default notification settings">
        <div className="exo-shelf-chips" role="radiogroup" aria-label="Default notification settings">
          {[{ v: 0, label: 'All messages' }, { v: 1, label: 'Only @mentions' }].map(option => (
            <button key={option.v} type="button" role="radio" className="exo-shelf-chip" disabled={!canEdit} aria-checked={notify === option.v} aria-pressed={notify === option.v}
              onClick={() => set({ default_message_notifications: option.v })}>{option.label}</button>
          ))}
        </div>
      </Row>
    </>
  )
}

const VERIFICATION = [
  { level: 0, name: 'None' },
  { level: 1, name: 'Low: verified email' },
  { level: 2, name: 'Medium: verified email, on Discord 5+ minutes' },
  { level: 3, name: 'High: also in this server 10+ minutes' },
  { level: 4, name: 'Highest: verified phone' },
]
const CONTENT_FILTER = [
  { level: 2, name: 'Filter messages from all members' },
  { level: 1, name: 'Filter messages from server members without roles' },
  { level: 0, name: 'Do not filter' },
]

// Discord's Safety Setup: verification level and the sensitive content filter wait for Save Changes; the owner's
// switches (2FA for moderators, prune for admins only) save at once, as Discord's do, the 2FA one after the owner verifies.
function Safety({ guild, moderation, channels, value, set, onSaved }: { guild: Guild; moderation: DiscordModeration; channels: DiscordChannel[]; value: Value; set: (change: Draft) => void; onSaved: () => void }) {
  const community = guild.features.includes('COMMUNITY')
  const discoverable = guild.features.includes('DISCOVERABLE')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const required = guild.mfa_level === 1
  // Discord's rule: only an owner with 2FA on their own account, and never off while the server is in Server Discovery.
  const canMfa = moderation.owner && moderation.mfa && !(required && discoverable)
  const why = required && discoverable ? 'Required while in Server Discovery'
    : !moderation.owner ? 'Only the server owner can change this'
      : moderation.mfa ? null : 'Turn on 2FA on your account first'
  async function owner(action: 'guildMfa' | 'pruneAdmins' | 'raidAlerts', on: boolean) {
    setBusy(true); setError(null)
    const result = await admin(action, { guildId: guild.id, on })
    setBusy(false)
    if (result.ok) onSaved()
    else if (result.message !== MFA_CANCELLED) setError(result.message ?? 'Discord did not save that.')
  }
  return (
    <>
      <h1>Safety setup</h1>
      <Row label="Verification level" stacked>
        <Options options={VERIFICATION.filter(v => !community || v.level !== 0)}
          value={value('verification_level')} label="Verification level" onPick={level => set({ verification_level: level })} />
      </Row>
      <Row label="Require 2FA for moderator actions" error={error}
        detail={why ?? undefined}>
        {moderation.owner && !moderation.mfa ? <button type="button" className="exo-set-btn" onClick={() => requestDiscordSettings('account')}>Account</button> : null}
        <Switch checked={required} disabled={!canMfa || busy} label="Require 2FA for moderator actions" onChange={on => void owner('guildMfa', on)} />
      </Row>
      {community ? null : (
        <Row label="Restrict member prune to admins">
          <Switch checked={guild.features.includes('PRUNE_REQUIRES_ADMIN')} disabled={!moderation.owner || busy} label="Restrict member prune to admins" onChange={on => void owner('pruneAdmins', on)} />
        </Row>
      )}
      <Row label="Sensitive content filters" stacked>
        <Options options={CONTENT_FILTER.filter(v => !community || v.level === 2)}
          value={value('explicit_content_filter')} label="Sensitive content filters" onPick={level => set({ explicit_content_filter: level })} />
      </Row>
      <SafetyAlerts guild={guild} channels={channels} busy={busy} onAlerts={on => void owner('raidAlerts', on)} onSaved={onSaved} />
    </>
  )
}

// Discord's Activity Alerts (DM or join activity beyond the server's usual) and the channel they go to; both save at once.
function SafetyAlerts({ guild, channels, busy, onAlerts, onSaved }: { guild: Guild; channels: DiscordChannel[]; busy: boolean; onAlerts: (on: boolean) => void; onSaved: () => void }) {
  const community = guild.features.includes('COMMUNITY')
  const alerts = community ? !guild.features.includes('RAID_ALERTS_DISABLED') : guild.features.includes('NON_COMMUNITY_RAID_ALERTS')
  const channel = guild.safety_alerts_channel_id ?? guild.public_updates_channel_id ?? null
  const [error, setError] = useState<string | null>(null)
  const pick = (id: string) => void admin('saveGuild', { guildId: guild.id, changes: { safety_alerts_channel_id: id } }).then(result => {
    if (result.ok) { setError(null); onSaved() } else setError(result.message ?? 'Discord did not save that.')
  })
  return (
    <>
      <Row label="Activity alerts">
        <Switch checked={alerts} disabled={busy || !channel} label="Activity alerts" onChange={onAlerts} />
      </Row>
      <Row label="Safety notifications channel" error={error}>
        <Choice label="Safety notifications channel" value={channel ?? ''} choices={channels.filter(c => c.kind === 'text' && c.type === 0).map(c => ({ value: c.id, label: `#${c.name}` }))} onChange={pick} />
      </Row>
    </>
  )
}

// What the verify dialog answers when the person closes it: no error to show, the setting just stays.
const MFA_CANCELLED = 'Multi-factor authentication cancelled'

/**
 * Discord's Delete Server, asked where it is pressed (DESIGN.md › Confirm): an owner with 2FA confirms on the button and
 * then verifies; one without types the server's name in the row, which springs open for it, as Discord asks. Unsaved
 * changes hold it first. The room leaves the server when Discord says it is gone.
 */
function DeleteServer({ guildId, name, confirmByName, guard, onDeleted }: { guildId: string; name: string; confirmByName: boolean; guard: (go: () => void) => void; onDeleted: () => void }) {
  const [typing, setTyping] = useState(false)
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function remove() {
    if (confirmByName && typed !== name) { setError('That isn’t the server’s name. Type it exactly as it is spelled.'); return }
    setBusy(true); setError(null)
    const result = await admin('deleteGuild', { guildId })
    setBusy(false)
    if (result.ok) onDeleted(); else if (result.message !== MFA_CANCELLED) setError(result.message ?? 'Discord did not delete it.')
  }
  const cancel = () => { setTyping(false); setTyped(''); setError(null) }
  const working = busy ? <Spinner label="Deleting the server" /> : null
  return (
    <>
      <Row label="Delete server" error={typing ? null : error}>
        {confirmByName
          ? <button type="button" className="exo-set-btn is-danger" aria-expanded={typing} disabled={typing} onClick={() => guard(() => setTyping(true))}>Delete server</button>
          : <ConfirmButton className="exo-set-btn is-danger" question="Delete server?" danger disabled={busy} lead={working} onConfirm={() => guard(() => void remove())}>Delete server</ConfirmButton>}
      </Row>
      <Unfold open={typing} className="exo-dc-unfold" onEscape={cancel}>
        <input className="exo-dc-modal-input" value={typed} autoFocus aria-label="Enter server name" placeholder="Enter server name"
          onChange={event => { setTyped(event.target.value); setError(null) }} />
        {error ? <ErrorNote className="is-bad" message={error} /> : null}
        <div className="exo-dc-unfold-actions">
          <button type="button" className="exo-set-btn" onClick={cancel}>Cancel</button>
          <button type="button" className="exo-set-btn is-danger" disabled={busy} onClick={() => void remove()}>{working}Delete server</button>
        </div>
      </Unfold>
    </>
  )
}

function Options({ options, value, label, onPick }: { options: { level: number; name: string }[]; value: number; label: string; onPick: (level: number) => void }) {
  return (
    <div className="exo-dc-options" role="radiogroup" aria-label={label}>
      {options.map(option => (
        <button key={option.level} type="button" role="radio" aria-checked={value === option.level}
          className={`exo-dc-option${value === option.level ? ' is-current' : ''}`} onClick={() => { if (value !== option.level) onPick(option.level) }}>
          <span className="exo-dc-option-dot" aria-hidden="true" />
          {option.name}
        </button>
      ))}
    </div>
  )
}

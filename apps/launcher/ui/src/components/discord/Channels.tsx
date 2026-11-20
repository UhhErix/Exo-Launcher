import { createContext, createElement, Fragment, lazy, Suspense, useContext, useEffect, useRef, useState } from 'react'
import { ChevronDown, Settings } from '../../brand/icons'
import { host } from '../../lib/host'
import { cdnSized, guildBannerUrl, performanceMode, type DiscordChannel, type DiscordChannels, type DiscordGuild } from '../../lib/discord'
import { channelGlyph, Icons } from './icons'
import { openThreadMenu } from './ThreadMenu'
import { emojiUrl } from './markdown'
import { VoiceMembers, type VoiceControls } from './voice'
import { CreateChannel, pageNamed, ServerRows } from './serverPages'
import { EventsDialog } from './events'
// Edit Channel loads the first time it opens, not with the room.
const ChannelSettings = lazy(() => import('./ChannelSettings').then(m => ({ default: m.ChannelSettings })))
import { useAccountSettings } from '../../lib/discordPrefs'
import { Confirm } from './admin'
import { ConfirmButton } from '../ui/ConfirmButton'
import { InviteDialog } from './InviteDialog'
import { transition } from '../../lib/viewTransition'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { LEVELS, MUTE_FOR } from './ChatHeader'
import { SubContext, Submenu } from './UserMenu'
import { useDismiss } from '../../lib/dismiss'
import { peek, unpeek } from './roomPlaces'
import { initials } from '../ui/artInitial'
import { mentionsSaid } from './Band'
import { errorText } from '../../lib/errorWords'

/** Where you are in the server, for the title line of whichever page of it is open. */
/** `name`: the server you are in (its own name, never that of a list still showing from the one you left). */
export type ServerNav = { list: DiscordChannels | null; open: string | null; name: string | null }
export const ServerNavContext = createContext<ServerNav | null>(null)

/** A page's title in a server: the channel you're in (its channels are always in the room's list, never folded into the title). */
export function ChannelNav() {
  const nav = useContext(ServerNavContext)
  if (!nav) return null
  const channel = nav.list?.channels.find(c => c.id === nav.open)
  const page = pageNamed(nav.open)
  const name = channel?.name ?? page?.label ?? ''
  return <span className="exo-dc-head-channel">{channel ? <span className="exo-dc-hash" aria-hidden="true"><ChannelIcon channel={channel} size={18} /></span> : page ? <page.Icon size={18} /> : null}<strong className="exo-dc-place-chip-name">{name}</strong></span>
}

/**
 * Every channel of a server, open under its row in the room's one list (Rail.tsx; DESIGN.md › Rooms › Discord): its own
 * rows (Guide, Events, Browse channels, Members), categories that fold, each channel with who is in voice there and the
 * threads you're in. The server's row is its head there; its banner and this head are hidden in the list.
 */
export function ChannelsPanel({ guildId, guild, name, list, open, thread, voice, onPick, onPreview, onJoin }: {
  guildId: string
  /** The server as the band has it: its mark on the column's head. */
  guild: DiscordGuild | null
  name: string
  /** The thread open inside `open`, if one is. */
  thread?: string | null
  list: DiscordChannels | null
  open: string | null
  voice: VoiceControls
  onPick: (id: string) => void
  /** A voice channel opened without leaving the list, so a second press joins it. */
  onPreview: (id: string) => void
  onJoin: (id: string) => void
}) {
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [collapsedOf, setCollapsedOf] = useState<DiscordChannels | null>(null)
  if (list !== collapsedOf) { setCollapsedOf(list); setCollapsed(new Set(list?.channels.filter(c => c.collapsed).map(c => c.id))) }
  // Create Channel in a category; the Events dialog.
  const [creating, setCreating] = useState<{ parentId: string; category: string } | null>(null)
  const [eventsOpen, setEventsOpen] = useState(false)
  const [channelMenu, setChannelMenu] = useState<MenuAt | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  // Invite People: the channel's own, or the server's (where ServerMenu's invite lands: the open channel, else the first text channel you can read).
  const [inviting, setInviting] = useState<DiscordChannel | null>(null)
  // A category folds or opens (folded: absent, it flips), kept on your account as Discord keeps it.
  const toggle = (id: string, folded?: boolean) => setCollapsed(set => {
    const next = new Set(set)
    const fold = folded ?? !next.has(id)
    if (fold === next.has(id)) return set
    if (fold) next.add(id); else next.delete(id)
    void host.discordChannel(id, 'collapse', fold).catch(() => { })
    return next
  })
  return (
    <>
      <nav className="exo-dc-channels exo-dc-side" aria-label="Channels" onPointerOver={peek} onPointerLeave={unpeek}>
        <ColumnHead guildId={guildId} guild={guild} name={name} list={list} open={open} onInvite={c => setInviting(c)} />
        <div className="exo-dc-side-list">
          {list?.banner ? <img className="exo-dc-guild-banner" src={guildBannerUrl(guildId, list.banner, !performanceMode() && !shouldReduceMotion())} alt="" draggable={false} decoding="async" /> : null}
          <ServerRows rows={list?.rows} events={list?.events ?? 0} open={open} onPick={onPick} onEvents={() => setEventsOpen(true)} />
          <ChannelList channels={followed(list, open)} open={open} collapsed={collapsed} onPick={onPick} onPreview={onPreview} voice={voice} onJoin={onJoin} onInvite={c => setInviting(c)} onEdit={c => void transition(() => setEditing(c.id), { kind: 'none' })}
            list={list} openThread={thread}
            onCreate={list?.canManage ? c => setCreating({ parentId: c.id, category: c.name }) : undefined}
            onMenu={(channel, x, y, inCategory) => setChannelMenu({ channel, x, y, inCategory })}
            onToggle={id => toggle(id)} />
        </div>
      </nav>
      <ColumnDialogs guildId={guildId} name={name} list={list} voice={voice} collapsed={collapsed} toggle={toggle} inviting={inviting} setInviting={setInviting} eventsOpen={eventsOpen} setEventsOpen={setEventsOpen} channelMenu={channelMenu} setChannelMenu={setChannelMenu} editing={editing} setEditing={setEditing} creating={creating} setCreating={setCreating} onPick={onPick} />
    </>
  )
}

// What opens from the column over the room.
function ColumnDialogs({ guildId, name, list, voice, collapsed, toggle, eventsOpen, setEventsOpen, channelMenu, setChannelMenu, editing, setEditing, creating, setCreating, inviting, setInviting, onPick }: {
  guildId: string
  name: string
  list: DiscordChannels | null
  voice: VoiceControls
  collapsed: Set<string>
  toggle: (id: string, folded?: boolean) => void
  eventsOpen: boolean
  setEventsOpen: (open: boolean) => void
  channelMenu: MenuAt | null
  setChannelMenu: (menu: MenuAt | null) => void
  editing: string | null
  setEditing: (id: string | null) => void
  creating: { parentId: string; category: string } | null
  setCreating: (at: { parentId: string; category: string } | null) => void
  inviting: DiscordChannel | null
  setInviting: (channel: DiscordChannel | null) => void
  onPick: (id: string) => void
}) {
  return (
    <>
      {inviting ? <InviteDialog name={name} channelId={inviting.id} channelName={inviting.name} onClose={() => setInviting(null)} /> : null}
      {eventsOpen ? <EventsDialog guildId={guildId} list={list} onClose={() => setEventsOpen(false)} /> : null}
      {channelMenu ? <SideMenu menu={channelMenu} guildId={guildId} serverName={name} list={list} collapsed={collapsed} voice={voice.voice} onToggle={toggle}
        onClose={() => setChannelMenu(null)} onEdit={() => void transition(() => { setEditing(channelMenu.channel.id); setChannelMenu(null) }, { kind: 'none' })} /> : null}
      {editing ? <Suspense fallback={null}><ChannelSettings channelId={editing} onClose={() => void transition(() => setEditing(null))} /></Suspense> : null}
      {creating ? <CreateChannel guildId={guildId} {...creating} onClose={() => setCreating(null)} onCreated={id => { setCreating(null); onPick(id) }} /> : null}
    </>
  )
}

// Where the server's invite lands, as ServerMenu's does: the open channel, else the first text channel you can read.
function inviteChannelOf(list: DiscordChannels | null, open: string | null) {
  return list?.channels.find(c => c.id === open && c.kind !== 'category') ?? list?.channels.find(c => c.kind === 'text' && c.canRead !== false) ?? null
}

// The column's head: the server, whose press or right-click is its menu (the band holds it), and Invite People at its right.
function ColumnHead({ guildId, guild, name, list, open, onInvite }: { guildId: string; guild: DiscordGuild | null; name: string; list: DiscordChannels | null; open: string | null; onInvite: (channel: DiscordChannel) => void }) {
  const menu = (x: number, y: number) => window.dispatchEvent(new CustomEvent('exo-dc-guild-menu', { detail: { guildId, x, y } }))
  const target = inviteChannelOf(list, open)
  return (
    <div className="exo-dc-channels-head">
      <button type="button" className="exo-dc-channels-head-main" aria-haspopup="menu"
        onClick={event => { const box = event.currentTarget.getBoundingClientRect(); menu(box.left, box.bottom + 4) }}
        onContextMenu={event => { event.preventDefault(); menu(event.clientX, event.clientY) }}>
        <span className="exo-dc-server-mark">{guild?.icon ? <img src={cdnSized(guild.icon, 24)} alt="" width={24} height={24} draggable={false} decoding="async" /> : <span className="exo-dc-initials">{initials(name)}</span>}</span>
        <strong>{name}</strong>
        <ChevronDown size={14} />
      </button>
      <button type="button" className="exo-dc-icon-btn exo-dc-channels-invite" aria-label={`Invite people to ${name}`} disabled={!target} onClick={() => { if (target) onInvite(target) }}><Icons.AddFriend size={20} /></button>
    </div>
  )
}

export function ChannelIcon({ channel, size = 18 }: { channel: DiscordChannel; size?: number }) {
  return createElement(channelGlyph(channel), { size })
}

function ChannelList({ channels, open, collapsed, onPick, onPreview, onToggle, onJoin, onCreate, onMenu, onInvite, onEdit, voice, list, openThread }: {
  channels: DiscordChannel[]
  open: string | null
  /** The server's list: the threads you're in that are open, each listed under its channel as Discord lists them. */
  list: DiscordChannels | null
  openThread?: string | null
  collapsed: Set<string>
  onPick: (id: string) => void
  onPreview: (id: string) => void
  onToggle: (id: string) => void
  onJoin: (id: string) => void
  /** A channel's tools: Invite People, and Edit Channel for those who may (the dialogs its menu opens). */
  onInvite: (channel: DiscordChannel) => void
  onEdit: (channel: DiscordChannel) => void
  onCreate?: (category: DiscordChannel) => void
  onMenu: (channel: DiscordChannel, x: number, y: number, inCategory: boolean) => void
  voice: VoiceControls
}) {
  let category: string | null = null
  const threads = list?.threads?.filter(t => t.joined && !t.archived) ?? []
  return channels.map(c => {
    if (c.kind === 'category') {
      category = c.id
      return (
        <div key={c.id} className="exo-dc-category-row">
          <button type="button" className="exo-dc-category" aria-expanded={!collapsed.has(c.id)} onClick={() => onToggle(c.id)}
            onContextMenu={event => { event.preventDefault(); onMenu(c, event.clientX, event.clientY, false) }}>
            <ChevronDown size={12} />
            <span>{c.name}</span>
          </button>
          {onCreate ? <button type="button" className="exo-dc-icon-btn exo-dc-category-add" aria-label={`Create channel in ${c.name}`} onClick={() => onCreate(c)}><Icons.Plus size={16} /></button> : null}
        </div>
      )
    }
    if (category && collapsed.has(category) && !(c.unread && !c.muted) && c.id !== open) return null
    const inCategory = category !== null
    if (c.kind === 'voice') return <VoiceChannel key={c.id} channel={c} voice={voice} open={open === c.id} onPreview={onPreview} onJoin={onJoin} onInvite={onInvite} onEdit={onEdit} onMenu={(channel, x, y) => onMenu(channel, x, y, inCategory)} />
    // Any text channel listed opens, as in Discord: one without Read Message History shows only what arrives while open.
    const readable = c.kind === 'text' || c.kind === 'forum'
    const inside = threads.filter(t => t.parentId === c.id)
    const selected = c.id === open && !inside.some(t => t.id === openThread)
    return (
      <Fragment key={c.id}>
      <div className={`exo-dc-chan${selected ? ' is-selected' : ''}`}>
        <button type="button"
          className={`exo-dc-item${c.unread ? ' is-unread' : ''}${c.muted ? ' is-muted' : ''}`}
          aria-current={selected}
          disabled={!readable}
          data-dc-channel={c.kind === 'text' && readable ? c.id : undefined}
          onContextMenu={event => { event.preventDefault(); onMenu(c, event.clientX, event.clientY, inCategory) }}
          aria-label={c.mentions ? `${c.name}, ${mentionsSaid(c.mentions)}` : undefined}
          onClick={() => onPick(c.id)}>
          <span className="exo-dc-hash" aria-hidden="true"><ChannelIcon channel={c} /></span>
          <span className="exo-dc-item-name">{c.name}</span>
          {c.mentions ? <span className="exo-dc-badge" aria-hidden="true">{c.mentions}</span> : null}
        </button>
        <ChannelTools channel={c} onInvite={onInvite} onEdit={onEdit} />
      </div>
      {inside.map(t => <ThreadItem key={t.id} thread={t} open={t.id === openThread} onPick={onPick} />)}
      </Fragment>
    )
  })
}

// A channel's tools at its right, on hover or while it's selected: Invite People, and Edit Channel where it may be edited.
function ChannelTools({ channel, onInvite, onEdit }: { channel: DiscordChannel; onInvite: (channel: DiscordChannel) => void; onEdit: (channel: DiscordChannel) => void }) {
  return (
    <span className="exo-dc-chan-tools">
      <button type="button" className="exo-dc-icon-btn" aria-label={`Invite people to ${channel.name}`} onClick={() => onInvite(channel)}><Icons.AddFriend size={16} /></button>
      {channel.canEdit ? <button type="button" className="exo-dc-icon-btn" aria-label={`Edit ${channel.name}`} onClick={() => onEdit(channel)}><Settings size={16} /></button> : null}
    </span>
  )
}

type SidebarThread = NonNullable<DiscordChannels['threads']>[number]

// A thread you're in, indented under its channel: picking it opens it there; right-click for its menu.
function ThreadItem({ thread, open, onPick }: { thread: SidebarThread; open: boolean; onPick: (id: string) => void }) {
  return (
    <button type="button" className={`exo-dc-item exo-dc-thread-item${thread.unread ? ' is-unread' : ''}${thread.muted ? ' is-muted' : ''}`}
      aria-current={open} data-dc-channel={thread.id} aria-label={thread.mentions ? `${thread.name}, ${mentionsSaid(thread.mentions)}` : undefined}
      onContextMenu={event => { event.preventDefault(); openThreadMenu(thread.id, event.clientX, event.clientY) }}
      onClick={() => onPick(thread.id)}>
      <span className="exo-dc-hash" aria-hidden="true"><Icons.Threads size={16} /></span>
      <span className="exo-dc-item-name">{thread.name}</span>
      {thread.mentions ? <span className="exo-dc-badge" aria-hidden="true">{thread.mentions}</span> : null}
    </button>
  )
}

/**
 * A voice channel: a click opens it without joining, as Discord's voice channel preview does (who is in it, their
 * cameras and streams marked, and Join voice); a double click joins at once. Who is in it shows underneath, ringed
 * while they speak. A channel you can't connect to still opens, so you can see who is there.
 */
function VoiceChannel({ channel, voice, open, onPreview, onJoin, onInvite, onEdit, onMenu }: {
  channel: DiscordChannel
  voice: VoiceControls
  open: boolean
  onPreview: (id: string) => void
  onJoin: (id: string) => void
  onInvite: (channel: DiscordChannel) => void
  onEdit: (channel: DiscordChannel) => void
  onMenu: (channel: DiscordChannel, x: number, y: number) => void
}) {
  const here = voice.voice.channelId === channel.id && voice.voice.state !== 'off'
  return (
    <div className={`exo-dc-voice-channel exo-dc-chan${here || open ? ' is-selected' : ''}`} onContextMenu={event => { if ((event.target as Element).closest('[data-dc-user]')) return; event.preventDefault(); onMenu(channel, event.clientX, event.clientY) }}>
      <button type="button" className={`exo-dc-item${here ? ' is-here' : ''}`}
        aria-label={here ? `${channel.name}, you're here` : channel.name} aria-current={here || open}
        onClick={() => onPreview(channel.id)} onDoubleClick={() => { if (!here && channel.canConnect !== false) onJoin(channel.id) }}>
        <span className="exo-dc-hash" aria-hidden="true"><ChannelIcon channel={channel} /></span>
        <span className="exo-dc-item-name">{channel.name}</span>
        {channel.userLimit ? <span className="exo-dc-voice-limit" aria-label={`${channel.members?.length ?? 0} of ${channel.userLimit} users`}>
          {channel.members?.length ?? 0}/{channel.userLimit}</span> : null}
      </button>
      <ChannelTools channel={channel} onInvite={onInvite} onEdit={onEdit} />
      {channel.stageTopic ? <VoiceStatus text={channel.stageTopic} /> : channel.status ? <VoiceStatus text={channel.status} /> : null}
      {channel.activity ? <VoiceActivity name={channel.activity} /> : null}
      {channel.members?.length ? <VoiceMembers members={channel.members} /> : null}
    </div>
  )
}

/**
 * Discord's right-click menu on a channel: Mark As Read (off when nothing is unread), Invite People, Copy Link, Mute
 * Channel for a while (or Unmute, with when it ends), Notification Settings (not on voice), Edit and Delete Channel,
 * and Copy Channel ID in Developer Mode.
 */
function ChannelMenu({ channel, x, y, inCategory, guildId, serverName, canManage, voice, onClose, onEdit }: {
  channel: DiscordChannel
  x: number
  y: number
  inCategory: boolean
  guildId: string
  serverName: string
  canManage: boolean
  /** Your call: in this voice channel, Set Channel Status is offered. */
  voice: VoiceControls['voice']
  onClose: () => void
  onEdit: () => void
}) {
  const developer = useAccountSettings().developerMode
  // Set Channel Status or Invite People, open over the room in the menu's place.
  const [dialog, setDialog] = useState<'status' | 'invite' | null>(null)
  const here = voice.state !== 'off' && voice.channelId === channel.id
  const [note, setNote] = useState<string | null>(null)
  const [sub, setSub] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !dialog })
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose])
  const run = (work: Promise<{ ok: boolean; message?: string | null }>) =>
    void work.then(result => { if (result.ok) onClose(); else setNote(errorText(result.message, 'Discord didn’t take that.')) }).catch(() => setNote('Discord didn’t take that.'))
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  const notify = (change: Parameters<typeof host.discordChannelNotify>[1]) => run(host.discordChannelNotify(channel.id, change))
  if (dialog) return { status: <VoiceStatusDialog channel={channel} onClose={onClose} />, invite: <InviteDialog name={serverName} channelId={channel.id} channelName={channel.name} onClose={onClose} /> }[dialog]
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div className="exo-dc-menu exo-dc-channel-menu" role="menu" aria-label={channel.name} style={{ left: x, top: y }} ref={box}>
      <button type="button" role="menuitem" disabled={!channel.unread && !channel.mentions} onClick={() => run(host.discordChannel(channel.id, 'read'))}>Mark as read</button>
      <button type="button" role="menuitem" onClick={() => setDialog('invite')}>Invite people</button>
      <button type="button" role="menuitem" onClick={() => copy(`https://discord.com/channels/${guildId}/${channel.id}`)}>Copy link</button>
      <span className="exo-dc-menu-line" role="separator" />
      <MuteItems what="channel" channel={channel} onChange={notify} />
      {channel.kind === 'voice' ? null : <NotifyLevels channel={channel} inCategory={inCategory} onPick={level => notify({ level })} />}
      {/* Discord's: Set Channel Status while you're in it, Clear Channel Status for a channel manager outside it. */}
      {channel.kind === 'voice' && here && channel.canSetStatus ? <button type="button" role="menuitem" onClick={() => setDialog('status')}>Set channel status</button>
        : channel.kind === 'voice' && channel.canEdit && channel.status ? <button type="button" role="menuitem" onClick={() => run(host.discordServer('voiceStatus', { channelId: channel.id, text: '' }))}>Clear channel status</button> : null}
      <span className="exo-dc-menu-line" role="separator" />
      {channel.canEdit ? <button type="button" role="menuitem" onClick={onEdit}>Edit channel</button> : null}
      {canManage ? (
        <ConfirmButton role="menuitem" className="is-danger" question="Delete channel?" danger onConfirm={() => run(host.discordChannel(channel.id, 'delete'))}>Delete channel</ConfirmButton>
      ) : null}

      {/* Copy Channel ID is Developer Mode's (Discord settings > Developer), as in Discord. */}
      {developer ? <button type="button" role="menuitem" onClick={() => copy(channel.id)}><Icons.CopyId size={16} />Copy channel ID</button> : null}
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

type MenuAt = { channel: DiscordChannel; x: number; y: number; inCategory: boolean }

// A right-click in the channel list: a category's menu, or a channel's.
function SideMenu({ menu, guildId, serverName, list, collapsed, voice, onToggle, onClose, onEdit }: {
  menu: MenuAt
  guildId: string
  serverName: string
  list: DiscordChannels | null
  collapsed: Set<string>
  voice: VoiceControls['voice']
  onToggle: (id: string, folded?: boolean) => void
  onClose: () => void
  onEdit: () => void
}) {
  const canManage = list?.canManage === true
  if (menu.channel.kind === 'category') {
    return <CategoryMenu category={menu.channel} x={menu.x} y={menu.y} channels={list?.channels ?? []} collapsed={collapsed} canManage={canManage} onToggle={onToggle} onClose={onClose} onEdit={onEdit} />
  }
  return <ChannelMenu {...menu} guildId={guildId} serverName={serverName} canManage={canManage} voice={voice} onClose={onClose} onEdit={onEdit} />
}

/**
 * Discord's category menu: Mark as read (its channels), Collapse category and Collapse all categories, Mute category,
 * its Notification settings, then Edit category and Delete category for those who may, and Copy category ID.
 */
function CategoryMenu({ category, x, y, channels, collapsed, canManage, onToggle, onClose, onEdit }: {
  category: DiscordChannel
  x: number
  y: number
  channels: DiscordChannel[]
  collapsed: Set<string>
  canManage: boolean
  onToggle: (id: string, folded?: boolean) => void
  onClose: () => void
  onEdit: () => void
}) {
  const developer = useAccountSettings().developerMode
  const [sub, setSub] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  const run = (work: Promise<{ ok: boolean; message?: string | null }>) =>
    void work.then(result => { if (result.ok) onClose(); else setNote(errorText(result.message, 'Discord didn’t take that.')) }).catch(() => setNote('Discord didn’t take that.'))
  const categories = channels.filter(c => c.kind === 'category')
  // Its channels: the ones listed after it, up to the next category.
  const at = channels.indexOf(category)
  const next = channels.findIndex((c, i) => i > at && c.kind === 'category')
  const unread = channels.slice(at + 1, next < 0 ? undefined : next).filter(c => c.unread || c.mentions)
  const notify = (change: Parameters<typeof host.discordChannelNotify>[1]) => run(host.discordChannelNotify(category.id, change))
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div className="exo-dc-menu exo-dc-channel-menu" role="menu" aria-label={category.name} style={{ left: x, top: y }} ref={box}>
      <button type="button" role="menuitem" disabled={!unread.length} onClick={() => run(Promise.all(unread.map(c => host.discordChannel(c.id, 'read'))).then(() => ({ ok: true })))}>Mark as read</button>
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitemcheckbox" aria-checked={collapsed.has(category.id)} onClick={() => { onToggle(category.id); onClose() }}>
        <span className="exo-dc-ctx-name">Collapse category</span>
        {collapsed.has(category.id) ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      {categories.every(c => collapsed.has(c.id)) ? null
        : <button type="button" role="menuitem" onClick={() => { for (const c of categories) onToggle(c.id, true); onClose() }}>Collapse all categories</button>}
      <span className="exo-dc-menu-line" role="separator" />
      <MuteItems what="category" channel={category} onChange={notify} />
      <NotifyLevels channel={category} inCategory={false} onPick={level => notify({ level })} />
      {category.canEdit || canManage ? <span className="exo-dc-menu-line" role="separator" /> : null}
      {category.canEdit ? <button type="button" role="menuitem" onClick={onEdit}>Edit category</button> : null}
      {canManage ? <ConfirmButton role="menuitem" className="is-danger" question="Delete category?" danger onConfirm={() => run(host.discordChannel(category.id, 'delete'))}>Delete category</ConfirmButton> : null}
      {developer ? <button type="button" role="menuitem" onClick={() => { void navigator.clipboard.writeText(category.id).catch(() => { }); onClose() }}><Icons.CopyId size={16} />Copy category ID</button> : null}
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

// Mute a channel or category for a while, or unmute it (saying until when a timed mute lasts).
function MuteItems({ what, channel, onChange }: { what: 'channel' | 'category'; channel: DiscordChannel; onChange: (change: { muted: boolean; minutes?: number }) => void }) {
  if (channel.muted) {
    return (
      <button type="button" role="menuitem" onClick={() => onChange({ muted: false })}>
        <span className="exo-dc-menu-text">Unmute {what}{channel.mutedUntil ? <small>Muted until {new Date(channel.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
      </button>
    )
  }
  return (
    <Submenu label={`Mute ${what}`}>
      {MUTE_FOR.map(([minutes, label]) => <button key={minutes} type="button" role="menuitem" onClick={() => onChange(minutes ? { muted: true, minutes } : { muted: true })}>{label}</button>)}
    </Submenu>
  )
}

// A voice channel's status under its name, as Discord draws it: its words, server emoji as pictures.
function VoiceStatus({ text }: { text: string }) {
  const parts = text.split(/<(a?):(\w+):(\d+)>/)
  return (
    <span className="exo-dc-channel-status">
      {parts.map((part, i) => i % 4 === 3 ? <img key={i} src={emojiUrl(part, parts[i - 2] === 'a', 32)} alt={`:${parts[i - 1]}:`} width={14} height={14} draggable={false} />
        : i % 4 === 0 ? part : null)}
    </span>
  )
}

function VoiceActivity({ name }: { name: string }) {
  return (
    <span className="exo-dc-channel-status exo-dc-channel-activity" aria-label={`Activity: ${name}`}>
      <Icons.Playing size={12} />
      <span>{name}</span>
    </span>
  )
}

// Discord's Set Channel Status: what the call is about (500 characters), under the channel's name for everyone.
function VoiceStatusDialog({ channel, onClose }: { channel: DiscordChannel; onClose: () => void }) {
  const [text, setText] = useState(channel.status ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const save = () => {
    if (text === (channel.status ?? '')) { onClose(); return }
    setBusy(true)
    void host.discordServer('voiceStatus', { channelId: channel.id, text }).then(result => {
      setBusy(false)
      if (result.ok) onClose(); else setError(errorText(result.message, 'Discord didn’t set that status.'))
    }).catch(() => { setBusy(false); setError('Discord didn’t set that status.') })
  }
  return (
    <Confirm title="What are we chatting about?" action="Set status" danger={false} form busy={busy} disabled={text.length > 500} error={error} onClose={onClose} onConfirm={save}>
      <p>Let others know what you're up to in the voice channel.</p>
      <input className="exo-dc-modal-input" value={text} autoFocus maxLength={500} placeholder={`Status for ${channel.name}`} aria-label="Status"
        onChange={event => setText(event.target.value.replace(/\n/g, ' '))} onKeyDown={event => { if (event.key === 'Enter') save() }} />
    </Confirm>
  )
}

// Notification Settings: the level above (Use Category Default, or Use Server Default outside a category, with what
// that is), All Messages, Only @mentions or Nothing, as Discord's channel menu offers them.
function NotifyLevels({ channel, inCategory, onPick }: { channel: DiscordChannel; inCategory: boolean; onPick: (level: 0 | 1 | 2 | 3) => void }) {
  const level = channel.notify ?? null
  return (
    <Submenu label="Notification settings">
      <button type="button" role="menuitemradio" aria-checked={level === null} onClick={() => onPick(3)}>
        <span className="exo-dc-menu-text">{inCategory ? 'Use category default' : 'Use server default'}<small>{LEVELS.find(([value]) => value === (channel.notifyDefault ?? 0))?.[1]}</small></span>
        {level === null ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      {LEVELS.map(([value, label]) => (
        <button key={value} type="button" role="menuitemradio" aria-checked={level === value} onClick={() => onPick(value)}>
          <span className="exo-dc-ctx-name">{label}</span>
          {level === value ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
        </button>
      ))}
    </Submenu>
  )
}

// In opt-in mode Discord lists only the channels followed (or in a followed category), private ones (Browse Channels
// never offers them), the open one, and their categories.
// The channels the list shows: in opt-in mode the followed ones; with Hide Muted Channels, none that are muted (the
// open one stays); a category with nothing left under it goes too, as in Discord.
function followed(list: DiscordChannels | null, open: string | null) {
  const all = list?.channels ?? []
  const channels = list?.hideMuted ? withoutEmpty(all.filter(c => c.kind === 'category' || !c.muted || c.id === open)) : all
  if (!list?.optIn) return channels
  return withoutEmpty(channels.filter(c => c.kind === 'category' || c.id === open || c.private || (c.optedIn && !(c.resource && list.rows?.guide))))
}

const withoutEmpty = (kept: DiscordChannel[]) => kept.filter((c, i) => c.kind !== 'category' || (kept[i + 1] && kept[i + 1].kind !== 'category'))

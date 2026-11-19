import { Suspense, lazy, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { guildBannerUrl, moveServer, openDiscord, requestServerProfile, type DiscordChannel, type DiscordChannels, type DiscordGuild, type DiscordModeration, type DiscordRailItem, type ServerMove } from '../../lib/discord'
import { Icons } from './icons'
import { showReport } from './Report'
import { CreateChannel, PAGES, ServerPrivacy } from './serverPages'
import { EventsDialog } from './events'
// Server Settings load the first time they open, not with the room.
const ServerSettings = lazy(() => import('./ServerSettings').then(m => ({ default: m.ServerSettings })))
import { useAccountSettings } from '../../lib/discordPrefs'
import { ConfirmButton } from '../ui/ConfirmButton'
import { NAV, type Section } from './serverSections'
import { InviteDialog } from './InviteDialog'
import { transition } from '../../lib/viewTransition'
import { LEVELS, MUTE_FOR } from './ChatHeader'
import { SubContext, Submenu } from './UserMenu'
import { useDismiss } from '../../lib/dismiss'
import { errorText } from '../../lib/errorWords'

// The server menu's Create Channel and Create Category (Manage Channels) and Create Event (Manage Events), as Discord's.
function CreateItems({ list, onCreate }: { list: DiscordChannels | null; onCreate: (what: 'channel' | 'category' | 'event') => void }) {
  return (
    <>
      {list?.canManage ? <button type="button" role="menuitem" onClick={() => onCreate('channel')}><Icons.Add size={16} />Create channel</button> : null}
      {list?.canManage ? <button type="button" role="menuitem" onClick={() => onCreate('category')}><Icons.Category size={16} />Create category</button> : null}
      {list?.canManageEvents ? <button type="button" role="menuitem" onClick={() => onCreate('event')}><Icons.Events size={16} />Create event</button> : null}
    </>
  )
}

// Where an invite lands, as Discord picks: the open channel, else the first text channel you can read.
function inviteChannel(list: DiscordChannels | null, open: string | null) {
  return list?.channels.find(c => c.id === open && c.kind !== 'category') ?? list?.channels.find(c => c.kind === 'text' && c.canRead !== false) ?? null
}

/**
 * A server's right-click menu, in Discord's order: Mark As Read (off when nothing is unread), Invite
 * to Server, Mute Server for a while (or Unmute, with when it ends), Notification Settings, Hide Muted Channels, Show All
 * Channels (a community server), Server Settings with each page you may open, Privacy Settings, Edit Per-server Profile,
 * Create Channel, Category and Event where allowed, Move To, Copy Server Info (its icon's and banner's links), Leave and
 * Report Server (not for its owner), and Copy Server ID in Developer Mode. What it opens stays on the server you're on.
 */
export function GuildMenu({ guild, open = null, x, y, rail, onGo, onMove, onClose }: {
  guild: DiscordGuild
  /** The channel you're in on it: where an invite lands. */
  open?: string | null
  x: number
  y: number
  rail: DiscordRailItem[]
  onGo: (place: string) => void
  onMove: (next: DiscordRailItem[]) => void
  onClose: () => void
}) {
  const developer = useAccountSettings().developerMode
  const [note, setNote] = useState<string | null>(null)
  const [dialog, setDialog] = useState<GuildDialog | null>(null)
  const [sub, setSub] = useState<string | null>(null)
  // Exo's copy of its channel list: Hide Muted Channels, Server Settings' pages and Create, as you may on this server.
  const [list, setList] = useState<DiscordChannels | null>(null)
  useEffect(() => {
    let live = true
    host.discordChannels(guild.id).then(result => { if (live) setList(result) }, () => { })
    return () => { live = false }
  }, [guild.id])
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !dialog })
  useEffect(() => {
    if (dialog) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose, dialog])
  const why = 'Discord did not take that.'
  const run = (work: Promise<{ ok: boolean; message?: string | null }>) =>
    void work.then(result => { if (result.ok) onClose(); else setNote(errorText(result.message, why)) }).catch(() => setNote(why))
  const server = (action: 'mute' | 'notify', params: Parameters<typeof host.discordServer>[1]) => run(host.discordServer(action, { guildId: guild.id, ...params }))
  const invite = () => {
    const channel = inviteChannel(list, open)
    if (channel) setDialog({ kind: 'invite', channel }); else setNote("There's no channel to invite people to.")
  }
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  if (dialog) return <GuildMenuDialog dialog={dialog} guild={guild} list={list} onClose={onClose} />
  const moderation = list?.moderation
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div ref={box} className="exo-dc-menu exo-dc-channel-menu exo-dc-guild-menu" role="menu" aria-label={guild.name} style={{ left: x, top: y }}>
      <button type="button" role="menuitem" disabled={!guild.unread && !guild.mentions} onClick={() => run(host.discordMarkRead(guild.id))}>Mark as read</button>
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitem" disabled={!list} onClick={invite}>Invite to server</button>
      <span className="exo-dc-menu-line" role="separator" />
      {guild.muted ? (
        <button type="button" role="menuitem" onClick={() => server('mute', { muted: false })}>
          <span className="exo-dc-menu-text">Unmute server{guild.mutedUntil ? <small>Muted until {new Date(guild.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
        </button>
      ) : (
        <Submenu label="Mute server">
          {MUTE_FOR.map(([minutes, label]) => <button key={minutes} type="button" role="menuitem" onClick={() => server('mute', minutes ? { muted: true, minutes } : { muted: true })}>{label}</button>)}
        </Submenu>
      )}
      <NotifyItems guild={guild} onChange={params => server('notify', params)} />
      <HideMutedItem list={list} onChange={hideMuted => server('notify', { hideMuted })} />
      <ShowAllItem guildId={guild.id} list={list} onDone={onClose} />
      <span className="exo-dc-menu-line" role="separator" />
      {moderation ? <SettingsItems moderation={moderation} features={list?.features ?? []} onPick={section => setDialog({ kind: 'settings', section })} /> : null}
      <button type="button" role="menuitem" onClick={() => setDialog({ kind: 'privacy' })}>Privacy settings</button>
      <button type="button" role="menuitem" onClick={() => { onClose(); requestServerProfile(guild.id) }}>Edit per-server profile</button>
      {list?.canManage || list?.canManageEvents ? <>
        <span className="exo-dc-menu-line" role="separator" />
        <CreateItems list={list} onCreate={what => setDialog(what === 'event' ? { kind: 'event' } : { kind: 'create', asCategory: what === 'category' })} />
      </> : null}
      <span className="exo-dc-menu-line" role="separator" />
      <MoveItems rail={rail} guildId={guild.id} onMove={next => { onClose(); onMove(next) }} />
      <CopyInfoItems guild={guild} banner={list?.banner ?? null} onCopy={copy} />
      {guild.owner ? null : <>
        <span className="exo-dc-menu-line" role="separator" />
        <ConfirmButton role="menuitem" className="is-danger" question="Leave server?" danger
          onConfirm={() => void host.discordServer('leave', { guildId: guild.id }).then(result => { if (result.ok) { onClose(); onGo('@me') } else setNote(errorText(result.message, why)) }, () => setNote(why))}>
          Leave server
        </ConfirmButton>
        <button type="button" role="menuitem" className="is-danger" onClick={() => {
          onClose()
          showReport({ kind: 'guild', ids: { guild_id: guild.id }, preview: { name: guild.name, image: guild.icon } })
        }}><Icons.Report size={16} />Report server</button>
      </>}
      {developer ? <>
        <span className="exo-dc-menu-line" role="separator" />
        <button type="button" role="menuitem" onClick={() => copy(guild.id)}><Icons.CopyId size={16} />Copy server ID</button>
      </> : null}
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

type GuildDialog = { kind: 'invite'; channel: DiscordChannel } | { kind: 'privacy' } | { kind: 'settings'; section: Section }
  | { kind: 'create'; asCategory: boolean } | { kind: 'event' }

// What a server's menu opened: its invite, Privacy Settings, Server Settings at a page, Create Channel or Category (a new
// channel opens), or Create Event.
function GuildMenuDialog({ dialog, guild, list, onClose }: { dialog: GuildDialog; guild: DiscordGuild; list: DiscordChannels | null; onClose: () => void }) {
  switch (dialog.kind) {
    case 'invite': return <InviteDialog name={guild.name} channelId={dialog.channel.id} channelName={dialog.channel.name} onClose={onClose} />
    case 'privacy': return <ServerPrivacy guildId={guild.id} name={guild.name} onClose={onClose} />
    case 'event': return <EventsDialog guildId={guild.id} list={list} create onClose={onClose} />
    case 'create':
      return (
        <CreateChannel guildId={guild.id} parentId={null} category="" asCategory={dialog.asCategory} onClose={onClose}
          onCreated={id => { onClose(); if (!dialog.asCategory) openDiscord({ guildId: guild.id, channelId: id, outside: false }) }} />
      )
    case 'settings':
      return list?.moderation ? (
        <Suspense fallback={null}>
          <ServerSettings guildId={guild.id} name={guild.name} moderation={list.moderation} channels={list.channels} start={dialog.section} onClose={() => void transition(onClose)}
            onMembers={list.rows?.members ? () => openDiscord({ guildId: guild.id, channelId: PAGES.members, outside: false }) : undefined} />
        </Suspense>
      ) : null
  }
}

// Hide Muted Channels, saved on your account as Discord's is.
function HideMutedItem({ list, onChange }: { list: DiscordChannels | null; onChange: (hide: boolean) => void }) {
  return (
    <button type="button" role="menuitemcheckbox" aria-checked={!!list?.hideMuted} disabled={!list} onClick={() => onChange(!list?.hideMuted)}>
      <span className="exo-dc-ctx-name">Hide muted channels</span>
      {list?.hideMuted ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
    </button>
  )
}

// Show All Channels, which Discord offers in a community server (off: only the channels you follow).
function ShowAllItem({ guildId, list, onDone }: { guildId: string; list: DiscordChannels | null; onDone: () => void }) {
  if (!list?.rows?.guide) return null
  return (
    <button type="button" role="menuitemcheckbox" aria-checked={!list.optIn}
      onClick={() => { onDone(); void host.discordServer('showAll', { guildId, on: list.optIn === true }).catch(() => { }) }}>
      <span className="exo-dc-ctx-name">Show all channels</span>
      {list.optIn ? null : <Icons.Check size={14} className="exo-dc-ctx-check" />}
    </button>
  )
}

// Server Settings, each page you may open on this server, as Discord's submenu lists them.
function SettingsItems({ moderation, features, onPick }: { moderation: DiscordModeration; features: string[]; onPick: (section: Section) => void }) {
  const pages = NAV.flatMap(group => group.items).filter(item => item.shown(moderation, { features }))
  if (!Object.values(moderation).some(Boolean) || !pages.length) return null
  return (
    <Submenu label="Server settings">
      {pages.map(page => <button key={page.id} type="button" role="menuitem" onClick={() => onPick(page.id)}>{page.label}</button>)}
    </Submenu>
  )
}

const MOVES: [ServerMove, string][] = [['folderFirst', 'First in folder'], ['folderLast', 'Last in folder'], ['listFirst', 'First in list'], ['listLast', 'Last in list'], ['noFolder', 'No folder']]

// Move To: the top or foot of its folder or of the server list, or out of its folder, saved as a drag saves it.
function MoveItems({ rail, guildId, onMove }: { rail: DiscordRailItem[]; guildId: string; onMove: (next: DiscordRailItem[]) => void }) {
  const inFolder = rail.some(item => item.id != null && item.guildIds.includes(guildId))
  const moves = MOVES.filter(([to]) => inFolder || to === 'listFirst' || to === 'listLast')
  return (
    <Submenu label="Move to">
      {moves.map(([to, label]) => {
        const next = moveServer(rail, guildId, to)
        return <button key={to} type="button" role="menuitem" disabled={!next} onClick={() => { if (next) onMove(next) }}>{label}</button>
      })}
    </Submenu>
  )
}

// Copy Server Info: links to its icon and banner at full size.
function CopyInfoItems({ guild, banner, onCopy }: { guild: DiscordGuild; banner: string | null; onCopy: (text: string) => void }) {
  if (!guild.icon && !banner) return null
  const full = (link: string) => link.replace(/([?&])size=\d+/, '$1size=4096')
  return (
    <Submenu label="Copy server info">
      {guild.icon ? <button type="button" role="menuitem" onClick={() => onCopy(full(guild.icon!))}>Copy server icon image link</button> : null}
      {banner ? <button type="button" role="menuitem" onClick={() => onCopy(full(guildBannerUrl(guild.id, banner, true)))}>Copy server banner image link</button> : null}
    </Submenu>
  )
}

// A server's Notification Settings, in its menus: the level, then Suppress @everyone and @here, Suppress all role
// @mentions, Suppress highlights and Mute new events, then Mobile push notifications, as Discord's.
function NotifyItems({ guild, onChange }: { guild: DiscordGuild; onChange: (params: Parameters<typeof host.discordServer>[1]) => void }) {
  const level = guild.notify ?? 0
  return (
    <Submenu label="Notification settings">
      {LEVELS.map(([value, label]) => (
        <button key={value} type="button" role="menuitemradio" aria-checked={level === value} onClick={() => onChange({ level: value })}>
          <span className="exo-dc-ctx-name">{label}</span>
          {level === value ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
        </button>
      ))}
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitemcheckbox" aria-checked={!!guild.suppressEveryone} onClick={() => onChange({ suppressEveryone: !guild.suppressEveryone })}>
        <span className="exo-dc-ctx-name">Suppress @everyone and @here</span>
        {guild.suppressEveryone ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      <button type="button" role="menuitemcheckbox" aria-checked={!!guild.suppressRoles} onClick={() => onChange({ suppressRoles: !guild.suppressRoles })}>
        <span className="exo-dc-ctx-name">Suppress all role @mentions</span>
        {guild.suppressRoles ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      <CheckRow label="Suppress highlights" on={!!guild.suppressHighlights} onChange={on => onChange({ suppressHighlights: on })} />
      <CheckRow label="Mute new events" on={!!guild.muteEvents} onChange={on => onChange({ muteEvents: on })} />
      <span className="exo-dc-menu-line" role="separator" />
      <CheckRow label="Mobile push notifications" on={guild.mobilePush !== false} onChange={on => onChange({ mobilePush: on })} />
    </Submenu>
  )
}

function CheckRow({ label, on, onChange }: { label: string; on: boolean; onChange: (on: boolean) => void }) {
  return (
    <button type="button" role="menuitemcheckbox" aria-checked={on} onClick={() => onChange(!on)}>
      <span className="exo-dc-ctx-name">{label}</span>
      {on ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
    </button>
  )
}

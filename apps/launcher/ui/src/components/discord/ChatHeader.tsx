import { MessagesWait } from './waits'
import { createElement, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { host } from '../../lib/host'
import { ActionButton } from '../ui/ConfirmButton'
import { report } from '../../lib/report'
import { type DiscordChannel, type DiscordFriend, type DiscordMessage, type DiscordPlace, type DiscordSearchFilters, type DiscordThread } from '../../lib/discord'
import { channelGlyph, Icons } from './icons'
import { openThreadMenu } from './ThreadMenu'
import { Markdown, MarkdownLine, type MarkdownContext } from './markdown'
import { Popover } from '../ui/Dialog'
import { Avatar } from './presence'
import { ChannelNav, ServerNavContext } from './Channels'
import { FriendPicker } from './Messages'
import { Result, useOutside } from './results'
import { SearchBox, SearchResults } from './SearchBar'
import { Wait, sample, times } from '../ui/Wait'
import { ErrorNote } from '../ui/ErrorNote'
import { useFriends } from '../../lib/friendsPlaying'
import { doingWords } from './ActivityLine'
import { errorText } from '../../lib/errorWords'

/**
 * The line over a conversation: where you are (a server's channel and its topic; a DM's person), then calls, pins and
 * search on the right, the search as wide as the column under it.
 */
export function ChatHeader({ channel, guildId, ctx, onCall, inCall, onOpenThread, parent, onBack, onHide }: {
  channel: DiscordChannel
  guildId: string | null
  onOpenThread?: (thread: DiscordChannel) => void
  /** A thread's channel, and closing the thread back to it. */
  parent?: DiscordChannel | null
  onBack?: () => void
  ctx: MarkdownContext
  /** A DM's Start Voice Call; video: Start Video Call. */
  onCall?: (video: boolean) => void
  inCall?: boolean
  /** A chat in a side panel: the channel's name and Close Chat, nothing else. */
  onHide?: () => void
}) {
  const [panel, setPanel] = useState<'pins' | 'search' | 'threads' | null>(null)
  const nav = useContext(ServerNavContext)
  // What was searched for: Enter searches, typing after it doesn't until the next Enter.
  const [searched, setSearched] = useState<DiscordSearchFilters | null>(null)
  if (onHide) return <PanelHead channel={channel} guildId={guildId} onHide={onHide} />
  // A press on a list's button opens it, and again closes it.
  const toggle = (next: 'pins' | 'threads') => setPanel(p => p === next ? null : next)
  return (
    <header className="exo-dc-chat-head">
      <HeadTitle channel={channel} guildId={guildId} ctx={ctx} parent={parent} onBack={onBack} />
      <span className="exo-dc-head-tools">
        {onCall ? (
          <>
            <button type="button" className={`exo-dc-icon-btn${inCall ? ' is-on' : ''}`} aria-label={inCall ? 'In call' : 'Start a voice call'} disabled={inCall} onClick={() => onCall(false)}>
              <Icons.Call size={20} />
            </button>
            <button type="button" className="exo-dc-icon-btn" aria-label="Start a video call" disabled={inCall} onClick={() => onCall(true)}>
              <Icons.Camera size={20} />
            </button>
          </>
        ) : null}
        <HeadSearch>
          <SearchBox key={`search:${guildId ?? channel.id}`} channel={channel} guildId={guildId} ctx={ctx} place={guildId ? nav?.name ?? null : channel.name}
            onSearch={filters => { setSearched(filters); setPanel('search') }} onClear={() => { setSearched(null); setPanel(p => p === 'search' ? null : p) }} />
        </HeadSearch>
        <HeadMore key={`more:${channel.id}`} channel={channel} guildId={guildId} threads={!!(guildId && onOpenThread)} panel={panel} toggle={toggle} />
      </span>
      {panel ? <HeadPanel panel={panel} channel={channel} searched={searched} ctx={ctx} onOpenThread={onOpenThread} onClose={() => setPanel(null)} /> : null}
    </header>
  )
}

/** The column beside the conversation: a server's people, a DM's person or a group's members. Always there (owner, 2026-11-14: no turning it off). */
export function PeopleColumn({ className, children }: { className?: string; children: React.ReactNode }) {
  return <div className={['exo-dc-beside', className].filter(Boolean).join(' ')}>{children}</div>
}

/** The head's search: one glyph until pressed; pressed, focused or holding words or filters, the field itself. */
function HeadSearch({ children }: { children: React.ReactNode }) {
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLSpanElement>(null)
  useEffect(() => { if (open) box.current?.querySelector('input')?.focus() }, [open])
  useEffect(() => {
    const focus = () => {
      setOpen(true)
      window.requestAnimationFrame(() => {
        const input = box.current?.querySelector('input')
        input?.focus()
        input?.select()
      })
    }
    window.addEventListener('exo-dc-focus-search', focus)
    return () => window.removeEventListener('exo-dc-focus-search', focus)
  }, [])
  // Focus leaving the field for somewhere outside it: empty, it folds back to the glyph (Escape blurs it, so folds too).
  const left = (event: React.FocusEvent<HTMLSpanElement>) => {
    if (event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)) return
    window.setTimeout(() => {
      const el = box.current
      if (!el || el.contains(document.activeElement)) return
      if (!el.querySelector('input')?.value && !el.querySelector('.exo-dc-search-chip')) setOpen(false)
    }, 160)
  }
  return (
    <span ref={box} className={`exo-dc-head-search${open ? ' is-open' : ''}`} onBlur={left}>
      {open ? null : <button type="button" className="exo-dc-icon-btn exo-dc-head-search-glyph" aria-label="Search" aria-expanded="false" onClick={() => setOpen(true)}><Icons.Search size={20} /></button>}
      <span className="exo-dc-head-search-field" hidden={!open}>{children}</span>
    </span>
  )
}

// The channel's topic, Discord's way: its first line in the bar, mentions and links working; a press anywhere else on it
// opens all of it in Channel topic.
export function Topic({ channel, guildId, topic, ctx }: { channel: DiscordChannel; guildId: string | null; topic: string; ctx: MarkdownContext }) {
  const [open, setOpen] = useState(false)
  const server = useContext(ServerNavContext)?.name
  return (
    <>
      <span className="exo-dc-head-line" aria-hidden="true" />
      <span className="exo-dc-chat-topic" onClick={event => { if (!(event.target as Element).closest('a, button, img, .exo-dc-mention')) setOpen(true) }}>
        <button type="button" className="sr-only" onClick={() => setOpen(true)}>Open channel topic</button>
        {server ? `${server} \u00b7 ` : null}<MarkdownLine text={topic} ctx={ctx} />
      </span>
      {open ? <TopicDialog channel={channel} guildId={guildId} topic={topic} ctx={ctx} onClose={() => setOpen(false)} /> : null}
    </>
  )
}

function TopicDialog({ channel, guildId, topic, ctx, onClose }: { channel: DiscordChannel; guildId: string | null; topic: string; ctx: MarkdownContext; onClose: () => void }) {
  return (
    <Popover portal={false} className="exo-dc-modal exo-dc-topic" label="Channel topic" onClick={event => event.stopPropagation()} onClose={onClose}>
      <h2>Channel topic</h2>
      <span className="exo-dc-topic-where"><HeadIcon channel={channel} guildId={guildId} />{channel.name}</span>
      <div className="exo-dc-topic-text"><Markdown text={topic} ctx={ctx} /></div>
    </Popover>
  )
}

// The list the line opened: pins, search results or the channel's threads.
function HeadPanel({ panel, channel, searched, ctx, onOpenThread, onClose }: {
  panel: 'pins' | 'search' | 'threads'
  channel: DiscordChannel
  searched: DiscordSearchFilters | null
  ctx: MarkdownContext
  onOpenThread?: (thread: DiscordChannel) => void
  onClose: () => void
}) {
  if (panel === 'pins') return <Pins channel={channel} ctx={ctx} onClose={onClose} />
  if (panel === 'search') return searched ? <SearchResults channel={channel} filters={searched} ctx={ctx} onClose={onClose} /> : null
  return onOpenThread ? <Threads channelId={channel.id} onOpen={thread => { onClose(); onOpenThread(thread) }} onClose={onClose} /> : null
}

// What the line is over. In a server: the channel, then a thread open in it (with closing it), else its topic. In a DM:
// the person or group.
function HeadTitle({ channel, guildId, ctx, parent, onBack }: { channel: DiscordChannel; guildId: string | null; ctx: MarkdownContext; parent?: DiscordChannel | null; onBack?: () => void }) {
  const nav = useContext(ServerNavContext)
  if (nav) {
    return (
      <>
        <ChannelNav />
        {parent && onBack ? (
          <>
            <Icons.Crumb size={16} />
            <span className="exo-dc-thread-title"><Icons.Threads size={18} /><strong>{channel.name}</strong></span>
            <button type="button" className="exo-dc-icon-btn" onClick={onBack} aria-label={`Back to ${parent.name}`}><Icons.Close size={16} /></button>
          </>
        ) : channel.topic ? <Topic channel={channel} guildId={guildId} topic={channel.topic} ctx={ctx} /> : nav.name ? <span className="exo-dc-chat-topic">{nav.name}</span> : null}
      </>
    )
  }
  const group = channel.dm?.group
  return (
    <>
    <div className="exo-dc-head-person">
      {channel.dm ? <Avatar src={channel.dm.avatar} name={channel.name} status={group ? null : channel.dm.status} size={24} /> : <Icons.Chat size={22} />}
      <strong>{channel.name}</strong>
    </div>
    {channel.dm && !group ? <DoingLine dm={channel.dm} /> : channel.dm?.activity ? <span className="exo-dc-chat-topic">{channel.dm.activity}</span> : null}
    </>
  )
}

// A conversation's person: what they are doing, else their status, where a channel has its server and topic.
function DoingLine({ dm }: { dm: NonNullable<DiscordChannel['dm']> }) {
  const { friends } = useFriends()
  const friend = dm.userId ? friends.find(f => f.id === dm.userId) : undefined
  return <span className="exo-dc-chat-topic">{doingWords(friend, dm.status, dm.activity)}</span>
}

// A chat in a side panel (beside a call, or a Server Guide resource): the channel
// and Close, nothing else.
function PanelHead({ channel, guildId, onHide }: { channel: DiscordChannel; guildId: string | null; onHide: () => void }) {
  return (
    <header className="exo-dc-chat-head">
      <HeadIcon channel={channel} guildId={guildId} />
      <strong>{channel.name}</strong>
      <span className="exo-dc-head-tools">
        <button type="button" className="exo-dc-icon-btn" aria-label="Close chat" onClick={onHide}><Icons.Close size={18} /></button>
      </span>
    </header>
  )
}

type Found = { messages: DiscordMessage[]; places: Record<string, DiscordPlace | null>; error: string | null; loading: boolean }

/** One of the header's lists from Discord, asked again when what it asks changes; a late answer to an old question is dropped. */
function useFound(ask: () => Promise<{ ok: boolean; message?: string | null; messages: DiscordMessage[]; places?: Record<string, DiscordPlace | null> }>, failed: string, key: string) {
  const [found, setFound] = useState<Found>({ messages: [], places: {}, error: null, loading: true })
  useEffect(() => {
    let live = true
    setFound({ messages: [], places: {}, error: null, loading: true })
    ask().then(result => {
      if (live) setFound({ messages: result.messages, places: result.places ?? {}, error: result.ok ? null : errorText(result.message, failed), loading: false })
    }).catch((err: unknown) => {
      report(err, 'ChatHeader.' + key)
      if (live) setFound({ messages: [], places: {}, error: failed, loading: false })
    })
    return () => { live = false }
  }, [key]) // eslint-disable-line react-hooks/exhaustive-deps -- key names what is asked
  return found
}

function Results({ title, found, ctx, empty, box, tools, here, onClose, onRemove }: {
  title: string
  found: Found
  ctx: MarkdownContext
  empty: string
  box: React.RefObject<HTMLDivElement | null>
  tools?: React.ReactNode
  /** The conversation open now: its rows don't repeat where they are. */
  here: string
  onClose: () => void
  /** The pins' Unpin on each row (Shift held: no question). */
  onRemove?: (message: DiscordMessage) => void
}) {
  const { messages, places, loading, error } = found
  return (
    <div className="exo-dc-popover" ref={box} role="dialog" aria-label={title}>
      <p className="exo-dc-popover-title">{title}</p>
      {tools}
      <div className="exo-dc-popover-list">
        {loading ? <MessagesWait label="Loading messages" /> : null}
        {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
        {!loading && !error && messages.length === 0 ? <p className="exo-dc-note">{empty}</p> : null}
        {messages.map(message => <Result key={message.id} message={message} place={places[message.channel_id] ?? null} here={here} ctx={ctx} onJumped={onClose}
          onRemove={onRemove ? () => onRemove(message) : undefined} removeLabel="Unpin" ask="Unpin?" />)}
      </div>
    </div>
  )
}

// The pins, each with Discord's Unpin where you may pin (asked in place, unless Shift is held).
function Pins({ channel, ctx, onClose }: { channel: DiscordChannel; ctx: MarkdownContext; onClose: () => void }) {
  const box = useOutside(onClose)
  const found = useFound(() => host.discordPins(channel.id).then(result => ({ ...result, messages: result.messages ?? [] })), 'Discord did not send the pins.', `pins:${channel.id}`)
  const [gone, setGone] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const unpin = (message: DiscordMessage) => {
    setError(null)
    setGone(set => new Set(set).add(message.id))
    const failed = (why?: string | null) => { setGone(set => { const next = new Set(set); next.delete(message.id); return next }); setError(why ?? 'Discord didn’t unpin that.') }
    void host.discordPins(channel.id, message.id, false).then(result => { if (!result.ok) failed(result.message) }, () => failed())
  }
  const shown = { ...found, error: error ?? found.error, messages: found.messages.filter(m => !gone.has(m.id)) }
  const canPin = !ctx.guildId || channel.canPin === true
  return (
    <Results title="Pinned messages" found={shown} ctx={ctx} here={channel.id} empty={channel.dm ? `This ${channel.dm.group ? 'group' : 'conversation'} doesn't have any pinned messages yet.` : "This channel doesn't have any pinned messages yet."} box={box} onClose={onClose}
      onRemove={canPin ? unpin : undefined} />
  )
}

// Discord's thread browser: the channel's open threads, or its archived ones.
function Threads({ channelId, onOpen, onClose }: { channelId: string; onOpen: (thread: DiscordChannel) => void; onClose: () => void }) {
  const [archived, setArchived] = useState(false)
  const [threads, setThreads] = useState<DiscordThread[] | null>(null)
  const box = useOutside(onClose)
  useEffect(() => {
    let live = true
    setThreads(null)
    host.discordThreads(channelId, { archived }).then(result => { if (live) setThreads(result.threads ?? []) }).catch(() => { if (live) setThreads([]) })
    return () => { live = false }
  }, [channelId, archived])
  return (
    <div className="exo-dc-popover" ref={box} role="dialog" aria-label="Threads">
      <p className="exo-dc-popover-title">Threads</p>
      <div className="exo-dc-popover-tabs" role="group" aria-label="Which threads">
        <button type="button" className="exo-dc-tab" aria-pressed={!archived} onClick={() => setArchived(false)}>Active</button>
        <button type="button" className="exo-dc-tab" aria-pressed={archived} onClick={() => setArchived(true)}>Archived</button>
      </div>
      <div className="exo-dc-popover-list">
        {threads === null ? <Wait label="Loading threads">{times(4, i => <span className="exo-dc-post"><strong>{sample(i)}</strong><span>00</span></span>)}</Wait> : null}
        {threads?.length === 0 ? <p className="exo-dc-note">{archived ? 'There are no archived threads.' : 'There are no active threads.'}</p> : null}
        {threads?.map(thread => (
          <button key={thread.id} type="button" className="exo-dc-post"
            onContextMenu={event => { event.preventDefault(); openThreadMenu(thread.id, event.clientX, event.clientY) }}
            onClick={() => onOpen({ id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: thread.thread_metadata?.locked !== true })}>
            <strong>{thread.name}</strong>
            <span><Icons.Chat size={14} /> {thread.message_count ?? 0}</span>
          </button>
        ))}
      </div>
    </div>
  )
}

export const MUTE_FOR: [number, string][] = [[15, 'For 15 minutes'], [60, 'For 1 hour'], [180, 'For 3 hours'], [480, 'For 8 hours'], [1440, 'For 24 hours'], [0, 'Until I turn it back on']]
export const LEVELS: [0 | 1 | 2, string][] = [[0, 'All messages'], [1, 'Only @mentions'], [2, 'Nothing']]

/**
 * The head's one More menu (three dots), after search: a server's threads, pinned messages and notification settings; a DM's
 * pinned messages and Add friends. Notification settings open their own menu from the same button.
 */
function HeadMore({ channel, guildId, threads, panel, toggle }: { channel: DiscordChannel; guildId: string | null; threads: boolean; panel: string | null; toggle: (next: 'pins' | 'threads') => void }) {
  const [view, setView] = useState<'more' | 'notify' | null>(null)
  const [adding, setAdding] = useState(false)
  const button = useRef<HTMLButtonElement>(null)
  const close = (focus = false) => { setView(null); if (focus) button.current?.focus() }
  const muted = channel.muted === true
  const addFriend = (friend: DiscordFriend) => {
    setAdding(false)
    void host.discordChannel(channel.id, 'add', undefined, friend.id).then(result => {
      if (result.ok && result.channelId && result.channelId !== channel.id) window.dispatchEvent(new CustomEvent('exo-dc-open-dm', { detail: result.channelId }))
    }).catch(() => { })
  }
  const pick = (run: () => void) => () => { setView(null); run() }
  return (
    <span className="exo-dc-menu-anchor">
      <button ref={button} type="button" className="exo-dc-icon-btn" aria-label="More" aria-haspopup="menu" aria-expanded={view !== null}
        onClick={() => setView(v => v ? null : 'more')}><Icons.More size={20} /></button>
      {view === 'more' ? (
        <MoreMenu onClose={close}>
          {guildId && threads ? (
            <button type="button" role="menuitem" onClick={pick(() => toggle('threads'))}>
              <Icons.Threads size={18} /><span className="exo-dc-menu-text">Threads</span>{panel === 'threads' ? <Icons.Check size={16} /> : null}
            </button>
          ) : null}
          <button type="button" role="menuitem" onClick={pick(() => toggle('pins'))}>
            <Icons.Pin size={18} /><span className="exo-dc-menu-text">Pinned messages</span>{panel === 'pins' ? <Icons.Check size={16} /> : null}
          </button>
          {guildId ? (
            <button type="button" role="menuitem" onClick={() => setView('notify')}>
              <Icons.Bell size={18} /><span className="exo-dc-menu-text">Notification settings{muted ? <small>Muted</small> : null}</span>
            </button>
          ) : null}
          {channel.dm ? (
            <>
              {muted ? (
                <NotifyRow channel={channel} onDone={() => setView(null)} next={{ muted: false }}>
                  <Icons.Bell size={18} /><span className="exo-dc-menu-text">Unmute conversation</span>
                </NotifyRow>
              ) : (
                <button type="button" role="menuitem" onClick={() => setView('notify')}>
                  <Icons.BellOff size={18} /><span className="exo-dc-menu-text">Mute conversation</span>
                </button>
              )}
              <button type="button" role="menuitem" onClick={pick(() => setAdding(true))}>
                <Icons.AddFriend size={18} /><span className="exo-dc-menu-text">Add friends</span>
              </button>
            </>
          ) : null}
        </MoreMenu>
      ) : null}
      {view === 'notify' && (guildId || channel.dm) ? <MoreMenu onClose={close}><NotifyMenu channel={channel} dm={!guildId} onDone={() => setView(null)} /></MoreMenu> : null}
      {adding ? <FriendPicker title="Select friends" onClose={() => setAdding(false)} onPick={addFriend} /> : null}
    </span>
  )
}

// The menu's keys, as the shared menu has them: the first row takes focus; the arrows, Home and End move; Escape closes it and
// gives focus back to the button.
function MoreMenu({ onClose, children }: { onClose: (focus?: boolean) => void; children: React.ReactNode }) {
  const box = useOutside(onClose)
  const rows = () => Array.from(box.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]') ?? [])
  useEffect(() => { rows()[0]?.focus() }, []) // eslint-disable-line react-hooks/exhaustive-deps -- once, as it opens
  const keys = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const list = rows()
    const at = list.indexOf(document.activeElement as HTMLElement)
    const to = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: list.length - 1 }[event.key]
    if (to !== undefined) { event.preventDefault(); list[(to + list.length) % list.length]?.focus() }
    else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(true) }
    else if (event.key === 'Tab') onClose()
  }
  return <div className="exo-dc-menu exo-dc-bell-menu" ref={box} role="menu" aria-label="More" onKeyDown={keys}>{children}</div>
}

// This channel's notifications: mute for a while or until turned back on (or unmute), and what it notifies you of.
function NotifyMenu({ channel, dm = false, onDone }: { channel: DiscordChannel; dm?: boolean; onDone: () => void }) {
  const muted = channel.muted === true
  const level = channel.notify ?? null
  return (
    <>
      {muted ? (
        <NotifyRow channel={channel} onDone={onDone} next={{ muted: false }}>
          <span className="exo-dc-menu-text">{dm ? 'Unmute conversation' : 'Unmute channel'}{channel.mutedUntil ? <small>Muted until {new Date(channel.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
        </NotifyRow>
      ) : MUTE_FOR.map(([minutes, label]) => (
        <NotifyRow channel={channel} onDone={onDone} key={minutes} next={minutes ? { muted: true, minutes } : { muted: true }}>
          <span className="exo-dc-menu-text">Mute {label.replace('For', 'for').replace('Until', 'until')}</span>
        </NotifyRow>
      ))}
      {dm ? null : (
        <>
          <span className="exo-dc-menu-line" role="separator" />
          <NotifyRow channel={channel} onDone={onDone} role="menuitemradio" aria-checked={level === null} next={{ level: 3 }}>
            <span className="exo-dc-menu-text">Use default<small>{LEVELS.find(([value]) => value === (channel.notifyDefault ?? 0))?.[1]}</small></span>
            {level === null ? <Icons.Check size={16} /> : null}
          </NotifyRow>
          {LEVELS.map(([value, label]) => (
            <NotifyRow channel={channel} onDone={onDone} key={value} role="menuitemradio" aria-checked={level === value} next={{ level: value }}>
              <span className="exo-dc-menu-text">{label}</span>
              {level === value ? <Icons.Check size={16} /> : null}
            </NotifyRow>
          ))}
        </>
      )}
    </>
  )
}

/**
 * A row of the channel's notifications that says how it went on itself (ActionButton): the ring, then the check, and
 * the menu closes; a refusal stays on the row with why.
 */
function NotifyRow({ channel, next, onDone, children, role = 'menuitem', ...rest }: {
  channel: DiscordChannel
  next: Parameters<typeof host.discordChannelNotify>[1]
  onDone: () => void
  children: ReactNode
  role?: string
  'aria-checked'?: boolean
}) {
  return (
    <ActionButton {...rest} role={role} context="Discord.channelNotify" fallback="Discord didn’t change that. Try again."
      onDone={onDone} onPress={() => host.discordChannelNotify(channel.id, next)}>{children}</ActionButton>
  )
}

function HeadIcon({ channel, guildId }: { channel: DiscordChannel; guildId: string | null }) {
  if (guildId) return createElement(channelGlyph(channel), { size: 22 })
  if (channel.dm) return <Avatar src={channel.dm.avatar} name={channel.name} status={channel.dm.group ? null : channel.dm.status} size={24} />
  return <Icons.Chat size={22} />
}

import { memo, useCallback, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import type { DiscordDm, DiscordFriend, DiscordHome } from '../../lib/discord'
import { Icons } from './icons'
import { Avatar } from './presence'
import { ConfirmButton } from '../ui/ConfirmButton'
import { EditGroup } from './GroupDm'
import { SubContext } from './UserMenu'
import { dmData, DmCopyId, DmMuteItem, DmPinItem, DmReadItem } from './dmMenu'
import { onPinnedDms, partitionPinnedDms, readPinnedDms } from '../../lib/discordPins'
import { useDismiss } from '../../lib/dismiss'
import { ErrorNote } from '../ui/ErrorNote'
import { peek, REQUESTS, unpeek } from './roomPlaces'
import { rowTime } from '../../lib/discordNow'
import { mentionsSaid } from './Band'
import { errorText } from '../../lib/errorWords'
import { newestMessages } from './messageCache'

const hydratedDms = new Set<string>()

/** Home's rows at the top of the room's one list (Rail.tsx): Now, Friends, Message requests while any wait, then the Conversations
    label (find and New conversation) and the newest eight conversations, each with what was said last (the rest under All
    conversations). The Shop is in
    your menu at the list's foot. */
export function MessagesPane({ home, open, onPick }: {
  home: DiscordHome | null
  open: string | null
  onPick: (id: string) => void
}) {
  const [query, setQuery] = useState('')
  const [starting, setStarting] = useState(false)
  const [finding, setFinding] = useState(false)
  const [pinnedIds, setPinnedIds] = useState<string[]>(() => readPinnedDms())
  useEffect(() => onPinnedDms(() => setPinnedIds(readPinnedDms())), [])
  // Closing the find field gives focus back to its glyph, not to the page.
  const findButton = useRef<HTMLButtonElement>(null)
  const refocus = useRef(false)
  useEffect(() => {
    if (finding || !refocus.current) return
    refocus.current = false
    findButton.current?.focus()
  }, [finding])
  const q = query.trim().toLowerCase()
  // Message requests and spam wait under Message Requests, as in Discord, not among your DMs.
  const dms = (home?.dms ?? []).filter(d => !d.request && !d.spam && (!q || d.name.toLowerCase().includes(q)))
  const { pinned, unpinned } = partitionPinnedDms(dms, pinnedIds)
  const requests = (home?.dms ?? []).filter(d => d.request && !d.spam).length
  const anyRequests = requests > 0 || (home?.dms ?? []).some(d => d.spam)
  const unread = (home?.dms ?? []).filter(d => d.unread && !d.muted && !d.request && !d.spam).length
  // Every row keeps its props while the list changes around it: 800 of them redraw only the one that changed.
  const picking = useRef(onPick)
  picking.current = onPick
  const pick = useCallback((id: string) => picking.current(id), [])
  const startedHydration = useRef(false)
  useEffect(() => {
    if (!home || startedHydration.current) return
    const missing = [...pinned, ...unpinned].filter(d => !d.line && !hydratedDms.has(d.id)).slice(0, 12)
    if (missing.length === 0) return
    startedHydration.current = true
    let cancelled = false
    void (async () => {
      for (const dm of missing) {
        if (cancelled) return
        hydratedDms.add(dm.id)
        await newestMessages(dm.id, true).catch(() => { })
      }
    })()
    return () => { cancelled = true }
  }, [home, pinned, unpinned])
  return (
    <nav className="exo-dc-side exo-dc-dms" aria-label="Direct messages" onPointerOver={peek} onPointerLeave={unpeek}>
      <header className="exo-dc-pane-head">
        {/* Finding is a glyph on the head that becomes the field, never a bar across the list. */}
        {finding || query
          ? <input className="exo-dc-find" autoFocus placeholder="Find a conversation" value={query} onChange={event => setQuery(event.target.value)} aria-label="Find a conversation"
              onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); refocus.current = true; setQuery(''); setFinding(false) } }}
              onBlur={() => { if (!query) setFinding(false) }} />
          : <>
            <h2 className="exo-dc-pane-title">Direct messages</h2>
            {unread ? <span className="exo-dc-pane-note">{unread} unread</span> : null}
            <button ref={findButton} type="button" className="exo-dc-icon-btn" aria-label="Find a conversation" onClick={() => setFinding(true)}><Icons.Search size={16} /></button>
            <button type="button" className="exo-dc-icon-btn" aria-label="New conversation" aria-expanded={starting} onClick={() => setStarting(value => !value)}><Icons.Plus size={16} /></button>
          </>}
        {starting ? <StartDm onClose={() => setStarting(false)} onOpen={id => { setStarting(false); onPick(id) }} /> : null}
      </header>
      <div className="exo-dc-side-list">
        {anyRequests || open === REQUESTS ? <RequestsRow requests={requests} current={open === REQUESTS} onPick={() => onPick(REQUESTS)} /> : null}
        <DmSections dms={dms} pinned={pinned} unpinned={unpinned} open={open} home={home} query={q} onPick={pick} />
      </div>
    </nav>
  )
}

function DmSections({ dms, pinned, unpinned, open, home, query, onPick }: {
  dms: readonly DiscordDm[]
  pinned: readonly DiscordDm[]
  unpinned: readonly DiscordDm[]
  open: string | null
  home: DiscordHome | null
  query: string
  onPick: (id: string) => void
}) {
  if (!home) return <p className="exo-dc-note">Loading</p>
  if (dms.length === 0) return <p className="exo-dc-note">{query ? 'No conversation by that name.' : 'No conversations yet.'}</p>
  return (
    <>
      {pinned.length > 0 ? <p className="exo-dc-dm-pin-label">Pinned</p> : null}
      {pinned.map(d => <DmRow key={d.id} dm={d} current={d.id === open} onPick={onPick} />)}
      {pinned.length > 0 && unpinned.length > 0 ? <p className="exo-dc-dm-pin-label">Direct messages</p> : null}
      {unpinned.map(d => <DmRow key={d.id} dm={d} current={d.id === open} onPick={onPick} />)}
    </>
  )
}

/** Message requests, at the list's top while any wait (or while it is open). */
function RequestsRow({ requests, current, onPick }: { requests: number; current: boolean; onPick: () => void }) {
  return (
    <button type="button" className="exo-dc-item is-big" aria-current={current} onClick={onPick}
      aria-label={requests ? `Message requests, ${requests} waiting` : undefined}>
      <span className="exo-dc-item-glyph"><Icons.Requests size={18} /></span>
      <span className="exo-dc-item-name">Message requests</span>
      {requests ? <span className="exo-dc-badge" aria-hidden="true">{requests}</span> : null}
    </button>
  )
}

const DmRow = memo(function DmRow({ dm, current, onPick }: { dm: DiscordDm; current: boolean; onPick: (id: string) => void }) {
  // A group's right-click menu, with Leave group (a 1:1 DM's is the person menu, with Close DM).
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const close = () => void host.discordChannel(dm.id, 'close').catch(() => { })
  const said = dm.line || dm.subtitle
  return (
    <div className="exo-dc-dm-row">
      <button type="button" className={`exo-dc-item is-dm${dm.unread ? ' is-unread' : ''}${dm.muted ? ' is-muted' : ''}`} aria-current={current} onClick={() => onPick(dm.id)} data-dc-channel={dm.id}
        aria-label={[dm.name, said, dm.mentions ? mentionsSaid(dm.mentions) : null].filter(Boolean).join(', ')}
        data-dc-user={dm.group ? undefined : dm.userId ?? undefined} data-dc-name={dm.name} data-dc-username={dm.username ?? undefined} {...dmData(dm)}
        onContextMenu={dm.group ? event => { event.preventDefault(); setMenu({ x: event.clientX, y: event.clientY }) } : undefined}>
        <Avatar src={dm.avatar} name={dm.name} status={dm.group ? null : dm.status ?? null} size={36} look={dm.look} scope=".exo-dc-item" />
        <span className="exo-dc-item-names">
          {/* The name is plain here; its colour and nameplate are the person's profile's. */}
          <span className="exo-dc-item-name">{dm.name}</span>
          {/* What was said last, else what they are doing while nothing has been. */}
          {said ? <span className="exo-dc-item-sub">{said}</span> : null}
        </span>
        <span className="exo-dc-item-meta">
          {dm.lastAt ? <time className="exo-dc-item-time">{rowTime(dm.lastAt)}</time> : null}
          {dm.mentions ? <span className="exo-dc-badge" aria-hidden="true">{dm.mentions > 99 ? '99+' : dm.mentions}</span>
            : dm.unread && !dm.muted ? <span className="exo-dc-item-dot" aria-hidden="true" /> : null}
        </span>
      </button>
      {menu ? <GroupDmMenu dm={dm} {...menu} onLeave={() => { setMenu(null); close() }} onClose={() => setMenu(null)} /> : null}
    </div>
  )
})

// Discord's group DM menu: Mark As Read, Mute Conversation (or Unmute), Leave Group, and Copy Channel ID.
function GroupDmMenu({ dm, x, y, onLeave, onClose }: { dm: DiscordDm; x: number; y: number; onLeave: () => void; onClose: () => void }) {
  const [sub, setSub] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [editing, setEditing] = useState(false)
  const info = { id: dm.id, unread: dm.unread || dm.mentions > 0, muted: !!dm.muted, mutedUntil: dm.mutedUntil ?? null }
  const done = (result: { ok: boolean; message?: string | null }) => { if (result.ok) onClose(); else setNote(errorText(result.message, 'Discord didn’t take that.')) }
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !editing })
  useEffect(() => {
    if (editing) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose, editing])
  if (editing) return <EditGroup dm={dm} onClose={onClose} />
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div ref={box} className="exo-dc-menu exo-dc-channel-menu exo-dc-gdm-menu" role="menu" aria-label={dm.name} style={{ left: x, top: y }}>
      <DmReadItem dm={info} onDone={done} />
      <DmPinItem dm={info} onDone={done} />
      <span className="exo-dc-menu-line" role="separator" />
      <DmMuteItem dm={info} label="Mute conversation" onDone={done} />
      <button type="button" role="menuitem" onClick={() => setEditing(true)}>Edit group</button>
      <span className="exo-dc-menu-line" role="separator" />
      <ConfirmButton role="menuitem" className="is-danger" question="Leave group?" danger onConfirm={onLeave}>Leave group</ConfirmButton>
      <DmCopyId dm={info} onClose={onClose} />
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

/** Discord's Select Friends for a new DM: one friend opens your DM with them; two to nine make a group (10 with you). */
function StartDm({ onOpen, onClose }: { onOpen: (channelId: string) => void; onClose: () => void }) {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [query, setQuery] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  useEffect(() => { host.discordFriends().then(result => setFriends(result.friends.filter(f => f.kind === 'friend'))).catch(() => { }) }, [])
  const left = 9 - picked.length
  const toggle = (id: string) => setPicked(list => list.includes(id) ? list.filter(x => x !== id) : list.length < 9 ? [...list, id] : list)
  async function create() {
    const one = picked.length === 1 ? friends.find(f => f.id === picked[0]) : null
    if (one?.dmId) { onOpen(one.dmId); return }
    setBusy(true)
    setError(null)
    const result = await host.discordFriend('dm', one ? { userId: one.id } : { userIds: picked }).catch(() => null)
    setBusy(false)
    if (result?.channelId) onOpen(result.channelId); else setError(errorText(result?.message, 'Discord didn’t make that DM.'))
  }
  const q = query.trim().toLowerCase()
  return (
    <div ref={box} className="exo-dc-menu is-start" role="dialog" aria-label="Select friends">
      {left === 0 ? <span className="exo-dc-note">This group has a 10 member limit.</span> : null}
      <input className="exo-dc-find" placeholder="Find a friend" value={query} autoFocus
        onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') onClose() }} aria-label="Find a friend" />
      <div className="exo-dc-start-list">
        {friends.filter(f => !q || f.name.toLowerCase().includes(q) || (f.username ?? '').toLowerCase().includes(q)).slice(0, 40).map(friend => (
          <button key={friend.id} type="button" aria-pressed={picked.includes(friend.id)} disabled={!picked.includes(friend.id) && left === 0} onClick={() => toggle(friend.id)}>
            <Avatar src={friend.avatar} name={friend.name} status={friend.status} size={24} />
            <span>{friend.name}</span>
            {picked.includes(friend.id) ? <Icons.Check size={16} className="exo-dc-ctx-check" /> : null}
          </button>
        ))}
      </div>
      {error ? <ErrorNote className="is-bad" message={error} /> : null}
      <button type="button" className="exo-set-btn is-primary" disabled={!picked.length || busy} onClick={() => void create()}>Create DM</button>
    </div>
  )
}

/** Discord's friend picker: type a name, pick a friend. */
export function FriendPicker({ title, onPick, onClose }: { title: string; onPick: (friend: DiscordFriend) => void; onClose: () => void }) {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [query, setQuery] = useState('')
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  useEffect(() => { host.discordFriends().then(result => setFriends(result.friends.filter(f => f.kind === 'friend'))).catch(() => { }) }, [])
  const q = query.trim().toLowerCase()
  return (
    <div ref={box} className="exo-dc-menu is-start" role="dialog" aria-label={title}>
      <input className="exo-dc-find" placeholder="Find a friend" value={query} autoFocus
        onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') onClose() }} aria-label="Find a friend" />
      <div className="exo-dc-start-list">
        {friends.filter(f => !q || f.name.toLowerCase().includes(q) || (f.username ?? '').toLowerCase().includes(q)).slice(0, 40).map(friend => (
          <button key={friend.id} type="button" onClick={() => onPick(friend)}>
            <Avatar src={friend.avatar} name={friend.name} status={friend.status} size={24} />
            <span>{friend.name}</span>
          </button>
        ))}
      </div>
    </div>
  )
}

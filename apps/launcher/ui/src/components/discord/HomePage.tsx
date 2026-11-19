import { useCallback, useDeferredValue, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { useGlide } from '../../lib/glide'
import type { DiscordFriend } from '../../lib/discord'
import { useFriends } from '../../lib/friendsPlaying'
import { Icons } from './icons'
import { Avatar } from './presence'
import { Nameplate, StyledName } from './collectibles'
import { showFullProfile } from './profileParts'
import { useStrangerAsk } from './admin'
import { useDismiss } from '../../lib/dismiss'
import { GuildTag } from './guildTag'
import { ErrorNote } from '../ui/ErrorNote'
import { ActivityLine } from './ActivityLine'
import { openUserMenu } from './UserMenu'
import { errorText } from '../../lib/errorWords'

type Tab = 'online' | 'all' | 'pending'
/** Resolves true when Discord wants an accept confirmed (someone you share no server with). */
type Act = (action: 'accept' | 'remove' | 'dm', friend: DiscordFriend, confirm?: boolean) => Promise<boolean>
type FriendList = ReturnType<typeof useFriends>

function useFriendAct(onOpenDm: (channelId: string) => void, reload: () => void) {
  const [error, setError] = useState<string | null>(null)
  const opening = useRef(onOpenDm)
  opening.current = onOpenDm
  const act = useCallback<Act>(async (action, friend, confirm = false) => {
    setError(null)
    if (action === 'dm' && friend.dmId) { opening.current(friend.dmId); return false }
    try {
      const result = await host.discordFriend(action, { userId: friend.id, confirm })
      if (result.stranger) return true
      if (!result.ok) setError(errorText(result.message, 'Discord didn’t take that.'))
      else if (action !== 'dm') reload()
      else if (result.channelId) opening.current(result.channelId)
    } catch (err) {
      report(err, 'HomePage.' + action)
      setError('Discord didn’t take that.')
    }
    return false
  }, [reload])
  return { act, error }
}

/** Home: Friends (Online, All and Pending) beside your conversations, which are the left panel. */
export function HomePage({ onOpenDm }: { onOpenDm: (channelId: string) => void }) {
  const list = useFriends()
  return (
    <div className="exo-dc-home" aria-label="Home">
      <FriendsPage list={list} onOpenDm={onOpenDm} />
    </div>
  )
}

/**
 * Friends: the title, Online, All and Pending, then one small search and Add friend at the head's end; the grids below.
 * bodyOnly is the same page without its head, for a page that holds it under its own sections: the tabs, search and
 * Add friend sit on a row of their own above the grids.
 */
export function FriendsPage({ list, onOpenDm }: { list: FriendList; onOpenDm: (channelId: string) => void }) {
  const [tab, setTab] = useState<Tab>('online')
  const { friends, loaded, reload } = list
  const { act, error } = useFriendAct(onOpenDm, reload)
  const [query, setQuery] = useState('')
  const tabs = useGlide<HTMLSpanElement>()
  const pending = friends.filter(f => f.kind !== 'friend')
  // The field answers each key at once; a long list filters behind it.
  const q = useDeferredValue(query).trim().toLowerCase()
  const matches = (f: DiscordFriend) => !q || f.name.toLowerCase().includes(q) || (f.username ?? '').toLowerCase().includes(q)
  const listed = (tab === 'pending' ? pending : friends.filter(f => f.kind === 'friend' && (tab === 'all' || f.status !== 'offline'))).filter(matches)
  // On Online, whoever is doing something leads the grid; the other tabs are one group.
  const active = tab === 'online' ? listed.filter(f => f.activity) : []
  const rest = tab === 'online' ? listed.filter(f => !f.activity) : listed
  const incoming = friends.filter(f => f.kind === 'incoming').length
  const label = tab === 'pending' ? 'Pending' : tab === 'all' ? 'All friends' : 'Online'
  const tabButtons = (
    <span ref={tabs} className="exo-dc-tabs exo-glide" role="group" aria-label="Friends">
      <button type="button" className="exo-dc-tab" aria-pressed={tab === 'online'} onClick={() => setTab('online')}>Online</button>
      <button type="button" className="exo-dc-tab" aria-pressed={tab === 'all'} onClick={() => setTab('all')}>All</button>
      <button type="button" className="exo-dc-tab" aria-pressed={tab === 'pending'} onClick={() => setTab('pending')}>
        Pending{incoming ? <span className="exo-dc-badge">{incoming}</span> : null}
      </button>
    </span>
  )
  // The search is one small field at the row's end, never a bar across the page.
  const search = (
    <>
      <label className="exo-dc-friend-search">
        <Icons.Search size={16} />
        <input placeholder="Search friends" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search friends"
          onKeyDown={event => { if (event.key === 'Escape' && query) { event.stopPropagation(); setQuery('') } }} />
      </label>
      <AddFriend />
    </>
  )
  const body = (
    <>
      {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
      {active.length ? <>
        <p className="exo-dc-side-label" role="heading" aria-level={2}>Active now</p>
        <div className="exo-dc-friend-grid">{active.map(friend => <FriendRow key={friend.id} friend={friend} act={act} />)}</div>
      </> : null}
      {loaded && rest.length ? <>
        <p className="exo-dc-side-label" role="heading" aria-level={2}>{label}</p>
        <div className="exo-dc-friend-grid">{rest.map(friend => <FriendRow key={friend.id} friend={friend} act={act} />)}</div>
      </> : null}
      {loaded && listed.length === 0 ? <p className="exo-dc-note">{q ? 'No friend by that name.' : tab === 'pending' ? 'No pending requests.' : 'No friends here yet.'}</p> : null}
    </>
  )
  return (
    <section className="exo-dc-block exo-dc-friends" aria-label="Friends">
      <header className="exo-dc-block-head exo-dc-friends-head">
        <h2 className="exo-dc-block-title">Friends</h2>
        {tabButtons}
        <span className="exo-dc-friends-tools">{search}</span>
      </header>
      <div className="exo-dc-block-body exo-dc-friend-list">{body}</div>
    </section>
  )
}

// A friend or a request: their picture (the profile), names and line; Message for a friend, Accept and Ignore for an
// incoming request, Cancel for yours. Right-click is the person menu (Remove friend, block and the rest).
function FriendRow({ friend, act }: { friend: DiscordFriend; act: Act }) {
  return (
    <div className={`exo-dc-friend${friend.look?.nameplate ? ' has-nameplate' : ''}`} data-dc-user={friend.id} data-dc-name={friend.name} data-dc-username={friend.username ?? undefined}>
      <Nameplate look={friend.look} scope=".exo-dc-friend" />
      <button type="button" className="exo-dc-avatar-btn" aria-label={`${friend.name}, profile`} onClick={() => showFullProfile(friend.id)}>
        <Avatar src={friend.avatar} name={friend.name} status={friend.kind === 'friend' ? friend.status : null} size={40} look={friend.look} scope=".exo-dc-friend" />
      </button>
      <span className="exo-dc-friend-names">
        <strong><span className="exo-dc-friend-name"><StyledName name={friend.name} look={friend.look} scope=".exo-dc-friend" /></span><GuildTag look={friend.look} /></strong>
        {friend.kind === 'friend' ? <span><ActivityLine friend={friend} /></span> : friend.kind === 'outgoing' ? <span>Sent</span> : null}
      </span>
      <span className="exo-dc-friend-actions">
      {friend.kind === 'friend' ? (
        <>
          <button type="button" className="exo-dc-round-btn" aria-label={`Message ${friend.name}`} onClick={() => void act('dm', friend)}><Icons.Chat size={18} /></button>
          <button type="button" className="exo-dc-round-btn" aria-label={`More options for ${friend.name}`}
            onClick={event => { const b = event.currentTarget.getBoundingClientRect(); openUserMenu(friend.id, b.left, b.bottom + 4, friend.name, friend.username ?? null) }}><Icons.More size={18} /></button>
        </>
      ) : null}
      {friend.kind === 'incoming' ? (
        <AcceptButton name={friend.name} act={confirm => act('accept', friend, confirm)} />
      ) : null}
      {friend.kind !== 'friend' ? (
        <button type="button" className="exo-dc-round-btn is-bad" aria-label={friend.kind === 'incoming' ? `Ignore ${friend.name}` : `Cancel request to ${friend.name}`}
          onClick={() => void act('remove', friend)}><Icons.Decline size={18} /></button>
      ) : null}
      </span>
    </div>
  )
}

// An incoming request's Accept: when Discord wants it confirmed, it asks in place and a second press accepts anyway.
function AcceptButton({ name, act }: { name: string; act: (confirm: boolean) => Promise<boolean> }) {
  const [busy, setBusy] = useState(false)
  const stranger = useStrangerAsk(() => void act(true), busy)
  return (
    <button type="button" ref={stranger.ref} className={`exo-dc-round-btn is-good exo-confirm-icon ${stranger.className}`} aria-label={`Accept ${name}`} disabled={busy}
      onClick={() => {
        if (stranger.armed) { stranger.press(); return }
        setBusy(true)
        void act(false).then(refused => { if (refused) stranger.refused() }).finally(() => setBusy(false))
      }}>
      {stranger.label(<Icons.Check size={18} />)}
    </button>
  )
}

// Add friend: a field and Send, in a popover on the title line.
function AddFriend() {
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, () => setOpen(false), { on: open })
  const [name, setName] = useState('')
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  async function send() {
    if (!name.trim() || busy) return
    setBusy(true)
    try {
      const reply = await host.discordFriend('add', { username: name.trim() })
      setResult(reply.ok ? { ok: true, text: `Friend request sent to ${name.trim()}.` } : { ok: false, text: errorText(reply.message, 'Discord didn’t take that.') })
      if (reply.ok) setName('')
    } catch (err) {
      report(err, 'HomePage.add')
      setResult({ ok: false, text: 'Discord did not take that.' })
    } finally { setBusy(false) }
  }
  return (
    <span className="exo-dc-menu-anchor">
      <button type="button" className="exo-set-btn is-compact" aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(o => !o)}>Add friend</button>
      {open ? (
        <div ref={box} className="exo-dc-menu exo-dc-add-friend" role="dialog" aria-label="Add friend"
          onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); setOpen(false) } }}>
          <div className="exo-dc-add-row">
            <input className="exo-dc-modal-input" value={name} onChange={event => { setName(event.target.value); setResult(null) }} placeholder="Username" aria-label="Username" autoFocus
              onKeyDown={event => { if (event.key === 'Enter') void send() }} />
            <button type="button" className="exo-set-btn is-primary" aria-label="Send friend request" disabled={!name.trim() || busy} onClick={() => void send()}>Send</button>
          </div>
          {result ? <p className={result.ok ? 'is-good' : 'is-bad'} role={result.ok ? 'status' : 'alert'}>{result.text}</p> : null}
        </div>
      ) : null}
    </span>
  )
}

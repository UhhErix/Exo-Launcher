import { sampleFace } from './waits'
import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { type DiscordMessage } from '../../lib/discord'
import { Avatar } from './presence'
import { Icons } from './icons'
import { Popover } from '../ui/Dialog'
import { Wait, sample, times } from '../ui/Wait'
import { ErrorNote } from '../ui/ErrorNote'

type Target = { id: string; name: string; place: string | null; avatar: string | null; dm: boolean }

// Your DMs (most recent first) and every server's channels you can write in, from what the client already holds.
function useTargets() {
  const [targets, setTargets] = useState<Target[] | null>(null)
  useEffect(() => {
    let live = true
    void (async () => {
      const home = await host.discordHome()
      const dms: Target[] = home.dms.map(d => ({ id: d.id, name: d.name, place: null, avatar: d.avatar, dm: true }))
      const lists = await Promise.all(home.guilds.map(g => host.discordChannels(g.id).then(list => ({ g, list })).catch(() => null)))
      const channels: Target[] = lists.flatMap(entry => entry ? entry.list.channels
        .filter(c => c.kind === 'text' && c.canSend !== false)
        .map(c => ({ id: c.id, name: c.name, place: entry.g.name, avatar: entry.g.icon, dm: false })) : [])
      if (live) setTargets([...dms, ...channels])
    })().catch(() => { if (live) setTargets([]) })
    return () => { live = false }
  }, [])
  return targets
}

/** Discord's Forward: pick up to five conversations, add an optional message, Send. */
export function ForwardDialog({ message, onClose }: { message: DiscordMessage; onClose: () => void }) {
  // One forward per conversation, in turn; those reached are said, so a retry skips them.
  const send = async (ids: string[], note: string) => {
    const sent: string[] = []
    for (const id of ids) {
      const result = await host.discordSend(id, note, undefined, undefined, { forward: { channelId: message.channel_id, messageId: message.id } })
        .catch(() => ({ ok: false, message: null }))
      if (!result.ok) return { ok: false, message: result.message, sent }
      sent.push(id)
    }
    return { ok: true, sent }
  }
  return <SendToDialog title="Forward to" failed="Discord did not forward that." send={send} onClose={onClose} />
}

/**
 * Pick up to five of your DMs and channels, add an optional message, Send: Forward, and Captures' Send to Discord. A
 * conversation reached leaves the pick, so a retry after a later one failed never sends to it twice.
 * `portal={false}` keeps it inside the room (or the `.exo-dc` scope) whose colors its words use.
 */
export function SendToDialog({ title, failed, send: sendTo, onClose, onSent }: {
  title: string
  failed: string
  /** Sends to the picked conversations; `sent`: those it reached, even when one failed. */
  send: (channelIds: string[], note: string) => Promise<{ ok: boolean; message?: string | null; sent?: string[] }>
  onClose: () => void
  /** The names it went to, once every pick was reached. */
  onSent?: (names: string[]) => void
}) {
  const targets = useTargets()
  const [query, setQuery] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const q = query.trim().toLowerCase()
  const shown = (targets ?? []).filter(t => !q || t.name.toLowerCase().includes(q) || t.place?.toLowerCase().includes(q)).slice(0, 60)
  const names = (list: string[]) => list.map(id => targets?.find(t => t.id === id)?.name ?? '').filter(Boolean)
  const send = async () => {
    if (busy || picked.length === 0) return
    setBusy(true); setError(null)
    const all = picked
    const result = await sendTo(picked, note.trim()).catch(() => ({ ok: false, message: null, sent: [] as string[] }))
    setPicked(list => list.filter(id => !result.sent?.includes(id)))
    if (!result.ok) { setBusy(false); setError(result.message ?? failed); return }
    onSent?.(names(all))
    onClose()
  }
  return (
    <Popover portal={false} className="exo-dc-modal exo-dc-forward-dialog" label={title} onClick={event => event.stopPropagation()} onClose={onClose}>
      <h2>{title}</h2>
      <input className="exo-dc-modal-input" placeholder="Search" value={query} autoFocus aria-label="Search conversations" onChange={event => setQuery(event.target.value)} />
      <div className="exo-dc-forward-list" role="listbox" aria-multiselectable="true" aria-label="Conversations">
        {targets === null ? <Wait label="Loading conversations">{times(5, i => <span className="exo-dc-forward-row">{sampleFace(24)}<span className="exo-dc-forward-name"><strong>{sample(i)}</strong><span>{sample(i + 2)}</span></span></span>)}</Wait> : null}
        {targets?.length === 0 ? <p className="exo-dc-note">Nowhere to send to. Sign in to Discord in Exo first.</p> : null}
        {shown.map(t => {
          const on = picked.includes(t.id)
          return (
            <button key={t.id} type="button" role="option" aria-selected={on} className="exo-dc-forward-row"
              onClick={() => setPicked(list => on ? list.filter(x => x !== t.id) : list.length < 5 ? [...list, t.id] : list)}>
              {t.dm ? <Avatar src={t.avatar} name={t.name} status={null} size={24} /> : <span className="exo-dc-forward-hash"><Icons.Hash size={16} /></span>}
              <span className="exo-dc-forward-name"><strong>{t.name}</strong>{t.place ? <span>{t.place}</span> : null}</span>
              <span className={`exo-dc-forward-check${on ? ' is-on' : ''}`} aria-hidden="true">{on ? <Icons.Check size={14} /> : null}</span>
            </button>
          )
        })}
      </div>
      <input className="exo-dc-modal-input" placeholder="Add an optional message" value={note} maxLength={4000} aria-label="Optional message"
        onChange={event => setNote(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void send() }} />
      {error ? <ErrorNote className="is-bad" message={error} /> : null}
      <footer className="exo-dc-delete-foot">
        <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy || picked.length === 0} onClick={() => void send()}>
          {picked.length > 1 ? `Send to ${picked.length}` : 'Send'}
        </button>
      </footer>
    </Popover>
  )
}

/** Create Thread from a message: a name (the message's start by default), then the thread opens. */
export function NewThreadDialog({ message, onClose, onCreated }: { message: DiscordMessage; onClose: () => void; onCreated: (thread: { id: string; name: string }) => void }) {
  const [name, setName] = useState(() => message.content.replace(/\s+/g, ' ').trim().slice(0, 40) || 'New thread')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const create = async () => {
    if (busy || !name.trim()) return
    setBusy(true); setError(null)
    const result = await host.discordThread(message.channel_id, { name: name.trim(), messageId: message.id }).catch(() => ({ ok: false, message: null, thread: null }))
    setBusy(false)
    if (result.ok && result.thread) onCreated(result.thread)
    else setError(result.message ?? 'Discord did not start the thread.')
  }
  return (
    <Popover portal={false} className="exo-dc-modal" label="Create thread" onClick={event => event.stopPropagation()} onClose={onClose}>
      <h2>Create thread</h2>
      <input className="exo-dc-modal-input" value={name} maxLength={100} autoFocus aria-label="Thread name"
        onChange={event => setName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void create() }} />
      {error ? <ErrorNote className="is-bad" message={error} /> : null}
      <footer className="exo-dc-delete-foot">
        <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy || !name.trim()} onClick={() => void create()}>Create</button>
      </footer>
    </Popover>
  )
}

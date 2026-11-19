import { sampleFace } from './waits'
import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { type DiscordMessage } from '../../lib/discord'
import { Avatar } from './presence'
import { Icons } from './icons'
import { Popover } from '../ui/Dialog'
import { Wait, sample, times } from '../ui/Wait'
import { ActionButton } from '../ui/ConfirmButton'

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
  const targets = useTargets()
  const [query, setQuery] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [note, setNote] = useState('')
  const q = query.trim().toLowerCase()
  const shown = (targets ?? []).filter(t => !q || t.name.toLowerCase().includes(q) || t.place?.toLowerCase().includes(q)).slice(0, 60)
  // Send says how it went in itself (ActionButton): the ring, the check, then the dialog closes; a refusal stays on it
  // with why, and Send again goes only to those still picked.
  const send = async () => {
    for (const id of picked) {
      const result = await host.discordSend(id, note.trim(), undefined, undefined, { forward: { channelId: message.channel_id, messageId: message.id } })
      if (!result.ok) return result
      // Sent: a retry after a later one failed goes only to those still picked, never twice to this one.
      setPicked(list => list.filter(other => other !== id))
    }
    return true
  }
  return (
    <Popover portal={false} className="exo-dc-modal exo-dc-forward-dialog" label="Forward to" onClick={event => event.stopPropagation()} onClose={onClose}>
      <h2>Forward to</h2>
      <input className="exo-dc-modal-input" placeholder="Search" value={query} autoFocus aria-label="Search conversations" onChange={event => setQuery(event.target.value)} />
      <div className="exo-dc-forward-list" role="listbox" aria-multiselectable="true" aria-label="Conversations">
        {targets === null ? <Wait label="Loading conversations">{times(5, i => <span className="exo-dc-forward-row">{sampleFace(24)}<span className="exo-dc-forward-name"><strong>{sample(i)}</strong><span>{sample(i + 2)}</span></span></span>)}</Wait> : null}
        {targets?.length === 0 ? <p className="exo-dc-note">Nowhere to forward to.</p> : null}
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
      {/* Enter in the message presses Send (the form's own submit), so it shows its ring and its answer too. */}
      <form className="exo-dc-modal-form" onSubmit={event => event.preventDefault()}>
        <input className="exo-dc-modal-input" placeholder="Add an optional message" value={note} maxLength={4000} aria-label="Optional message"
          onChange={event => setNote(event.target.value)} />
        <footer className="exo-dc-delete-foot">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <ActionButton type="submit" className="exo-set-btn is-primary" disabled={picked.length === 0} context="Discord.forward"
            fallback="Discord didn’t forward that. Try again." onDone={onClose} onPress={send}>
            {picked.length > 1 ? `Send to ${picked.length}` : 'Send'}
          </ActionButton>
        </footer>
      </form>
    </Popover>
  )
}

/** Create Thread from a message: a name (the message's start by default), then the thread opens. */
export function NewThreadDialog({ message, onClose, onCreated }: { message: DiscordMessage; onClose: () => void; onCreated: (thread: { id: string; name: string }) => void }) {
  const [name, setName] = useState(() => message.content.replace(/\s+/g, ' ').trim().slice(0, 40) || 'New thread')
  // Create says how it went in itself (ActionButton); the thread opens once its check has shown.
  const create = async () => {
    const result = await host.discordThread(message.channel_id, { name: name.trim(), messageId: message.id })
    return result.ok && result.thread ? result : { ok: false, message: result.message }
  }
  return (
    <Popover portal={false} className="exo-dc-modal" label="Create thread" onClick={event => event.stopPropagation()} onClose={onClose}>
      <h2>Create thread</h2>
      <form className="exo-dc-modal-form" onSubmit={event => event.preventDefault()}>
        <input className="exo-dc-modal-input" value={name} maxLength={100} autoFocus aria-label="Thread name"
          onChange={event => setName(event.target.value)} />
        <footer className="exo-dc-delete-foot">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <ActionButton type="submit" className="exo-set-btn is-primary" disabled={!name.trim()} context="Discord.thread" fallback="Discord didn’t start the thread. Try again."
            onDone={result => { const thread = (result as { thread?: { id: string; name: string } | null }).thread; if (thread) onCreated(thread) }} onPress={create}>Create</ActionButton>
        </footer>
      </form>
    </Popover>
  )
}

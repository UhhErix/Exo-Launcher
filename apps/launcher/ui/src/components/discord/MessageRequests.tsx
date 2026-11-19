import { useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import type { DiscordDm, DiscordHome, DiscordMessage } from '../../lib/discord'
import { observeNearViewport } from '../../lib/nearViewport'
import { Icons } from './icons'
import { Avatar } from './presence'
import type { PlainError } from '../../lib/errorWords'
import { ActionButton } from '../ui/ConfirmButton'

const FAILED = "Discord didn't update this message request. Try again in a moment."

/**
 * Accept or Ignore a request, as a control that says how it went in itself (ActionButton: its ring, its check, or Bad
 * and a shake); Discord's words when it doesn't take go on the request's own line (`error`).
 */
function useAnswer(channelId: string) {
  const [error, setError] = useState<string | null>(null)
  const answer = (accept: boolean) => ({
    context: 'Discord.messageRequest',
    fallback: FAILED,
    why: 'none' as const,
    onFailed: (failure: PlainError) => setError(failure.text),
    onPress: () => { setError(null); return host.discordChannel(channelId, accept ? 'accept' : 'ignore') },
  })
  return { error, answer }
}

/**
 * Discord's Message Requests: DMs from people you aren't friends with (Requests), and the ones Discord filed as Spam,
 * each with its latest message, Accept and Ignore, in rows as Friends has them. Accepted, it joins your DMs; ignored,
 * it goes away. A request opens to read, its reply waiting for Accept.
 */
export function MessageRequestsPage({ home, onOpenDm }: { home: DiscordHome | null; onOpenDm: (id: string) => void }) {
  const [tab, setTab] = useState<'requests' | 'spam'>('requests')
  const requests = (home?.dms ?? []).filter(d => d.request && !d.spam)
  const spam = (home?.dms ?? []).filter(d => d.spam)
  const shown = tab === 'requests' ? requests : spam
  return (
    <section className="exo-dc-chat exo-dc-friends" aria-label="Message requests">
      <header className="exo-dc-chat-head">
        <Icons.Requests size={22} />
        <strong>Message requests</strong>
        <span className="exo-dc-head-line" aria-hidden="true" />
        <span className="exo-dc-tabs" role="group" aria-label="Message requests">
          <button type="button" className="exo-dc-tab" aria-pressed={tab === 'requests'} onClick={() => setTab('requests')}>Requests{requests.length ? <span className="exo-dc-badge">{requests.length}</span> : null}</button>
          <button type="button" className="exo-dc-tab" aria-pressed={tab === 'spam'} onClick={() => setTab('spam')}>Spam</button>
        </span>
      </header>
      <div className="exo-dc-friends-body">
        <div className="exo-dc-friend-list">
          <p className="exo-dc-side-label">{tab === 'requests' ? 'Requests' : 'Spam'} ({shown.length})</p>
          {shown.map(dm => <RequestRow key={dm.id} dm={dm} spam={tab === 'spam'} onOpen={() => onOpenDm(dm.id)} />)}
          {shown.length === 0 ? <p className="exo-dc-note">{tab === 'requests' ? 'There are no pending message requests.' : 'Nothing in spam.'}</p> : null}
        </div>
      </div>
    </section>
  )
}

// A row reads its latest message once it nears the screen: a long Spam list asks Discord only for the rows you reach.
function RequestRow({ dm, spam, onOpen }: { dm: DiscordDm; spam: boolean; onOpen: () => void }) {
  const [latest, setLatest] = useState<DiscordMessage | null>(null)
  const [near, setNear] = useState(false)
  const row = useRef<HTMLDivElement>(null)
  const { error, answer } = useAnswer(dm.id)
  useEffect(() => (row.current ? observeNearViewport(row.current, () => setNear(true)) : undefined), [])
  useEffect(() => {
    if (!near) return
    let live = true
    host.discordMessages(dm.id, { peek: true }).then(result => { if (live) setLatest(result.messages?.[0] ?? null) }).catch(() => { })
    return () => { live = false }
  }, [dm.id, near])
  const preview = latest ? latest.content || (latest.attachments.length ? 'Attachment' : 'Embed') : ''
  return (
    <div ref={row} className="exo-dc-friend" data-dc-user={dm.userId ?? undefined} data-dc-name={dm.name}>
      <button type="button" className="exo-dc-avatar-btn" aria-label={`Open ${dm.name}`} onClick={onOpen}>
        <Avatar src={dm.avatar} name={dm.name} status={null} size={36} look={dm.look} />
      </button>
      <button type="button" className="exo-dc-friend-names exo-dc-request-open" onClick={onOpen}>
        <strong>{dm.name}</strong>
        <span className={error ? 'is-bad' : undefined}>{error ?? preview}</span>
      </button>
      <ActionButton className="exo-dc-round-btn is-good" aria-label={spam ? `Mark ${dm.name} as not spam` : `Accept ${dm.name}`} {...answer(true)}><Icons.Check size={18} /></ActionButton>
      <ActionButton className="exo-dc-round-btn is-bad" aria-label={`Ignore ${dm.name}`} {...answer(false)}><Icons.Decline size={18} /></ActionButton>
    </div>
  )
}

/** In a request's conversation, in the composer's place: Discord's line, then Ignore and Accept. */
export function RequestBanner({ channelId, spam }: { channelId: string; spam: boolean }) {
  const { error, answer } = useAnswer(channelId)
  return (
    <div className="exo-dc-request-bar" role="group" aria-label="Message request">
      <span className={error ? 'is-bad' : undefined}>{error ?? 'Accept the request to send a message, view attachments, and use links.'}</span>
      <span className="exo-dc-settings-buttons">
        <ActionButton className="exo-set-btn is-compact" {...answer(false)}>Ignore</ActionButton>
        <ActionButton className="exo-set-btn is-primary is-compact" {...answer(true)}>{spam ? 'Mark as not spam' : 'Accept DM'}</ActionButton>
      </span>
    </div>
  )
}

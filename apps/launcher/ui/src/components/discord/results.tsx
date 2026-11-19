import { useEffect, useMemo, useRef } from 'react'
import { avatarUrl, displayName, jumpToMessage, type DiscordMessage, type DiscordPlace } from '../../lib/discord'
import { Icons } from './icons'
import { Markdown, type MarkdownContext } from './markdown'
import { useDismiss } from '../../lib/dismiss'
import { ConfirmButton } from '../ui/ConfirmButton'

/** A popover's box: a press outside it, or Escape, closes it. */
export function useOutside(onClose: () => void) {
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [onClose])
  return box
}

/**
 * A found message (search, pins, the Inbox): where it is (when not here), who and when, the text, and
 * Jump, which opens it in its conversation; onRemove adds the Inbox's × (the pins' Unpin), which asks `ask` in place first
 * when given (Shift held skips it, as in Discord).
 */
export function Result({ message, place, here, ctx, onJumped, onRemove, removeLabel = 'Remove from inbox', ask }: {
  message: DiscordMessage; place: DiscordPlace | null; here: string; ctx: MarkdownContext; onJumped: () => void; onRemove?: () => void; removeLabel?: string; ask?: string
}) {
  const reachable = place !== null || message.channel_id === here
  const named = useMemo(() => message.mentions?.length ? { ...ctx, users: new Map([...ctx.users, ...message.mentions.map(user => [user.id, user] as const)]) } : ctx, [ctx, message.mentions])
  const jump = () => {
    onJumped()
    jumpToMessage({
      guildId: place ? place.guildId : ctx.guildId, channelId: message.channel_id, messageId: message.id,
      parentId: place?.parentId ?? null, name: place?.name ?? null,
    })
  }
  return (
    <article className="exo-dc-result">
      {place && message.channel_id !== here ? <p className="exo-dc-result-place">{place.guildId ? `#${place.name}` : place.name}{place.guild ? <span> · {place.guild}</span> : null}</p> : null}
      <div className="exo-dc-result-row">
        <img className="exo-dc-avatar" src={avatarUrl(message.author, 40)} alt="" width={40} height={40} draggable={false} loading="lazy" decoding="async" />
        <div>
          <p className="exo-dc-msg-head"><strong>{displayName(message)}</strong><time className="exo-dc-msg-time">{new Date(message.timestamp).toLocaleString()}</time></p>
          {message.content ? <Markdown text={message.content} ctx={named} /> : <p className="exo-dc-small">Attachment</p>}
        </div>
        <span className="exo-dc-result-tools">
          {reachable ? <button type="button" className="exo-dc-jump" onClick={jump}>Jump</button> : null}
          {onRemove && ask ? (
            <ConfirmButton className="exo-dc-jump exo-confirm-icon" aria-label={removeLabel} question={ask} now={event => event.shiftKey} onConfirm={onRemove}><Icons.Remove size={14} /></ConfirmButton>
          ) : onRemove ? <button type="button" className="exo-dc-jump" aria-label={removeLabel} onClick={onRemove}><Icons.Remove size={14} /></button> : null}
        </span>
      </div>
    </article>
  )
}

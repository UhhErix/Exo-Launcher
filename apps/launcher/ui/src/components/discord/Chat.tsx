import { MessagesWait } from './waits'
import { CopyLogs, ErrorNote } from '../ui/ErrorNote'
import { QuietBoundary } from '../shell/ErrorBoundary'
import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { applyPollVote, applyReaction, avatarHashOf, hasJump, idBefore, reactionKey, takeJump, type DiscordChannel, type DiscordEmoji, type DiscordHome, type DiscordRoleIcon, type DiscordMessage, type DiscordUser, type PendingUpload } from '../../lib/discord'
import { SILENT } from '../../lib/discordApps'
import { Message, type MessageActions } from './Message'
import { RequestBanner } from './MessageRequests'
import { Composer, fileDescriptions } from './Composer'
import { ChatHeader, PeopleColumn } from './ChatHeader'
import { DmProfile } from './DmProfile'
import { GroupMembers } from './GroupDm'
import { ForwardDialog, NewThreadDialog } from './MessageTools'
import { Icons } from './icons'
import type { MarkdownContext } from './markdown'
import { useAccountSetting, useDiscordPrefs } from '../../lib/discordPrefs'
import { applyMessage, byId, forgetMessages, FRESH_MS, isAfter, isAsking, keepMessages, keptMessages, mergeNewest, newestMessages, PAGE, releaseMessages } from './messageCache'
import { errorText } from '../../lib/errorWords'

const GROUP_MS = 7 * 60 * 1000

// Discord's groups: the same author within 7 minutes, a plain message that isn't a reply. A @silent message after a normal
// one starts a group (its mark shows on the name line), and so does a message that pings after a @silent one.
function continues(before: DiscordMessage | undefined, message: DiscordMessage) {
  if (!before || before.author.id !== message.author.id || message.referenced_message || message.type !== 0) return false
  if (new Date(message.timestamp).getTime() - new Date(before.timestamp).getTime() >= GROUP_MS) return false
  const quiet = (m: DiscordMessage) => ((m.flags ?? 0) & SILENT) !== 0
  if (quiet(message)) return quiet(before)
  return !quiet(before) || (!message.mentions.length && !message.mention_roles?.length && !message.mention_everyone)
}
/** The most messages a conversation holds at once (as Discord does): a page scrolled to lets go of the far end, which
    loads again scrolling back, so history read far back costs what four pages do. */
const KEEP = PAGE * 4
const UPLOAD_LIMIT = 25 * 1024 * 1024
// Your reactions shown before Discord's echo of them, by message, emoji (its variation selector aside, which Discord may
// drop), super or not, and added or taken back; each with the emoji it was shown as, to take that one back.
const ownReactions = new Map<string, DiscordEmoji>()
const ownKey = (messageId: string, r: { emoji: DiscordEmoji; burst?: boolean; add: boolean }) =>
  `${messageId}|${reactionKey(r.emoji).replace(/️/g, '')}|${r.burst ? 1 : 0}|${r.add ? 1 : 0}`
// A reaction as the API takes it (name:id, or the emoji itself) back as Discord's emoji.
function emojiOf(key: string): DiscordEmoji {
  const custom = /^(.+):(\d{5,20})$/.exec(key)
  return custom ? { name: custom[1], id: custom[2] } : { name: key, id: null }
}

/** How long the room stays put away before its conversation lets go of what it can read again. */
const RELEASE_MS = 5 * 60_000
let releasing = 0

type Load = { state: 'loading' | 'ready' | 'error'; message?: string | null }
type Edge = { where: 'older' | 'newer'; message: string }

/** A channel or DM: its messages (older ones load on scrolling up), live changes, typing and the composer. */
export function Chat({ channel, guildId, meId, me, blocked, ignored, channelName, roles, active, quiet = false, onCall, inCall, call, onOpenThread, parent, onBack, onHide }: {
  channel: DiscordChannel
  /** Opened by landing on Messages, not by you: nothing is marked read until you do something here (or press its row). */
  quiet?: boolean
  guildId: string | null
  meId: string | null
  /** You, as the side panel shows you: a message you send shows under your name and picture at once. */
  me?: DiscordHome['me']
  /** Who you blocked or ignored: their messages fold away, as in Discord. Lists, not all of home: a DM's new
      message or someone's status redrew the conversation. */
  blocked?: string[]
  ignored?: string[]
  channelName: (id: string) => string | null
  roles?: Record<string, { name: string; color: string | null }>
  active: boolean
  /** A DM: start (or join) its call. */
  /** A DM's Start Voice Call; video: Start Video Call. */
  onCall?: (video: boolean) => void
  inCall?: boolean
  /** A DM's call while it's on: under the DM's header, over the conversation, as in Discord. */
  call?: ReactNode
  onOpenThread?: (thread: DiscordChannel) => void
  /** A thread's channel (or forum), named above it to go back to. */
  parent?: DiscordChannel | null
  onBack?: () => void
  /** Beside a voice call: no member list of its own. */
  /** Beside a voice call, as Discord's chat panel: its own short header, and Close Chat hides it. */
  onHide?: () => void
}) {
  const channelId = channel.id
  const scroller = useRef<HTMLDivElement>(null)
  const history = useHistory(channelId, meId, scroller)
  const { messages, setMessages, load, newer, edge, lastRead, pinned } = history
  const [typing, setTyping] = useTyping(channelId)
  const [replyTo, setReplyTo] = useState<DiscordMessage | null>(null)
  // Discord's @ ON/OFF on a reply: on, and no choice (null) when replying to yourself.
  const [replyPing, setReplyPing] = useState<boolean | null>(true)
  const [editing, setEditing] = useState<DiscordMessage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const acked = useRef<string | null>(null)
  // What Exo has marked read here this visit: the new-messages bar counts only what came after it, as Discord's counts
  // the read state's unreads (gone once read at the bottom); the NEW line stays where it was.
  const [readTo, setReadTo] = useState<string | null>(null)
  // After Mark Unread the conversation stays unread until you write in it or open it again, as in Discord.
  const holdUnread = useRef(false)
  const landed = useRef(quiet)

  // A new message from someone ends their typing.
  useEffect(() => onHostEvent('discord.message', event => {
    const author = event.kind === 'create' && event.channelId === channelId ? event.message?.author.id : undefined
    if (author) setTyping(map => { if (!map.has(author)) return map; const next = new Map(map); next.delete(author); return next })
  }), [channelId, setTyping])

  // Notifications skip the conversation on screen; a hidden room shows none.
  useEffect(() => {
    if (!active) return
    void host.discordView(channelId).catch(() => { })
    return () => { void host.discordView(null).catch(() => { }) }
  }, [active, channelId])

  // Marks read what the user has seen, as Discord's automatic ack: while the room shows, the newest page is loaded and
  // the list sits at its bottom, measured (a jump that lands at the bottom, or a channel too short to scroll, sends no
  // scroll). The host reads to the channel's newest message, as Discord acks its last_message_id.
  const last = messages.findLast(m => !m.pending)?.id ?? null
  const ackIfSeen = useCallback(() => {
    const el = scroller.current
    if (!el || !active || !last || load.state !== 'ready' || newer || acked.current === last || holdUnread.current || landed.current) return
    if (el.scrollHeight - el.scrollTop - el.clientHeight >= 48) return
    pinned.current = true
    acked.current = last
    setReadTo(last)
    void host.discordAck(channelId, last).catch(() => { })
  }, [active, last, load.state, newer, channelId, pinned])
  useEffect(ackIfSeen, [ackIfSeen])
  // Landing on Messages shows the newest conversation as it is, as glancing at Discord's list would; the first thing you do here (a press, a key, the wheel) is reading it.
  const engage = () => { if (landed.current) { landed.current = false; ackIfSeen() } }
  useEffect(() => { if (!quiet && landed.current) { landed.current = false; ackIfSeen() } }, [quiet, ackIfSeen])

  const { setLastRead } = history
  // Mark Unread moves the NEW line back: the bar counts from it again, not from what was read before.
  const markUnreadFrom = useCallback((id: string | null) => { setReadTo(null); setLastRead(id) }, [setLastRead])
  const newBar = useNewBar(scroller, messages, lastRead, readTo, meId, history.older)
  const markAllRead = () => {
    holdUnread.current = false
    if (last) { acked.current = last; setReadTo(last); void host.discordAck(channelId, last).catch(() => { }) }
    setLastRead(last)
  }

  function onScroll() {
    const el = scroller.current
    if (!el) return
    newBar.check()
    const bottom = el.scrollHeight - el.scrollTop - el.clientHeight < 48
    // Exo's own scroll to the bottom keeps it pinned; only the reader's scrolling unpins it.
    if (Math.abs(el.scrollTop - history.selfTop.current) >= 2) pinned.current = bottom && !newer
    if (el.scrollTop < 400) void history.loadOlder()
    if (newer && el.scrollHeight - el.scrollTop - el.clientHeight < 400) void history.loadNewer()
    ackIfSeen()
  }

  const users = useUsers(messages)
  const { colors, avatars, roleIcons } = useAuthorColors(guildId, messages)
  const dmName = dmPartner(channel)
  const manages = channel.canManageMessages
  const ctx = useMemo<MarkdownContext>(() => ({ users, channelName, meId, guildId, colors, avatars, roleIcons, roles, moderator: !!guildId && manages === true, dmName }),
    [users, channelName, meId, guildId, colors, avatars, roleIcons, roles, manages, dmName])
  const groupBeside = useGroupBeside(!!onHide, guildId, channel)
  const profile = useProfileColumn(!!onHide, guildId, channel)
  const canSend = channel.canSend !== false
  const onReply = useCallback((message: DiscordMessage) => { setEditing(null); setReplyTo(message); setReplyPing(message.author.id !== meId ? true : null) }, [meId])
  const { actions, dialogs } = useMessageTools({
    channel, guildId, threadable: !!guildId && channel.kind === 'text' && channel.type !== 11 && channel.type !== 12 && !parent, onOpenThread,
    setMessages, setError, setEditing, setLastRead: markUnreadFrom, holdUnread, onReply,
  })

  const sendNow = useSend({ channelId, me: me ?? null, meId, replyTo, replyPing, setReplyTo, setError, setMessages, pinned, newer, toPresent: history.toPresent })
  const send = (content: string, files: File[]) => { holdUnread.current = false; return sendNow(content, files) }
  const withRetry = useRetry(actions, sendNow, setMessages)

  function editLast() {
    const mine = messages.findLast(m => m.author.id === meId && !m.pending && (m.type === 0 || m.type === 19))
    if (mine) setEditing(mine)
  }

  const typers = [...typing.values()].map(t => t.name)
  // The conversation and its composer are two panels in one column; the composer is its own panel under the chat.
  return (
    <>
    <div className="exo-dc-chat-col" onPointerDownCapture={engage} onKeyDownCapture={engage} onWheelCapture={engage}>
    <section className="exo-dc-chat" aria-label={channel.name}>
      {dialogs}
      <ChatHeader channel={channel} guildId={guildId} onOpenThread={onOpenThread} ctx={ctx} onCall={onCall} inCall={inCall}
        parent={parent} onBack={onBack} onHide={onHide}
        />
      {call}
      <div className="exo-dc-chat-body">
      <div className="exo-dc-chat-main">
      <NewBar bar={newBar} messages={messages} meId={meId} onRead={markAllRead} />
      <div className="exo-dc-scroll" ref={scroller} onScroll={onScroll}>
        <HistoryTop history={history} />
        <MessageRows messages={messages} lastRead={lastRead} meId={meId} blocked={blocked} ignored={ignored} ctx={ctx} canSend={canSend} editing={editing?.id ?? null} actions={withRetry}
          onRead={newBar.count > 0 ? markAllRead : null} />
        {edge?.where === 'newer' ? <Retry message={edge.message} onRetry={() => void history.loadNewer(true)} /> : null}
      </div>
      {newer ? (
        <div className="exo-dc-present">
          <button type="button" onClick={history.toPresent}>Jump to present</button>
        </div>
      ) : null}
      </div>
      {groupBeside ? <SidePane channel={channel} me={me} /> : null}
      </div>
    </section>
      <div className="exo-dc-compose-panel">
        <RequestGate channel={channel}><Composer
          key={channelId}
          channelId={channelId}
          guildId={guildId}
          users={users}
          placeholder={canSend ? `Message ${target(channel, guildId)}` : "You can't send messages here"}
          disabled={!canSend || load.state === 'error'}
          replyTo={replyTo}
          replyPing={replyPing}
          onReplyPing={() => setReplyPing(on => on === null ? null : !on)}
          slowmode={channel.slowmode}
          error={error}
          typers={typers}
          onSend={send}
          onCancel={() => { setReplyTo(null); setError(null) }}
          onEditLast={editLast}
          onMarkRead={markAllRead}
        /></RequestGate>
      </div>
    </div>
    {/* The person: a column beside the conversation, as a server's people are, or a panel over the page without room for it. */}
    {profile.shown ? <PeopleColumn className="is-profile"><SidePane channel={channel} me={me} /></PeopleColumn> : null}
    </>
  )
}

type ToolsSetup = {
  channel: DiscordChannel
  guildId: string | null
  threadable: boolean
  onOpenThread?: (thread: DiscordChannel) => void
  setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>
  setError: (error: string | null) => void
  setEditing: (message: DiscordMessage | null) => void
  setLastRead: (id: string | null) => void
  holdUnread: React.RefObject<boolean>
  onReply: (message: DiscordMessage) => void
}

/**
 * What a message's hover bar and menu do here, and the dialogs they open (Forward To, Create Thread). Delete others'
 * messages with Manage Messages, pin with Pin Messages (anyone in a DM); the menu's row asks before a delete, a pin or an
 * unpin, in place, unless Shift is held (Message.tsx).
 */
function useMessageTools({ channel, guildId, threadable, onOpenThread, setMessages, setError, setEditing, setLastRead, holdUnread, onReply }: ToolsSetup) {
  const channelId = channel.id
  const [forwarding, setForwarding] = useState<DiscordMessage | null>(null)
  const [threading, setThreading] = useState<DiscordMessage | null>(null)
  const fail = useCallback((fallback: string) => (result: { ok: boolean; message?: string | null }) => { if (!result.ok) setError(errorText(result.message, fallback)) }, [setError])
  // Delete and Pin answer the menu row pressed, which shows how it went in itself (ActionButton).
  const removeMessage = useCallback((message: DiscordMessage) => host.discordDelete(channelId, message.id), [channelId])
  const pin = useCallback(async (message: DiscordMessage) => {
    const result = await host.discordPins(channelId, message.id, !message.pinned)
    if (result.ok) setMessages(list => list.map(m => m.id === message.id ? { ...m, pinned: !message.pinned } : m))
    return result
  }, [channelId, setMessages])
  const actions = useMemo<MessageActions>(() => ({
    canManageMessages: !!guildId && channel.canManageMessages === true,
    canPin: !guildId || channel.canPin === true,
    announcement: channel.type === 5,
    onError: setError,
    onReply,
    onForward: setForwarding,
    onThread: threadable ? setThreading : undefined,
    onOpenThread: onOpenThread ? thread => onOpenThread({ id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: true }) : undefined,
    onMarkUnread: message => {
      const before = idBefore(message.id)
      holdUnread.current = true
      setLastRead(before)
      void host.discordAck(channelId, before, true).then(fail('Discord didn’t mark that unread.')).catch(() => setError('Discord didn’t mark that unread.'))
    },
    onEdit: message => setEditing(message),
    onEditDone: () => setEditing(null),
    // A reaction shows at once, as Discord's own does; Discord's echo of it then lands in its place, and a refusal takes
    // it back with why.
    onReact: (message, emoji, add, burst) => {
      const mine = { add, me: true, emoji: emojiOf(emoji), burst: burst === true }
      const react = (event: typeof mine) => setMessages(list => list.map(m => m.id === message.id ? { ...m, reactions: applyReaction(m.reactions, event) } : m))
      const key = ownKey(message.id, mine)
      ownReactions.set(key, mine.emoji)
      while (ownReactions.size > 100) ownReactions.delete(ownReactions.keys().next().value!)
      react(mine)
      const refused = (why?: string | null) => {
        if (ownReactions.delete(key)) react({ ...mine, add: !add })
        setError(why ?? 'Discord didn’t take that reaction.')
      }
      void host.discordReact(channelId, message.id, emoji, add, { burst }).then(result => { if (!result.ok) refused(result.message) }, () => refused())
    },
    onPin: pin,
    onDelete: removeMessage,
  }), [channelId, guildId, channel.canManageMessages, channel.canPin, channel.type, onReply, threadable, onOpenThread, holdUnread, setLastRead, fail, setError, setEditing, setMessages, removeMessage, pin])
  const dialogs = (
    <>
      {forwarding ? <ForwardDialog message={forwarding} onClose={() => setForwarding(null)} /> : null}
      {threading ? <NewThreadDialog message={threading} onClose={() => setThreading(null)} onCreated={thread => { setThreading(null); actions.onOpenThread?.(thread) }} /> : null}
    </>
  )
  return { actions, dialogs }
}

// Whether the new-messages label in the flow is off screen (or the first unread isn't loaded yet), and how many arrived since.
function useNewBar(scroller: React.RefObject<HTMLDivElement | null>, messages: DiscordMessage[], lastRead: string | null, readTo: string | null, meId: string | null, older: boolean) {
  const from = readTo && (!lastRead || isAfter(readTo, lastRead)) ? readTo : lastRead
  const count = useMemo(() => from ? messages.filter(m => !m.pending && m.author.id !== meId && isAfter(m.id, from)).length : 0, [messages, from, meId])
  const [above, setAbove] = useState(false)
  const check = useCallback(() => {
    const el = scroller.current
    if (!el || count === 0 || !from) { setAbove(false); return }
    // The label where the new ones start: out of the view (or its message not loaded), the pill to jump there shows.
    const label = el.querySelector('.exo-dc-new')
    if (!label) { setAbove(older && !!messages[0] && isAfter(messages[0].id, from)); return }
    const at = label.getBoundingClientRect()
    const view = el.getBoundingClientRect()
    setAbove(at.bottom < view.top + 8 || at.top > view.bottom - 8)
  }, [scroller, count, from, older, messages])
  useLayoutEffect(check, [check])
  return { count, above, check, from }
}

function NewBar({ bar, messages, meId, onRead }: { bar: ReturnType<typeof useNewBar>; messages: DiscordMessage[]; meId: string | null; onRead: () => void }) {
  if (!bar.above || !bar.from) return null
  const { count, from } = bar
  const jumpTo = firstUnread(messages, from, meId) ?? from
  return (
    <div className="exo-dc-new-bar" role="status">
      <button type="button" className="exo-dc-new-bar-jump" onClick={() => window.dispatchEvent(new CustomEvent('exo-dc-jump', { detail: jumpTo }))}>
        {count >= 50 ? '50+' : count} new {count === 1 ? 'message' : 'messages'}
      </button>
      <button type="button" className="exo-dc-new-bar-read" onClick={onRead}>Mark as read <Icons.Check size={14} /></button>
    </div>
  )
}

// The messages with Discord's day lines and the new-messages label, each grouped under the one before when it continues it.
function MessageRows({ messages, lastRead, meId, blocked, ignored, ctx, canSend, editing, actions, onRead }: {
  messages: DiscordMessage[]
  lastRead: string | null
  meId: string | null
  blocked?: string[]
  ignored?: string[]
  ctx: MarkdownContext
  canSend: boolean
  editing: string | null
  actions: MessageActions
  /** Mark as read, beside the label while anything after it is still unread. */
  onRead: (() => void) | null
}) {
  // Runs of blocked or ignored people's messages fold into one line, opened by its first message's id.
  const [shown, setShown] = useState<Set<string>>(() => new Set())
  const runs = foldRuns(messages, meId, blocked, ignored)
  // How many came after the last read, as the label says it (it stays where they start until you leave or mark read).
  const fresh = lastRead ? messages.filter(m => !m.pending && m.author.id !== meId && isAfter(m.id, lastRead)).length : 0
  const toggle = (id: string) => setShown(set => { const next = new Set(set); if (!next.delete(id)) next.add(id); return next })
  return messages.map((message, i) => {
    const fold = runs[i]
    const open = !fold || shown.has(fold.first)
    if (!open) return fold.start ? <Folded key={message.id} reason={fold.reason} count={fold.count} onShow={() => toggle(fold.first)} /> : null
    const before = messages[i - 1]
    const newDay = !before || new Date(before.timestamp).toDateString() !== new Date(message.timestamp).toDateString()
    const grouped = !newDay && continues(before, message)
    const firstNew = lastRead !== null && !message.pending && message.author.id !== meId && isAfter(message.id, lastRead) &&
      (!before || before.pending || !isAfter(before.id, lastRead))
    return (
      <Fragment key={message.id}>
        {newDay ? <DayDivider iso={message.timestamp} /> : null}
        {firstNew ? (
          <div className="exo-dc-new">
            <span>{fresh >= 50 ? '50+' : fresh} new {fresh === 1 ? 'message' : 'messages'}</span>
            {onRead ? <button type="button" className="exo-dc-new-read" onClick={onRead}>Mark as read</button> : null}
          </div>
        ) : null}
        {fold?.start ? <Folded reason={fold.reason} count={fold.count} open onShow={() => toggle(fold.first)} /> : null}
        {/* One message that can't be drawn says so in its place; the rest of the chat goes on. */}
        <QuietBoundary label="Discord message" resetKey={message} fallback={<div className="exo-dc-msg"><ErrorNote message="This message couldn't be shown." /></div>}>
          <Message message={message} grouped={grouped} ctx={ctx} canSend={canSend} editing={editing === message.id}
            mine={message.author.id === meId} actions={actions} />
        </QuietBoundary>
      </Fragment>
    )
  })
}

type Fold = { reason: 'blocked' | 'ignored'; first: string; count: number; start: boolean }

// Each message by someone you blocked or ignored, with the run of them it belongs to (as Discord groups them).
function foldRuns(messages: DiscordMessage[], meId: string | null, blocked?: string[], ignored?: string[]): (Fold | null)[] {
  const reasons = messages.map(m => m.author.id === meId ? null
    : blocked?.includes(m.author.id) ? 'blocked' as const : ignored?.includes(m.author.id) ? 'ignored' as const : null)
  const runs: (Fold | null)[] = []
  let head: Fold | null = null
  reasons.forEach((reason, i) => {
    if (reason && head?.reason === reason) head.count++
    else head = reason ? { reason, first: messages[i].id, count: 1, start: true } : null
    runs.push(head)
  })
  return runs.map((fold, i) => fold && { ...fold, start: runs[i - 1] !== fold })
}

// Discord's folded line: "3 blocked messages", then Show messages (or, opened, Collapse messages).
function Folded({ reason, count, open, onShow }: { reason: string; count: number; open?: boolean; onShow: () => void }) {
  const plural = count === 1 ? '' : 's'
  return (
    <p className="exo-dc-folded">
      <span>{count} {reason} message{plural}</span>
      <span aria-hidden="true">—</span>
      <button type="button" className="exo-dc-link" onClick={onShow}>{open ? 'Collapse' : 'Show'} message{plural}</button>
    </p>
  )
}

// Over the messages: loading, or what failed with Retry.
function HistoryTop({ history }: { history: ReturnType<typeof useHistory> }) {
  const { load, edge } = history
  if (load.state === 'loading') return <MessagesWait label="Loading messages" rows={8} />
  if (load.state === 'error') return <Retry message={errorText(load.message, "Discord didn't send this channel.")} onRetry={history.toPresent} />
  return edge?.where === 'older' ? <Retry message={edge.message} onRetry={() => void history.loadOlder(true)} /> : null
}

function Retry({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <p className="exo-dc-note exo-dc-retry" role="alert">
      {message}
      <button type="button" className="exo-dc-tab" onClick={onRetry}>Try again</button>
      <CopyLogs error={message} />
    </p>
  )
}

/**
 * A channel's messages: the newest page (or the one around a message jumped to), older pages scrolling up,
 * newer ones scrolling down after a jump, and live changes. A page that fails stops asking until Retry.
 * A conversation seen before (or hovered) shows at once as it was kept, and Discord's newest page is laid over it.
 */
function useHistory(channelId: string, meId: string | null, scroller: React.RefObject<HTMLDivElement | null>) {
  const [kept] = useState(() => hasJump(channelId) ? null : keptMessages(channelId))
  const [messages, setMessages] = useState<DiscordMessage[]>(() => kept?.messages ?? [])
  const [load, setLoad] = useState<Load>({ state: kept ? 'ready' : 'loading' })
  const [older, setOlder] = useState(kept?.older ?? true)
  // Newer messages than the ones shown (after a jump): live ones wait until Jump to Present.
  const [newer, setNewer] = useState(false)
  const [edge, setEdge] = useState<Edge | null>(null)
  // The newest message read when the channel opened: Discord's red NEW line sits after it.
  const [lastRead, setLastRead] = useState<string | null>(kept?.lastRead ?? null)
  const pinned = useRef(true)
  // Where Exo itself last scrolled the list: the scroll event that follows is its own, not the reader's, and must not
  // unpin it (a picture growing in between made it read as "scrolled up" and the chat stopped short of the bottom).
  const selfTop = useRef(-1)
  // The first message on screen and how far below the list's top it sits, held while a page arrives (and the far end
  // goes): it stays where it was.
  const anchor = useRef<{ id: string; at: number } | null>(null)
  // How far the list was from its bottom, as Exo last left it or the reader scrolled it (the room shown again and a row
  // growing keep it).
  const fromBottom = useRef(0)
  const scrollTo = useRef<string | null>(null)
  const busy = useRef(false)
  // Each first page replaces what any earlier request would still bring.
  const generation = useRef(0)
  const firstRead = useRef(kept?.lastRead === undefined)
  // The message the conversation opened to show (kept for the mount, so a remount in development shows it too).
  const opening = useRef<string | null | undefined>(undefined)
  const newerNow = useRef(false)
  const back = useRef(false)
  newerNow.current = newer
  const shown = useRef({ messages, older, newer, ready: load.state === 'ready' })
  shown.current = { messages, older, newer, ready: load.state === 'ready' }

  const loadPage = useCallback(async (around: string | null) => {
    const generationNow = ++generation.current
    setLoad({ state: 'loading' }); setEdge(null)
    try {
      const result = around ? await host.discordMessages(channelId, { around }) : await newestMessages(channelId, false)
      if (generationNow !== generation.current) return
      if (!result.ok || !result.messages) { setLoad({ state: 'error', message: result.message }); return }
      if (firstRead.current) { firstRead.current = false; setLastRead(result.lastRead ?? null) }
      const page = byId(result.messages)
      const after = around ? page.filter(m => isAfter(m.id, around)).length : 0
      setMessages(page)
      setOlder(around ? page.length - after > PAGE / 2 - 1 : page.length >= PAGE)
      setNewer(around ? after >= PAGE / 2 - 1 : false)
      pinned.current = !around
      scrollTo.current = around
      setLoad({ state: 'ready' })
    } catch (err) {
      report(err, 'DiscordChat.messages')
      if (generationNow === generation.current) setLoad({ state: 'error' })
    }
  }, [channelId])

  // Discord's newest page over what shows (a conversation kept from before, or after a reconnect).
  const refresh = useCallback(async () => {
    if (newerNow.current) return
    const generationNow = generation.current
    try {
      const result = await newestMessages(channelId, false)
      if (generationNow !== generation.current || newerNow.current || !result.ok || !result.messages) return
      if (firstRead.current) { firstRead.current = false; setLastRead(result.lastRead ?? null) }
      const page = byId(result.messages)
      if (mergeNewest(shown.current.messages, page).gap) setOlder(true)
      setMessages(list => mergeNewest(list, page).messages)
    } catch (err) {
      report(err, 'DiscordChat.refresh')
    }
  }, [channelId])

  // First page: the newest, or around the message a search, pin or the Inbox jumps to; a kept one shows as it was.
  // Back on screen (the room was hidden, which pauses its effects): what shows stays, and only the newest is laid over it.
  useEffect(() => {
    if (opening.current === undefined) opening.current = takeJump(channelId)
    if (back.current && shown.current.ready && shown.current.messages.length > 0) void refresh()
    else if (kept && !opening.current) {
      if (Date.now() - kept.at > FRESH_MS || isAsking(channelId)) void refresh()
    } else {
      setMessages([]); setOlder(true); setNewer(false)
      firstRead.current = true
      void loadPage(opening.current)
    }
    back.current = true
    const requests = generation
    return () => { requests.current++ }
  }, [channelId, loadPage, kept, refresh])

  // Kept for coming back, unless it shows older history after a jump.
  useEffect(() => () => {
    const { messages, older, newer, ready } = shown.current
    if (newer || !ready) forgetMessages(channelId)
    else keepMessages(channelId, messages, older)
  }, [channelId])

  // A reconnect may have missed messages: Discord's newest page again.
  useEffect(() => onHostEvent('discord.state', event => { if (event.state === 'ready') void refresh() }), [refresh])

  // A jump to a message here: to it when it's loaded, else to the page around it.
  useEffect(() => {
    const jump = (id: string) => {
      const el = scroller.current?.querySelector(`[data-msg-id="${id}"]`)
      if (el) flash(el, true)
      else void loadPage(id)
    }
    const fromQuote = (event: Event) => jump((event as CustomEvent<string>).detail)
    const fromOpen = () => { const id = takeJump(channelId); if (id) jump(id) }
    window.addEventListener('exo-dc-jump', fromQuote)
    window.addEventListener('exo-discord-open', fromOpen)
    return () => { window.removeEventListener('exo-dc-jump', fromQuote); window.removeEventListener('exo-discord-open', fromOpen) }
  }, [channelId, loadPage, scroller])

  useLiveMessages(channelId, meId, setMessages, newerNow)

  // Stays at the bottom when it was there; keeps the reading position when older messages arrive above; shows a message jumped to.
  useLayoutEffect(() => {
    const el = scroller.current
    if (!el) return
    if (scrollTo.current) {
      const target = el.querySelector(`[data-msg-id="${scrollTo.current}"]`)
      scrollTo.current = null
      if (target) flash(target, false)
    } else if (anchor.current) {
      const row = el.querySelector(`[data-msg-id="${anchor.current.id}"]`)
      if (row) el.scrollTop += row.getBoundingClientRect().top - el.getBoundingClientRect().top - anchor.current.at
      anchor.current = null
    } else if (pinned.current) el.scrollTop = el.scrollHeight
    selfTop.current = el.scrollTop
    if (el.clientHeight > 0) fromBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight
  }, [messages, scroller])

  // The room hidden and shown again (display: none drops an element's scroll position) or the messages growing under
  // it (a picture or embed loading): back at the bottom when it was there, else as far from the bottom as it was left.
  useLayoutEffect(() => {
    const el = scroller.current
    if (!el) return
    const remember = () => { if (el.clientHeight > 0) fromBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight }
    const settle = () => {
      if (el.clientHeight === 0) return
      el.scrollTop = pinned.current ? el.scrollHeight : el.scrollHeight - el.clientHeight - fromBottom.current
      selfTop.current = el.scrollTop
    }
    const watch = new ResizeObserver(settle)
    watch.observe(el)
    // Each message is its own row in the scroller: new ones are watched as they come.
    for (const child of el.children) watch.observe(child)
    const rows = new MutationObserver(changes => { for (const change of changes) for (const node of change.addedNodes) if (node instanceof Element) watch.observe(node) })
    rows.observe(el, { childList: true })
    el.addEventListener('scroll', remember, { passive: true })
    return () => { watch.disconnect(); rows.disconnect(); el.removeEventListener('scroll', remember) }
  }, [scroller])

  // Put away a while (the room hidden: Activity runs no effects, so this cleanup is the moment it went), the conversation
  // lets go of all but its newest page and the cache of all but the latest few; coming back reads the newest again over
  // it, and scrolling up reads the rest.
  useEffect(() => {
    window.clearTimeout(releasing)
    return () => {
      window.clearTimeout(releasing)
      releasing = window.setTimeout(() => {
        releaseMessages()
        const { messages: now, newer: ahead } = shown.current
        if (ahead || now.filter(m => !m.pending).length <= PAGE) return
        pinned.current = true
        setOlder(true)
        setMessages(list => [...list.filter(m => !m.pending).slice(-PAGE), ...list.filter(m => m.pending)])
      }, RELEASE_MS)
    }
  }, [])

  // A new look (text size, compact) changes every message's height: stay at the bottom if there.
  const { fontSize } = useDiscordPrefs()
  const compact = useAccountSetting('messageDisplayCompact')
  useLayoutEffect(() => {
    const el = scroller.current
    if (el && pinned.current) { el.scrollTop = el.scrollHeight; selfTop.current = el.scrollTop }
  }, [fontSize, compact, scroller])

  const loadEdge = useCallback(async (where: 'older' | 'newer', retry: boolean) => {
    const real = messages.filter(m => !m.pending)
    const from = where === 'older' ? real[0] : real.at(-1)
    if ((where === 'older' ? !older : !newer) || busy.current || !from || (edge && !retry) || load.state !== 'ready') return
    busy.current = true
    const generationNow = generation.current
    setEdge(null)
    const failed = where === 'older' ? "Discord didn't send older messages." : "Discord didn't send newer messages."
    try {
      const result = await host.discordMessages(channelId, where === 'older' ? { before: from.id } : { after: from.id })
      if (generationNow !== generation.current) return
      if (!result.ok || !result.messages) { setEdge({ where, message: errorText(result.message, failed) }); return }
      const page = byId(result.messages)
      anchor.current = firstOnScreen(scroller.current)
      // The far end let go loads again on the way back.
      const cut = cutFor(messages, page, where)
      if (where === 'older') {
        setOlder(page.length >= PAGE)
        if (cut) setNewer(true)
      } else {
        setNewer(page.length >= PAGE)
        if (cut) setOlder(true)
      }
      setMessages(list => joinPage(list, page, where, cut))
    } catch (err) {
      report(err, 'DiscordChat.' + where)
      if (generationNow === generation.current) setEdge({ where, message: failed })
    } finally {
      busy.current = false
    }
  }, [channelId, messages, older, newer, edge, load.state, scroller])

  const toPresent = useCallback(() => loadPage(null), [loadPage])
  return {
    messages, setMessages, load, older, newer, edge, lastRead, setLastRead, pinned, selfTop, toPresent,
    loadOlder: (retry = false) => loadEdge('older', retry),
    loadNewer: (retry = false) => loadEdge('newer', retry),
  }
}

/** The first message on screen and how far below the list's top it sits. */
function firstOnScreen(el: HTMLElement | null) {
  if (!el) return null
  const top = el.getBoundingClientRect().top
  const row = [...el.querySelectorAll<HTMLElement>('[data-msg-id]')].find(r => r.getBoundingClientRect().bottom > top)
  return row?.dataset.msgId ? { id: row.dataset.msgId, at: row.getBoundingClientRect().top - top } : null
}

/** Where a page joining at `where` cuts the far end so KEEP stay: the newest (older page) or oldest (newer page) kept,
    or null when all fit or a message is still sending. */
function cutFor(list: DiscordMessage[], page: DiscordMessage[], where: 'older' | 'newer') {
  const fresh = page.filter(m => !list.some(x => x.id === m.id))
  const all = where === 'older' ? [...fresh, ...list] : [...list, ...fresh]
  if (all.length <= KEEP || all.some(m => m.pending)) return null
  return where === 'older' ? all[KEEP - 1].id : all[all.length - KEEP].id
}

function joinPage(list: DiscordMessage[], page: DiscordMessage[], where: 'older' | 'newer', cut: string | null) {
  const fresh = page.filter(m => !list.some(x => x.id === m.id))
  const all = where === 'older' ? [...fresh, ...list] : [...list, ...fresh]
  return cut === null ? all : all.filter(m => m.pending || (where === 'older' ? !isAfter(m.id, cut) : !isAfter(cut, m.id)))
}

// Live: new, edited and deleted messages, reactions and poll votes. New ones wait while older messages show.
function useLiveMessages(channelId: string, meId: string | null, setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>, newer: React.RefObject<boolean>) {
  useEffect(() => {
    const offMessage = onHostEvent('discord.message', event => {
      if (event.channelId === channelId) setMessages(list => applyMessage(list, event, meId, newer.current))
    })
    const offReaction = onHostEvent('discord.reaction', event => {
      if (event.channelId !== channelId) return
      // Discord's echo of a reaction already shown: the one shown is taken back and Discord's put in (its emoji as Discord
      // has it, a super reaction's colors), so it counts once.
      const key = ownKey(event.messageId, event)
      const shown = event.me ? ownReactions.get(key) : undefined
      if (shown) ownReactions.delete(key)
      const undo = shown && { ...event, emoji: shown, add: !event.add }
      setMessages(list => list.map(m => m.id === event.messageId
        ? { ...m, reactions: applyReaction(undo ? applyReaction(m.reactions, undo) : m.reactions, event) } : m))
    })
    const offPoll = onHostEvent('discord.pollVote', event => {
      if (event.channelId !== channelId) return
      setMessages(list => list.map(m => m.id === event.messageId && m.poll ? { ...m, poll: applyPollVote(m.poll, event.answerId, event.add, event.me) } : m))
    })
    return () => { offMessage(); offReaction(); offPoll() }
  }, [channelId, meId, setMessages, newer])
}

// Who is typing; names fade after ten seconds without another ping.
function useTyping(channelId: string) {
  const [typing, setTyping] = useState<Map<string, { name: string; until: number }>>(new Map())
  useEffect(() => {
    setTyping(new Map())
    return onHostEvent('discord.typing', event => {
      if (event.channelId !== channelId) return
      setTyping(map => new Map(map).set(event.userId, { name: event.name, until: Date.now() + 10_000 }))
    })
  }, [channelId])
  useEffect(() => {
    if (typing.size === 0) return
    const timer = window.setInterval(() => {
      setTyping(map => {
        const now = Date.now()
        const next = new Map([...map].filter(([, value]) => value.until > now))
        return next.size === map.size ? map : next
      })
    }, 1000)
    return () => window.clearInterval(timer)
  }, [typing.size])
  return [typing, setTyping] as const
}

type SendSetup = {
  channelId: string
  me: DiscordHome['me']
  meId: string | null
  replyTo: DiscordMessage | null
  replyPing: boolean | null
  setReplyTo: (message: DiscordMessage | null) => void
  setError: (error: string | null) => void
  setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>
  pinned: React.RefObject<boolean>
  newer: boolean
  toPresent: () => Promise<void>
}

// Retry sends a failed message again (with the reply it had); Delete drops it, as Discord's failed message offers.
function useRetry(actions: MessageActions, send: ReturnType<typeof useSend>, setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>) {
  const latest = useRef(send)
  useEffect(() => { latest.current = send })
  return useMemo<MessageActions>(() => ({
    ...actions,
    onRetry: message => void latest.current(message.content, [], message),
    onDiscard: message => setMessages(list => list.filter(m => m.id !== message.id)),
  }), [actions, setMessages])
}

/**
 * Sending, Discord's way: the message shows at once under your name (its files with their upload's
 * progress), then as Discord has it. A file that fails takes back the ones already sent
 * to the host, and its message goes back to the box; a text Discord didn't take stays
 * in the list with why, to Retry (retry) or Delete.
 */
function useSend({ channelId, me, meId, replyTo, replyPing, setReplyTo, setError, setMessages, pinned, newer, toPresent }: SendSetup) {
  return async function send(content: string, files: File[] = [], retry?: DiscordMessage): Promise<boolean> {
    setError(null)
    const total = files.reduce((sum, file) => sum + file.size, 0)
    if (total > UPLOAD_LIMIT) { setError('Files can be 25 MB at most.'); return false }
    if (newer) await toPresent()
    const pendingId = `pending-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`
    const reply = retry ? retry.referenced_message ?? null : replyTo
    if (retry) setMessages(list => list.filter(m => m.id !== retry.id))
    const uploading: PendingUpload | undefined = files.length ? { files: files.map(f => ({ name: f.name, size: f.size })), progress: 0 } : undefined
    pinned.current = true
    setMessages(list => [...list, {
      id: pendingId, channel_id: channelId, type: 0, content, timestamp: new Date().toISOString(),
      author: meAsAuthor(me, meId), attachments: [], embeds: [], mentions: [], pending: true, uploading,
      referenced_message: reply,
    }])
    if (!retry) setReplyTo(null)
    const drop = (message: string) => {
      if (!files.length) {
        setMessages(list => list.map(m => m.id === pendingId ? { ...m, failed: message } : m))
        return true
      }
      setMessages(list => list.filter(m => m.id !== pendingId))
      setReplyTo(reply)
      setError(message)
      return false
    }
    const uploads: string[] = []
    // Each file's alt text (Modify Attachment), by the upload id it went up as.
    const descriptions: Record<string, string> = {}
    let done = 0
    for (const file of files) {
      const uploaded = await uploadFile(file, sent => {
        const progress = total ? (done + sent) / total : 1
        setMessages(list => list.map(m => m.id === pendingId && m.uploading ? { ...m, uploading: { ...m.uploading, progress } } : m))
      })
      if (typeof uploaded !== 'string') {
        for (const id of uploads) void host.discordUpload({ uploadId: id, cancel: true }).catch(() => { })
        return drop(uploaded.message)
      }
      uploads.push(uploaded)
      const alt = fileDescriptions.get(file)
      if (alt) descriptions[uploaded] = alt
      done += file.size
    }
    try {
      const result = await deliver({ channelId, content, reply, replyPing, uploads, descriptions })
      if (!result.ok) return drop(errorText(result.message, 'Discord didn’t send that.'))
      const sent = result.sent
      setMessages(list => {
        const without = list.filter(m => m.id !== pendingId)
        return sent && !without.some(m => m.id === sent.id) ? [...without, sent] : without
      })
      return true
    } catch (err) {
      report(err, 'DiscordChat.send')
      return drop('Discord did not send that.')
    }
  }
}

// The message to Discord: its words and files.
function deliver({ channelId, content, reply, replyPing, uploads, descriptions }: {
  channelId: string; content: string; reply: DiscordMessage | null; replyPing: boolean | null; uploads: string[]; descriptions: Record<string, string>
}) {
  const ping = reply ? { ping: replyPing !== false } : {}
  return host.discordSend(channelId, content, reply?.id, uploads, { ...ping, ...(uploads.some(id => descriptions[id]) ? { descriptions } : {}) })
}

// You as a message's author, with your picture (the side panel has it as a link; messages want the hash).
function meAsAuthor(me: DiscordHome['me'], meId: string | null): DiscordUser {
  return { id: me?.id ?? meId ?? '0', username: me?.name ?? 'You', global_name: me?.name ?? null, avatar: avatarHashOf(me?.avatar) }
}

const PIECE = 600 * 1024

/** Sends a file to the host in base64 pieces, telling how many bytes are in; the upload id goes with the message. */
export async function uploadFile(file: File, onProgress?: (sent: number) => void): Promise<string | { message: string }> {
  if (file.size > UPLOAD_LIMIT) return { message: `${file.name} is over 25 MB.` }
  let uploadId: string | undefined
  for (let at = 0; at < Math.max(file.size, 1); at += PIECE) {
    const bytes = new Uint8Array(await file.slice(at, at + PIECE).arrayBuffer())
    let binary = ''
    for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
    const result = await host.discordUpload({ uploadId, name: file.name, type: file.type || 'application/octet-stream', data: btoa(binary) })
      .catch(() => ({ ok: false, uploadId: undefined, message: undefined }))
    if (!result.ok || !result.uploadId) {
      if (uploadId) void host.discordUpload({ uploadId, cancel: true }).catch(() => { })
      return { message: errorText(result.message, `${file.name} couldn't be added.`) }
    }
    uploadId = result.uploadId
    onProgress?.(Math.min(file.size, at + PIECE))
  }
  return uploadId!
}

/**
 * The people in this channel's messages (authors and mentions), the same map while nobody new
 * appears, so one more message doesn't redraw every other one.
 */
function useUsers(messages: DiscordMessage[]) {
  const kept = useRef<Map<string, DiscordUser>>(new Map())
  return useMemo(() => {
    const next = new Map<string, DiscordUser>()
    for (const m of messages) {
      if (!m.pending) next.set(m.author.id, m.author)
      for (const u of m.mentions) next.set(u.id, u)
    }
    const before = kept.current
    const same = next.size === before.size && [...next].every(([id, u]) => {
      const was = before.get(id)
      return was && was.username === u.username && was.global_name === u.global_name && was.avatar === u.avatar
    })
    if (!same) kept.current = next
    return kept.current
  }, [messages])
}

// Name colors and server pictures for this channel's authors: what Exo knows now, then whatever Discord sends for the rest.
type Looks = { colors: Map<string, string | null>; avatars: Map<string, string>; roleIcons: Map<string, DiscordRoleIcon> }
const NO_LOOKS = (): Looks => ({ colors: new Map(), avatars: new Map(), roleIcons: new Map() })

function useAuthorColors(guildId: string | null, messages: DiscordMessage[]) {
  const [looks, setLooks] = useState<Looks>(NO_LOOKS)
  const authors = useMemo(() => [...new Set(messages.filter(m => !m.pending).map(m => m.author.id))].sort().join(','), [messages])
  useEffect(() => {
    if (!guildId || !authors) { setLooks(NO_LOOKS()); return }
    let live = true
    let waiting: number | undefined
    const load = () => host.discordColors(guildId, authors.split(',')).then(result => {
      if (live) setLooks({ colors: new Map(Object.entries(result.colors)), avatars: new Map(Object.entries(result.avatars ?? {})), roleIcons: new Map(Object.entries(result.icons ?? {})) })
    }).catch(() => { })
    load()
    // Member chunks land in bursts after a channel opens, each announcing new colors: one fetch a beat covers them.
    const off = onHostEvent('discord.colors', event => {
      if (event.guildId !== guildId || waiting !== undefined) return
      waiting = window.setTimeout(() => { waiting = undefined; if (live) load() }, 400)
    })
    return () => { live = false; window.clearTimeout(waiting); off() }
  }, [guildId, authors])
  return looks
}

// A message request's conversation takes Accept or Ignore before a reply, as Discord's does.
function RequestGate({ channel, children }: { channel: DiscordChannel; children: React.ReactNode }) {
  return channel.dm?.request ? <RequestBanner channelId={channel.id} spam={channel.dm.spam === true} /> : children
}

/** Scrolls a message into the middle and lights it up a moment, as Discord does for a jump. */
function flash(el: Element, smooth: boolean) {
  el.scrollIntoView({ block: 'center', behavior: smooth && !shouldReduceMotion() ? 'smooth' : 'auto' })
  el.classList.add('is-flash')
  window.setTimeout(() => el.classList.remove('is-flash'), 1600)
}

function DayDivider({ iso }: { iso: string }) {
  return (
    <div className="exo-dc-day" role="separator">
      <span>{new Date(iso).toLocaleDateString(undefined, { dateStyle: 'long' })}</span>
    </div>
  )
}

// A window narrow enough that the member list would squeeze the chat (a portrait screen): discord.css's 900 px.
const NARROW = '(max-width: 1099px)'
function useNarrowWindow() {
  return useSyncExternalStore(changed => {
    const query = window.matchMedia(NARROW)
    query.addEventListener('change', changed)
    return () => query.removeEventListener('change', changed)
  }, () => window.matchMedia(NARROW).matches)
}

// A group's members are beside it while the window has room and no call's side panel is open.
function useGroupBeside(hiddenByCall: boolean, guildId: string | null, channel: DiscordChannel) {
  const narrow = useNarrowWindow()
  return !guildId && !!channel.dm?.group && !narrow && !hiddenByCall
}

// Discord's Hide User Profile: a 1:1 conversation's person, a column beside it while the page has room (shown until
// hidden, for this conversation), else a panel over the page's right side that the toggle opens and Escape or a press
// outside closes (never remembered).
// A DM's person beside it, always (owner, 2026-11-14: the right column has no off).
function useProfileColumn(inPanel: boolean, guildId: string | null, channel: DiscordChannel) {
  return { shown: !inPanel && !guildId && !!channel.dm && !channel.dm.group }
}

// The first message after what was read that isn't yours, loaded or not (null: load around the read mark).
function firstUnread(messages: DiscordMessage[], lastRead: string, meId: string | null) {
  return messages.find(m => !m.pending && m.author.id !== meId && isAfter(m.id, lastRead))?.id ?? null
}

// "#chat", "@lazy" or a group's name, as Discord's composer names its conversation.
function target(channel: DiscordChannel, guildId: string | null) {
  if (guildId) return `#${channel.name}`
  return channel.dm?.group ? channel.name : `@${channel.name}`
}

// Beside a DM: a group's members, or the person's profile.
function SidePane({ channel, me }: { channel: DiscordChannel; me?: DiscordHome['me'] }) {
  const dm = channel.dm
  if (dm?.group) return <GroupMembers channel={channel} me={me ?? null} />
  if (!dm?.userId) return null
  const fallback = { id: dm.userId, username: dm.username ?? channel.name, global_name: channel.name, avatar: avatarHashOf(dm.avatar) }
  return <DmProfile userId={dm.userId} status={dm.status} fallback={fallback} />
}

// The other person's name in a 1:1 DM.
function dmPartner(channel: { name: string; dm?: { group?: boolean } | null }) {
  return channel.dm && !channel.dm.group ? channel.name : null
}

import { MessagesWait } from './waits'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { onUiPrefs, setUiPref, uiPref } from '../../lib/uiPrefs'
import { onPerformanceMode, performanceMode, shareDiscordHome, takeDiscordSettings, takePendingOpen, type DiscordChannel, type DiscordChannels, type DiscordDm, type DiscordHome, type DiscordVoiceMember, type OpenTarget } from '../../lib/discord'
import { Chat } from './Chat'
import { HomePage } from './HomePage'
import { CallDock } from './CallDock'
import { ChannelNav, ChannelsPanel, ServerNavContext, type ServerNav } from './Channels'
import { MemberList } from './MemberList'
import { PeopleColumn } from './ChatHeader'
import { InboxButton, MePanel } from './MePanel'
import { MessagesPane } from './Messages'
import { FRIENDS, MESSAGES, NOW, REQUESTS, SERVERS, SHOP } from './roomPlaces'
import { Band, type PlaceTab } from './Band'
import { MessageRequestsPage } from './MessageRequests'
import { ShopPage } from './ShopPage'
import { IncomingCall, useDiscordVoice, type VoiceControls, VoiceRoom } from './voice'
import { AddServer, ImageHost, ProfileHost, useProfileReadAhead } from './extras'
import { ForumView } from './Forum'
import { isPage, ServerPage } from './serverPages'
import { FullProfileHost } from './DmProfile'
import { ReportHost } from './Report'
import { ProfilePlaces } from './profileParts'
import { UserMenuHost } from './UserMenu'
import { ThreadMenuHost } from './ThreadMenu'
import { ImageMenuHost } from './ImageMenu'
import { AppModalHost } from './AppModal'
import { MfaHost } from './MfaDialog'
import { UserSettings } from './UserSettings'
import type { DiscordSettingsSection } from '../../lib/settingsSections'
// Discord's settings load the first time they open, not with the room.
import { startDiscordSettings, useRoomLook } from '../../lib/discordPrefs'
import { prefetchMessages } from './messageCache'
import { pane, pressedBefore, transition, visible } from '../../lib/viewTransition'
import { pageGet, pageSet } from '../../lib/pageStore'

const HOME = '@me'
/** The server a place is (Direct messages is none). */
const guildOf = (place: string) => place === HOME ? null : place
const WHERE_KEY = 'exo.discord.where'
type SavedThread = { id: string; name: string; parentId: string }
type Where = { place: string; channels: Record<string, string>; threads: Record<string, SavedThread> }
function readWhere(): Where {
  try {
    const saved = JSON.parse(pageGet(WHERE_KEY) ?? 'null') as Partial<Where> | null
    if (saved && saved.channels && typeof saved.channels === 'object')
      return { place: HOME, channels: saved.channels, threads: saved.threads && typeof saved.threads === 'object' ? saved.threads : {} }
  } catch { /* a convenience */ }
  return { place: HOME, channels: {}, threads: {} }
}
// The room opens on Home each time Exo starts. The channel (and thread) last open in each server are kept across
// restarts, as Discord does, and where you are is kept across remounts (a reconnect, a room switch).
const where = readWhere()
const lastChannel = new Map<string, string>([...Object.entries(where.channels).filter(([place, id]) => place !== HOME && typeof id === 'string'), [HOME, NOW]])
const lastThread = new Map<string, SavedThread>(Object.entries(where.threads).filter(([place, t]) => place !== HOME && typeof t?.id === 'string' && typeof t.parentId === 'string'))
function saveWhere(place: string) {
  where.place = place
  try { pageSet(WHERE_KEY, JSON.stringify({ place, channels: Object.fromEntries(lastChannel), threads: Object.fromEntries(lastThread) })) } catch { /* a convenience */ }
}
// The thread last open in a place, when it is still inside the channel the place reopens at.
function savedThread(place: string, channelId: string | null): DiscordChannel | null {
  const saved = lastThread.get(place)
  return saved && saved.parentId === channelId ? threadChannel(saved) : null
}

// Each server's channel list as last read: switching back shows it at once while it is read again. The latest 24 servers.
const shownLists = new Map<string, DiscordChannels>()
function keepList(guildId: string, list: DiscordChannels) {
  shownLists.delete(guildId)
  shownLists.set(guildId, list)
  while (shownLists.size > 24) shownLists.delete(shownLists.keys().next().value!)
}

/**
 * Discord's answer laid over what shows: each part that came back the same keeps its object, so new DMs don't redraw
 * the rail, and an answer with nothing new (most of the reads a burst of changes asks for) redraws nothing.
 */
function sameParts<T extends object>(current: T | null | undefined, next: T): T {
  if (!current) return next
  const out = { ...next }
  let same = Object.keys(current).length === Object.keys(next).length
  for (const key of Object.keys(next) as (keyof T)[]) {
    if (current[key] === next[key] || JSON.stringify(current[key]) === JSON.stringify(next[key])) out[key] = current[key]
    else same = false
  }
  return same ? current : out
}

// The conversation a place opens at, read ahead while the pointer rests on its tile or chip.
function peekPlace(place: string | null) {
  const channelId = place ? lastChannel.get(place) : undefined
  const text = place === HOME ? ![NOW, FRIENDS, MESSAGES, SERVERS, SHOP, REQUESTS].includes(channelId ?? '') : shownLists.get(place ?? '')?.channels.some(c => c.id === channelId && c.kind === 'text')
  prefetchMessages(place && channelId && text ? savedThread(place, channelId)?.id ?? channelId : null)
}

function dmChannel(dm: DiscordDm): DiscordChannel {
  return {
    id: dm.id, name: dm.name, type: dm.group ? 3 : 1, kind: 'text', canSend: !dm.request && !dm.spam, canRead: true,
    muted: dm.muted, mutedUntil: dm.mutedUntil, dm: { userId: dm.userId ?? null, avatar: dm.avatar, status: dm.status ?? null, activity: dm.group ? null : dm.subtitle ?? null, username: dm.username ?? null, group: dm.group, request: dm.request || dm.spam, spam: dm.spam, ownerId: dm.ownerId, members: dm.members },
  }
}

/** A thread (or forum post) as the chat opens it. */
function threadChannel(thread: { id: string; name: string; locked?: boolean }): DiscordChannel {
  return { id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: thread.locked !== true }
}

/** Keeps the server (or DMs) you're in and the channel open there across restarts; a server you've left falls back to your DMs. */
type Stop = { place: string; open: string | null; thread: DiscordChannel | null }

/**
 * Back and Forward through where you've been in the room (servers, DMs, channels, threads), as Discord's client has
 * them: the mouse's side buttons, or Alt+Left and Alt+Right, while the room is showing. At most 50 steps back.
 */
function useBackForward(active: boolean, place: string, open: string | null, thread: DiscordChannel | null,
  navigate: (to: string, channelId: string | null, inThread?: DiscordChannel | null) => void) {
  const trail = useRef<Stop[]>([])
  const at = useRef(-1)
  const stepping = useRef(false)
  useEffect(() => {
    if (!open) return
    const here = trail.current[at.current]
    if (here && here.place === place && here.open === open && here.thread?.id === thread?.id) return
    // A step taken with Back or Forward lands where it went; anything else starts a new way forward from here.
    if (stepping.current) { stepping.current = false; return }
    trail.current = [...trail.current.slice(Math.max(0, at.current - 48), at.current + 1), { place, open, thread }]
    at.current = trail.current.length - 1
  }, [place, open, thread])
  useEffect(() => {
    if (!active) return
    const step = (by: -1 | 1) => {
      const next = trail.current[at.current + by]
      if (!next) return
      at.current += by
      stepping.current = true
      navigate(next.place, next.open, next.thread)
    }
    const onMouse = (event: MouseEvent) => {
      if (event.button !== 3 && event.button !== 4) return
      event.preventDefault()
      if (event.type === 'mouseup') step(event.button === 3 ? -1 : 1)
    }
    const onKey = (event: KeyboardEvent) => {
      if (!event.altKey || event.ctrlKey || event.shiftKey || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) return
      event.preventDefault()
      step(event.key === 'ArrowLeft' ? -1 : 1)
    }
    window.addEventListener('mousedown', onMouse)
    window.addEventListener('mouseup', onMouse)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('mousedown', onMouse)
      window.removeEventListener('mouseup', onMouse)
      window.removeEventListener('keydown', onKey)
    }
  }, [active, navigate])
}

function useRememberWhere(home: DiscordHome | null, place: string, open: string | null, thread: DiscordChannel | null, leave: () => void) {
  useEffect(() => {
    if (open) {
      lastChannel.set(place, open)
      if (thread) lastThread.set(place, { id: thread.id, name: thread.name, parentId: open })
      else lastThread.delete(place)
    }
    saveWhere(place)
  }, [place, open, thread])
  const gone = !!home && place !== HOME && !home.guilds.some(g => g.id === place)
  useEffect(() => { if (gone) leave() }, [gone, leave])
}

/** The channel list the room holds: the server you are in (none at Home). */
function useShownList(place: string) {
  const listGuild = place !== HOME ? place : null
  const [list, setList] = useState<DiscordChannels | null>(() => shownLists.get(listGuild ?? '') ?? null)
  // Which server the list on screen belongs to. A server you just left keeps its list up (marked its own) until the new
  // server's answer lands, so the channel panel never flashes empty; the channel pick leaves `open` alone meanwhile.
  const listPlace = useRef(shownLists.has(listGuild ?? '') ? listGuild : null)
  // The same, for drawing: the channels drawn as a server's are only ever its own.
  const [listOf, setListOf] = useState(listPlace.current)
  const showList = useCallback((guildId: string | null, next: DiscordChannels | null) => {
    listPlace.current = guildId
    setListOf(guildId)
    setList(next)
  }, [])
  return { listGuild, list, setList, listPlace, listOf, showList }
}

// The server as the servers list has it (its name and notification settings), when it's in Discord's list.
const railGuild = (home: DiscordHome | null, id: string) => home?.guilds.find(g => g.id === id) ?? null

/** Signed in and connected: the band of places over Home, Messages, Servers or a server (DESIGN.md › Rooms › Discord). */
export function DiscordClient({ active }: { active: boolean }) {
  const [home, setHome] = useState<DiscordHome | null>(null)
  const [place, setPlace] = useState(where.place)
  const [landedOn, setLandedOn] = useState<string | null>(null)
  const { listGuild, list, setList, listPlace, listOf, showList } = useShownList(place)
  const [open, setOpen] = useState<string | null>(() => lastChannel.get(where.place) ?? null)
  // A thread or forum post open in the channel `open`; picking any channel (its parent too) closes it.
  const [thread, setThread] = useState<DiscordChannel | null>(() => savedThread(where.place, lastChannel.get(where.place) ?? null))
  const now = useRef({ place, list, listGuild })
  now.current = { place, list, listGuild }
  // A channel opened that the list doesn't have yet (just made, a thread, a jump): the list asked for it decides.
  const awaiting = useRef<{ id: string; list: DiscordChannels | null } | null>(null)

  // One read at a time, and one more after it for whatever changed meanwhile: a burst of Discord's changes (a server's
  // roles, channels and threads landing together) is two reads, not one each.
  const homeAsk = useRef({ busy: false, again: false })
  const refreshHome = useCallback(() => {
    const ask = homeAsk.current
    if (ask.busy) { ask.again = true; return }
    const read = () => {
      ask.busy = true
      ask.again = false
      host.discordHome().then(next => setHome(current => sameParts(current, next)))
        .catch((error: unknown) => report(error, 'DiscordClient.home'))
        .finally(() => { ask.busy = false; if (ask.again) read() })
    }
    read()
  }, [])

  // A late answer for a server you've since left doesn't replace the one you're in. A list awaited for a channel it may
  // still lack lands as a new object either way, so the wait for it ends.
  const listAsks = useRef(new Map<string, boolean>())
  const refreshChannels = useCallback((guildId: string) => {
    const asks = listAsks.current
    if (asks.has(guildId)) { asks.set(guildId, true); return }
    const read = () => {
      asks.set(guildId, false)
      host.discordChannels(guildId).then(answer => {
        const shown = now.current.listGuild === guildId ? now.current.list : shownLists.get(guildId)
        const next = awaiting.current ? answer : sameParts(shown, answer)
        keepList(guildId, next)
        if (now.current.listGuild === guildId) showList(guildId, next)
      }).catch((error: unknown) => report(error, 'DiscordClient.channels'))
        .finally(() => { const again = asks.get(guildId); asks.delete(guildId); if (again) read() })
    }
    read()
  }, [showList])

  /** Opens a place (a server or DMs) at a channel, or its last one; a thread opens inside its parent. */
  const navigate = useCallback((to: string, channelId: string | null, inThread: DiscordChannel | null = null) => {
    setLandedOn(null)
    const { place: current, list: shown } = now.current
    const target = channelId ?? lastChannel.get(to) ?? null
    setThread(inThread ?? (channelId ? null : savedThread(to, target)))
    if (to !== current) {
      setPlace(to)
      if (to !== HOME) {
        const cached = shownLists.get(to)
        // Never opened: the list already on screen stays (still marked the server it came from) until this one's answers.
        showList(cached ? to : listPlace.current, cached ?? shown)
      }
    }
    setOpen(target)
    // A channel the list to be shown doesn't have yet waits for the list asked for, not the server's first channel.
    const known = to === current ? shown : shownLists.get(to)
    if (to !== HOME && known && target && !isPage(target) && !known.channels.some(c => c.id === target)) {
      awaiting.current = { id: target, list: known }
      refreshChannels(to)
    }
  }, [refreshChannels, showList, listPlace])

  const toDms = useCallback(() => navigate(HOME, null), [navigate])
  useRememberWhere(home, place, open, thread, toDms)
  useBackForward(active, place, open, thread, navigate)

  useEffect(() => { refreshHome() }, [refreshHome])

  // Each open from outside (Ctrl+K, a notification, a jump) closes what floats over the room: by remounting their hosts,
  // Server Settings, profiles, menus and the image viewer.
  const [opened, setOpened] = useState(0)
  // The sidebar (it holds Server Settings) remounts only for opens from outside the room, so a #channel click keeps its scroll.
  const [fromOutside, setFromOutside] = useState(0)
  // Join a Server, open (true) or holding an invite link Discord refused, with its reason.
  const [adding, setAdding] = useState<boolean | { invite: string; message: string }>(false)

  // A clicked notification, Ctrl+K or a jump to a message opens its conversation, now or when the room first mounts.
  useEffect(() => {
    const show = (target: OpenTarget) => {
      setAdding(false)
      setOpened(n => n + 1)
      if (target.outside !== false) setFromOutside(n => n + 1)
      const inThread = target.parentId && target.channelId ? threadChannel({ id: target.channelId, name: target.name ?? '' }) : null
      navigate(target.guildId ?? HOME, inThread ? target.parentId! : target.channelId, inThread)
    }
    const waiting = takePendingOpen()
    if (waiting) show(waiting)
    const fromPalette = () => { const target = takePendingOpen(); if (target) show(target) }
    window.addEventListener('exo-discord-open', fromPalette)
    const offHost = onHostEvent('discord.open', target => { takePendingOpen(); show(target) })
    return () => { window.removeEventListener('exo-discord-open', fromPalette); offHost() }
  }, [navigate])

  useEffect(() => {
    if (!listGuild) { showList(null, null); return }
    // A server gone back to: its kept list shows at once while it is read again.
    const kept = shownLists.get(listGuild)
    if (listPlace.current !== listGuild && kept) showList(listGuild, kept)
    refreshChannels(listGuild)
  }, [listGuild, refreshChannels, showList, listPlace])

  useLiveLists(listGuild, home, refreshHome, refreshChannels, setHome, setList)
  useProfileReadAhead(guildOf(place))

  // A server opens on its last channel, or its first one with text; a thread's id opens the thread in its channel.
  useEffect(() => {
    // A kept list belongs to the server you left: it shows, but its channels are never picked as this server's.
    // (listOf, not the ref, so a list answered the same as the kept one still counts as this server's once it lands.)
    if (place === HOME || !list || listOf !== place) return
    if (open && (list.channels.some(c => c.id === open && c.kind !== 'category') || isPage(open))) { awaiting.current = null; return }
    const asThread = open ? list.threads?.find(t => t.id === open) : undefined
    if (asThread) { awaiting.current = null; setThread(threadChannel(asThread)); setOpen(asThread.parentId); return }
    if (awaiting.current && awaiting.current.id === open && awaiting.current.list === list) return
    awaiting.current = null
    const remembered = lastChannel.get(place)
    const texts = list.channels.filter(c => c.kind === 'text' && c.canRead !== false)
    setOpen(texts.find(c => c.id === remembered)?.id ?? texts[0]?.id ?? null)
  }, [place, list, listOf, open])

  const go = useCallback((next: string) => { if (next !== now.current.place) navigate(next, null) }, [navigate])
  const { tab, full, onTab, serverGo } = usePlaces(place, open, navigate)
  // Messages asked for before your conversations were read (or from elsewhere): the newest opens as soon as they are.
  const firstDm = home?.dms.find(d => !d.request && !d.spam)?.id ?? null
  useEffect(() => {
    if (place === HOME && open === MESSAGES && firstDm) { navigate(HOME, firstDm); setLandedOn(firstDm) }
  }, [place, open, firstDm, navigate])
  const places = useProfilePlaces(home, go)
  const meId = home?.me?.id ?? null
  const pick = (id: string) => navigate(place, id)

  // Who is speaking is followed by the rings themselves (useSpeaking), never by the whole room.
  const voice = useDiscordVoice({ speaking: false })
  const caller = voice.ringing ? home?.dms.find(d => d.id === voice.ringing) : undefined
  // The voice bar opens the call's own view: the voice channel in its server, or the DM.
  const openVoice = () => {
    const { guildId, channelId } = voice.voice
    if (channelId) navigate(guildId ?? HOME, channelId)
  }
  const channelName = useCallback((id: string) => list?.channels.find(c => c.id === id)?.name ?? null, [list])

  // An invite link clicked in a message joins the server (already in: opens it) at the invite's channel, as Discord's app
  // does; when Discord refuses (an expired invite, a captcha), Join a Server shows the link and why.
  useEffect(() => {
    const join = (event: Event) => {
      const invite = (event as CustomEvent<string>).detail
      const refused = (message?: string | null) => setAdding({ invite: `https://discord.gg/${invite}`, message: message ?? 'Discord did not take that invite.' })
      host.discordServer('join', { invite }).then(result => {
        if (!result.ok || !(result.guildId || result.channelId)) return refused(result.message)
        refreshHome()
        navigate(result.guildId ?? HOME, result.channelId ?? null)
      }).catch((error: unknown) => { report(error, 'DiscordClient.invite'); refused() })
    }
    window.addEventListener('exo-dc-invite', join)
    return () => window.removeEventListener('exo-dc-invite', join)
  }, [navigate, refreshHome])

  // A DM made elsewhere in the room (Add Friends to DM): open it in Direct messages.
  useEffect(() => {
    const show = (event: Event) => {
      navigate(HOME, (event as CustomEvent<string>).detail)
      refreshHome()
    }
    window.addEventListener('exo-dc-open-dm', show)
    return () => window.removeEventListener('exo-dc-open-dm', show)
  }, [navigate, refreshHome])

  // Start a Call from the person menu: their DM opens and rings, as its call button does.
  const call = useRef(voice.call)
  call.current = voice.call
  useEffect(() => {
    const ring = (event: Event) => {
      const channelId = (event as CustomEvent<string>).detail
      navigate(HOME, channelId)
      refreshHome()
      void call.current(channelId)
    }
    window.addEventListener('exo-dc-call-dm', ring)
    return () => window.removeEventListener('exo-dc-call-dm', ring)
  }, [navigate, refreshHome])

  // Join Voice from someone's profile: their voice channel opens and you join it, as Discord's card does.
  const join = useRef(voice.join)
  join.current = voice.join
  useEffect(() => {
    const go = (event: Event) => {
      const { guildId, channelId } = (event as CustomEvent<{ guildId: string; channelId: string }>).detail
      navigate(guildId, channelId)
      void join.current(guildId, channelId)
    }
    window.addEventListener('exo-dc-join-voice', go)
    return () => window.removeEventListener('exo-dc-join-voice', go)
  }, [navigate])

  // Message from a profile card: Discord's DM with that person, opened in Direct messages.
  const messageUser = useCallback((userId: string) => {
    void host.discordFriend('dm', { userId }).then(result => {
      if (!result.channelId) return
      navigate(HOME, result.channelId)
      refreshHome()
    }).catch(() => { })
  }, [navigate, refreshHome])

  // Discord's settings read the room's home.
  useEffect(() => shareDiscordHome(home), [home])
  const { settings, closeSettings } = useUserSettings()
  const [performance, setPerformance] = useState(performanceMode)
  useEffect(() => onPerformanceMode(() => setPerformance(performanceMode())), [])
  // Discord settings in effect: the account's, Streamer Mode, /tts, and the room's look.
  useEffect(startDiscordSettings, [])
  const look = useRoomLook()
  const { nav, join: joinVoice } = useServerNav(place, home, list, listOf, open, voice, navigate)
  const voiceChat = useVoiceChat()
  // A server's people are always beside its conversation (owner, 2026-11-14: no turning them off), but not beside a
  // call that shows its own chat.
  const people = place !== HOME && peopleRoom(place, list, open, thread, voiceChat)
  const atHome = place === HOME

  return (
    <ProfilePlaces.Provider value={places}>
    <main className={`exo-dc${performance ? ' is-performance' : ''}`} {...look} aria-label="Discord">
      <RoomHosts opened={opened} place={place} list={list} home={home} meId={meId} onMessage={messageUser} />
      {adding ? <AddServer refused={adding} onClose={() => setAdding(false)} onJoined={id => { setAdding(false); refreshHome(); go(id) }} /> : null}
      {settings ? <UserSettings key={settings.n} start={settings.section} onClose={closeSettings} /> : null}
      {caller ? <IncomingCall name={caller.name} onJoin={video => { navigate(HOME, caller.id); void voice.join(null, caller.id, video) }} onDecline={() => voice.decline(caller.id)} /> : null}
      <Band home={home} tab={tab} place={place} open={open} onTab={onTab} onServer={serverGo} onAdd={() => setAdding(true)} onPeek={peekPlace}
        inbox={<InboxButton meId={meId} place={guildOf(place)} open={open} channelName={channelName} />} />
      <ServerNavContext.Provider value={nav}>
      <div className="exo-dc-body" data-place={atHome ? 'home' : 'server'}>
        <LeftPane key={`${place}:${fromOutside}`} place={place} home={home} list={listOf === place ? list : null} open={open} thread={thread} voice={voice}
          onPick={pick} onJoin={id => joinVoice(place, id)}
          foot={<>
            <CallDock voice={voice} voicePlace={voicePlaceName(voice, list, home)} callIds={voiceCallIds(voice, list, home)} onOpenVoice={openVoice} />
            <MePanel home={home} voice={voice} />
          </>} />
        <div className={stageClass(full)} data-columns={columnsNow(place, people)}>
          <Conversation place={place} open={open} home={home} list={list} voice={voice} active={active} landedOn={landedOn} channelName={channelName} onPick={pick}
            thread={thread} onThread={setThread} voiceChat={voiceChat} />
          {!atHome && people ? (
            <PeopleColumn>
              <MemberList guildId={place} channelId={peopleChannel(place, listOf, list, open)} />
            </PeopleColumn>
          ) : null}
        </div>
      </div>
      </ServerNavContext.Provider>
    </main>
    </ProfilePlaces.Provider>
  )
}

/**
 * What floats over the room: profiles, menus, the image viewer, an app's modal, two-step sign-in and Report. Each open
 * from outside (`opened`: Ctrl+K, a notification, a jump) remounts them, which closes them.
 */
function RoomHosts({ opened, place, list, home, meId, onMessage }: {
  opened: number
  place: string
  list: DiscordChannels | null
  home: DiscordHome | null
  meId: string | null
  onMessage: (userId: string) => void
}) {
  return (
    <>
      <ProfileHost key={`p${opened}`} meId={meId} onMessage={onMessage} />
      <UserMenuHost key={`m${opened}`} place={place} list={list} home={home} />
      <ThreadMenuHost key={`t${opened}`} />
      <FullProfileHost key={`f${opened}`} meId={meId} onMessage={onMessage} />
      <ImageHost key={`i${opened}`} />
      <ImageMenuHost />
      <AppModalHost guildId={guildOf(place)} />
      <MfaHost />
      <ReportHost />
    </>
  )
}

/** Discord's settings sheet: opened at a section asked for before the room was up, then on each ask while it is. */
function useUserSettings() {
  const [settings, setSettings] = useState<{ section: DiscordSettingsSection; n: number } | null>(() => {
    const section = takeDiscordSettings()
    return section ? { section, n: 0 } : null
  })
  useEffect(() => {
    const open = () => { const section = takeDiscordSettings(); if (section) setSettings(now => ({ section, n: (now?.n ?? 0) + 1 })) }
    window.addEventListener('exo-discord-settings', open)
    return () => window.removeEventListener('exo-discord-settings', open)
  }, [])
  const closeSettings = useCallback(() => setSettings(null), [])
  return { settings, closeSettings }
}

/** The text channel a server's people are listed for (its members), when the list on screen is that server's. */
const peopleChannel = (place: string, listOf: string | null, list: DiscordChannels | null, open: string | null) =>
  listOf === place ? list?.channels.find(c => c.id === open && c.kind === 'text')?.id ?? null : null

/**
 * The left panel: your conversations at Home, the server's channels (its head, banner and rows) in a server; you, and a
 * call's rows, at its foot.
 */
function LeftPane({ place, home, list, open, thread, voice, onPick, onJoin, foot }: {
  place: string
  home: DiscordHome | null
  /** The server's channels, only when the list on screen is this server's. */
  list: DiscordChannels | null
  open: string | null
  thread: DiscordChannel | null
  voice: VoiceControls
  onPick: (id: string) => void
  onJoin: (id: string) => void
  foot: React.ReactNode
}) {
  const atHome = place === HOME
  const guild = atHome ? null : railGuild(home, place)
  return (
    <aside className="exo-dc-pane" aria-label={atHome ? 'Direct messages' : guild?.name ?? 'Channels'}>
      {atHome
        ? <MessagesPane home={home} open={open} onPick={onPick} />
        : <ChannelsPanel guildId={place} guild={guild} name={guild?.name ?? list?.name ?? ''} list={list} open={open} thread={thread?.id} voice={voice}
            onPick={onPick} onPreview={onPick} onJoin={onJoin} />}
      <div className="exo-dc-you">{foot}</div>
    </aside>
  )
}

const stageClass = (full: boolean) => `exo-dc-view${full ? ' is-full' : ''}`
/** What the stage leaves room for, in a server (its CSS); elsewhere it has no columns. */
const columnsNow = (place: string, people: boolean) => place === HOME ? undefined : people ? 'both' : 'channels'

/** Whether a server's people can show: no chat beside a call takes their place (as it always has). */
function peopleRoom(place: string, list: DiscordChannels | null, open: string | null, thread: DiscordChannel | null, voiceChat: VoiceChat) {
  const inVoice = !thread && serverChannel(place, list, open, 'voice')
  return !(inVoice && inVoice.canRead !== false && voiceChat.shown)
}

/**
 * What a server's title line needs (its channels are always in the list, never folded into the title; its name is the
 * server's own, never a list still showing from the one you left), and joining a voice channel of the server open in the list.
 */
function useServerNav(place: string, home: DiscordHome | null, list: DiscordChannels | null, listOf: string | null, open: string | null,
  voice: VoiceControls, navigate: (to: string, channelId: string | null) => void) {
  const join = (guildId: string, id: string) => {
    navigate(guildId, id)
    if (voice.voice.channelId !== id || voice.voice.state === 'off') void voice.join(guildId, id)
  }
  const name = railGuild(home, place)?.name ?? (listOf === place ? list?.name : null) ?? null
  const nav: ServerNav | null = useMemo(() => place === HOME ? null : { list, open, name }, [place, list, open, name])
  return { nav, join }
}

// The stage's page (not the people beside it).
const PAGE = '.exo-dc-view > :not(.exo-dc-beside)'

/**
 * Which place you are in and going to another. One picked in the band: the page and the left panel push from the side
 * it was picked on, while the band and a call's dock hold still.
 */
function usePlaces(place: string, open: string | null, navigate: (to: string, channelId: string | null) => void) {
  const now = useRef(place)
  now.current = place
  const push = useCallback((update: () => void) => {
    const band = visible('.exo-dc-band')
    const dock = visible('.exo-dc-dock')
    void transition(update, {
      kind: 'push',
      back: pressedBefore('.exo-dc-band [aria-current="true"]'),
      morph: [pane(visible(PAGE)), { name: 'exo-dc-band', from: band, to: () => band }, { name: 'exo-dc-dock', from: dock, to: () => visible('.exo-dc-dock') }],
    })
  }, [])
  const onTab = useCallback((to: PlaceTab) => push(() => navigate(HOME, to === 'shop' ? SHOP : NOW)), [navigate, push])
  const serverGo = useCallback((next: string) => {
    if (next !== now.current) push(() => navigate(next, null))
    else navigate(next, null)
  }, [navigate, push])
  const tab: PlaceTab = place !== HOME ? 'servers' : open === SHOP ? 'shop' : 'home'
  return { tab, full: place !== HOME, onTab, serverGo }
}

// Your servers and opening one, for the profiles' Member Since and Mutual Servers.
function useProfilePlaces(home: DiscordHome | null, openServer: (guildId: string) => void) {
  const guilds = home?.guilds
  return useMemo(() => ({ guilds: guilds ?? [], openServer }), [guilds, openServer])
}

// Lists stay live: changes refetch, unread marks patch in place.
function useLiveLists(
  /** The server whose channels the room holds (the one you are in, else the one open in the list). */
  listGuild: string | null,
  home: DiscordHome | null,
  refreshHome: () => void,
  refreshChannels: (guildId: string) => void,
  setHome: React.Dispatch<React.SetStateAction<DiscordHome | null>>,
  setList: React.Dispatch<React.SetStateAction<DiscordChannels | null>>,
) {
  // Read by the events, which stay subscribed while home changes under them.
  const homeNow = useRef(home)
  homeNow.current = home
  // Each patch hands back what showed when nothing in it changed: a presence or unread for someone or somewhere not on
  // screen (most of them, in a big server) redraws nothing.
  useEffect(() => {
    const offChanged = onHostEvent('discord.changed', event => {
      // A server's voice change shows in its channel list and on its rail mark (patched in place); a DM call's shows on Home.
      if (event.voice && event.guildId && event.inVoice !== undefined) {
        const { guildId, inVoice } = event
        setHome(current => current && current.guilds.some(g => g.id === guildId && g.inVoice !== inVoice)
          ? { ...current, guilds: current.guilds.map(g => g.id === guildId ? { ...g, inVoice } : g) } : current)
      }
      if (!(event.voice && event.guildId)) refreshHome()
      if (listGuild && (event.guildId === null || event.guildId === listGuild)) refreshChannels(listGuild)
    })
    // What a DM's row says changed: it says so, and a newer message brings it to the top as the host's list would.
    const offLine = onHostEvent('discord.dmLine', event => setHome(current => {
      if (!current) return current
      const at = current.dms.findIndex(d => d.id === event.channelId)
      if (at < 0 || (current.dms[at].line === event.line && (event.at === null || current.dms[at].lastAt === event.at))) return current
      const dm = { ...current.dms[at], line: event.line, lastAt: event.at ?? current.dms[at].lastAt }
      const rest = current.dms.filter((_, i) => i !== at)
      const place = rest.findIndex(d => (d.lastAt ?? 0) < (dm.lastAt ?? 0))
      return { ...current, dms: place < 0 ? [...rest, dm] : [...rest.slice(0, place), dm, ...rest.slice(place)] }
    }))
    const offUnread = onHostEvent('discord.unread', event => {
      setHome(current => {
        if (!current) return current
        const guilds = current.guilds.some(g => g.id === event.guildId && (g.unread !== event.guildUnread || g.mentions !== event.guildMentions))
          ? current.guilds.map(g => g.id === event.guildId ? { ...g, unread: event.guildUnread, mentions: event.guildMentions } : g) : current.guilds
        const sorted = event.guildId ? current.dms
          : sortDms(current.dms.map(d => d.id === event.channelId && (d.unread !== event.unread || d.mentions !== event.mentions) ? { ...d, unread: event.unread, mentions: event.mentions } : d), event)
        const dms = sorted.length === current.dms.length && sorted.every((d, i) => d === current.dms[i]) ? current.dms : sorted
        return guilds === current.guilds && dms === current.dms ? current : { ...current, guilds, dms }
      })
      setList(current => current && current.channels.some(c => c.id === event.channelId && (c.unread !== event.unread || c.mentions !== event.mentions))
        ? { ...current, channels: current.channels.map(c => c.id === event.channelId ? { ...c, unread: event.unread, mentions: event.mentions } : c) } : current)
      const shown = homeNow.current
      if (!event.guildId && shown && !shown.dms.some(d => d.id === event.channelId)) refreshHome()
    })
    const offPresence = onHostEvent('discord.presence', event => setHome(current => {
      if (!current) return current
      const me = current.me && event.userId === current.me.id && current.me.status !== event.status ? { ...current.me, status: event.status } : current.me
      // What they're doing is the DM's title line, and its row's line while nothing has been said.
      const subtitle = event.activity ?? null
      const dms = current.dms.some(d => d.userId === event.userId && (d.status !== event.status || (d.subtitle ?? null) !== subtitle))
        ? current.dms.map(d => d.userId === event.userId ? { ...d, status: event.status, subtitle } : d) : current.dms
      return me === current.me && dms === current.dms ? current : { ...current, me, dms }
    }))
    return () => { offChanged(); offLine(); offUnread(); offPresence() }
  }, [listGuild, refreshHome, refreshChannels, setHome, setList])
}

function sortDms(dms: DiscordDm[], event: { channelId: string; unread: boolean }) {
  if (!event.unread) return dms
  const at = dms.findIndex(d => d.id === event.channelId)
  return at <= 0 ? dms : [dms[at], ...dms.slice(0, at), ...dms.slice(at + 1)]
}

function NoChat({ home, loaded }: { home: boolean; loaded: boolean }) {
  return (
    <section className="exo-dc-chat is-empty">
      {home ? null : loaded ? <><header className="exo-dc-chat-head"><ChannelNav /></header><p className="exo-dc-note">No channel here you can read.</p></> : <MessagesWait label="Loading channels" rows={8} />}
    </section>
  )
}

// A server's open channel of one kind (never in Direct messages).
function serverChannel(place: string, list: DiscordChannels | null, open: string | null, kind: DiscordChannel['kind']) {
  return place === HOME ? undefined : list?.channels.find(c => c.id === open && c.kind === kind)
}

function openChannel(place: string, open: string | null, home: DiscordHome | null, list: DiscordChannels | null): DiscordChannel | null {
  if (place === HOME) {
    const dm = home?.dms.find(d => d.id === open)
    return dm ? dmChannel(dm) : null
  }
  return list?.channels.find(c => c.id === open && c.kind === 'text') ?? null
}

// Where the call is, as Discord's voice bar names it: "channel / server" from any server you're looking at, or the DM.
function voicePlaceName(voice: VoiceControls, list: DiscordChannels | null, home: DiscordHome | null): string | null {
  const { channelId, guildId } = voice.voice
  if (!channelId) return null
  if (!guildId) return home?.dms.find(d => d.id === channelId)?.name ?? null
  const channels = (list?.channels.some(c => c.id === channelId) ? list : shownLists.get(guildId))?.channels
  const name = channels?.find(c => c.id === channelId)?.name
  const server = home?.guilds.find(g => g.id === guildId)?.name
  return name ? (server ? `${name} / ${server}` : name) : server ?? null
}

// Who is in your call: the voice channel's people in its server's list, or the DM's call.
function voiceMembers(voice: VoiceControls, list: DiscordChannels | null, home: DiscordHome | null): DiscordVoiceMember[] {
  const { channelId, guildId, state } = voice.voice
  if (!channelId || state !== 'connected') return []
  const members = guildId
    ? (list?.channels.some(c => c.id === channelId) ? list : shownLists.get(guildId))?.channels.find(c => c.id === channelId)?.members
    : home?.dms.find(d => d.id === channelId)?.call
  return members ?? []
}

// Who is in your call, by id: the Jam box looks for their songs.
function voiceCallIds(voice: VoiceControls, list: DiscordChannels | null, home: DiscordHome | null): string[] {
  return voiceMembers(voice, list, home).map(member => member.id)
}

type ConversationProps = {
  /** The conversation landing on Messages opened. */
  landedOn: string | null
  place: string
  open: string | null
  home: DiscordHome | null
  list: DiscordChannels | null
  voice: VoiceControls
  active: boolean
  channelName: (id: string) => string | null
  onPick: (id: string) => void
  thread: DiscordChannel | null
  onThread: (thread: DiscordChannel | null) => void
  voiceChat: VoiceChat
}

// The stage's page: Home, a conversation, or what to do next.
function Conversation(props: ConversationProps) {
  const { place, open, home, list, active, channelName, onPick } = props
  const page = place === HOME ? homePage(props) : null
  if (page) return page
  if (place !== HOME && open && isPage(open)) return <ServerPage page={open} guildId={place} list={list} onPick={onPick} meId={home?.me?.id ?? null} meName={home?.me?.name ?? null} channelName={channelName} active={active} />
  return <ConversationIn {...props} />
}

// The pages of Direct messages' place that are not a conversation: Home, the Shop and Message requests.
function homePage({ open, home, onPick }: ConversationProps) {
  if (open === NOW || open === FRIENDS) return <HomePage onOpenDm={onPick} />
  if (open === SHOP) return <ShopPage me={home?.me ?? null} />
  if (open === REQUESTS) return <MessageRequestsPage home={home} onOpenDm={onPick} />
  return null
}

// A channel, a DM, or a forum and the post opened from it; a thread shows its channel above it to go back.
function ConversationIn({ place, open, home, list, voice, active, landedOn, channelName, thread, onThread, voiceChat }: ConversationProps) {
  const forum = serverChannel(place, list, open, 'forum')
  if (forum && !thread) return <ForumView key={forum.id} channel={forum} guildId={place} channelName={channelName} active={active} onOpen={onThread} />
  const voiceChannel = serverChannel(place, list, open, 'voice')
  if (voiceChannel && !thread) return <VoiceWithChat {...{ place, home, list, voice, active, landedOn, channelName, onThread, voiceChat }} channel={voiceChannel} />
  const parent = thread ? list?.channels.find(c => c.id === open) ?? null : null
  const channel = thread ?? openChannel(place, open, home, list)
  if (!channel) return <NoChat home={place === HOME} loaded={!!list} />
  return <ChannelChat {...{ place, home, list, voice, active, landedOn, channelName, onThread, channel, parent }} />
}

function readVoiceChat() {
  return uiPref('voiceChat') !== false
}
type VoiceChat = { shown: boolean; show: (next: boolean) => void }
function useVoiceChat(): VoiceChat {
  const [shown, setShown] = useState(readVoiceChat)
  useEffect(() => onUiPrefs(() => setShown(readVoiceChat())), [])
  const show = useCallback((next: boolean) => {
    setShown(next)
    setUiPref('voiceChat', next)
  }, [])
  return useMemo(() => ({ shown, show }), [shown, show])
}

// A voice or stage channel: the call, and its own text chat beside it as Discord's chat panel (not without Connect),
// which Hide Chat and Close Chat put away and Show Chat brings back, remembered.
function VoiceWithChat({ channel, voiceChat, ...props }: Omit<ConversationProps, 'open' | 'onPick' | 'thread'> & { channel: DiscordChannel }) {
  const { shown, show } = voiceChat
  const { place, voice, home } = props
  const readable = channel.canRead !== false
  const room = <VoiceRoom channel={channel} controls={voice} meId={home?.me?.id ?? null} onJoin={() => void voice.join(place, channel.id)}
    chat={readable ? { shown, onToggle: () => show(!shown) } : undefined} />
  if (!readable || !shown) return room
  return <div className="exo-dc-voice-split">{room}<ChannelChat {...props} channel={channel} parent={null} onHide={() => show(false)} /></div>
}

// A text channel, thread or DM; a DM's call shows above it.
function ChannelChat({ place, home, list, voice, active, landedOn, channelName, onThread, channel, parent, onHide }: Omit<ConversationProps, 'open' | 'onPick' | 'thread' | 'voiceChat'> & {
  channel: DiscordChannel
  parent: DiscordChannel | null
  onHide?: () => void
}) {
  const meId = home?.me?.id ?? null
  const dm = place === HOME
  const inCall = voice.voice.channelId === channel.id && voice.voice.state !== 'off'
  const callMembers = dm ? home?.dms.find(d => d.id === channel.id)?.call : undefined
  // A call already on (from your phone too): Call joins it, as Discord's Join Call does, without ringing everyone again.
  const onCall = (video: boolean) => void (callMembers?.length ? voice.join(null, channel.id, video) : voice.call(channel.id, video))
  return (
    <Chat key={channel.id} channel={channel} guildId={dm ? null : place} onOpenThread={onThread} meId={meId} channelName={channelName} active={active} quiet={landedOn === channel.id} onHide={onHide}
      me={home?.me ?? null} blocked={home?.blocked} ignored={home?.ignored} roles={list?.roles} parent={parent} onBack={() => onThread(null)}
      onCall={dm ? onCall : undefined} inCall={inCall} call={dm ? <DmCall channel={channel} members={callMembers} inCall={inCall} voice={voice} meId={meId} /> : null} />
  )
}

// A DM's call, as Discord shows it: under the DM's header while you're in it, or while it's on without you.
function DmCall({ channel, members, inCall, voice, meId }: {
  channel: DiscordChannel
  members: DiscordChannel['members']
  inCall: boolean
  voice: VoiceControls
  meId: string | null
}) {
  if (!inCall && !members?.length) return null
  return <VoiceRoom dm channel={{ ...channel, members: members ?? [] }} controls={voice} meId={meId} onJoin={() => void voice.join(null, channel.id)} />
}

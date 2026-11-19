import { Wait } from '../ui/Wait'
import { CopyLogs, ErrorNote } from '../ui/ErrorNote'
import { useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties } from 'react'
import { createPortal, flushSync } from 'react-dom'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { useLens } from '../../lib/glass'
import { cdnSized, openDiscord, type DiscordChannel, type DiscordVoice, type DiscordVoiceMember } from '../../lib/discord'
import { useVideoFeed } from '../../lib/discordVideo'
import { clearGoLiveMessage, startCamera, stopCamera, useCamera, useGoLiveValue } from '../../lib/goLive'
import { ChevronDown, ChevronUp, Close } from '../../brand/icons'
import { MyStreamTile, PanelStream, ShareScreen, useStreamTile } from './GoLivePicker'
import { AboveTip, anchorOf, type TipAnchor } from './AboveTip'
import { Icons } from './icons'
import { SoundboardButton } from './Soundboard'
import { setAudioUser, useAccountSetting } from '../../lib/discordPrefs'
import { Slider } from '../ui/Slider'
import { Decoration } from './collectibles'
import { Spinner } from '../ui/Spinner'
import { holdSpring, springHome } from '../../lib/spring'
import { ChannelNav } from './Channels'
import { pageGet, pageSet } from '../../lib/pageStore'
import { errorText } from '../../lib/errorWords'

const OFF: DiscordVoice = { state: 'off', guildId: null, channelId: null, muted: false, deafened: false, e2ee: false }

// Who is speaking, read once for every view of the call. Voices start and stop several times a second in a busy call:
// only what draws the rings (the voice list's people, the call's tiles, the Island's faces) redraws, never the room.
const NO_ONE: Set<string> = new Set()
let speakingNow: Set<string> = NO_ONE
const speakingListeners = new Set<() => void>()
let speakingWired = false
function setSpeaking(next: Set<string>) {
  if (next === speakingNow) return
  speakingNow = next
  speakingListeners.forEach(listener => listener())
}
function subscribeSpeaking(listener: () => void) {
  if (!speakingWired) {
    speakingWired = true
    onHostEvent('discord.speaking', ({ userId, speaking: on }) => {
      if (speakingNow.has(userId) === on) return
      const next = new Set(speakingNow)
      if (on) next.add(userId); else next.delete(userId)
      setSpeaking(next)
    })
    // Leaving, or moving straight to another channel (no 'off' between): no one from the last call is still speaking.
    let channel: string | null = null
    onHostEvent('discord.voice', next => {
      const now = next.state === 'off' ? null : next.channelId
      if (next.state === 'off' || now !== channel) setSpeaking(NO_ONE)
      channel = now
    })
  }
  speakingListeners.add(listener)
  return () => { speakingListeners.delete(listener) }
}
const noSpeaking = () => () => { }

/** Who in the call is speaking now. */
export function useSpeaking(): Set<string> {
  return useSyncExternalStore(subscribeSpeaking, () => speakingNow)
}

/**
 * The call this client is in, who is speaking, an incoming DM call, and the actions on them. speaking false: who is
 * speaking isn't followed here (the Discord room, whose rings follow it themselves through useSpeaking).
 */
export function useDiscordVoice({ speaking: follow = true }: { speaking?: boolean } = {}) {
  const [voice, setVoice] = useState<DiscordVoice>(OFF)
  const speaking = useSyncExternalStore(follow ? subscribeSpeaking : noSpeaking, () => follow ? speakingNow : NO_ONE)
  const [ringing, setRinging] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // A call whose camera goes on once it connects: Start Video Call, Join with Video.
  const cameraFor = useRef<string | null>(null)
  useEffect(() => {
    if (!cameraFor.current || voice.state !== 'connected' || voice.channelId !== cameraFor.current) return
    cameraFor.current = null
    void startCamera().then(why => { if (why) setError(why) })
  }, [voice.state, voice.channelId])

  useEffect(() => {
    // A call that started ringing before this mounted (the room opened from its notification) shows too.
    void host.discordVoice('state').then(result => {
      if (!result.voice) return
      setVoice(result.voice)
      const rung = result.voice.ringing
      if (rung) setRinging(current => current ?? rung)
    }).catch(() => { })
    const offVoice = onHostEvent('discord.voice', next => {
      setVoice(next)
      // The call ended on its own (a join Discord never answered, a connection that gave up): say why.
      if (next.state === 'off' && next.message) setError(next.message)
    })
    const offCall = onHostEvent('discord.call', call => setRinging(current => call.ringing ? call.channelId : current === call.channelId ? null : current))
    return () => { offVoice(); offCall() }
  }, [])

  const run = useCallback(async (action: Parameters<typeof host.discordVoice>[0], params?: Parameters<typeof host.discordVoice>[1]) => {
    setError(null)
    try {
      const result = await host.discordVoice(action, params)
      if (result.voice) setVoice(result.voice)
      if (!result.ok) setError(errorText(result.message, 'Discord voice did not answer.'))
      return result.ok
    } catch (err) {
      report(err, 'DiscordVoice.' + action)
      setError('Discord voice did not answer.')
      return false
    }
  }, [])

  return {
    voice, speaking, ringing, error,
    /** video: the camera goes on as it connects (Join with Video). */
    join: async (guildId: string | null, channelId: string, video = false) => {
      setRinging(null)
      cameraFor.current = video ? channelId : null
      const ok = await run('join', { guildId, channelId })
      if (!ok) cameraFor.current = null
      return ok
    },
    /** A DM call that rings the others, as Discord's Start Voice Call; video: Start Video Call, the camera on as it connects. */
    call: async (channelId: string, video = false) => {
      cameraFor.current = video ? channelId : null
      if (await run('join', { guildId: null, channelId })) void host.discordRing(channelId).catch(() => { })
      else cameraFor.current = null
    },
    leave: () => run('leave'),
    setMuted: (muted: boolean) => run('set', { muted }),
    setDeafened: (deafened: boolean) => run('set', { deafened }),
    /** Decline: Discord stops ringing you here and in your other apps. */
    decline: (channelId: string) => {
      setRinging(null)
      void host.discordDecline(channelId).catch((err: unknown) => report(err, 'DiscordVoice.decline'))
    },
    dismissError: () => setError(null),
  }
}

export type VoiceControls = ReturnType<typeof useDiscordVoice>

// The one mark Discord shows after a person in a voice channel: deafened (by the server, or themselves), muted (by
// the server, themselves, or you), most serious first; the server's own in red.
function VoiceFlag({ member, mutedByYou }: { member: DiscordVoiceMember; mutedByYou: boolean }) {
  const flag = member.serverDeaf ? ['Server deafened', 'is-server', true] as const : member.deaf ? ['Deafened', '', true] as const
    : member.serverMute ? ['Server muted', 'is-server', false] as const : member.mute ? ['Muted', '', false] as const
      : mutedByYou ? ['Muted by you', 'is-yours', false] as const : null
  if (!flag) return null
  const [label, kind, deaf] = flag
  return <span className={`exo-dc-voice-flag ${kind}`} role="img" aria-label={label}>{deaf ? <Icons.Deafened size={14} /> : <Icons.Muted size={14} />}</span>
}

/**
 * Who is in a voice channel, under it in the list, as Discord shows them: a ring while someone speaks (not someone
 * you muted), LIVE while they stream, a camera while it's on, and their mute or deafen mark.
 */
export function VoiceMembers({ members }: { members: DiscordVoiceMember[] }) {
  const audio = useAccountSetting('audioUsers')
  const speaking = useSpeaking()
  return (
    <ul className="exo-dc-voice-members">
      {members.map(member => {
        const mutedByYou = audio[member.id]?.muted === true
        return (
          <li key={member.id} className={speaking.has(member.id) && !member.mute && !mutedByYou ? 'is-speaking' : undefined} data-dc-user={member.id} data-dc-name={member.name} data-dc-call="">
            {member.avatar ? <img src={cdnSized(member.avatar, 22)} alt="" width={22} height={22} draggable={false} loading="lazy" decoding="async" /> : <span className="exo-dc-voice-dot" />}
            <span className="exo-dc-item-name">{member.name}</span>
            {member.stream ? <span className="exo-dc-voice-live">LIVE</span> : null}
            {member.video ? <span className="exo-dc-voice-flag" role="img" aria-label="Camera on"><Icons.Camera size={14} /></span> : null}
            <VoiceFlag member={member} mutedByYou={mutedByYou} />
          </li>
        )
      })}
    </ul>
  )
}

// Discord's volume curve: its slider moves in steps you hear as even (amplitude^(1/2.8) below 100, +6 dB at 200),
// what plays is the amplitude; within one step of 100 it is 100.
const toSlider = (volume: number) => volume <= 0 ? 0 : (volume < 100 ? Math.pow(volume / 100, 1 / 2.8) : 20 * Math.log10(volume / 100) / 6 + 1) * 100
const fromSlider = (at: number) => at <= 0 ? 0 : Math.abs(at - 100) < 1 ? 100 : (at < 100 ? Math.pow(at / 100, 2.8) : Math.pow(10, (at / 100 - 1) * 6 / 20)) * 100

/**
 * Discord's call items in a person's menu: User Volume (0-200%) and Mute, only for you, heard at once and kept on
 * the account; while you watch their Go Live, its Stream Volume and Mute Stream too, as in Discord (User Volume is
 * their voice alone, so it never moved the stream's sound); on yourself, your own Mute and Deafen.
 */
export function CallPersonItems({ userId, me }: { userId: string; me: boolean }) {
  const audio = useAccountSetting('audioUsers')[userId] ?? { muted: false, volume: 100 }
  const sound = useAccountSetting('audioStreams')[userId] ?? { muted: false, volume: 100 }
  const watched = useWatchedIds().includes(userId)
  const own = useDiscordVoice({ speaking: false }).voice
  if (me) {
    return (
      <>
        <span className="exo-dc-menu-line" role="separator" />
        <MenuCheck label="Mute" on={own.muted} onToggle={() => void host.discordVoice('set', { muted: !own.muted })} />
        <MenuCheck label="Deafen" on={own.deafened} onToggle={() => void host.discordVoice('set', { deafened: !own.deafened })} />
      </>
    )
  }
  return (
    <>
      <span className="exo-dc-menu-line" role="separator" />
      <VolumeRow label="User volume" volume={audio.volume} onChange={volume => setAudioUser(userId, { volume })} />
      {watched ? <VolumeRow label="Stream volume" volume={sound.volume} disabled={sound.muted} onChange={volume => setAudioUser(userId, { volume }, true)} /> : null}
      <span className="exo-dc-menu-line" role="separator" />
      <MenuCheck label="Mute" on={audio.muted} onToggle={() => setAudioUser(userId, { muted: !audio.muted })} />
      {watched ? <MenuCheck label="Mute stream" on={sound.muted} onToggle={() => setAudioUser(userId, { muted: !sound.muted }, true)} /> : null}
    </>
  )
}

// A volume row of the person menu: 0-200% on Discord's curve.
function VolumeRow({ label, volume, disabled, onChange }: { label: string; volume: number; disabled?: boolean; onChange: (volume: number) => void }) {
  const at = toSlider(volume)
  return (
    <div className="exo-dc-ctx-volume" role="group" aria-label={label}>
      <span>{label}</span>
      <span className="exo-dc-ctx-volume-row">
        <Slider min={0} max={200} value={at} aria-label={label} disabled={disabled} onChange={event => onChange(fromSlider(Number(event.target.value)))} />
        <span>{Math.round(at)}%</span>
      </span>
    </div>
  )
}

// A checkbox row, as the person menu draws them.
function MenuCheck({ label, on, onToggle }: { label: string; on: boolean; onToggle: () => void }) {
  return (
    <button type="button" role="menuitemcheckbox" aria-checked={on} onClick={onToggle}>
      {label}{on ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
    </button>
  )
}

/**
 * A voice channel opened from the list, as Discord shows it: everyone in it as a tile (a ring while
 * they speak, their mute and deafen), and under them the call's controls in one floating bar: mute,
 * deafen and disconnect, or Join Voice when you aren't in it. A DM's call (dm) is the same stage
 * above the conversation, under the DM's own header.
 */
// The streams you watch outlive the call's page, as Discord's do: leave for another server and come back, and they
// are still on (the host never stopped them). As in Discord you watch as many as you like: one is big above the call
// (the last you started or clicked), the others are tiles among the people. Each stops when it ends, when you stop
// watching it, or when you leave the call (MiniStream sees to that from outside the room).
type Watched = { channelId: string; userIds: string[]; focus: string | null }
let watchedStreams: Watched | null = null
// The big stream on screen now (its stage shown in the Discord room): everywhere else the mini player stands in for it.
let staged: string | null = null
const watchedListeners = new Set<() => void>()
const subscribeWatched = (listener: () => void) => { watchedListeners.add(listener); return () => { watchedListeners.delete(listener) } }
function setWatchedStreams(next: Watched | null) {
  watchedStreams = next
  watchedListeners.forEach(listener => listener())
}
function setStaged(next: string | null) {
  staged = next
  watchedListeners.forEach(listener => listener())
}
// One stream no longer watched: the big one passes to the stream watched last.
function dropWatched(userId: string) {
  if (!watchedStreams) return
  const userIds = watchedStreams.userIds.filter(id => id !== userId)
  setWatchedStreams(userIds.length ? { ...watchedStreams, userIds, focus: watchedStreams.focus === userId ? userIds[userIds.length - 1] : watchedStreams.focus } : null)
}
function stopWatching(userId: string) {
  dropWatched(userId)
  void host.discordStream('stop', userId).catch(() => { })
}

/** Whose Go Live you watch now: a person's menu adds Stream volume for them, and the Island keeps the call read for the mini player. */
export function useWatchedIds() {
  return useSyncExternalStore(subscribeWatched, () => watchedStreams)?.userIds ?? []
}

// Whose streams you watch in this call, which one is big, and Watch Stream and Stop Watching.
function useWatching(channelId: string, members: DiscordVoiceMember[]) {
  const current = useSyncExternalStore(subscribeWatched, () => watchedStreams)
  const mine = current?.channelId === channelId ? current : null
  const watching = mine?.userIds ?? []
  const watch = (userId: string) => {
    const others = watchedStreams?.channelId === channelId ? watchedStreams.userIds.filter(id => id !== userId) : []
    setWatchedStreams({ channelId, userIds: [...others, userId], focus: userId })
    void host.discordStream('watch', userId).then(result => { if (!result.ok) dropWatched(userId) }).catch(() => dropWatched(userId))
  }
  const stop = stopWatching
  const focus = (userId: string) => { if (watchedStreams) setWatchedStreams({ ...watchedStreams, focus: userId }) }
  const big = mine?.focus ? members.find(m => m.id === mine.focus && m.stream) : undefined
  const small = watching.filter(id => id !== big?.id).map(id => members.find(m => m.id === id && m.stream)).filter((m): m is DiscordVoiceMember => !!m)
  return { watching, big, small, watch, stop, focus }
}

export function VoiceRoom({ channel, controls, meId, onJoin, dm = false, chat }: {
  channel: DiscordChannel
  controls: VoiceControls
  meId: string | null
  onJoin: () => void
  dm?: boolean
  /** The channel's text chat beside the call: whether it shows, and Discord's Show Chat / Hide Chat. */
  chat?: { shown: boolean; onToggle: () => void }
}) {
  const { voice, error } = controls
  const speaking = useSpeaking()
  const here = voice.channelId === channel.id && voice.state !== 'off'
  const members = channel.members ?? []
  // Only why a stream ended: its numbers, a second apart, are the stream tile's.
  const liveEnded = useGoLiveValue(live => live.state === 'off' ? live.message : null)
  const [cameraError, setCameraError] = useState<string | null>(null)
  const notes = [error, cameraError, liveEnded].filter((note): note is string => !!note)
  // What went wrong with the call says so in the call itself, over its bar (where its buttons are), not in a notice;
  // its × clears it.
  const dismissNotes = () => { controls.dismissError(); setCameraError(null); clearGoLiveMessage() }
  const { watching, big, small, watch, stop, focus } = useWatching(channel.id, members)
  // Discord's Hide Members while watching: the stream takes the room, the people strip folds away.
  const [membersHidden, setMembersHidden] = useState(false)
  const myStream = useStreamTile() && here
  const [stage, tileWidth] = useTileWidth(members.length + small.length + (myStream ? 1 : 0))
  const audio = useAccountSetting('audioUsers')
  return (
    <section className={dm ? 'exo-dc-voice-room is-dm' : 'exo-dc-chat exo-dc-voice-room'} aria-label={dm ? `Call with ${channel.name}` : channel.name}>
      {dm ? null : <VoiceHead chat={chat} count={members.length} state={here ? voice.state : null} />}
      {big ? <StreamStage key={big.id} member={big} onStop={() => stop(big.id)} onRetry={() => watch(big.id)} membersHidden={membersHidden} onToggleMembers={() => setMembersHidden(h => !h)} /> : null}
      <div ref={stage} className="exo-dc-voice-stage" hidden={!!big && membersHidden} style={{ '--tile-w': `${tileWidth}px` } as CSSProperties}>
        {/* Joining: your tile on its way, shining in its place (DESIGN.md › Waits). */}
        {members.length === 0 ? here ? <Wait label="Connecting to voice"><span className="exo-dc-voice-tile" skullet-block="" /></Wait> : (
          <div className="exo-dc-voice-empty">
            <span className="exo-dc-voice-empty-icon" aria-hidden="true"><Icons.Voice size={28} /></span>
            <strong>{channel.name}</strong>
            <span>No one is here yet.</span>
          </div>
        ) : members.map(member => (
          <VoiceTile key={member.id} member={member} me={member.id === meId} speaking={speaking.has(member.id) && !audio[member.id]?.muted} muted={member.mute || audio[member.id]?.muted === true} canWatch={here && member.stream === true && !watching.includes(member.id) && member.id !== meId}
            onWatch={() => watch(member.id)} />
        ))}
        {members.length > 0 ? small.map(member => <StreamTile key={`stream-${member.id}`} member={member} onFocus={() => focus(member.id)} onStop={() => stop(member.id)} />) : null}
        {myStream && members.length > 0 ? <MyStreamTile meId={meId} members={members} /> : null}
      </div>
      {notes.length > 0 ? (
        <div className="exo-dc-voice-note">
          <ErrorNote message={notes.join(' ')} />
          <button type="button" className="exo-dc-icon-btn" aria-label="Dismiss" onClick={dismissNotes}><Icons.Close size={16} /></button>
        </div>
      ) : null}
      <VoiceBar here={here} dm={dm} channel={channel} controls={controls} onJoin={onJoin} onCameraError={setCameraError} />
    </section>
  )
}

/**
 * The call's bar, glass like the Bar's capsules and Music's player (its edge bends what passes under it): the call's
 * buttons, or out of the call the Join button itself, one glass capsule rather than a button inside one.
 */
function VoiceBar({ here, dm, channel, controls, onJoin, onCameraError }: {
  here: boolean
  dm: boolean
  channel: DiscordChannel
  controls: VoiceControls
  onJoin: () => void
  onCameraError: (why: string | null) => void
}) {
  const bar = useRef<HTMLElement>(null)
  useLens(bar)
  return (
    <footer ref={bar} className={`exo-dc-voice-bar exo-glass${here ? '' : ' is-join'}`}>
      {here ? <CallButtons controls={controls} channelId={channel.id} onCameraError={onCameraError} /> : (
        <button type="button" className="exo-dc-voice-join" disabled={channel.canConnect === false} onClick={onJoin}>{dm ? 'Join call' : 'Join voice'}</button>
      )}
    </footer>
  )
}

// A voice channel's bar: where you are, who is here, whether you are connected, and Show Chat / Hide Chat.
function VoiceHead({ chat, count, state }: { chat?: { shown: boolean; onToggle: () => void }; count: number; state: DiscordVoice['state'] | null }) {
  return (
    <header className="exo-dc-chat-head">
      <ChannelNav />
      <span className="exo-dc-voice-here">{count === 0 ? 'Nobody here' : `${count} here`}</span>
      {state ? <span className={`exo-dc-voice-state${state === 'connected' ? ' is-on' : ''}`}>{state === 'connected' ? 'Voice connected' : 'Connecting'}</span> : null}
      {chat ? (
        <span className="exo-dc-head-tools">
          <button type="button" className="exo-dc-icon-btn" aria-label={chat.shown ? 'Hide chat' : 'Show chat'} aria-pressed={chat.shown} onClick={chat.onToggle}>
            <Icons.Chat size={20} />
          </button>
        </span>
      ) : null}
    </header>
  )
}

/** The call bar's controls while you're in the call, grouped mic · deafen · camera | share · activities | leave; devices live in Discord's settings › Discord › Voice. */
function CallButtons({ controls, channelId, onCameraError }: { controls: VoiceControls; channelId: string; onCameraError: (why: string | null) => void }) {
  const { voice } = controls
  return (
    <>
      <button type="button" className={`exo-dc-voice-btn${voice.muted ? ' is-off' : ''}`} aria-pressed={voice.muted} aria-label={voice.muted ? 'Unmute' : 'Mute'}
        onClick={() => void controls.setMuted(!voice.muted)}>{voice.muted ? <Icons.Muted size={20} /> : <Icons.Mic size={20} />}<span className="exo-dc-voice-word">{voice.muted ? 'Unmute' : 'Mute'}</span></button>
      <button type="button" className={`exo-dc-voice-btn${voice.deafened ? ' is-off' : ''}`} aria-pressed={voice.deafened} aria-label={voice.deafened ? 'Undeafen' : 'Deafen'}
        onClick={() => void controls.setDeafened(!voice.deafened)}>{voice.deafened ? <Icons.Deafened size={20} /> : <Icons.Headphones size={20} />}<span className="exo-dc-voice-word">{voice.deafened ? 'Undeafen' : 'Deafen'}</span></button>
      <CameraButton onError={onCameraError} />
      <span className="exo-dc-voice-sep" aria-hidden="true" />
      <ShareScreen />
      <SoundboardButton guildId={voice.guildId} channelId={channelId} />
      <span className="exo-dc-voice-sep" aria-hidden="true" />
      <button type="button" className="exo-dc-voice-btn is-hangup" aria-label="Disconnect" onClick={() => void controls.leave()}><Icons.HangUp size={20} /><span className="exo-dc-voice-word">Leave</span></button>
    </>
  )
}

/** The widest 16:9 tile that lets everyone fit the stage at once (FaceTime's grid); one person never grows past 1280. */
function fitTiles(width: number, height: number, count: number, gap = 12) {
  let best = 0
  for (let columns = 1; columns <= count; columns++) {
    const rows = Math.ceil(count / columns)
    best = Math.max(best, Math.min((width - gap * (columns - 1)) / columns, (height - gap * (rows - 1)) / rows * 16 / 9))
  }
  return Math.max(120, Math.floor(Math.min(best, 1280)))
}

function useTileWidth(count: number) {
  const stage = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const element = stage.current
    if (!element) return
    const measure = () => {
      const style = getComputedStyle(element)
      const inner = (size: number, a: string, b: string) => size - parseFloat(a) - parseFloat(b)
      setWidth(fitTiles(inner(element.clientWidth, style.paddingLeft, style.paddingRight), inner(element.clientHeight, style.paddingTop, style.paddingBottom), Math.max(1, count)))
    }
    measure()
    const sizes = new ResizeObserver(measure)
    sizes.observe(element)
    return () => sizes.disconnect()
  }, [count])
  return [stage, width] as const
}

/** One person in the call: their camera when it's on, else their picture over its own blurred light; LIVE and Watch while they stream. */
// muted: by the server, themselves, or you.
function VoiceTile({ member, me, speaking, muted, canWatch, onWatch }: { member: DiscordVoiceMember; me: boolean; speaking: boolean; muted: boolean; canWatch: boolean; onWatch: () => void }) {
  const camera = useVideoFeed('call', member.video && !me ? member.id : null)
  // Discord doesn't send this client its own camera: show the local picture instead.
  const own = useCamera()
  const mine = me ? own.preview : null
  const showing = mine !== null || (member.video === true && camera.live)
  return (
    <article className={`exo-dc-voice-tile${speaking ? ' is-speaking' : ''}${showing ? ' has-video' : ''}`} data-dc-user={member.id} data-dc-name={member.name} data-dc-call="">
      {mine ? <LocalVideo stream={mine} /> : member.video && !me ? <video ref={camera.ref} className="exo-dc-voice-video" hidden={!showing} muted playsInline disablePictureInPicture /> : null}
      {/* The backdrop is the face blurred over the tile: the face's own picture, decoded once for both. */}
      {showing || !member.avatar ? null : <img className="exo-dc-voice-backdrop" src={cdnSized(member.avatar, 128)} alt="" aria-hidden="true" draggable={false} decoding="async" />}
      {showing ? null : member.avatar ? <img className="exo-dc-voice-face" src={cdnSized(member.avatar, 128)} alt="" draggable={false} decoding="async" /> : <span className="exo-dc-voice-tile-initial">{member.name.slice(0, 1).toUpperCase()}</span>}
      {showing ? null : <Decoration look={member.look} size={128} />}
      {member.stream ? <span className="exo-dc-live">LIVE</span> : null}
      {canWatch ? <button type="button" className="exo-dc-watch" onClick={onWatch}>Watch stream</button> : null}
      <span className="exo-dc-voice-tile-name">
        {member.deaf ? <Icons.Deafened size={14} /> : muted ? <Icons.Muted size={14} /> : null}
        <span className="exo-dc-voice-tile-label">{member.name}</span>
      </span>
    </article>
  )
}

/** A stream you watch beside the big one, as a tile among the people: click it to make it big; Stop Watching on it. */
function StreamTile({ member, onFocus, onStop }: { member: DiscordVoiceMember; onFocus: () => void; onStop: () => void }) {
  const stream = useVideoFeed('stream', member.id)
  return (
    <article className="exo-dc-voice-tile has-video exo-dc-stream-tile" data-dc-user={member.id} data-dc-name={member.name}>
      <video ref={stream.ref} className="exo-dc-voice-video" hidden={!stream.live} muted playsInline disablePictureInPicture />
      <button type="button" className="exo-dc-stream-tile-focus" aria-label={`Focus ${member.name}'s stream`} onClick={onFocus} />
      <button type="button" className="exo-dc-voice-btn is-hangup exo-dc-stream-tile-stop" aria-label="Stop watching" onClick={onStop}><Icons.StopWatching size={16} /></button>
      <span className="exo-dc-voice-tile-name"><span className="exo-dc-voice-tile-label">{member.name}</span></span>
    </article>
  )
}

// Where the mini player sits: the window corner it keeps to, how far it is from that corner's edges, and its width.
// What isn't set is the CSS default (the bottom right, the window's gap in, 18vw wide).
type Place = { left: boolean; top: boolean; x?: number; y?: number; w?: number }
const PLACE_KEY = 'exo.discord.miniStream'
// Movement under this is a press (back to the call), not a drag.
const DRAG_PX = 4
// A drag let go carries on for this long at its speed, and stays where that takes it.
const THROW_MS = 200
// Its name, LIVE and Stop watching still fit.
const MIN_W = 240

function savedPlace(): Place {
  const size = (value: unknown) => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined
  try {
    const saved: unknown = JSON.parse(pageGet(PLACE_KEY) ?? 'null')
    if (saved && typeof saved === 'object' && 'left' in saved && 'top' in saved) {
      const { left, top, x, y, w } = saved as Record<string, unknown>
      return { left: left === true, top: top === true, x: size(x), y: size(y), w: size(w) }
    }
  } catch { /* a convenience */ }
  return { left: false, top: false }
}

// Inside the window, under the Bar, the window's gap in all round (as discord.css › .exo-mini-stream has it).
function bounds() {
  const css = getComputedStyle(document.documentElement)
  const gap = parseFloat(css.getPropertyValue('--exo-gap')) || 10
  const bar = parseFloat(css.getPropertyValue('--exo-titlebar-h')) || 52
  return { left: gap, top: bar + gap, right: innerWidth - gap, bottom: innerHeight - gap }
}
const widest = () => { const b = bounds(); return Math.max(MIN_W, Math.min(innerWidth * 0.6, (b.bottom - b.top) * 16 / 9)) }
const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), Math.max(min, max))
// Past an edge it gives a little, against growing resistance, and glides back when let go.
const give = (value: number, min: number, max: number) => value < min ? min - Math.sqrt(min - value) * 3 : value > max ? max + Math.sqrt(value - max) * 3 : value

// The corner nearest its middle, and its offsets from that corner's edges.
function placeOf(box: { left: number; top: number; width: number; height: number }, w: number | undefined): Place {
  const left = box.left + box.width / 2 < innerWidth / 2
  const top = box.top + box.height / 2 < innerHeight / 2
  return { left, top, x: Math.round(left ? box.left : innerWidth - box.left - box.width), y: Math.round(top ? box.top : innerHeight - box.top - box.height), w }
}

// A move: grabbed at (grabX, grabY) of the tile laid out at (baseX, baseY); a resize: from a corner (grip), the
// opposite corner (fixX, fixY) held still.
type Drag = { id: number; startX: number; startY: number; grabX: number; grabY: number; baseX: number; baseY: number; dragging: boolean
  at: number; lastX: number; lastY: number; vx: number; vy: number; grip?: string; fixX: number; fixY: number; w: number; h: number
  b: ReturnType<typeof bounds> }

/**
 * The stream you watch while its call isn't on screen (another room, Settings over it, another conversation): its tile
 * floats over the window, as Discord's picture in picture, its sound playing as before. A press goes back to the call
 * with it big; a drag moves it anywhere in the window, a corner resizes it, and arrows (+ and -) do both while it has
 * focus; where it is and how big are kept. The Island holds it, always mounted, so it also stops a stream that ended
 * and every stream of a call you left, wherever you are.
 */
export function MiniStream({ voice, members, inDiscord, onOpen }: {
  voice: DiscordVoice
  /** Who is in the call and who streams (the Island's read); empty while it loads. */
  members: DiscordVoiceMember[]
  inDiscord: boolean
  onOpen: () => void
}) {
  const current = useSyncExternalStore(subscribeWatched, () => watchedStreams)
  const big = useSyncExternalStore(subscribeWatched, () => staged)
  const call = current && voice.state !== 'off' && voice.channelId === current.channelId ? current : null
  const left = current !== null && call === null
  const ended = call && members.length > 0 ? call.userIds.filter(id => !members.some(m => m.id === id && m.stream)).join() : ''
  useEffect(() => {
    if (left) { setWatchedStreams(null); void host.discordStream('stop').catch(() => { }); return }
    for (const id of ended ? ended.split(',') : []) stopWatching(id)
  }, [left, ended])
  const [place, setPlace] = useState(savedPlace)
  const drag = useRef<Drag | null>(null)
  const member = call?.focus ? members.find(m => m.id === call.focus && m.stream) : undefined
  if (!call || !member || (inDiscord && big)) return null

  const open = () => {
    openDiscord({ guildId: voice.guildId, channelId: call.channelId, outside: !inDiscord })
    onOpen()
  }
  const keep = (next: Place) => {
    try { pageSet(PLACE_KEY, JSON.stringify(next)) } catch { /* a convenience */ }
    flushSync(() => setPlace(next))
  }
  const press = (event: React.PointerEvent<HTMLDivElement>) => {
    const target = event.target as Element
    if (event.button !== 0 || target.closest('.exo-dc-stream-tile-stop')) return
    const element = event.currentTarget
    const grip = target.closest<HTMLElement>('[data-grip]')?.dataset.grip
    // A glide still under way is caught where it is on screen.
    const [tx, ty] = holdSpring(element)
    const box = element.getBoundingClientRect()
    const { clientX, clientY, timeStamp } = event
    drag.current = {
      id: event.pointerId, startX: clientX, startY: clientY, grabX: clientX - box.left, grabY: clientY - box.top, baseX: box.left - tx, baseY: box.top - ty,
      dragging: !!grip, at: timeStamp, lastX: clientX, lastY: clientY, vx: 0, vy: 0,
      grip, fixX: grip?.endsWith('l') ? box.right : box.left, fixY: grip?.startsWith('t') ? box.bottom : box.top, w: box.width, h: box.height, b: bounds(),
    }
    // Held from the press: a quick drag whose first step already leaves the tile still moves it, and the click that
    // follows lands on the tile itself, not on its press-to-open (a press that didn't move opens the call on release).
    element.setPointerCapture(event.pointerId)
  }
  const move = (event: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current
    if (!d || d.id !== event.pointerId) return
    const element = event.currentTarget
    const { clientX, clientY, timeStamp } = event
    if (!d.dragging) {
      if (Math.hypot(clientX - d.startX, clientY - d.startY) < DRAG_PX) return
      d.dragging = true
    }
    const { b } = d
    if (d.grip) {
      // 16:9 from the corner held still to the pointer, as far as the window lets it grow.
      const roomX = d.grip.endsWith('l') ? d.fixX - b.left : b.right - d.fixX
      const roomY = d.grip.startsWith('t') ? d.fixY - b.top : b.bottom - d.fixY
      d.w = clamp(Math.max(Math.abs(clientX - d.fixX), Math.abs(clientY - d.fixY) * 16 / 9), MIN_W, Math.min(widest(), roomX, roomY * 16 / 9))
      const h = d.w * 9 / 16
      Object.assign(element.style, { left: `${d.grip.endsWith('l') ? d.fixX - d.w : d.fixX}px`, top: `${d.grip.startsWith('t') ? d.fixY - h : d.fixY}px`, right: 'auto', bottom: 'auto', translate: '' })
      element.style.setProperty('--mini-w', `${d.w}px`)
      return
    }
    const dt = timeStamp - d.at
    if (dt > 0) {
      d.vx = d.vx / 2 + (clientX - d.lastX) / dt / 2
      d.vy = d.vy / 2 + (clientY - d.lastY) / dt / 2
    }
    Object.assign(d, { at: timeStamp, lastX: clientX, lastY: clientY })
    // Its size as pressed: a move only writes its translate, never reads the layout that write just dirtied.
    const x = give(clientX - d.grabX, b.left, b.right - d.w)
    const y = give(clientY - d.grabY, b.top, b.bottom - d.h)
    element.style.translate = `${x - d.baseX}px ${y - d.baseY}px`
  }
  const release = (event: React.PointerEvent<HTMLDivElement>) => {
    const d = drag.current
    if (!d || d.id !== event.pointerId) return
    drag.current = null
    const element = event.currentTarget
    const before = element.getBoundingClientRect()
    if (d.grip) {
      // Kept as its corner and offsets: laid out where it was let go, it doesn't move.
      keep(placeOf(before, Math.round(d.w)))
      Object.assign(element.style, { left: '', top: '', right: '', bottom: '', translate: '' })
      return
    }
    // A drag held still before it was let go carries nothing on.
    const carry = d.dragging && event.timeStamp - d.at <= 80 ? THROW_MS : 0
    if (d.dragging) {
      const b = bounds()
      const left = clamp(before.left + d.vx * carry, b.left, b.right - before.width)
      const top = clamp(before.top + d.vy * carry, b.top, b.bottom - before.height)
      keep(placeOf({ left, top, width: before.width, height: before.height }, place.w))
    }
    // Laid out where it lands at once, it springs there from where it was let go or caught, at the speed it was thrown.
    element.style.translate = ''
    const after = element.getBoundingClientRect()
    springHome(element, before.left - after.left, before.top - after.top, carry ? d.vx : 0, carry ? d.vy : 0)
    // A press that didn't move: back to the call, the stream big.
    if (!d.dragging && event.type === 'pointerup') open()
  }
  // Arrows move it, Shift farther, and + and - resize it from the corner it keeps to; the keyboard lands at once.
  const key = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const step = event.shiftKey ? 40 : 10
    const dx = event.key === 'ArrowRight' ? step : event.key === 'ArrowLeft' ? -step : 0
    const dy = event.key === 'ArrowDown' ? step : event.key === 'ArrowUp' ? -step : 0
    const grow = event.key === '+' || event.key === '=' ? 32 : event.key === '-' ? -32 : 0
    if (!dx && !dy && !grow) return
    event.preventDefault()
    event.stopPropagation()
    const box = event.currentTarget.getBoundingClientRect()
    const b = bounds()
    const w = grow ? clamp(box.width + grow, MIN_W, widest()) : box.width
    const h = w * 9 / 16
    const left = clamp((place.left ? box.left : box.right - w) + dx, b.left, b.right - w)
    const top = clamp((place.top ? box.top : box.bottom - h) + dy, b.top, b.bottom - h)
    keep(placeOf({ left, top, width: w, height: h }, grow ? Math.round(w) : place.w))
  }
  const at = { '--mini-x': place.x === undefined ? undefined : `${place.x}px`, '--mini-y': place.y === undefined ? undefined : `${place.y}px`, '--mini-w': place.w === undefined ? undefined : `${place.w}px` } as CSSProperties
  return createPortal(
    <div className={`exo-portal exo-mini-stream ${place.top ? 'is-top' : 'is-bottom'} ${place.left ? 'is-left' : 'is-right'}`} style={at} role="group" aria-label={`${member.name}'s stream`}
      onPointerDown={press} onPointerMove={move} onPointerUp={release} onPointerCancel={release} onKeyDown={key}>
      <StreamTile member={member} onFocus={open} onStop={() => stopWatching(member.id)} />
      {['tl', 'tr', 'bl', 'br'].map(grip => <span key={grip} className="exo-mini-stream-grip" data-grip={grip} aria-hidden="true" />)}
    </div>,
    document.body)
}

function LocalVideo({ stream }: { stream: MediaStream }) {
  return <video className="exo-dc-voice-video is-mirrored" autoPlay muted playsInline ref={video => { if (video && video.srcObject !== stream) video.srcObject = stream }} />
}

/** The camera, Discord's Turn On Camera (pressed again while it opens, it stops); why it didn't open goes above the bar. */
function CameraButton({ onError }: { onError: (why: string | null) => void }) {
  const { state } = useCamera()
  const on = state !== 'off'
  return (
    <button type="button" className={`exo-dc-voice-btn${on ? ' is-live' : ''}`} aria-label={on ? 'Turn off camera' : 'Turn on camera'} aria-pressed={on}
      onClick={() => { onError(null); if (on) void stopCamera(); else void startCamera().then(onError) }}>
      <Icons.Camera size={20} /><span className="exo-dc-voice-word">Camera</span>
    </button>
  )
}

// How long a watched stream may take to show its first picture before it counts as not starting.
const STREAM_WAIT_MS = 20_000

/** The Go Live being watched, big above the call, with its sound; Stop Watching ends it, Try Again when it didn't start. */
function StreamStage({ member, onStop, onRetry, membersHidden, onToggleMembers }: {
  member: DiscordVoiceMember
  onStop: () => void
  onRetry: () => void
  membersHidden: boolean
  onToggleMembers: () => void
}) {
  const stream = useVideoFeed('stream', member.id)
  // On screen while its room shows it (a hidden room runs no effects): in a layout effect, so the mini player leaves in
  // the same frame the stage arrives, and the stream flies between them (LauncherApp › goToView).
  useLayoutEffect(() => {
    setStaged(member.id)
    return () => setStaged(null)
  }, [member.id])
  // Discord's Stream Volume and Mute: only for you, kept on the account like a person's User Volume.
  const sound = useAccountSetting('audioStreams')[member.id] ?? { muted: false, volume: 100 }
  const level = toSlider(sound.volume)
  // Fullscreen, as Discord's button or a double-click: the stream alone on the whole screen; Esc or the button again leaves.
  const box = useRef<HTMLDivElement>(null)
  const [full, setFull] = useState(false)
  useEffect(() => {
    const changed = () => setFull(document.fullscreenElement === box.current)
    document.addEventListener('fullscreenchange', changed)
    return () => document.removeEventListener('fullscreenchange', changed)
  }, [])
  const toggleFull = () => {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => { })
    else void box.current?.requestFullscreen().catch(() => { })
  }
  const [round, setRound] = useState(0)
  const [late, setLate] = useState(false)
  useEffect(() => {
    if (stream.live) return
    const timer = window.setTimeout(() => setLate(true), STREAM_WAIT_MS)
    return () => window.clearTimeout(timer)
  }, [stream.live, round])
  const failed = !stream.live && (stream.ended || late)
  const retry = () => { setLate(false); setRound(r => r + 1); onRetry() }
  return (
    <div ref={box} className={`exo-dc-stream${membersHidden ? ' is-alone' : ''}${stream.live || failed ? '' : ' exo-shine'}`} onDoubleClick={toggleFull}>
      <video ref={stream.ref} className="exo-dc-stream-video" hidden={!stream.live} muted playsInline disablePictureInPicture />
      {stream.live ? null : failed ? (
        <p className="exo-dc-note" role="alert">
          {member.name}'s stream didn't start.{' '}
          <button type="button" className="exo-dc-tab" onClick={retry}>Try again</button>{' '}
          <CopyLogs error={`${member.name}'s stream didn't start.`} />
        </p>
      ) : <span className="sr-only" role="status">Joining {member.name}'s stream</span>}
      <span className="exo-dc-live">LIVE</span>
      <span className="exo-dc-stream-bar">
        <span className="exo-dc-stream-name">{member.name}</span>
        <span className="exo-dc-stream-tools">
          <span className="exo-dc-stream-sound">
            <button type="button" className={`exo-dc-voice-btn${sound.muted ? ' is-off' : ''}`} aria-label={sound.muted ? 'Unmute stream' : 'Mute stream'} aria-pressed={sound.muted}
              onClick={() => setAudioUser(member.id, { muted: !sound.muted }, true)}>
              {sound.muted ? <Icons.SoundOff size={20} /> : <Icons.Sound size={20} />}
            </button>
            <Slider min={0} max={200} value={level} aria-label="Stream volume" disabled={sound.muted}
              onChange={event => setAudioUser(member.id, { volume: fromSlider(Number(event.target.value)) }, true)} />
          </span>
          {full ? null : (
            <button type="button" className="exo-dc-voice-btn" aria-label={membersHidden ? 'Show members' : 'Hide members'} aria-pressed={membersHidden} onClick={onToggleMembers}>
              {membersHidden ? <ChevronUp size={20} /> : <ChevronDown size={20} />}
            </button>
          )}
          <button type="button" className="exo-dc-voice-btn" aria-label={full ? 'Exit full screen' : 'Full screen'} onClick={toggleFull}>
            {full ? <Icons.ExitFullScreen size={20} /> : <Icons.FullScreen size={20} />}
          </button>
          <button type="button" className="exo-dc-voice-btn is-hangup" aria-label="Stop watching" onClick={onStop}><Icons.StopWatching size={20} /></button>
        </span>
      </span>
    </div>
  )
}

const CLIPS_ON = 'Someone in this call has clips on'

/** Someone else's clips (or their Exo Capture with the call's voices on) record the call: a red dot, said in a tip. */
function RecordedDot() {
  const [tip, setTip] = useState<TipAnchor | null>(null)
  const show = (el: HTMLElement) => setTip(anchorOf(el))
  return (
    <>
      <span className="exo-dc-voice-rec" role="img" aria-label={CLIPS_ON} tabIndex={0}
        onPointerEnter={event => show(event.currentTarget)} onPointerLeave={() => setTip(null)}
        onFocus={event => show(event.currentTarget)} onBlur={() => setTip(null)}>
        <span className="exo-dc-voice-rec-dot" aria-hidden="true" />
        <span className="exo-dc-voice-rec-text" aria-hidden="true">Clips on</span>
      </span>
      {tip ? <AboveTip at={tip}>{CLIPS_ON}</AboveTip> : null}
    </>
  )
}

/**
 * Discord's voice bar above your name: connected (a lock when end-to-end encrypted, a red dot while someone in the call
 * has clips on, as Discord tells everyone in it), the call's "channel / server" as a link that opens it, the stream's
 * controls, and the hang-up button.
 */
export function VoicePanel({ controls, place, onOpen }: { controls: VoiceControls; place: string | null; onOpen: () => void }) {
  const { voice, error } = controls
  if (voice.state === 'off' && !error) return null
  const recorded = voice.state === 'connected' && voice.recorded === true
  return (
    <section className="exo-dc-voice" aria-label="Voice">
      <div className="exo-dc-voice-status">
        <strong className={voice.state === 'connected' ? 'is-on' : undefined}>
          {voice.state === 'connecting' ? <Spinner label="Connecting to voice" /> : null}
          {voice.state === 'connected' ? <><Icons.VoiceSignal size={14} className="exo-dc-voice-signal" />Voice connected</> : voice.state === 'connecting' ? 'Connecting' : 'Voice'}
          {voice.state === 'connected' && voice.e2ee ? <span className="exo-dc-voice-e2ee" role="img" aria-label="End-to-end encrypted"><Icons.Encrypted size={12} /></span> : null}
          {recorded ? <RecordedDot /> : null}
        </strong>
        {error ? <ErrorNote as="span" message={error} /> : voice.state !== 'off' && place
          ? <button type="button" className="exo-dc-voice-place" onClick={onOpen}>{place}</button>
          : null}
        {voice.state === 'connected' && voice.micSilent && !voice.muted
          ? <span className="exo-dc-voice-warn" role="status">Your mic seems silent. Check it’s plugged in, unmuted and its gain is up.</span>
          : null}
        <span className="sr-only" role="status">{recorded ? CLIPS_ON : ''}</span>
      </div>
      {voice.state !== 'off' ? (
        <span className="exo-dc-voice-actions">
          {voice.state === 'connected' ? <PanelStream /> : null}
          <button type="button" className="exo-dc-icon-btn is-hangup" aria-label="Disconnect" onClick={() => void controls.leave()}>
            <Icons.HangUp size={20} />
          </button>
        </span>
      ) : (
        <button type="button" className="exo-dc-icon-btn" aria-label="Dismiss" onClick={controls.dismissError}><Close size={14} /></button>
      )}
    </section>
  )
}

/** Someone is calling you in a DM: Join, Join with video (the camera on as it connects), or Decline (Discord stops ringing you everywhere). */
export function IncomingCall({ name, onJoin, onDecline }: { name: string; onJoin: (video: boolean) => void; onDecline: () => void }) {
  return (
    <div className="exo-dc-call exo-glass" role="alert">
      <span><strong>{name}</strong> is calling</span>
      <button type="button" onClick={() => onJoin(false)}>Join</button>
      <button type="button" onClick={() => onJoin(true)}>Join with video</button>
      <button type="button" onClick={onDecline}>Decline</button>
    </div>
  )
}

import { useEffect, useState, useSyncExternalStore } from 'react'
import type { DiscordCallSong, DiscordFriend, DiscordRich } from './discord'
import { host, onHostEvent } from './host'
import { expectedPosition, nextLatency, shouldCorrect } from './listenSync'
import { musicPosition, playbackAnchor, savedMusicService, type MusicPlayback, type MusicService } from './music'
import { appleAdapter, spotifyAdapter, youtubeAdapter } from './musicAdapters'
import { notify } from './notify'
import { report } from './report'

/**
 * Listen along: someone's song played here on the listener's own service and account, at the same moment, song after
 * song. In your Discord call it is what their Exo plays (its Music room, through Exo's call relay: end-to-end encrypted
 * with the call's own keys, never through Discord; DiscordSession.CallSongs.cs); otherwise, and for a friend not in
 * your call, it is their Discord Listening activity (Spotify's own or another app's). A track id on the listener's own
 * service plays as it is; any other song is found by the listener's own search and MusicMatch.cs. Only the song's name
 * and artists leave Exo, to the listener's service.
 *
 * How it can fail: the friend pauses or goes offline (their activity goes: the music pauses after a moment and picks
 * up when it comes back, and listening along ends after 15 minutes without it); the song is not on this service (said,
 * and the next song is tried); the service's search or play fails (said apart from that, and tried twice more); the listener plays something else mid-song (listening along stops, as Discord's does;
 * the song after a followed one ending on its own is the service's autoplay, not a choice); clocks a second or two
 * apart (only a jump of more than 2.5 s seeks); the listener's own player falls behind or runs ahead (it stalled,
 * buffered or a seek missed: the player's position is checked against the friend's on every update and moved when it
 * is more than 1.5 s off for a call song, 5 s for a Discord activity, whose clock is the friend's PC's, at most once
 * per 8 s or 20 s: listenSync.ts); the listener is in a Spotify Jam (listening along on Spotify would change what the
 * whole Jam plays, so it does not start); the listener switches services (it stops). A call song that pauses
 * pauses here at once; one that stops or leaves the call falls back to their Discord activity, if any; a track id that
 * won't play is looked for by name instead.
 */
export type ListenFriend = { id: string; name: string; avatar: string | null }
export type ListenAlong =
  | { status: 'off' }
  | { status: 'finding' | 'playing' | 'paused' | 'missing' | 'failed'; friend: ListenFriend; title: string; artist: string; on: string }

/** A friend's activity that can be listened along to: a song, and when it started. */
export function canListenAlong(rich: DiscordRich | null | undefined): rich is DiscordRich & { details: string; start: number } {
  return !!rich && rich.type === 2 && !!rich.details && typeof rich.start === 'number'
}

const adapters = { spotify: spotifyAdapter, youtube: youtubeAdapter, apple: appleAdapter }

/** Songs the Exo apps in your call play, by user id: only those playing or paused. Kept from the first use on. */
const callSongs = new Map<string, DiscordCallSong>()
const NO_SONGS: DiscordCallSong[] = []
let callList = NO_SONGS
const callWatchers = new Set<() => void>()
let callsWired = false

function takeCallSong(song: DiscordCallSong) {
  if (song.rich || song.paused) callSongs.set(song.userId, song)
  else if (!callSongs.delete(song.userId)) return
  callList = [...callSongs.values()]
  callWatchers.forEach(watch => watch())
}

function wireCallSongs() {
  if (callsWired) return
  callsWired = true
  onHostEvent('discord.callSong', takeCallSong)
  host.discordCallSongs().then(result => result.songs.forEach(song => { if (!callSongs.has(song.userId)) takeCallSong(song) }),
    (error: unknown) => report(error, 'listenAlong.callSongs'))
}

/** What the Exo apps in your call play now (playing or paused). */
export function useCallSongs(): DiscordCallSong[] {
  return useSyncExternalStore(watch => { wireCallSongs(); callWatchers.add(watch); return () => { callWatchers.delete(watch) } }, () => callList, () => NO_SONGS)
}

/** Who you can listen along with now: people in your call by what their Exo plays, then your Discord friends by their
    presence (a friend whose Exo song is paused is not listed); signedIn: Discord is, in Exo.
    enabled false: friends are not asked or listened to (the Jam box while there is no call, music or listening along). */
export function useListeningFriends(enabled = true) {
  const [friends, setFriends] = useState<DiscordFriend[] | null>(null)
  const [signedIn, setSignedIn] = useState(true)
  useEffect(() => {
    if (!enabled) return
    let live = true
    host.discordStatus().then(async status => {
      if (!live) return
      setSignedIn(status.signedIn)
      if (status.signedIn) { const result = await host.discordFriends(); if (live) setFriends(result.friends) }
    }).catch((error: unknown) => { report(error, 'listenAlong.friends'); if (live) setFriends([]) })
    // Presence comes for everyone Discord tells Exo about; one not in the list leaves it as it is, so nothing redraws.
    const off = onHostEvent('discord.presence', event =>
      setFriends(list => list?.some(friend => friend.id === event.userId)
        ? list.map(friend => friend.id === event.userId ? { ...friend, status: event.status, rich: event.rich } : friend)
        : list))
    return () => { live = false; off() }
  }, [enabled])
  const calls = useCallSongs()
  const inCall = calls.flatMap(song => canListenAlong(song.rich) ? [{ friend: { id: song.userId, name: song.name, avatar: song.avatar } as ListenFriend, rich: song.rich }] : [])
  const fromDiscord = (friends ?? []).flatMap(friend =>
    friend.kind === 'friend' && friend.status !== 'offline' && !calls.some(song => song.userId === friend.id) && canListenAlong(friend.rich)
      ? [{ friend: { id: friend.id, name: friend.name, avatar: friend.avatar } as ListenFriend, rich: friend.rich }] : [])
  return { listening: [...inCall, ...fromDiscord], loaded: friends !== null, signedIn }
}

/** The same song by name, as two services or a service and its media session write it. */
export function sameSong(a: string | null | undefined, b: string | null | undefined) {
  const fold = (text: string) => text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '')
  const [x, y] = [fold(a ?? ''), fold(b ?? '')]
  return !!x && !!y && (x.includes(y) || y.includes(x))
}

const OFF: ListenAlong = { status: 'off' }
let state: ListenAlong = OFF
const watchers = new Set<() => void>()

export function useListenAlong(): ListenAlong {
  return useSyncExternalStore(watch => { watchers.add(watch); return () => { watchers.delete(watch) } }, () => state, () => OFF)
}

function show(next: ListenAlong) {
  state = next
  watchers.forEach(watch => watch())
}

/** The followed song: its key (Spotify's track id, else name and artists), when it started, and what plays here for it.
    call: their Exo's song in your call (playing or paused), which wins; presence: their Discord activity. */
type Follow = { friend: ListenFriend; on: string; key: string; start: number; end: number | null; uri: string | null; title: string; paused: boolean
  /** When this song was last moved (or began), for the drift check's cooldown. */
  movedAt: number
  call: DiscordCallSong | null; presence: DiscordRich | null }
let follow: Follow | null = null
let unsubscribe: (() => void) | null = null
let gone = 0
let ended = 0
let retry = 0
let turn = 0
const found = new Map<string, { uri: string; title: string } | null>()

export function listenAlong(friend: ListenFriend, rich: DiscordRich) {
  stopListening()
  const service = savedMusicService()
  wireCallSongs()
  const call = callSongs.get(friend.id) ?? null
  const now: Follow = { friend, on: adapters[service].name, key: '', start: 0, end: null, uri: null, title: '', paused: false, movedAt: 0, call, presence: call ? null : rich }
  follow = now
  const offPresence = onHostEvent('discord.presence', event => {
    if (follow !== now || event.userId !== friend.id) return
    now.presence = event.rich ?? null
    if (!now.call) void apply(now, now.presence)
  })
  const offCall = onHostEvent('discord.callSong', song => {
    if (follow !== now || song.userId !== friend.id) return
    now.call = song.rich || song.paused ? song : null
    void apply(now, now.call ? now.call.rich : now.presence, song.paused)
  })
  const offPlayback = onHostEvent('music.playback', playback => {
    // Another song mid-song is the listener's own choice; after the followed one ends it is the service's autoplay.
    if (follow !== now || !now.uri || playback?.status !== 'playing') return
    if (sameSong(playback.title, now.title)) return void drift(now, playback)
    if (now.end !== null && Date.now() < now.end - 5000) stopListening('You played another song, so listening along stopped.')
  })
  const offService = () => { if (follow === now) stopListening() }
  window.addEventListener('exo-music-service', offService)
  unsubscribe = () => { offPresence(); offCall(); offPlayback(); window.removeEventListener('exo-music-service', offService) }
  notify({ id: 'listen-along', title: friend.name, text: `Listening along on ${adapters[service].name}.`, tone: 'good' })
  void adapters[service].start().then(async started => {
    if (follow !== now) return
    if (!started.ok || started.state !== 'running') return stopListening(`Sign in to ${adapters[service].name} in Music to listen along.`)
    // In a Spotify Jam, what plays is the Jam's: a song played here would change it for everyone in it.
    if (service === 'spotify' && await inSpotifyJam() && follow === now) return stopListening('You are in a Spotify Jam. Leave it to listen along.')
    if (follow === now) void apply(now, rich)
  }, () => { if (follow === now) stopListening(`${adapters[service].name} did not open. Try again.`) })
}

/** Stops following; the song playing keeps playing. why: a notice saying so. */
export function stopListening(why?: string) {
  if (!follow) return
  follow = null
  turn++
  window.clearTimeout(gone)
  window.clearTimeout(ended)
  window.clearTimeout(retry)
  unsubscribe?.()
  unsubscribe = null
  show(OFF)
  if (why) notify({ id: 'listen-along', text: why })
}

/** paused: their Exo said they paused, so the music pauses at once instead of after a moment. */
function apply(now: Follow, rich: DiscordRich | null, paused = false) {
  window.clearTimeout(gone)
  window.clearTimeout(ended)
  window.clearTimeout(retry)
  if (!canListenAlong(rich)) return away(now, paused ? 0 : 4000)
  const key = rich.syncId ?? `${rich.details}|${rich.state ?? ''}`
  return key === now.key ? catchUp(now, rich) : next(now, rich, key)
}

/** Their activity went: between songs that is a moment, so the music pauses only when it stays gone (wait). */
function away(now: Follow, wait: number) {
  gone = window.setTimeout(() => {
    if (follow !== now) return
    now.paused = true
    if (state.status !== 'off') show({ ...state, status: 'paused' })
    void host.musicControl('pause').catch(() => null)
  }, wait)
  ended = window.setTimeout(() => { if (follow === now) stopListening(`${now.friend.name} stopped listening.`) }, 15 * 60_000)
}

const shown = (now: Follow, rich: DiscordRich & { details: string }) =>
  ({ status: 'playing' as const, friend: now.friend, title: rich.details, artist: rich.state ?? '', on: now.on })

/** The same song: catch up after their pause or seek; smaller differences are clocks. */
async function catchUp(now: Follow, rich: DiscordRich & { details: string; start: number }) {
  const jumped = Math.abs(rich.start - now.start) > 2500
  now.start = rich.start
  if (!now.uri || !now.paused && !jumped) return
  if (now.paused) await host.musicControl('play').catch(() => null)
  now.paused = false
  await seek(now)
  if (follow === now) show(shown(now, rich))
}

/** A new song: found on this service, played, and moved to where the friend is in it. attempt: tries after a failure. */
async function next(now: Follow, rich: DiscordRich & { details: string; start: number }, key: string, attempt = 0) {
  const mine = ++turn
  const current = () => follow === now && mine === turn
  Object.assign(now, { key, start: rich.start, end: rich.end && rich.end > rich.start ? rich.end : null, uri: null, title: '', paused: false, movedAt: Date.now() })
  show({ ...shown(now, rich), status: 'finding' })
  const play = (uri: string) => adapters[savedMusicService()].act('play', { uri }).catch(() => ({ ok: false }))
  let track = await find(rich).catch(() => undefined)
  if (!current()) return
  let played = track ? await play(track.uri) : { ok: false }
  if (!current()) return
  // Their own track that won't play here (another storefront, a song taken down) is looked for by name.
  if (track?.direct && !played.ok) {
    track = await find(rich, true).catch(() => undefined)
    if (!current()) return
    played = track ? await play(track.uri) : { ok: false }
    if (!current()) return
  }
  if (!track || !played.ok) {
    // Not in the service's catalog is said as that; a search or play that failed is not, and is tried again shortly.
    show({ ...shown(now, rich), status: track === null ? 'missing' : 'failed' })
    void host.musicControl('pause').catch(() => null)
    if (track !== null && attempt < 2) retry = window.setTimeout(() => { if (current()) void next(now, rich, key, attempt + 1) }, 8000)
    return
  }
  Object.assign(now, { uri: track.uri, title: track.title })
  await playing(track.title)
  if (!current()) return
  await seek(now)
  show(shown(now, rich))
}

/** How long a seek took to land, as it was measured (ms); the next one is aimed that far ahead. */
let seekLatency: number | null = null

/** Where the friend is in the song now; a start under 3 s plays from the top. */
async function seek(now: Follow) {
  const at = expectedPosition(now.start, Date.now(), seekLatency ?? 0)
  now.movedAt = Date.now()
  if (at > 3000 && (now.end === null || Date.now() < now.end - 3000)) {
    const sent = performance.now()
    const result = await host.musicControl('seek', { positionMs: at }).catch(() => null)
    if (result) seekLatency = nextLatency(seekLatency, performance.now() - sent)
  }
}

/** The listener's own player against the friend's clock: moved when it is far enough off (listenSync.ts). */
function drift(now: Follow, playback: MusicPlayback) {
  const tick = performance.now()
  const wall = Date.now()
  const actual = musicPosition(playback, playbackAnchor(playback, tick), tick)
  if (!playback.canSeek || now.paused || !shouldCorrect({
    source: now.call ? 'call' : 'presence', actual, expected: expectedPosition(now.start, wall), playing: true,
    sinceLastSeek: wall - now.movedAt, endsIn: now.end === null ? null : now.end - wall })) return
  void seek(now)
}

/** Waits (at most 5 s) until the service says the song plays, so the seek lands in it and not in the one before. */
function playing(title: string) {
  return new Promise<void>(resolve => {
    const done = () => { window.clearTimeout(timer); off(); resolve() }
    const off = onHostEvent('music.playback', playback => { if (playback?.status === 'playing' && sameSong(playback.title, title)) done() })
    const timer = window.setTimeout(done, 5000)
  })
}

/** Whether the listener is in a Spotify Jam now; a Jam that can't be read counts as none. */
async function inSpotifyJam() {
  const jam = await spotifyAdapter.view('jam').catch(() => null)
  return !!jam?.ok && !!jam.data?.session
}

/** How each service names one of its own tracks (a call song's uri). */
const TRACKS: Record<MusicService, string> = { spotify: 'spotify:track:', youtube: 'ytm:track:', apple: 'am:songs:' }

/** The friend's song on the listener's service: its own track when their song names one there (Spotify's activity, or a
    call song from the same service), else the best search result. byName: the search even then. */
async function find(rich: DiscordRich & { details: string; start: number }, byName = false): Promise<{ uri: string; title: string; direct?: boolean } | null | undefined> {
  const service = savedMusicService()
  if (!byName && rich.uri?.startsWith(TRACKS[service])) return { uri: rich.uri, title: rich.details, direct: true }
  if (!byName && service === 'spotify' && rich.name === 'Spotify' && rich.syncId && /^[A-Za-z0-9]{22}$/.test(rich.syncId))
    return { uri: `spotify:track:${rich.syncId}`, title: rich.details, direct: true }
  const key = `${service}|${rich.details}|${rich.state ?? ''}`
  if (found.has(key)) return found.get(key) ?? null
  const answer = await search(service, rich)
  if (found.size > 200) found.clear()
  // A search that failed is not remembered: the next try may find it.
  if (answer !== undefined) found.set(key, answer)
  return answer
}

/** The best of the service's search results for the song, null when none is it, undefined when the search failed. */
async function search(service: MusicService, rich: DiscordRich & { details: string; start: number }) {
  // Spotify's activity joins artists with "; "; others write one line.
  const artists = (rich.state ?? '').split(/;\s*/).filter(Boolean)
  const result = await adapters[service].view('search', { query: `${rich.details.replace(/\s*[([].*$/, '')} ${artists[0] ?? ''}`.trim() })
  if (!result.ok) return undefined
  const tracks = (result.data?.tracks ?? []).filter(track => track.playable !== false && track.uri).slice(0, 20)
  if (!tracks.length) return null
  const durationMs = rich.end && rich.end > rich.start ? rich.end - rich.start : null
  const match = await host.musicMatch({ title: rich.details, artists, durationMs },
    tracks.map(track => ({ title: track.title, artists: track.artists.map(artist => artist.name), durationMs: track.durationMs || null })))
  const track = match.ok && typeof match.index === 'number' && match.index >= 0 ? tracks[match.index] : null
  return track ? { uri: track.uri, title: track.title } : null
}

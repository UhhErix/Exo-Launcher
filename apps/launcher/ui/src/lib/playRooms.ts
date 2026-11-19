/**
 * Play's rooms, worked out from what the page already holds (docs/architecture/chat-rooms.md): who is on which game and how ready
 * each is, which voice room they are in, and what Launch together does. Pure: no host call, no clock but the one it is given.
 *
 * How it can fail, each of them a wrong line on a friend's row: a friend who is installing a game is shown as playing it, or
 * not at all; one who is playing and has an old "ready" is counted twice; a friend whose title differs by a trademark sign or case
 * lands in a party of their own; a voice room that holds one party friend and four strangers is offered as the party's; an offline
 * friend's last readiness stays on the board; you are missing from your own party or counted ready without the game installed.
 */
import type { DiscordDm, DiscordFriend, DiscordVoiceNow } from './discord'
import type { Game } from './host'

/** What a person on a party board is doing about the game, most advanced first. */
export type PartyState = 'playing' | 'ready' | 'compiling' | 'installing' | 'voice' | 'missing'

export type Member = {
  id: string
  name: string
  avatar: string | null
  state: PartyState
  /** An install's progress, in tens, when they said. */
  percent: number | null
  /** When they started playing (ms), when Discord says. */
  since: number | null
  /** What they are doing in the game: Discord's details ("Quick match"). */
  detail: string | null
  /** Their Exo told this (over the friend link), not Discord. */
  throughExo: boolean
  friend: DiscordFriend | null
}

export type Party = {
  key: string
  title: string
  game: Game | null
  /** Friends only; you are counted by `readiness`. */
  members: Member[]
  /** The voice room that holds the most of them, when one holds any. */
  room: DiscordVoiceNow | null
}

type Group = { key: string; title: string; game: Game | null; friends: DiscordFriend[] }

const RANK: Record<PartyState, number> = { playing: 0, ready: 1, compiling: 2, installing: 3, voice: 4, missing: 5 }

/** What a state says, in the board's words. */
export function stateWords(state: PartyState, percent: number | null): string {
  switch (state) {
    case 'playing': return 'Playing'
    case 'ready': return 'Ready'
    case 'compiling': return 'Compiling shaders'
    case 'installing': return percent === null ? 'Installing' : `Installing ${percent}%`
    case 'voice': return 'In voice'
    case 'missing': return 'Not installed'
  }
}


const online = (friend: DiscordFriend) => friend.kind === 'friend' && friend.status !== 'offline'

const person = (friend: DiscordFriend, state: PartyState, extra: Partial<Member> = {}): Member => ({
  id: friend.id, name: friend.name, avatar: friend.avatar, state, percent: null, since: null, detail: null, throughExo: false, friend, ...extra,
})

/** The voice room with the most of these people in it (a muted server's is left out), or none when it holds none of them. */
function roomOf(ids: ReadonlySet<string>, rooms: readonly DiscordVoiceNow[]): DiscordVoiceNow | null {
  const here = (room: DiscordVoiceNow) => room.people.filter(p => ids.has(p.id)).length
  return rooms.filter(room => !room.muted && here(room) > 0).sort((a, b) => here(b) - here(a) || b.count - a.count)[0] ?? null
}

type Gathering = { title: string; game: Game | null; members: Map<string, Member> }

/**
 * The parties: one per game that an online friend is playing or getting ready for, the most people first. A friend is on one
 * only: what they are playing beats what they are getting ready, so a friend in one game never also waits in another. The voice
 * room is the one with the most party friends in it, offered only when it holds at least one.
 * @param identity folds a title the way friends' Discord names a game (utils.titleIdentity)
 * @param playingNow what a friend is playing, by Discord's activity (friendsPlaying.playingName)
 */
export function buildParties(input: {
  friends: readonly DiscordFriend[]
  groups: readonly Group[]
  rooms: readonly DiscordVoiceNow[]
  games: readonly Game[]
  identity: (title: string) => string
  playingNow: (friend: DiscordFriend) => string | null
}): Party[] {
  const { friends, groups, rooms, games, identity, playingNow } = input
  const gatherings = new Map<string, Gathering>()
  const gameOf = (title: string) => games.find(game => identity(game.title) === identity(title) || (game.includes ?? []).some(part => identity(part.title) === identity(title))) ?? null
  const slot = (title: string, game: Game | null) => {
    const key = game ? game.id : `title:${identity(title)}`
    let gathering = gatherings.get(key)
    if (!gathering) gatherings.set(key, gathering = { title: game ? game.title : title, game, members: new Map() })
    return gathering
  }
  const place = (gathering: Gathering, member: Member) => {
    const had = gathering.members.get(member.id)
    if (!had || RANK[member.state] < RANK[had.state]) gathering.members.set(member.id, member)
  }
  const playing = new Set<string>()
  for (const group of groups) {
    const gathering = slot(group.title, group.game)
    for (const friend of group.friends.filter(online)) {
      playing.add(friend.id)
      place(gathering, person(friend, 'playing', { since: friend.rich?.start ?? null, detail: friend.rich?.details ?? null, throughExo: friend.rich?.throughExo === true }))
    }
  }
  for (const friend of friends) {
    const prep = friend.prep
    if (!prep || !online(friend) || playing.has(friend.id) || playingNow(friend)) continue
    place(slot(prep.title, gameOf(prep.title)), person(friend, prep.kind, { percent: prep.kind === 'installing' ? prep.percent ?? null : null, throughExo: true }))
  }
  const parties = [...gatherings].filter(([, gathering]) => gathering.members.size > 0).map(([key, gathering]): Party => {
    const members = [...gathering.members.values()]
    const ids = new Set(members.map(member => member.id))
    const room = roomOf(ids, rooms)
    // Someone in that room with no game state yet is gathering too.
    const joining = room ? room.people.filter(p => !ids.has(p.id)).flatMap(p => friends.filter(f => f.id === p.id && online(f))).map(f => person(f, 'voice')) : []
    return { key, title: gathering.title, game: gathering.game, room, members: [...members, ...joining].sort((a, b) => RANK[a.state] - RANK[b.state] || a.name.localeCompare(b.name)) }
  })
  return parties.sort((a, b) => b.members.length - a.members.length || a.title.localeCompare(b.title))
}

/** Your own place on the board: playing it now, ready (installed), or not installed. */
export function myState(game: Game | null): PartyState {
  if (!game) return 'missing'
  if (game.isRunning) return 'playing'
  return game.installed ? 'ready' : 'missing'
}

/** How many are ready out of everyone, you included: playing counts, since they are in. */
export function readiness(members: readonly Pick<Member, 'state'>[], me: PartyState): { ready: number; total: number } {
  const ready = (state: PartyState) => state === 'ready' || state === 'playing'
  return { ready: members.filter(m => ready(m.state)).length + (ready(me) ? 1 : 0), total: members.length + 1 }
}

/**
 * What Launch together does for you: join the party's voice room when there is one you are not in (and can join), then
 * the game's own Play or Install. Never a message to anyone.
 */
export function launchPlan(input: { party: Pick<Party, 'room'>; inVoiceChannelId: string | null }): { joinVoice: { guildId: string; channelId: string } | null } {
  const { room } = input.party
  if (!room || !room.canConnect || room.channelId === input.inVoiceChannelId || (room.limit && room.count >= room.limit)) return { joinVoice: null }
  return { joinVoice: { guildId: room.guildId, channelId: room.channelId } }
}

/** A conversation a clip can go to: its channel id and name, and whether it is someone on the clip's game (offered first). */
export type ShareTarget = { channelId: string; name: string; onGame: boolean }

/**
 * Where a clip can be shared: the conversations with the people on its game first, then your latest others, at most `limit`.
 * Message requests and spam are never offered, and a conversation is offered once.
 */
export function shareTargets(input: { gameId: string | null; parties: readonly Party[]; dms: readonly DiscordDm[]; limit?: number }): ShareTarget[] {
  const { gameId, parties, dms, limit = 8 } = input
  const onGame = new Set(gameId ? parties.filter(party => party.game?.id === gameId).flatMap(party => party.members.map(member => member.id)) : [])
  const usable = dms.filter(dm => !dm.request && !dm.spam)
  const rank = (dm: DiscordDm) => (dm.userId && onGame.has(dm.userId) ? 0 : 1)
  return [...usable].sort((a, b) => rank(a) - rank(b) || (b.lastAt ?? 0) - (a.lastAt ?? 0))
    .slice(0, limit).map(dm => ({ channelId: dm.id, name: dm.name, onGame: rank(dm) === 0 }))
}

/** Games for the Games room: those friends are on first (most people), then the most recently played; installed before not. */
export function orderGames(games: readonly Game[], people: ReadonlyMap<string, number>, limit = 24): Game[] {
  const playable = games.filter(game => !game.isAddPortable)
  const last = (game: Game) => (game.lastPlayedUtc ? Date.parse(game.lastPlayedUtc) || 0 : 0)
  return [...playable].sort((a, b) =>
    (people.get(b.id) ?? 0) - (people.get(a.id) ?? 0)
    || Number(b.installed) - Number(a.installed)
    || last(b) - last(a)
    || (b.playtimeMinutes ?? 0) - (a.playtimeMinutes ?? 0)
    || a.title.localeCompare(b.title)).slice(0, limit)
}

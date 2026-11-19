// Play's rooms (src/lib/playRooms.ts) on made-up friends. `npm test` (Node's own runner, which reads TypeScript as it is);
// `npm run check` runs it too.
//
// How it could fail: a friend installing a game is shown as playing it, or twice; a friend whose game's title differs by a
// trademark sign or case lands in a party of their own; a voice room with one party friend and four strangers is offered
// as the party's; an offline friend's last readiness stays on the board; you are counted ready without the game installed;
// Launch together joins a room you are already in, one that is full or one you can't connect to; a clip is offered to a
// message request or spam; the games friends are on are not the first on the page.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildParties, launchPlan, myState, orderGames, readiness, shareTargets, stateWords } from '../src/lib/playRooms.ts'
import type { DiscordDm, DiscordFriend, DiscordPrep, DiscordVoiceNow } from '../src/lib/discord.ts'
import type { Game } from '../src/lib/host.ts'

const identity = (title: string) => title.toLowerCase().replace(/[^a-z0-9]/g, '')
const friend = (id: string, name: string, extra: Partial<DiscordFriend> = {}): DiscordFriend => ({ id, name, avatar: null, kind: 'friend', status: 'online', ...extra })
const game = (id: string, title: string, extra: Partial<Game> = {}) => ({ id, title, store: 'steam', installed: true, status: '', deps: [], launchNote: '', ...extra }) as Game
const prep = (title: string, kind: DiscordPrep['kind'], percent?: number): DiscordPrep => ({ title, kind, percent })
const room = (channelId: string, ids: string[], extra: Partial<DiscordVoiceNow> = {}) => ({
  channelId, name: 'Squad', guildId: 'g1', guild: 'Night Raid', icon: null, muted: false, canConnect: true, count: ids.length, friends: ids.length, streaming: false,
  people: ids.map(id => ({ id, name: id, avatar: null })), ...extra,
}) as DiscordVoiceNow

const rivals = game('rivals', 'Marvel Rivals')
const playing = (f: DiscordFriend) => (f.rich?.type === 0 ? f.rich.name : null)
const groupsOf = (friends: DiscordFriend[], games: Game[]) => {
  const groups = new Map<string, { key: string; title: string; game: Game | null; friends: DiscordFriend[] }>()
  for (const f of friends) {
    const name = playing(f)
    if (!name) continue
    const g = games.find(x => identity(x.title) === identity(name)) ?? null
    const key = g ? g.id : `title:${identity(name)}`
    const found = groups.get(key) ?? { key, title: g ? g.title : name, game: g, friends: [] }
    found.friends.push(f)
    groups.set(key, found)
  }
  return [...groups.values()]
}
const parties = (friends: DiscordFriend[], rooms: DiscordVoiceNow[] = [], games: Game[] = [rivals]) =>
  buildParties({ friends, groups: groupsOf(friends, games), rooms, games, identity, playingNow: playing })

test('a party is the friends playing a game and those getting ready for it, playing first', () => {
  const [party] = parties([
    friend('1', 'Bea', { rich: { type: 0, name: 'Marvel Rivals', start: 5 } }),
    friend('2', 'Rowan', { prep: prep('Marvel Rivals', 'compiling') }),
    friend('3', 'Juno', { prep: prep('MARVEL RIVALS', 'installing', 60) }),
    friend('4', 'Sol', { prep: prep('Marvel Rivals', 'ready') }),
  ])
  assert.equal(party.title, 'Marvel Rivals')
  assert.equal(party.game?.id, 'rivals')
  assert.deepEqual(party.members.map(m => [m.name, m.state]), [['Bea', 'playing'], ['Sol', 'ready'], ['Rowan', 'compiling'], ['Juno', 'installing']])
  assert.equal(party.members.find(m => m.name === 'Juno')?.percent, 60)
})

test('a title that differs by case or a sign still lands in the same party, and a game you do not own gets one of its own', () => {
  const found = parties([
    friend('1', 'Bea', { rich: { type: 0, name: 'MARVEL RIVALS', start: 5 } }),
    friend('2', 'Rowan', { prep: prep('Marvel  Rivals™', 'ready') }),
    friend('3', 'Dex', { rich: { type: 0, name: 'Rocket League', start: 5 } }),
  ])
  assert.equal(found.length, 2)
  assert.equal(found[0].members.length, 2)
  assert.equal(found[1].title, 'Rocket League')
  assert.equal(found[1].game, null)
})

test('a friend who is playing is on that game only, not also waiting for another', () => {
  const found = parties([friend('1', 'Bea', { rich: { type: 0, name: 'Hades', start: 5 }, prep: prep('Marvel Rivals', 'compiling') })], [], [rivals, game('hades', 'Hades')])
  assert.equal(found.length, 1)
  assert.equal(found[0].title, 'Hades')
})

test('an offline friend and a request are not on a board, whatever they last said', () => {
  const found = parties([
    friend('1', 'Bea', { status: 'offline', prep: prep('Marvel Rivals', 'ready') }),
    friend('2', 'Cole', { kind: 'incoming', prep: prep('Marvel Rivals', 'ready') }),
    friend('3', 'Dex', { status: 'offline', rich: { type: 0, name: 'Marvel Rivals', start: 5 } }),
  ])
  assert.deepEqual(found, [])
})

test('the voice room holding most of the party is offered, and others in it with no game yet join the board', () => {
  const friends = [
    friend('1', 'Bea', { rich: { type: 0, name: 'Marvel Rivals', start: 5 } }),
    friend('2', 'Rowan', { prep: prep('Marvel Rivals', 'compiling') }),
    friend('3', 'Kai'),
  ]
  const [party] = parties(friends, [room('r-one', ['1', 'x', 'y', 'z']), room('r-two', ['1', '2', '3'])])
  assert.equal(party.room?.channelId, 'r-two')
  assert.deepEqual(party.members.map(m => [m.name, m.state]), [['Bea', 'playing'], ['Rowan', 'compiling'], ['Kai', 'voice']])
})

test('a muted server\'s room, or a room with none of the party, is never the party\'s', () => {
  const friends = [friend('1', 'Bea', { rich: { type: 0, name: 'Marvel Rivals', start: 5 } })]
  assert.equal(parties(friends, [room('r', ['1'], { muted: true })])[0].room, null)
  assert.equal(parties(friends, [room('r', ['x', 'y'])])[0].room, null)
})

test('the biggest party comes first', () => {
  const found = parties([
    friend('1', 'Dex', { rich: { type: 0, name: 'Rocket League', start: 5 } }),
    friend('2', 'Bea', { rich: { type: 0, name: 'Marvel Rivals', start: 5 } }),
    friend('3', 'Cole', { rich: { type: 0, name: 'Marvel Rivals', start: 5 } }),
  ])
  assert.deepEqual(found.map(p => p.title), ['Marvel Rivals', 'Rocket League'])
})

test('you are ready only with the game installed, and playing counts as in', () => {
  assert.equal(myState(game('a', 'A')), 'ready')
  assert.equal(myState(game('a', 'A', { installed: false })), 'missing')
  assert.equal(myState(game('a', 'A', { isRunning: true })), 'playing')
  assert.equal(myState(null), 'missing')
  const members = [{ state: 'playing' as const }, { state: 'ready' as const }, { state: 'compiling' as const }, { state: 'installing' as const }, { state: 'voice' as const }]
  assert.deepEqual(readiness(members, 'ready'), { ready: 3, total: 6 })
  assert.deepEqual(readiness(members, 'missing'), { ready: 2, total: 6 })
  assert.deepEqual(readiness([], 'ready'), { ready: 1, total: 1 })
})

test('Launch together joins the party\'s room only when there is one you can and are not in', () => {
  const open = room('r', ['1', '2'], { limit: 4 })
  assert.deepEqual(launchPlan({ party: { room: open }, inVoiceChannelId: null }), { joinVoice: { guildId: 'g1', channelId: 'r' } })
  assert.deepEqual(launchPlan({ party: { room: open }, inVoiceChannelId: 'r' }), { joinVoice: null })
  assert.deepEqual(launchPlan({ party: { room: null }, inVoiceChannelId: null }), { joinVoice: null })
  assert.deepEqual(launchPlan({ party: { room: { ...open, canConnect: false } }, inVoiceChannelId: null }), { joinVoice: null })
  assert.deepEqual(launchPlan({ party: { room: room('r', ['1', '2', '3', '4'], { limit: 4 }) }, inVoiceChannelId: null }), { joinVoice: null })
  assert.deepEqual(launchPlan({ party: { room: room('r', ['1', '2', '3', '4']) }, inVoiceChannelId: 'other' }), { joinVoice: { guildId: 'g1', channelId: 'r' } })
})

test('a clip goes first to the people on its game, never to a request or spam, and each conversation once', () => {
  const dm = (id: string, name: string, userId: string | undefined, lastAt: number, extra: Partial<DiscordDm> = {}) =>
    ({ id, name, avatar: null, group: false, unread: false, mentions: 0, userId, lastAt, ...extra }) as DiscordDm
  const [party] = parties([friend('1', 'Bea', { rich: { type: 0, name: 'Marvel Rivals', start: 5 } })])
  const dms = [dm('d-new', 'Newest', '9', 300), dm('d-bea', 'Bea', '1', 100), dm('d-req', 'Request', '8', 400, { request: true }), dm('d-spam', 'Spam', '7', 500, { spam: true }), dm('d-old', 'Old', '6', 50)]
  const targets = shareTargets({ gameId: 'rivals', parties: [party], dms })
  assert.deepEqual(targets.map(t => [t.channelId, t.onGame]), [['d-bea', true], ['d-new', false], ['d-old', false]])
  assert.deepEqual(shareTargets({ gameId: null, parties: [party], dms }).map(t => t.channelId), ['d-new', 'd-bea', 'd-old'])
  assert.equal(shareTargets({ gameId: 'rivals', parties: [party], dms, limit: 1 }).length, 1)
  assert.deepEqual(shareTargets({ gameId: 'rivals', parties: [], dms: [] }), [])
})

test('games friends are on come first, then installed ones, then the most recently played', () => {
  const games = [
    game('old', 'Old', { lastPlayedUtc: '2026-01-01T00:00:00Z' }),
    game('new', 'New', { lastPlayedUtc: '2026-10-01T00:00:00Z' }),
    game('away', 'Away', { installed: false }),
    game('busy', 'Busy', { lastPlayedUtc: '2025-01-01T00:00:00Z' }),
    game('add', 'Add portable game', { isAddPortable: true }),
  ]
  assert.deepEqual(orderGames(games, new Map([['busy', 2]])).map(g => g.id), ['busy', 'new', 'old', 'away'])
  assert.equal(orderGames(games, new Map(), 2).length, 2)
})

test('a state reads in a few plain words', () => {
  assert.equal(stateWords('installing', 40), 'Installing 40%')
  assert.equal(stateWords('installing', null), 'Installing')
  assert.equal(stateWords('compiling', null), 'Compiling shaders')
  assert.equal(stateWords('ready', null), 'Ready')
  assert.equal(stateWords('missing', null), 'Not installed')
})

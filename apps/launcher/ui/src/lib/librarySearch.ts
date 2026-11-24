/**
 * The Library's own search and filters, all on this PC: no network, no model. A search is read as plain words
 * ("racing games I haven't played", "co-op installed", "steam over 20 hours"): the words Exo knows become filters, the
 * rest must be in the title. The filters are the same ones the Filter menu sets, so a search and the chips combine.
 * Genres and modes come from Steam's own record of each game (Game.tags); a game Steam knows nothing about has none,
 * so a genre search leaves it out rather than guessing. No React, no host calls.
 */
import type { Game } from './host'
import { storeLabel } from './storeLabel.ts'

/** On this PC: installed, or preloaded for its release (host.ts › isPreloaded). */
const onPc = (game: Game) => game.installed || game.status === 'Preloaded'

/** One filter. Stores are one group (any of them), installed and not installed another, played and not played a third; every group must match. */
export type LibraryFilter =
  | { kind: 'store'; store: string }
  | { kind: 'installed' }
  | { kind: 'notInstalled' }
  | { kind: 'update' }
  | { kind: 'unplayed' }
  | { kind: 'played' }
  | { kind: 'recent' }
  | { kind: 'friends' }
  | { kind: 'family' }
  | { kind: 'tag'; tag: string }

/** The words Steam's genres and modes reach the page as (SteamAppTags.cs), in the order the Filter menu lists them. */
export const LIBRARY_TAGS = [
  'action', 'adventure', 'rpg', 'strategy', 'simulation', 'racing', 'sports', 'casual', 'indie', 'mmo',
  'free to play', 'early access', 'single-player', 'multiplayer', 'co-op', 'local co-op', 'controller',
] as const

const TAG_LABELS: Record<string, string> = {
  rpg: 'RPG', mmo: 'MMO', 'co-op': 'Co-op', 'local co-op': 'Local co-op', 'single-player': 'Single-player',
  'free to play': 'Free to play', 'early access': 'Early access',
}

export function filterKey(filter: LibraryFilter): string {
  return filter.kind === 'store' ? `store:${filter.store}` : filter.kind === 'tag' ? `tag:${filter.tag}` : filter.kind
}

export function filterLabel(filter: LibraryFilter): string {
  switch (filter.kind) {
    case 'store': return storeLabel(filter.store)
    case 'tag': return TAG_LABELS[filter.tag] ?? filter.tag[0].toUpperCase() + filter.tag.slice(1)
    case 'installed': return 'Installed'
    case 'notInstalled': return 'Not installed'
    case 'update': return 'Update ready'
    case 'unplayed': return 'Not played'
    case 'played': return 'Played'
    case 'recent': return 'Played lately'
    case 'friends': return 'Friends playing'
    case 'family': return 'Family shared'
  }
}

/** Which filters stand in for each other: any one of a group will do. */
function groupOf(filter: LibraryFilter): string {
  switch (filter.kind) {
    case 'store': return 'store'
    case 'installed': case 'notInstalled': return 'install'
    case 'played': case 'unplayed': return 'played'
    case 'tag': return `tag:${filter.tag}`
    default: return filter.kind
  }
}

/** Played lately: in the last two weeks. */
const RECENT_MS = 14 * 86_400_000

export type MatchContext = {
  /** Games a Discord friend is playing right now. */
  friendsPlaying?: ReadonlySet<string>
  now?: number
}

const playedAt = (game: Game) => game.lastPlayedUtc ? Date.parse(game.lastPlayedUtc) : NaN
const hasPlayed = (game: Game) => !!(game.playtimeMinutes && game.playtimeMinutes > 0) || !!game.lastPlayedUtc

/** Each kind of filter's test. */
const TESTS: { [K in LibraryFilter['kind']]: (game: Game, filter: Extract<LibraryFilter, { kind: K }>, context: MatchContext) => boolean } = {
  store: (game, filter) => game.store === filter.store || !!game.stores?.includes(filter.store),
  installed: game => onPc(game),
  notInstalled: game => !onPc(game),
  update: game => !!game.updateAvailable || !!game.variants?.some(variant => variant.updateAvailable),
  unplayed: game => !hasPlayed(game),
  played: game => hasPlayed(game),
  recent: (game, _filter, context) => (context.now ?? Date.now()) - playedAt(game) < RECENT_MS,
  friends: (game, _filter, context) => !!context.friendsPlaying?.has(game.id),
  family: game => !!game.familyShared,
  tag: (game, filter) => !!game.tags?.includes(filter.tag),
}

function matchesOne(game: Game, filter: LibraryFilter, context: MatchContext): boolean {
  return (TESTS[filter.kind] as (game: Game, filter: LibraryFilter, context: MatchContext) => boolean)(game, filter, context)
}

function collectStoresAndTags(game: Game, stores: Set<string>, tags: Set<string>) {
  if (game.stores?.length) {
    for (let j = 0; j < game.stores.length; j++) stores.add(game.stores[j])
  } else {
    stores.add(game.store)
  }
  if (game.tags?.length) {
    for (let j = 0; j < game.tags.length; j++) tags.add(game.tags[j])
  }
}

export function computeFilterFlags(games: readonly Game[], now = Date.now()) {
  let installed = false
  let notInstalled = false
  let update = false
  let recent = false
  let unplayed = false
  let family = false
  const stores = new Set<string>()
  const tags = new Set<string>()
  for (let i = 0; i < games.length; i++) {
    const game = games[i]
    if (onPc(game)) installed = true
    else notInstalled = true
    if (!update && (game.updateAvailable || game.variants?.some(variant => variant.updateAvailable))) update = true
    if (!unplayed && !hasPlayed(game)) unplayed = true
    if (!recent && game.lastPlayedUtc && now - Date.parse(game.lastPlayedUtc) < RECENT_MS) recent = true
    if (!family && game.familyShared) family = true
    collectStoresAndTags(game, stores, tags)
  }
  return { installed, notInstalled, update, recent, unplayed, family, stores: [...stores].sort(), tags }
}

/** Every group of filters matches (within a group, any one does). */
export function matchesFilters(game: Game, filters: readonly LibraryFilter[], context: MatchContext = {}): boolean {
  if (filters.length === 0) return true
  const groups = new Map<string, LibraryFilter[]>()
  for (const filter of filters) groups.set(groupOf(filter), [...(groups.get(groupOf(filter)) ?? []), filter])
  for (const group of groups.values()) if (!group.some(filter => matchesOne(game, filter, context))) return false
  return true
}

/** A search, read: the filters it names, hours bounds, what else must be in the title, and the words understood for the line under it. */
export type LibraryQuery = {
  filters: LibraryFilter[]
  minHours: number | null
  maxHours: number | null
  big: boolean
  small: boolean
  words: string[]
  /** What Exo understood, in the order typed, as short labels ("Racing", "Not played", "Over 20 hr"). */
  understood: string[]
}

const tag = (name: string): LibraryFilter => ({ kind: 'tag', tag: name })

/** Phrases first (longest wins), then single words. Each maps to its filters. */
const PHRASES: [string, LibraryFilter[]][] = [
  ['havent played yet', [{ kind: 'unplayed' }]],
  ['have not played', [{ kind: 'unplayed' }]],
  ['havent played', [{ kind: 'unplayed' }]],
  ['never played', [{ kind: 'unplayed' }]],
  ['not played', [{ kind: 'unplayed' }]],
  ['didnt play', [{ kind: 'unplayed' }]],
  ['not started', [{ kind: 'unplayed' }]],
  ['not installed', [{ kind: 'notInstalled' }]],
  ['isnt installed', [{ kind: 'notInstalled' }]],
  ['arent installed', [{ kind: 'notInstalled' }]],
  ['to install', [{ kind: 'notInstalled' }]],
  ['played recently', [{ kind: 'recent' }]],
  ['recently played', [{ kind: 'recent' }]],
  ['played lately', [{ kind: 'recent' }]],
  ['friends are playing', [{ kind: 'friends' }]],
  ['friends playing', [{ kind: 'friends' }]],
  ['my friends play', [{ kind: 'friends' }]],
  ['family shared', [{ kind: 'family' }]],
  ['family sharing', [{ kind: 'family' }]],
  ['shared with me', [{ kind: 'family' }]],
  ['needs an update', [{ kind: 'update' }]],
  ['need an update', [{ kind: 'update' }]],
  ['needs update', [{ kind: 'update' }]],
  ['update ready', [{ kind: 'update' }]],
  ['local co op', [tag('local co-op')]],
  ['split screen', [tag('local co-op')]],
  ['couch co op', [tag('local co-op')]],
  ['online co op', [tag('co-op')]],
  ['co op', [tag('co-op')]],
  ['single player', [tag('single-player')]],
  ['multi player', [tag('multiplayer')]],
  ['free to play', [tag('free to play')]],
  ['early access', [tag('early access')]],
  ['role playing', [tag('rpg')]],
  ['battle net', [{ kind: 'store', store: 'battlenet' }]],
  ['itch io', [{ kind: 'store', store: 'itch' }]],
]

const WORDS: Record<string, LibraryFilter[]> = {
  installed: [{ kind: 'installed' }], downloaded: [{ kind: 'installed' }], ready: [{ kind: 'installed' }],
  uninstalled: [{ kind: 'notInstalled' }],
  unplayed: [{ kind: 'unplayed' }], new: [{ kind: 'unplayed' }], backlog: [{ kind: 'unplayed' }],
  played: [{ kind: 'played' }],
  recent: [{ kind: 'recent' }], recently: [{ kind: 'recent' }], lately: [{ kind: 'recent' }],
  updates: [{ kind: 'update' }], update: [{ kind: 'update' }], outdated: [{ kind: 'update' }],
  family: [{ kind: 'family' }], shared: [{ kind: 'family' }], borrowed: [{ kind: 'family' }],
  friends: [{ kind: 'friends' }],
  coop: [tag('co-op')], cooperative: [tag('co-op')],
  multiplayer: [tag('multiplayer')], online: [tag('multiplayer')], pvp: [tag('multiplayer')], competitive: [tag('multiplayer')],
  singleplayer: [tag('single-player')], solo: [tag('single-player')], offline: [tag('single-player')],
  splitscreen: [tag('local co-op')], couch: [tag('local co-op')],
  racing: [tag('racing')], racer: [tag('racing')], racers: [tag('racing')], driving: [tag('racing')], cars: [tag('racing')],
  action: [tag('action')],
  adventure: [tag('adventure')], adventures: [tag('adventure')],
  rpg: [tag('rpg')], rpgs: [tag('rpg')], roleplaying: [tag('rpg')],
  strategy: [tag('strategy')], tactics: [tag('strategy')], tactical: [tag('strategy')],
  simulation: [tag('simulation')], simulator: [tag('simulation')], simulators: [tag('simulation')], sim: [tag('simulation')], sims: [tag('simulation')],
  sports: [tag('sports')], sport: [tag('sports')],
  casual: [tag('casual')], relaxing: [tag('casual')], cozy: [tag('casual')],
  indie: [tag('indie')], indies: [tag('indie')],
  mmo: [tag('mmo')], mmos: [tag('mmo')], mmorpg: [tag('mmo'), tag('rpg')],
  f2p: [tag('free to play')], free: [tag('free to play')],
  controller: [tag('controller')], gamepad: [tag('controller')], pad: [tag('controller')],
  steam: [{ kind: 'store', store: 'steam' }], epic: [{ kind: 'store', store: 'epic' }], gog: [{ kind: 'store', store: 'gog' }],
  riot: [{ kind: 'store', store: 'riot' }], xbox: [{ kind: 'store', store: 'xbox' }], ea: [{ kind: 'store', store: 'ea' }],
  ubisoft: [{ kind: 'store', store: 'ubisoft' }], uplay: [{ kind: 'store', store: 'ubisoft' }],
  battlenet: [{ kind: 'store', store: 'battlenet' }], blizzard: [{ kind: 'store', store: 'battlenet' }],
  amazon: [{ kind: 'store', store: 'amazon' }], rockstar: [{ kind: 'store', store: 'rockstar' }],
  itch: [{ kind: 'store', store: 'itch' }], local: [{ kind: 'store', store: 'local' }],
}

/** Words that carry nothing in a search for games ("show me the games I haven't played"). */
const FILLER = new Set([
  'a', 'an', 'the', 'my', 'me', 'i', 'im', 'ive', 'id', 'show', 'find', 'list', 'all', 'any', 'some', 'games', 'game',
  'that', 'which', 'what', 'with', 'and', 'or', 'are', 'is', 'on', 'in', 'from', 'of', 'to', 'for', 'yet', 'still',
  'havent', 'have', 'got', 'own', 'owned', 'library', 'please', 'by', 'whats', 'thats', 'can', 'play', 'titles', 'stuff',
])

const BIG = new Set(['big', 'huge', 'large', 'biggest', 'largest'])
const SMALL = new Set(['small', 'tiny', 'little', 'smallest'])
/** Big: 50 GB and up; small: under 10 GB, both of what is known (an installed game's size). */
const BIG_BYTES = 50 * 1024 ** 3
const SMALL_BYTES = 10 * 1024 ** 3

const HOURS = /\b(over|more than|at least|above|under|less than|below|fewer than)\s+(\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hour|hours)\b/g

/** A search, folded as titles are ("Haven't" reaches "havent", "co-op" reaches "co op"). */
function fold(query: string): string {
  return query.replace(/['’]/g, '').normalize('NFD').toLowerCase().replace(/\p{Mark}+/gu, '').replace(/[^\p{Letter}\p{Number}]+/gu, ' ').trim()
}

export function parseLibraryQuery(query: string): LibraryQuery {
  const result: LibraryQuery = { filters: [], minHours: null, maxHours: null, big: false, small: false, words: [], understood: [] }
  let text = ` ${query.toLowerCase().replace(/['’]/g, '')} `
  // Hours first, on the raw words, so "over 20 hours" doesn't read 20 as a title word.
  text = text.replace(HOURS, (_whole, word: string, amount: string) => {
    const hours = Number(amount)
    const over = /over|more|least|above/.test(word)
    if (over) result.minHours = hours; else result.maxHours = hours
    result.understood.push(`${over ? 'Over' : 'Under'} ${hours} hr`)
    return ' '
  })
  let rest = ` ${fold(text)} `
  const seen = new Set<string>()
  const take = (filters: LibraryFilter[]) => {
    for (const filter of filters) {
      const key = filterKey(filter)
      if (seen.has(key)) continue
      seen.add(key)
      result.filters.push(filter)
      result.understood.push(filterLabel(filter))
    }
  }
  for (const [phrase, filters] of PHRASES) {
    if (!rest.includes(` ${phrase} `)) continue
    rest = rest.replace(` ${phrase} `, ' ')
    take(filters)
  }
  for (const word of rest.split(' ').filter(Boolean)) {
    if (WORDS[word]) take(WORDS[word])
    else if (BIG.has(word)) { if (!result.big) result.understood.push('50 GB and up'); result.big = true }
    else if (SMALL.has(word)) { if (!result.small) result.understood.push('Under 10 GB'); result.small = true }
    else if (!FILLER.has(word)) result.words.push(word)
  }
  // "played" beside "not played" (or "installed" beside "not installed") is the phrase's: the plain word goes.
  if (seen.has('unplayed') && seen.has('played')) drop(result, 'played')
  if (seen.has('notInstalled') && seen.has('installed')) drop(result, 'installed')
  return result
}

function drop(result: LibraryQuery, kind: LibraryFilter['kind']) {
  const index = result.filters.findIndex(filter => filter.kind === kind)
  if (index < 0) return
  result.understood.splice(result.understood.indexOf(filterLabel(result.filters[index])), 1)
  result.filters.splice(index, 1)
}

/** Every title word the search names starts a word of the title (or of a game it holds). */
function titleHas(game: Game, words: readonly string[]): boolean {
  if (words.length === 0) return true
  const title = ` ${fold([game.title, ...(game.includes ?? []).map(part => part.title)].join(' '))} `
  return words.every(word => title.includes(` ${word}`))
}

export function matchesQuery(game: Game, query: LibraryQuery, context: MatchContext = {}): boolean {
  const hours = (game.playtimeMinutes ?? 0) / 60
  if (query.minHours != null && !(hours > query.minHours)) return false
  if (query.maxHours != null && !(hours < query.maxHours)) return false
  if (query.big && !((game.sizeBytes ?? 0) >= BIG_BYTES)) return false
  if (query.small && !(game.sizeBytes != null && game.sizeBytes > 0 && game.sizeBytes < SMALL_BYTES)) return false
  return matchesFilters(game, query.filters, context) && titleHas(game, query.words)
}

/** Nothing typed but spaces and filler: no search at all. */
export function isEmptyQuery(query: LibraryQuery): boolean {
  return !query.filters.length && !query.words.length && query.minHours == null && query.maxHours == null && !query.big && !query.small
}

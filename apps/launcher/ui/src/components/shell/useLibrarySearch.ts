import { useCallback, useDeferredValue, useMemo, useState } from 'react'
import type { Game } from '../../lib/host'
import { filterKey, isEmptyQuery, LIBRARY_TAGS, matchesFilters, matchesQuery, parseLibraryQuery, type LibraryFilter } from '../../lib/librarySearch'
import type { FilterOption } from './LibraryHead'

/** Genres first, then modes (librarySearch › LIBRARY_TAGS): where the Filter menu starts its Mode section. */
const FIRST_MODE = LIBRARY_TAGS.indexOf('single-player')

/**
 * The Filter menu's rows for this library: what a game here can be (Friends playing, greyed until the friends list has
 * someone on a game here), then each store with a game here, then the genres and modes Steam gives the games here. A row
 * nothing here matches is left out, so no one choice empties the grid by itself.
 */
function filterOptions(games: Game[], friendsPlaying: ReadonlySet<string> | null): FilterOption[] {
  const has = (filter: LibraryFilter) => games.some(game => matchesFilters(game, [filter]))
  const show: LibraryFilter[] = [{ kind: 'installed' }, { kind: 'notInstalled' }, { kind: 'update' }, { kind: 'recent' }, { kind: 'unplayed' }, { kind: 'family' }]
  const options: FilterOption[] = show.filter(has).map(filter => ({ section: 'Show', filter }))
  options.push({ section: 'Show', filter: { kind: 'friends' }, disabled: !friendsPlaying?.size })
  const stores = [...new Set(games.flatMap(game => game.stores?.length ? game.stores : [game.store]))].sort()
  if (stores.length > 1) options.push(...stores.map(store => ({ section: 'Store', filter: { kind: 'store', store } as LibraryFilter })))
  const tags = new Set(games.flatMap(game => game.tags ?? []))
  LIBRARY_TAGS.forEach((tag, index) => {
    if (tags.has(tag)) options.push({ section: index < FIRST_MODE ? 'Genre' : 'Mode', filter: { kind: 'tag', tag } })
  })
  return options
}

type Shelf = { heading: string; count: number | undefined; games: Game[]; note: string | null }

/**
 * The Library's search and Filter chips (DESIGN.md › Library): a search reads as filters and title words, and the result
 * is every game that matches, Continue's and the folders' too, in the sort's order, in place of the room's shelves.
 * Neither is saved. `browse` is the grid without them (All games, or Hidden games).
 */
export function useLibrarySearch(games: Game[], browse: Shelf, showHidden: boolean) {
  const [query, setQuery] = useState('')
  const [filters, setFilters] = useState<LibraryFilter[]>([])
  const typed = useDeferredValue(query)
  const read = useMemo(() => parseLibraryQuery(typed), [typed])
  // The friends list is read only once Filter is pointed at or a search names friends (LibraryPane › FriendsPlaying).
  const [friendsWanted, setFriendsWanted] = useState(false)
  const [friendsPlaying, setFriendsPlaying] = useState<ReadonlySet<string> | null>(null)
  const wantsFriends = friendsWanted || [...filters, ...read.filters].some(filter => filter.kind === 'friends')
  const searching = !showHidden && (filters.length > 0 || !isEmptyQuery(read))
  const found = useMemo(() => {
    if (!searching) return null
    const context = { friendsPlaying: friendsPlaying ?? undefined }
    return games.filter(game => matchesFilters(game, filters, context) && matchesQuery(game, read, context))
  }, [searching, games, filters, read, friendsPlaying])
  const toggleFilter = useCallback((filter: LibraryFilter) => setFilters(current =>
    current.some(item => filterKey(item) === filterKey(filter)) ? current.filter(item => filterKey(item) !== filterKey(filter)) : [...current, filter]), [])
  const clear = useCallback(() => { setFilters([]); setQuery('') }, [])
  const options = useMemo(() => filterOptions(games, friendsPlaying), [games, friendsPlaying])
  const shelf: Shelf = found ? { heading: 'Results', count: undefined, games: found, note: found.length ? null : 'No games match' } : browse
  return {
    query, setQuery, filters, toggleFilter, clear, read, found, options, shelf,
    /** Continue and Gifts show: neither a search nor Hidden games. */
    browsing: !found && !showHidden,
    wantsFriends, wantFriends: () => setFriendsWanted(true), setFriendsPlaying,
  }
}


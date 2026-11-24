import { useCallback, useDeferredValue, useEffect, useMemo, useState } from 'react'
import type { Game } from '../../lib/host'
import { computeFilterFlags, filterKey, isEmptyQuery, LIBRARY_TAGS, matchesFilters, matchesQuery, parseLibraryQuery, type LibraryFilter } from '../../lib/librarySearch'
import type { FilterOption } from './LibraryHead'

const NO_FILTERS: LibraryFilter[] = []
const EMPTY_QUERY = parseLibraryQuery('')

/** Genres first, then modes (librarySearch › LIBRARY_TAGS): where the Filter menu starts its Mode section. */
const FIRST_MODE = LIBRARY_TAGS.indexOf('single-player')

/**
 * The Filter menu's rows for this library: what a game here can be (Friends playing, greyed until the friends list has
 * someone on a game here), then each store with a game here, then the genres and modes Steam gives the games here. A row
 * nothing here matches is left out, so no one choice empties the grid by itself.
 */
const NO_OPTIONS: FilterOption[] = []

function filterOptions(games: Game[], friendsPlaying: ReadonlySet<string> | null): FilterOption[] {
  const flags = computeFilterFlags(games)
  const options: FilterOption[] = []
  if (flags.installed) options.push({ section: 'Show', filter: { kind: 'installed' } })
  if (flags.notInstalled) options.push({ section: 'Show', filter: { kind: 'notInstalled' } })
  if (flags.update) options.push({ section: 'Show', filter: { kind: 'update' } })
  if (flags.recent) options.push({ section: 'Show', filter: { kind: 'recent' } })
  if (flags.unplayed) options.push({ section: 'Show', filter: { kind: 'unplayed' } })
  if (flags.family) options.push({ section: 'Show', filter: { kind: 'family' } })
  options.push({ section: 'Show', filter: { kind: 'friends' }, disabled: !friendsPlaying?.size })
  if (flags.stores.length > 1) options.push(...flags.stores.map(store => ({ section: 'Store', filter: { kind: 'store', store } as LibraryFilter })))
  LIBRARY_TAGS.forEach((tag, index) => {
    if (flags.tags.has(tag)) options.push({ section: index < FIRST_MODE ? 'Genre' : 'Mode', filter: { kind: 'tag', tag } })
  })
  return options
}

type Shelf = { heading: string; games: Game[]; note: string | null }

/**
 * The Library's search and Filter chips (DESIGN.md › Library): a search reads as filters and title words, and the result
 * is every game that matches, Continue's and the folders' too, in the sort's order, in place of the room's shelves.
 * Neither is saved. `browse` is the grid without them (All games, or Hidden games).
 */
export function useLibrarySearch(games: Game[], browse: Shelf, showHidden: boolean, needOptions = true) {
  const [query, setQuery] = useState('')
  const [filters, setFilters] = useState<LibraryFilter[]>([])
  useEffect(() => {
    if (!showHidden) return
    setFilters([])
    setQuery('')
  }, [showHidden])
  const typed = useDeferredValue(query)
  const read = useMemo(() => parseLibraryQuery(typed), [typed])
  // The friends list is read only once Filter is pointed at or a search names friends (LibraryPane › FriendsPlaying).
  const [friendsWanted, setFriendsWanted] = useState(false)
  const [friendsPlaying, setFriendsPlaying] = useState<ReadonlySet<string> | null>(null)
  const wantsFriends = friendsWanted || (!showHidden && [...filters, ...read.filters].some(filter => filter.kind === 'friends'))
  const searching = !showHidden && (filters.length > 0 || !isEmptyQuery(read))
  const found = useMemo(() => {
    if (!searching) return null
    const context = { friendsPlaying: friendsPlaying ?? undefined }
    return games.filter(game => matchesFilters(game, filters, context) && matchesQuery(game, read, context))
  }, [searching, games, filters, read, friendsPlaying])
  const toggleFilter = useCallback((filter: LibraryFilter) => setFilters(current =>
    current.some(item => filterKey(item) === filterKey(filter)) ? current.filter(item => filterKey(item) !== filterKey(filter)) : [...current, filter]), [])
  const clear = useCallback(() => { setFilters([]); setQuery('') }, [])
  const options = useMemo(() => needOptions ? filterOptions(games, friendsPlaying) : NO_OPTIONS, [needOptions, games, friendsPlaying])
  const shelf: Shelf = found ? { heading: 'Results', games: found, note: found.length ? null : 'No games match' } : browse
  return {
    query: showHidden ? '' : query,
    setQuery,
    filters: showHidden ? NO_FILTERS : filters,
    toggleFilter,
    clear,
    read: showHidden ? EMPTY_QUERY : read,
    found,
    options,
    shelf,
    /** Continue and Gifts show: neither a search nor Hidden games. */
    browsing: !found && !showHidden,
    wantsFriends, wantFriends: () => setFriendsWanted(true), setFriendsPlaying,
  }
}


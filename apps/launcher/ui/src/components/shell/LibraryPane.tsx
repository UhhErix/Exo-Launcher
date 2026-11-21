import { CopyLogs } from '../ui/ErrorNote'
import { lazy, memo, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent, type RefObject } from 'react'
import { HomeDeck } from './HomeDeck'
import { FolderPanel } from './FolderPanel'
const FolderSheet = lazy(() => import('./FolderSheet').then(m => ({ default: m.FolderSheet })))
import { GameMenu, type GameMenuAt } from './GameMenu'
import { LibrarySelectBar } from './LibrarySelectBar'
import { type Game, type InstallProgress, type LibraryFolder, type SortMode } from '../../lib/host'
import { addToFolder, folderColor, folderTileId, freeFolderName, isFolderTile, moveFolder, newFolderId, renameFolder, saveFolder, sortFolder, withFolders, type FolderTile } from '../../lib/folders'
import { resolveGridCardLayout } from '../../lib/gridWindow'
import { transition, visible } from '../../lib/viewTransition'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { report } from '../../lib/report'
import { focusWhenActive } from '../../lib/focusTrap'
import { BrowseShelf } from '../BrowseShelf'
import { LibraryEmpty, LibraryTools, type LibraryRefreshRun } from './LibraryShelf'
import { setCoversShining } from '../GameCard'
import { markShellReady } from '../../lib/shellReady'
import { LibraryHead, type LibraryTab } from './LibraryHead'
import { useLibrarySearch } from './useLibrarySearch'
import { gamesFriendsPlay, useFriends } from '../../lib/friendsPlaying'

export type LibraryPaneProps = {
  mainRef: RefObject<HTMLElement | null>
  active: boolean
  /** The details sheet is up: the shelf stays put and takes no input. */
  overlayOpen: boolean
  /** Nothing to show and a reason why (or null when the PC simply has no installs). */
  empty: boolean
  error: string | null
  /** The one grid's games. */
  games: Game[]
  /** The games you hid: only in the Hidden view (the sort menu's Show section). */
  hidden: Game[]
  /** Their ids, and the change to them (shown at once, then saved). */
  hiddenIds: string[]
  onHidden: (next: string[]) => void
  /** Continue: what you are playing. */
  playing: Game[]
  selectedId: string | null
  activeGameId: string | null
  queuedIds: string[]
  sort: SortMode
  reduceMotion: boolean
  transferFor: (game: Game) => InstallProgress | null
  onActiveGameChange: (id: string) => void
  onOpen: (game: Game) => void
  /** Play (or install) straight from Continue and a card's menu. */
  onPlay: (game: Game) => void
  /** A Riot game's menu: switch to another saved Riot account, then Play. */
  onPlayAs: (game: Game, account: { key: string; name: string }) => void
  /** The card whose Play or Install is starting right now; its button says so. */
  startingId: string | null
  /** A card's menu: open its folder. */
  onOpenFolder: (game: Game) => void
  /** Opens the game's page with its Properties over it. */
  onProperties: (game: Game) => void
  onSort: (mode: SortMode) => void
  /** The Library's folders, and the change to them (shown at once, then saved). */
  folders: LibraryFolder[]
  onFolders: (next: LibraryFolder[]) => void
  onAddGame: () => void
  onActivateKey: () => void
  /** Library › Refresh: stores again, then every cover. */
  onRefresh: () => LibraryRefreshRun
  onRetry: () => void
  onStoreSettings: () => void
  /** Select mode's Uninstall: these games, one after another. */
  onUninstall: (games: Game[]) => Promise<void>
}

/**
 * The one card width for the grids, from the pane's own
 * width. It reaches CSS as --exo-card-w and the grids as a prop, so a width
 * change re-windows them even when their own boxes keep their size.
 */
function useCardWidth(mainRef: RefObject<HTMLElement | null>): number | null {
  const [width, setWidth] = useState<number | null>(null)
  useLayoutEffect(() => {
    const main = mainRef.current
    if (!main) return
    // Floor, not round: a rounded-up width can push the last column off the row.
    const measure = () => {
      if (main.clientWidth <= 0) return
      setWidth(Math.floor(resolveGridCardLayout(main.clientWidth).cardWidth * 100) / 100)
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(main)
    return () => observer.disconnect()
  }, [mainRef])
  return width
}

/**
 * Refresh: every store again (games that weren't showing appear), then the
 * store's art again for every game, installed or not (covers you chose stay).
 * Its glyph turns for the whole run; the covers shine only while the stores are
 * read again (DESIGN.md › Waits), then new art swaps in quietly as each cover
 * lands. The cards stay pressable throughout: the art can take minutes with a
 * big library, and a list that lands mid-press is what every push does already.
 */
/** One shine, seen whole: waits.css fades it in after 300ms and sweeps it in 1.6s. */
const ShineSweepMs = 1900

function useRefresh(onRefresh: () => LibraryRefreshRun): [boolean, () => void, string | null] {
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const run = useCallback(() => {
    if (refreshing) return
    setRefreshing(true)
    setError(null)
    const { done } = onRefresh()
    // The covers shine for the whole refresh, as the Store's tiles do, and for one full sweep at least: the stores alone
    // are read in about a third of a second, gone before the shine showed.
    const started = performance.now()
    const stopShining = () => window.setTimeout(() => setCoversShining(false), Math.max(0, ShineSweepMs - (performance.now() - started)))
    setCoversShining(true)
    done
      .catch((cause: unknown) => {
        report(cause, 'LibraryTools.refresh')
        setError(cause instanceof Error && cause.message ? cause.message : 'Try again.')
      })
      .finally(() => { setRefreshing(false); stopShining() })
  }, [refreshing, onRefresh])
  return [refreshing, run, error]
}


/** The open folder takes the keyboard back once a sheet over it closes, so Escape closes the folder. */
const focusPanel = () => requestAnimationFrame(() =>
  focusWhenActive(document.querySelector<HTMLElement>('.exo-folder-panel button[data-game-id]')))

/** A motion token in ms, in whichever unit it computes to (".3s" read as 0.3 made a flight last a blink). */
function tokenMs(styles: CSSStyleDeclaration, name: string, fallback: number): number {
  const value = styles.getPropertyValue(name).trim()
  const n = parseFloat(value)
  return Number.isFinite(n) ? (value.endsWith('ms') ? n : n * 1000) : fallback
}

/**
 * A copy of a cover, laid over the page where the cover was, flying into a folder's card on the spring over the flight
 * time: it shrinks to a paper's size in the folder's mouth and goes in as it lands. Nothing under Reduce motion.
 */
function dropInto(cover: Element | null, target: () => Element | null) {
  const picture = cover?.querySelector('img')
  if (!cover || !picture || shouldReduceMotion()) return
  const from = cover.getBoundingClientRect()
  const copy = document.createElement('div')
  copy.className = 'exo-filing-copy'
  Object.assign(copy.style, { left: `${from.left}px`, top: `${from.top}px`, width: `${from.width}px`, height: `${from.height}px`, borderRadius: getComputedStyle(cover).borderRadius })
  copy.append(picture.cloneNode())
  document.body.append(copy)
  // The folder's card once the grid has laid out without the game.
  requestAnimationFrame(() => {
    const to = target()?.getBoundingClientRect()
    if (!to) { copy.remove(); return }
    const scale = (to.width * 0.46) / from.width
    const x = to.left + to.width / 2 - (from.left + from.width / 2)
    const y = to.top + to.height * 0.42 - (from.top + from.height / 2)
    const styles = getComputedStyle(document.documentElement)
    const flight = tokenMs(styles, '--dur-flight', 300)
    const motion = copy.animate([
      { transform: 'none', opacity: 1 },
      { transform: `translate(${x}px, ${y}px) scale(${scale})`, opacity: 1, offset: 0.8 },
      { transform: `translate(${x}px, ${y + to.height * 0.1}px) scale(${scale})`, opacity: 0 },
    ], { duration: flight, easing: styles.getPropertyValue('--ease-spring').trim() || 'ease-out' })
    motion.finished.finally(() => copy.remove())
  })
}

/** A folder's card art on screen: where its panel and its sheet grow from and fold back into. */
const tileArt = (id: string) => visible(`[data-game-id="${CSS.escape(folderTileId(id))}"] [data-vt-art]`)

/** A card leaving where it is (out of the open folder, hidden, back from hidden) dissolves there, evenly, then `done`. */
function dissolve(card: Element | null | undefined, done: () => void) {
  if (!card || shouldReduceMotion()) { done(); return }
  // Over the flight time, so the ease-out (spent mostly in its first third) still reads as the card going, not gone.
  const styles = getComputedStyle(document.documentElement)
  card.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'scale(0.9)' }], {
    duration: tokenMs(styles, '--dur-flight', 300),
    easing: styles.getPropertyValue('--ease-out').trim() || 'ease-out',
    fill: 'forwards',
  }).finished.then(done, done)
}

/** A grid card's tile, while it is mounted. */
const gridTile = (id: string) => document.querySelector(`.exo-library-pane .exo-windowed-game-grid [data-game-id="${CSS.escape(id)}"]`)?.closest('.exo-tile')

const paneClass = (overlayOpen: boolean, selecting: boolean, isHome: boolean) =>
  `exo-library-pane min-w-0 flex-1${overlayOpen ? ' is-overlay-open' : ''}${selecting ? ' is-selecting' : ''}${isHome ? ' is-home' : ''}`

function FriendsReader({ games, onPlaying }: { games: Game[]; onPlaying: (ids: ReadonlySet<string>) => void }) {
  const { friends } = useFriends()
  useEffect(() => onPlaying(gamesFriendsPlay(friends, games)), [friends, games, onPlaying])
  return null
}

const FriendsPlaying = ({ on, ...rest }: { on: boolean } & Parameters<typeof FriendsReader>[0]) => on ? <FriendsReader {...rest} /> : null

function ShelfNote({ note, searching, onClear }: { note: string | null; searching: boolean; onClear: () => void }) {
  if (!note) return null
  return (
    <p className="exo-library-note">
      {note}
      {searching && <button type="button" className="exo-set-btn is-compact" onClick={onClear}>Clear search</button>}
    </p>
  )
}

function LibraryRefreshNotice({
  empty,
  error,
  refreshError,
  refreshing,
  onRetry,
  onRefresh,
}: {
  empty: boolean
  error: string | null
  refreshError: string | null
  refreshing: boolean
  onRetry: () => void
  onRefresh: () => void
}) {
  if (empty) return null
  if (error) {
    return (
      <div className="exo-library-refresh-error" role="status">
        <span>Showing saved games. Exo could not refresh them.</span>
        <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button>
        <CopyLogs error={error} />
      </div>
    )
  }
  if (!refreshError || refreshing) return null
  return (
    <div className="exo-library-refresh-error" role="alert">
      <span>Exo could not refresh the library.</span>
      <button type="button" className="exo-set-btn" onClick={onRefresh}>Try again</button>
      <CopyLogs error={refreshError} />
    </div>
  )
}

function shelfView(showHidden: boolean, hidden: Game[], allGrid: Game[]) {
  if (showHidden) {
    return {
      heading: 'Hidden games',
      games: hidden,
      note: hidden.length ? null : 'No hidden games',
    }
  }
  return {
    heading: 'All games',
    games: allGrid,
    note: allGrid.length === 0 && hidden.length > 0 ? 'Every game is hidden' : null,
  }
}

export const LibraryPane = memo(function LibraryPane(props: LibraryPaneProps) {
  const { overlayOpen } = props
  const cardWidth = useCardWidth(props.mainRef)
  const { active, sort, onSort, onActivateKey, onAddGame } = props
  const [refreshing, runRefresh, refreshError] = useRefresh(props.onRefresh)
  const [menu, setMenu] = useState<GameMenuAt | null>(null)
  const { onOpen, folders, onFolders, overlayOpen: covered, hiddenIds, onHidden } = props
  const [tab, setTab] = useState<LibraryTab>('home')
  const [focusSearch, setFocusSearch] = useState(false)
  const [homeSearchOpen, setHomeSearchOpen] = useState(false)
  const [showHidden, setShowHidden] = useState(false)
  const { playing, games } = props
  const grid = useMemo(() => withFolders(games, games, folders), [games, folders])
  const search = useLibrarySearch(
    games,
    shelfView(showHidden, props.hidden, grid),
    showHidden,
  )
  const { shelf, found } = search
  const showingHome = !props.empty && tab !== 'all' && !showHidden
  const deckGames = useMemo(() => {
    if (search.query.trim()) {
      return shelf.games.filter((item): item is Game => !isFolderTile(item))
    }
    return playing.length ? playing : games.slice(0, 20)
  }, [search.query, shelf.games, playing, games])
  const openSearchView = useCallback(() => {
    if (showingHome) {
      setHomeSearchOpen(true)
      return
    }
    setShowHidden(false)
    setTab('all')
    setFocusSearch(true)
  }, [showingHome])
  useEffect(() => {
    if (!active || covered) return
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'f' || event.shiftKey || event.altKey) return
      event.preventDefault()
      openSearchView()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [active, covered, openSearchView])
  const [picked, setPicked] = useState<ReadonlySet<string> | null>(null)
  const anchor = useRef<string | null>(null)
  const selectMode = useCallback((on: boolean) => {
    if (on && tab === 'home') setTab('all')
    setPicked(on ? new Set() : null)
    anchor.current = null
  }, [tab])
  const pickable = useMemo(() => shelf.games.filter(game => !isFolderTile(game)), [shelf.games])
  const pickedGames = useMemo(() => picked ? pickable.filter(game => picked.has(game.id)) : [], [picked, pickable])
  const selecting = !!picked
  const pick = useCallback((game: Game, run: boolean) => {
    const to = pickable.indexOf(game)
    if (to < 0) return
    const from = run && anchor.current ? pickable.findIndex(item => item.id === anchor.current) : -1
    if (from < 0) anchor.current = game.id
    setPicked(current => {
      if (!current) return current
      const next = new Set(current)
      if (from >= 0) for (const item of pickable.slice(Math.min(from, to), Math.max(from, to) + 1)) next.add(item.id)
      else if (!next.delete(game.id)) next.add(game.id)
      return next
    })
  }, [pickable])
  const hiddenView = useCallback((on: boolean) => {
    if (on && tab === 'home') setTab('all')
    setShowHidden(on)
    setPicked(current => current && new Set())
  }, [tab])
  const switchTab = useCallback((next: LibraryTab) => {
    setTab(next)
    setFocusSearch(false)
    setHomeSearchOpen(false)
    setShowHidden(false)
    setPicked(null)
    if (next !== 'all') search.clear()
  }, [search])
  const resetScroll = useRef(false)
  useEffect(() => {
    if (!resetScroll.current) { resetScroll.current = true; return }
    if (props.mainRef.current) props.mainRef.current.scrollTop = 0
  }, [props.mainRef, tab, showHidden, sort, search.query, search.filters])
  // The open folder; a new `rename` key opens it again with its name ready to type.
  const [open, setOpen] = useState<{ id: string; rename: number } | null>(null)
  const openTile = useMemo(() => open ? grid.find((item): item is FolderTile => isFolderTile(item) && item.folder.id === open.id) : undefined, [open, grid])
  // A folder grows out of its card into its panel and back into it (DESIGN.md › Motion › Move: art's flight).
  // Its card's slot stays empty while it is open, and as it lands back its front swings shut (home.css › exo-folder-shut).
  const openFolder = useCallback((id: string, rename = false) => {
    // Its own card, never what was pressed or focused last: that (a Continue banner, say) stayed hidden after.
    const from = tileArt(id)
    void transition(() => { setOpen({ id, rename: rename ? Date.now() : 0 }); from?.toggleAttribute('data-exo-away', true) }, {
      kind: 'morph',
      morph: { name: 'exo-folder', from, to: () => visible('.exo-folder-panel') },
    })
  }, [])
  const closeFolder = useCallback(() => {
    const id = open?.id
    const shut = () => {
      const art = id ? tileArt(id) : null
      art?.removeAttribute('data-exo-away')
      art?.classList.add('exo-folder-shut')
      return art
    }
    void transition(() => { setOpen(null); shut() }, {
      kind: 'morph',
      back: true,
      morph: { name: 'exo-folder', from: visible('.exo-folder-panel'), to: () => id ? tileArt(id) : null },
    }).then(() => {
      const art = id ? tileArt(id) : null
      art?.addEventListener('animationend', () => art.classList.remove('exo-folder-shut'), { once: true })
    })
  }, [open])
  const select = useCallback((game: Game, event?: MouseEvent) => {
    if (selecting) pick(game, !!event?.shiftKey)
    else if (isFolderTile(game)) openFolder(game.folder.id); else onOpen(game)
  }, [openFolder, onOpen, selecting, pick])
  // A folder dragged onto another moves beside it; not while Select picks games.
  const moveTo = useMemo(() => picked ? undefined : (from: string, to: string, after: boolean) => onFolders(moveFolder(folders, from, to, after)),
    [picked, folders, onFolders])
  // New folder and Edit folder: a sheet over the Library (FolderSheet).
  const [sheet, setSheet] = useState<(LibraryFolder & { isNew: boolean }) | null>(null)
  const newFolder = useCallback((gameIds: string[] = []) => setSheet({
    id: newFolderId(folders), name: freeFolderName(folders), color: 'blue', games: gameIds, isNew: true,
  }), [folders])
  const editFolder = useCallback((id: string) => {
    const folder = folders.find(item => item.id === id)
    if (folder) setSheet({ ...folder, color: folderColor(folder.color), isNew: false })
  }, [folders])
  const { mainRef } = props
  // Saved, the sheet folds into the folder's card: a new one at the front of All games (scrolled into sight first),
  // which then glows once in its color. From inside the open folder the sheet just closes over it.
  const saveSheet = useCallback((folder: LibraryFolder) => {
    const next = saveFolder(folders, folder)
    const main = mainRef.current
    const front = main?.querySelector<HTMLElement>('.exo-windowed-game-grid')
    if (main && front && open?.id !== folder.id) {
      const top = front.getBoundingClientRect().top - main.getBoundingClientRect().top
      if (top < 0 || top > main.clientHeight - 160) main.scrollTop += top - 96
    }
    const land = () => {
      const art = tileArt(folder.id)
      if (!art) return
      art.classList.add('exo-folder-landing')
      art.addEventListener('animationend', () => art.classList.remove('exo-folder-landing'), { once: true })
    }
    void transition(() => { onFolders(next); setSheet(null) }, open?.id === folder.id ? { kind: 'none' } : {
      kind: 'morph',
      morph: { name: 'exo-folder', from: visible('.exo-folder-sheet'), to: () => tileArt(folder.id) },
    }).then(() => { if (open?.id === folder.id) focusPanel(); else window.setTimeout(land, 260) })
  }, [folders, onFolders, open, mainRef])
  const cancelSheet = useCallback(() => { setSheet(null); if (open) focusPanel() }, [open])
  // A game filed from its menu moves, rather than blinking out of one place and into another: into a folder from the
  // grid or Continue, a copy of its cover drops into the folder's card and goes in (the card stays in place, live,
  // under it); out of the open folder, its card dissolves where it is, and a folder it emptied fades away. Its other
  // folders keep it.
  const fileGame = useCallback((next: LibraryFolder[]) => {
    const game = menu?.game
    if (!game || isFolderTile(game)) { onFolders(next); return }
    const holds = (list: LibraryFolder[], id: string) => list.some(folder => folder.id === id && folder.games.includes(game.id))
    const into = next.find(folder => folder.games.includes(game.id) && !holds(folders, folder.id))?.id
    const out = folders.find(folder => folder.games.includes(game.id) && !holds(next, folder.id))?.id
    const card = menu.from?.closest('.exo-tile')
    const inPanel = !!card?.closest('.exo-folder-panel')
    if (out && inPanel && open?.id === out) {
      // Out of the open folder: its card dissolves where it is, then the folder closes the gap (a flight from the panel
      // to the grid behind it read choppy).
      const emptied = !next.some(folder => folder.id === out)
      dissolve(card, () => emptied ? void transition(() => { onFolders(next); setOpen(null) }, { kind: 'fade' }) : onFolders(next))
      return
    }
    if (into && !inPanel) dropInto(card?.querySelector('[data-vt-art]') ?? visible(`[data-game-id="${CSS.escape(game.id)}"] [data-vt-art]`), () => tileArt(into))
    onFolders(next)
  }, [menu, folders, onFolders, open])
  // Hide and Unhide: the card dissolves where it is (the grid, the Hidden view or the open folder; a folder it leaves
  // empty fades away), then it goes to the other list. From Continue nothing moves.
  const hideGame = useCallback((game: Game) => {
    const next = hiddenIds.includes(game.id) ? hiddenIds.filter(id => id !== game.id) : [...hiddenIds, game.id]
    const card = menu?.game.id === game.id ? menu.from?.closest('.exo-tile') : null
    const emptied = !!openTile && !!card?.closest('.exo-folder-panel') && openTile.folder.games.every(item => item.id === game.id)
    dissolve(card, () => emptied ? void transition(() => { onHidden(next); setOpen(null) }, { kind: 'fade' }) : onHidden(next))
  }, [menu, hiddenIds, onHidden, openTile])
  // Every card has a menu now: a game can always go into a folder. In Select mode the bar acts instead.
  const openMenu = useCallback((at: GameMenuAt) => { if (!selecting) setMenu(at) }, [selecting])
  // Select mode's bar. Hide (or Unhide): the picked cards dissolve together, then go to the other list. Add to folder:
  // each picked cover drops into the folder's card, as one filed from its menu; New folder opens with them ticked.
  const hidePicked = useCallback(() => {
    const ids = new Set(pickedGames.map(game => game.id))
    const next = showHidden ? hiddenIds.filter(id => !ids.has(id)) : [...hiddenIds, ...ids]
    setPicked(null)
    void Promise.all([...ids].map(id => new Promise<void>(done => dissolve(gridTile(id), done)))).then(() => onHidden(next))
  }, [pickedGames, showHidden, hiddenIds, onHidden])
  const filePicked = useCallback((folderId: string | null) => {
    const ids = pickedGames.map(game => game.id)
    setPicked(null)
    if (!folderId) { newFolder(ids); return }
    for (const id of ids) dropInto(gridTile(id)?.querySelector('[data-vt-art]') ?? null, () => tileArt(folderId))
    onFolders(ids.reduce((list, id) => addToFolder(list, id, folderId), folders))
  }, [pickedGames, newFolder, onFolders, folders])
  const { onUninstall } = props
  const uninstallPicked = useCallback(() => { void onUninstall(pickedGames); setPicked(null) }, [onUninstall, pickedGames])
  // The Library is up: a usable start (an update's startup check).
  useEffect(markShellReady, [])
  // Escape ends Select mode and Ctrl+A picks every game, unless a sheet, a folder or a menu over the grid takes the key.
  useEffect(() => {
    if (!active || covered) setPicked(null)
  }, [active, covered])
  // Ended from its bar, the keyboard goes back to the grid rather than the page.
  const wasSelecting = useRef(false)
  useEffect(() => {
    if (selecting || !wasSelecting.current) { wasSelecting.current = selecting; return }
    wasSelecting.current = false
    requestAnimationFrame(() => {
      const main = mainRef.current
      if (document.activeElement !== document.body || !main) return
      const to = main.querySelector<HTMLElement>('.exo-windowed-game-grid [tabindex="0"]') ?? main.querySelector<HTMLElement>('.exo-library-tools button')
      focusWhenActive(to)
    })
  }, [selecting, mainRef])
  const blocked = [sheet, open, menu].some(Boolean)
  useEffect(() => {
    if (!selecting || blocked) return
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.isComposing) return
      if (event.key === 'Escape') {
        event.preventDefault()
        selectMode(false)
      } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a'
        && !(event.target instanceof HTMLElement && event.target.closest('input, textarea, [contenteditable="true"]'))) {
        event.preventDefault()
        setPicked(new Set(pickable.map(game => game.id)))
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [selecting, blocked, pickable, selectMode])
  // Retained rooms keep their grid, but menus must leave with their route. A game page over the
  // Library keeps them (the pane is inert under it), so the shelf's header never changes height.
  const tools = useMemo(() => active ? (
    <LibraryTools selecting={selecting} onSelecting={selectMode} sort={sort} onSort={onSort} hidden={showHidden} onHidden={hiddenView}
      onActivateKey={onActivateKey} onNewFolder={() => newFolder()} onAddGame={onAddGame} refreshing={refreshing} onRefresh={runRefresh} showSort={!showingHome} compact={showingHome} />
  ) : null, [active, selecting, selectMode, sort, onSort, showHidden, hiddenView, onActivateKey, newFolder, onAddGame, refreshing, runRefresh, showingHome])
  return (
    <div className="relative z-10 flex min-h-0 min-w-0 flex-1">
      <main
        ref={props.mainRef}
        className={paneClass(overlayOpen, selecting, showingHome)}
        inert={overlayOpen ? true : undefined}
        style={cardWidth == null ? undefined : { '--exo-card-w': `${cardWidth}px` } as CSSProperties}
      >
        {!showingHome ? (
          <LibraryHead
            empty={props.empty}
            onBack={() => switchTab('home')}
            total={games.length}
            found={found}
            query={search.query}
            onQuery={next => { if (showHidden && next) setShowHidden(false); search.setQuery(next) }}
            understood={search.read.understood}
            filters={search.filters}
            options={search.options}
            onFilter={filter => { if (showHidden) setShowHidden(false); search.toggleFilter(filter) }}
            onClear={search.clear}
            onWarmFilters={search.wantFriends}
            tools={tools}
            autoFocusSearch={focusSearch}
          />
        ) : null}
        <LibraryRefreshNotice
          empty={props.empty}
          error={props.error}
          refreshError={refreshError}
          refreshing={refreshing}
          onRetry={props.onRetry}
          onRefresh={runRefresh}
        />
        {props.empty ? (
          <LibraryEmpty
            error={props.error}
            onRetry={props.onRetry}
            onAddGame={props.onAddGame}
            onActivateKey={props.onActivateKey}
            onStoreSettings={props.onStoreSettings}
          />
        ) : showingHome ? (
          <HomeDeck
            onTab={switchTab}
            query={search.query}
            onQuery={search.setQuery}
            searchOpen={homeSearchOpen}
            onSearchOpenChange={setHomeSearchOpen}
            games={deckGames}
            active={active && !covered}
            blocked={blocked}
            transferFor={props.transferFor}
            startingId={props.startingId}
            onOpen={onOpen}
            onPlay={props.onPlay}
            onMenu={openMenu}
            tools={tools}
          />
        ) : (
          <div className="flex min-h-0 min-w-0 w-full flex-1 flex-col">
            <div className="exo-library-grid">
              <BrowseShelf
                heading={shelf.heading}
                games={shelf.games}
                selectedId={props.selectedId}
                activeGameId={props.activeGameId}
                onActiveGameChange={props.onActiveGameChange}
                onSelect={select}
                onMenu={openMenu}
                onMoveFolder={moveTo}
                transferFor={props.transferFor}
                queuedIds={props.queuedIds}
                pickedIds={picked}
                scrollRootRef={props.mainRef}
                cardWidth={cardWidth}
              />
            </div>
            <ShelfNote note={shelf.note} searching={!!found} onClear={search.clear} />
          </div>
        )}
      </main>
      <FriendsPlaying on={search.wantsFriends} games={games} onPlaying={search.setFriendsPlaying} />
      {openTile && active ? (
        <FolderPanel
          key={`${openTile.folder.id}:${open?.rename ?? 0}`}
          folder={openTile.folder}
          renaming={!!open?.rename}
          inert={covered}
          cardWidth={cardWidth}
          selectedId={props.selectedId}
          queuedIds={props.queuedIds}
          transferFor={props.transferFor}
          onRename={name => onFolders(renameFolder(folders, openTile.folder.id, name))}
          onSort={folderSort => onFolders(sortFolder(folders, openTile.folder.id, folderSort))}
          onEdit={() => editFolder(openTile.folder.id)}
          onClose={closeFolder}
          onOpen={onOpen}
          onMenu={openMenu}
        />
      ) : null}
      {picked ? (
        <LibrarySelectBar games={pickedGames} total={pickable.length} hiddenView={showHidden} folders={folders}
          onAll={() => setPicked(new Set(pickable.map(game => game.id)))} onClear={() => setPicked(new Set())} onDone={() => selectMode(false)}
          onHide={hidePicked} onFolder={filePicked} onUninstall={uninstallPicked} />
      ) : null}
      {sheet && active ? (
        <Suspense fallback={null}><FolderSheet key={sheet.id} folder={sheet} library={games} onSave={saveSheet} onCancel={cancelSheet} /></Suspense>
      ) : null}
      {menu ? (
        <GameMenu
          key={`${menu.game.id}:${menu.x}:${menu.y}`}
          at={menu}
          folders={folders}
          hidden={hiddenIds.includes(menu.game.id)}
          onFolders={fileGame}
          onHide={hideGame}
          onNewFolder={id => newFolder([id])}
          onEditFolder={editFolder}
          onClose={() => setMenu(current => (current === menu ? null : current))}
          onPlay={props.onPlay}
          onPlayAs={props.onPlayAs}
          onOpenFolder={props.onOpenFolder}
          onProperties={props.onProperties}
        />
      ) : null}
    </div>
  )
})

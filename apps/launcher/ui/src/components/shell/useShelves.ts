import { useMemo } from 'react'
import type { Game, InstallProgress, LauncherSettings } from '../../lib/host'
import { isPlayableLibraryGame } from '../../lib/catalogHygiene'
import { isInstallingGame, librarySortMode } from '../../lib/library'
import { sortGames } from '../../lib/utils'

export type Shelves = {
  /** The one grid: every card the library shows, sorted the user's way. Installed or not is a mark, not a place. Empty means the empty state. */
  library: Game[]
  /** What you are playing: running, then downloading or updating, then the most recently played; four at most (three across shows three, two across all four). */
  playing: Game[]
  /** The games you hid: out of the grid, Continue and folders; Library's Hidden view lists them, sorted the same way. */
  hidden: Game[]
}

const NONE: Game[] = []

/** A card the library shows: playable, and installed, owned, downloading or a local folder. */
function isShelfGame(game: Game, transferId: string | null, showTools: boolean): boolean {
  if (!isPlayableLibraryGame(game, showTools)) return false
  if (isInstallingGame(game, transferId)) return true
  if (game.installed || game.owned) return true
  return game.store === 'local'
}

const CONTINUE_MAX = 8

function continueOrder(games: Game[], transferId: string | null): Game[] {
  const played = (game: Game) => (game.lastPlayedUtc ? Date.parse(game.lastPlayedUtc) : 0)
  const rank = (game: Game) => {
    if (game.isRunning) return 0
    if (isInstallingGame(game, transferId)) return 1
    if (game.installed && played(game) > 0) return 2
    if (game.installed) return 3
    return 4
  }
  return [...games]
    .sort((a, b) =>
      rank(a) - rank(b)
      || played(b) - played(a)
      || (b.playtimeMinutes ?? 0) - (a.playtimeMinutes ?? 0)
      || a.title.localeCompare(b.title))
    .slice(0, CONTINUE_MAX)
}

/**
 * The library for its three places (DESIGN.md › Library): Continue, Friends
 * are playing, and one grid, less the games you hid. Pure derivation over
 * `games`. `recent` is the host's launch order from the latest library read.
 */
export function useShelves(games: Game[], progress: InstallProgress | null, settings: LauncherSettings | null, recent: string[] | undefined): Shelves {
  const sortMode = settings?.sortMode
  const showTools = settings?.showLibraryTools === true
  const transferId = progress?.isActive ? progress.gameId : null

  const hiddenIds = settings?.hiddenGames
  const all = useMemo(
    () => sortGames(games.filter((game) => isShelfGame(game, transferId, showTools)), librarySortMode(sortMode), recent ?? []),
    [games, sortMode, recent, transferId, showTools],
  )
  const [library, hidden] = useMemo(() => {
    if (!hiddenIds?.length) return [all, NONE]
    const out = new Set(hiddenIds)
    return [all.filter(game => !out.has(game.id)), all.filter(game => out.has(game.id))]
  }, [all, hiddenIds])
  const playing = useMemo(() => continueOrder(library, transferId), [library, transferId])

  return useMemo(() => ({ library, playing, hidden }), [library, playing, hidden])
}

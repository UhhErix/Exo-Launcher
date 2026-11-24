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

const CONTINUE_MAX = 20
const continueCollator = new Intl.Collator(undefined, { sensitivity: 'base' })

export function continueOrder(games: Game[], transferId: string | null): Game[] {
  const entries = games.map((game) => {
    const played = game.lastPlayedUtc ? Date.parse(game.lastPlayedUtc) : 0
    const rank = game.isRunning ? 0
      : isInstallingGame(game, transferId) ? 1
      : game.installed && played > 0 ? 2
      : game.installed ? 3
      : 4
    return { game, rank, played: Number.isNaN(played) ? 0 : played, playtime: game.playtimeMinutes ?? 0 }
  })
  entries.sort((a, b) =>
    a.rank - b.rank
    || b.played - a.played
    || b.playtime - a.playtime
    || continueCollator.compare(a.game.title, b.game.title))
  return entries.slice(0, CONTINUE_MAX).map((entry) => entry.game)
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

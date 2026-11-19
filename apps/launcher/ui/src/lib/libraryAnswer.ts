import type { InstallProgress, LibraryResponse, StoreStatus } from './host'

type Answer = Pick<LibraryResponse, 'games' | 'stores' | 'progress' | 'queuedGameIds' | 'recent'>

/**
 * What the page takes from a library answer. The game list is dropped when a pushed snapshot landed after the request was
 * made (the push is newer); everything else in the answer (recent, store matrix, queue, active download) is not in the
 * push and is always taken. Each part is null when there is nothing to take.
 */
export function libraryAnswerParts(res: Answer, gamesStale: boolean): {
  games: LibraryResponse['games'] | null
  recent: string[] | null
  stores: StoreStatus[] | null
  queuedGameIds: string[] | null
  progress: InstallProgress | null
} {
  return {
    games: gamesStale ? null : res.games,
    recent: res.recent ?? null,
    stores: res.stores?.length ? res.stores : null,
    queuedGameIds: res.queuedGameIds ?? null,
    progress: res.progress?.isActive ? res.progress : null,
  }
}

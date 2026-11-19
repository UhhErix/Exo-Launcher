import { memo, useId, type MouseEvent, type RefObject } from 'react'
import type { Game, InstallProgress } from '../lib/host'
import { WindowedGameGrid } from './WindowedGameGrid'
import type { GameMenuAt } from './shell/GameMenu'

export type BrowseShelfProps = {
  /** Games are already filtered and sorted in their final visual order. */
  games: Game[]
  selectedId: string | null
  activeGameId: string | null
  onActiveGameChange: (gameId: string) => void
  onSelect: (game: Game, event?: MouseEvent) => void
  onMenu?: (at: GameMenuAt) => void
  onMoveFolder?: (folderId: string, toId: string, after: boolean) => void
  transferFor?: (game: Game) => InstallProgress | null
  heading: string
  queuedIds?: string[]
  /** Select mode: the picked games' ids; null outside it. */
  pickedIds?: ReadonlySet<string> | null
  scrollRootRef: RefObject<HTMLElement | null>
  cardWidth: number | null
}

export const BrowseShelf = memo(function BrowseShelf({
  games,
  selectedId,
  activeGameId,
  onActiveGameChange,
  onSelect,
  onMenu,
  onMoveFolder,
  transferFor,
  heading,
  queuedIds = [],
  pickedIds,
  scrollRootRef,
  cardWidth,
}: BrowseShelfProps) {
  const headingId = useId()

  return (
    <section className="min-w-0" aria-labelledby={headingId}>
      <header className="exo-shelf-head">
        <h2 className="exo-shelf-title" id={headingId}>{heading}</h2>
      </header>

      {games.length === 0 ? null : (
        <WindowedGameGrid
          games={games}
          selectedId={selectedId}
          activeGameId={activeGameId}
          onActiveGameChange={onActiveGameChange}
          onSelect={onSelect}
          onMenu={onMenu}
          onMoveFolder={onMoveFolder}
          transferFor={transferFor}
          queuedIds={queuedIds}
          pickedIds={pickedIds}
          layoutKey={heading}
          scrollRootRef={scrollRootRef}
          labelledBy={headingId}
          cardWidth={cardWidth}
        />
      )}
    </section>
  )
})

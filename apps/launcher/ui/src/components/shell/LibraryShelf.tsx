import { useState } from 'react'
import { Menu } from '../ui/Menu'
import { DownloadsSheet } from '../DownloadsSheet'
import { ChevronDown, Download, FolderPlus, Controller, Key, More, Refresh, SelectMany } from '../../brand/icons'
import type { SortMode } from '../../lib/host'
import { SORT_LABELS, SORT_OPTIONS } from '../../lib/library'
import { ErrorState } from '../ui/ErrorState'

const SORT_ITEMS = SORT_OPTIONS.map((mode) => ({ id: mode, label: SORT_LABELS[mode], section: 'Sort by' }))
const ADD_ITEMS = [
  { id: 'new-folder', label: 'New folder', icon: <FolderPlus size={16} /> },
  { id: 'game', label: 'Add a game', icon: <Controller size={16} /> },
  { id: 'key', label: 'Activate key', icon: <Key size={16} /> },
]

/**
 * The grid's two tools. The sort menu's Show section is what the grid lists: All games, or Hidden games (only the games
 * you hid). More holds the rest: adding (a folder, a game's .exe, a key), Select games, which picks games to act on
 * together (its bar's Done, or Escape, ends it), and Refresh library, while the covers shine.
 */
export function LibraryTools({ selecting, onSelecting, sort, onSort, hidden, onHidden, onActivateKey, onNewFolder, onAddGame, refreshing, onRefresh }: {
  selecting: boolean
  onSelecting: (on: boolean) => void
  sort: SortMode
  onSort: (mode: SortMode) => void
  hidden: boolean
  onHidden: (hidden: boolean) => void
  onActivateKey: () => void
  /** A Library folder: its name, color and games (FolderSheet). */
  onNewFolder: () => void
  /** A game's own .exe. */
  onAddGame: () => void
  refreshing: boolean
  onRefresh: () => void
}) {
  const [downloads, setDownloads] = useState(false)
  const actions: Record<string, () => void> = { 'new-folder': onNewFolder, game: onAddGame, key: onActivateKey, select: () => onSelecting(true), refresh: onRefresh, downloads: () => setDownloads(true) }
  return (
    <div className="exo-library-tools">
      <DownloadsSheet open={downloads} onClose={() => setDownloads(false)} />
      <Menu
        label="Sort library"
        anchor="--exo-sort-menu"
        offset={4}
        triggerClassName="exo-shelf-sort-trigger"
        trigger={<>{SORT_LABELS[sort]}<ChevronDown size={12} /></>}
        items={[
          ...SORT_ITEMS.map(item => ({ ...item, on: item.id === sort })),
          { id: 'all', label: 'All games', section: 'Show', on: !hidden },
          { id: 'hidden', label: 'Hidden games', section: 'Show', on: hidden },
        ]}
        onAction={(id) => id === 'all' || id === 'hidden' ? onHidden(id === 'hidden') : onSort(id as SortMode)}
      />
      <Menu
        label="More"
        anchor="--exo-library-more"
        offset={8}
        triggerClassName="exo-library-more"
        trigger={<More size={16} />}
        items={[
          ...ADD_ITEMS,
          ...(selecting ? [] : [{ id: 'select', label: 'Select games', icon: <SelectMany size={16} /> }]),
          { id: 'downloads', label: 'Downloads', icon: <Download size={16} /> },
          { id: 'refresh', label: 'Refresh library', icon: <Refresh size={16} />, disabled: refreshing },
        ]}
        onAction={id => actions[id]?.()}
      />
    </div>
  )
}

/** Library › Refresh under way: stores, once every store has been read again; done, once every cover has too (the covers shine until then). */
export type LibraryRefreshRun = { stores: Promise<unknown>; done: Promise<unknown> }

/** The shelf with nothing on it: a load failure, or no games from any store yet. */
export function LibraryEmpty({ error, onRetry, onAddGame, onActivateKey, onStoreSettings }: {
  error: string | null
  onRetry: () => void
  onAddGame: () => void
  onActivateKey: () => void
  onStoreSettings: () => void
}) {
  if (error) {
    return (
      <ErrorState title="Library unavailable" message="Exo could not load your games." log={error} onRetry={onRetry} />
    )
  }
  return (
    <div className="exo-empty">
      <h2>No games yet</h2>
      <div className="exo-empty-actions">
        <button type="button" className="exo-set-btn" onClick={onStoreSettings}>Store settings</button>
        <button type="button" className="exo-set-btn" onClick={onAddGame}>Add a game</button>
        <button type="button" className="exo-set-btn" onClick={onActivateKey}>Activate key</button>
      </div>
    </div>
  )
}

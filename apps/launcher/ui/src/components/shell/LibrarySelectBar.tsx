import { useRef } from 'react'
import { Eye, EyeOff, Folder, FolderPlus, Trash } from '../../brand/icons'
import type { Game, LibraryFolder } from '../../lib/host'
import { useLens } from '../../lib/glass'
import { formatSize } from '../../lib/utils'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Menu } from '../ui/Menu'

/**
 * Select mode's bar: glass floating over the grid's foot, as Discord's call bar does. The count, then what applies to
 * every picked game: Hide (Unhide in Hidden games), Add to folder, Uninstall for installed games that aren't running
 * (it asks first, as in a game's Properties; Remove when they were all added from a folder); then Select all or Clear, and Done.
 */
export function LibrarySelectBar({ games, total, hiddenView, folders, onAll, onClear, onDone, onHide, onFolder, onUninstall }: {
  /** The picked games. */
  games: Game[]
  /** How many the grid can pick. */
  total: number
  hiddenView: boolean
  folders: LibraryFolder[]
  onAll: () => void
  onClear: () => void
  onDone: () => void
  onHide: () => void
  /** Into this folder; null: a new one. */
  onFolder: (folderId: string | null) => void
  onUninstall: () => void
}) {
  const count = games.length
  const removable = count > 0 && games.every(game => game.installed && !game.canStop && !game.isRunning)
  const verb = games.every(game => game.store === 'local') ? 'Remove' : 'Uninstall'
  const freed = games.reduce((sum, game) => sum + (game.sizeBytes ?? 0), 0)
  const question = verb === 'Remove' ? `Remove ${count}? Files stay` : freed > 0 ? `Uninstall ${count}? ${formatSize(freed)}` : `Uninstall ${count}?`
  const glass = useRef<HTMLDivElement>(null)
  useLens(glass)
  return (
    <div ref={glass} className="exo-glass exo-select-bar" role="toolbar" aria-label="Selected games">
      <span className="exo-select-count" role="status">{count ? `${count} selected` : 'Select games'}</span>
      <button type="button" className="exo-set-btn" disabled={!count} onClick={onHide}>
        {hiddenView ? <Eye size={16} /> : <EyeOff size={16} />}
        {hiddenView ? 'Unhide' : 'Hide'}
      </button>
      {hiddenView ? null : (
        <Menu
          label="Add to folder"
          anchor="--exo-select-folder"
          offset={8}
          triggerClassName="exo-set-btn"
          trigger={<><Folder size={16} />Add to folder</>}
          disabled={!count}
          items={[
            ...folders.map(folder => ({ id: `in:${folder.id}`, label: folder.name, icon: <Folder size={16} /> })),
            { id: 'new', label: 'New folder', icon: <FolderPlus size={16} /> },
          ]}
          onAction={id => onFolder(id.startsWith('in:') ? id.slice(3) : null)}
        />
      )}
      {removable ? (
        <ConfirmButton className="exo-set-btn is-danger" question={question} danger onConfirm={onUninstall} lead={<Trash size={16} />}>
          {verb}
        </ConfirmButton>
      ) : null}
      <span className="exo-select-rule" aria-hidden="true" />
      {count < total
        ? <button type="button" className="exo-set-btn" onClick={onAll}>Select all</button>
        : <button type="button" className="exo-set-btn" onClick={onClear}>Clear</button>}
      <button type="button" className="exo-set-btn is-primary" onClick={onDone}>Done</button>
    </div>
  )
}

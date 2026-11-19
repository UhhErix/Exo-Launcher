import { useEffect, useState, type MouseEvent, type ReactNode } from 'react'
import { host, shelfAction, type Game, type LibraryFolder } from '../../lib/host'
import { report } from '../../lib/report'
import { addToFolder, deleteFolder, isFolderTile, leaveFolder, moveFolder } from '../../lib/folders'
import { ChevronLeft, ChevronRight, Download, Eye, EyeOff, Folder, FolderOpen, FolderPlus, Pencil, Play, Sliders, Trash } from '../../brand/icons'
import { PointMenu } from '../ui/Menu'

/** Where a card's menu opens. */
export type GameMenuAt = { game: Game; x: number; y: number; from: HTMLElement | null }

/** A card's right-click as a menu's place; the context-menu key reports no pointer, so it opens at the card. */
export function menuAt(game: Game, event: MouseEvent<HTMLElement>): GameMenuAt {
  const box = event.currentTarget.getBoundingClientRect()
  const keyboard = event.clientX === 0 && event.clientY === 0
  return { game, x: keyboard ? box.left + 12 : event.clientX, y: keyboard ? box.top + 12 : event.clientY, from: event.currentTarget }
}

type Item = { id: string; label: string; icon: ReactNode; run: () => void; section?: string; on?: boolean; confirm?: string; danger?: boolean }

/**
 * A Library card's menu (a right-click on any card): Play or Install, Play as
 * another saved Riot account for an installed Riot game, and Open folder and Properties for an installed game, then
 * Hide (Unhide on a hidden game), as Exo's menu at a point (PointMenu). Then New folder, and every folder with a check on
 * those the game is in (a game may be in several), a choice putting it in or taking it out, with no heading or line; a
 * folder's card is edited or deleted.
 */
export function GameMenu({ at, folders: liveFolders, hidden, onFolders, onHide, onNewFolder, onEditFolder, onClose, onPlay, onPlayAs, onOpenFolder, onProperties }: {
  at: GameMenuAt
  folders: LibraryFolder[]
  /** The game is hidden: Unhide, and no folders (it shows in none until it's back). */
  hidden: boolean
  /** Hide or Unhide. */
  onHide: (game: Game) => void
  /** The folders after a change. */
  onFolders: (next: LibraryFolder[]) => void
  /** New folder, this game ticked in it (FolderSheet). */
  onNewFolder: (gameId: string) => void
  /** A folder's name, color and games (FolderSheet). */
  onEditFolder: (folderId: string) => void
  onClose: () => void
  onPlay: (game: Game) => void
  onPlayAs: (game: Game, account: { key: string; name: string }) => void
  onOpenFolder: (game: Game) => void
  onProperties: (game: Game) => void
}) {
  const { game } = at
  const shortcut = shelfAction(game)
  const riot = game.store === 'riot' && game.installed && shortcut?.action === 'play'
  const [others, setOthers] = useState<Array<{ key: string; name: string }>>([])
  useEffect(() => {
    if (!riot) return
    let live = true
    host.accountsList()
      .then(accounts => { if (live) setOthers((accounts.riot?.accounts ?? []).filter(account => account.saved && !account.current)) })
      .catch((error: unknown) => report(error, 'GameMenu.riotAccounts'))
    return () => { live = false }
  }, [riot])
  // The folders as they were when the menu opened: a choice changes them while the menu fades out, and its rows then
  // read the change ("Add to" turning into "Remove from" on the way out).
  const [folders] = useState(liveFolders)
  const tile = isFolderTile(game) ? game.folder : null
  // A folder's neighbors on the grid (a folder with no games has no card there), for Move earlier and Move later.
  const shown = folders.filter(folder => folder.games.length > 0)
  const place = tile ? shown.findIndex(folder => folder.id === tile.id) : -1
  const [earlier, later] = [shown[place - 1], place >= 0 ? shown[place + 1] : undefined]
  const items: Item[] = tile ? [
    { id: 'edit', label: 'Edit folder', icon: <Pencil size={16} />, run: () => onEditFolder(tile.id) },
    ...(earlier ? [{ id: 'earlier', label: 'Move earlier', icon: <ChevronLeft size={16} />, run: () => onFolders(moveFolder(folders, tile.id, earlier.id, false)) }] : []),
    ...(later ? [{ id: 'later', label: 'Move later', icon: <ChevronRight size={16} />, run: () => onFolders(moveFolder(folders, tile.id, later.id, true)) }] : []),
    {
      id: 'delete', label: 'Delete folder', icon: <Trash size={16} />,
      confirm: 'Delete? Games stay', danger: true, run: () => onFolders(deleteFolder(folders, tile.id)),
    },
  ] : [
    ...(shortcut ? [{ id: 'play', label: shortcut.label, icon: shortcut.action === 'install' ? <Download size={16} /> : <Play size={16} />, run: () => onPlay(game) }] : []),
    ...others.map(account => ({ id: `as:${account.key}`, label: `Play as ${account.name}`, icon: <Play size={16} />, run: () => onPlayAs(game, account) })),
    ...(game.installed ? [{ id: 'folder', label: 'Open folder', icon: <FolderOpen size={16} />, run: () => onOpenFolder(game) }] : []),
    ...(game.installed && !game.isAddPortable ? [{ id: 'properties', label: 'Properties', icon: <Sliders size={16} />, run: () => onProperties(game) }] : []),
    { id: 'hide', label: hidden ? 'Unhide' : 'Hide', icon: hidden ? <Eye size={16} /> : <EyeOff size={16} />, run: () => onHide(game) },
    // New folder sits with the rest; each folder follows as one more choice, checked when the game is in it.
    ...(hidden ? [] : [
      { id: 'new-folder', label: 'New folder', icon: <FolderPlus size={16} />, run: () => onNewFolder(game.id) },
      ...folders.map(folder => {
        const on = folder.games.includes(game.id)
        return {
          id: `in:${folder.id}`, label: folder.name, on, icon: <Folder size={16} />,
          run: () => onFolders(on ? leaveFolder(folders, game.id, folder.id) : addToFolder(folders, game.id, folder.id)),
        }
      }),
    ]),
  ]

  return <PointMenu label={game.title} at={at} from={at.from} items={items} onClose={onClose} />
}

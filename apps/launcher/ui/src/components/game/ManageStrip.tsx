import { useEffect, useState } from 'react'
import { FolderOpen } from '../../brand/icons'
import { host, type Game } from '../../lib/host'
import { formatSize } from '../../lib/utils'
import { ConfirmButton } from '../ui/ConfirmButton'
import { errorText } from '../../lib/errorWords'
import { SUCCESS_HOLD_MS } from '../../lib/actionState'
import { ArtworkTools, useArtwork } from './Artwork'
import { useSelectionGuard } from './useSelectionGuard'

export function UninstallButton({
  selected,
  locked,
  onStatus,
  onUninstalled,
  onBusyChange,
}: {
  selected: Game
  locked: boolean
  onStatus: (msg: string | null, sticky?: boolean) => void
  onUninstalled: () => void
  onBusyChange: (busy: boolean) => void
}) {
  const key = `${selected.store}:${selected.id}`
  const guard = useSelectionGuard(key)
  const [uninstallingKey, setUninstallingKey] = useState<string | null>(null)
  const uninstalling = uninstallingKey === key

  useEffect(() => onBusyChange(uninstalling), [uninstalling, onBusyChange])

  const addedHere = selected.store === 'local'
  const verb = addedHere ? 'remove' : 'uninstall'
  const failed = `Exo couldn’t ${verb} the game. Try again.`

  async function remove() {
    setUninstallingKey(key)
    try {
      const r = await host.uninstall(selected.id)
      const handoff = !!r.handoffOnly
      if (r.ok || handoff) {
        window.setTimeout(guard(() => {
          if (handoff || r.queued) onStatus(r.message ?? 'Finish uninstalling it in the store app.')
          else onUninstalled()
        }), SUCCESS_HOLD_MS)
        return { ...r, ok: true }
      }
      return r
    } finally {
      setUninstallingKey(current => current === key ? null : current)
    }
  }

  return (
    <ConfirmButton
      disabled={locked || !!selected.canStop}
      className="exo-set-btn is-danger"
      question={addedHere ? 'Remove? Files stay' : selected.sizeBytes ? `Uninstall? ${formatSize(selected.sizeBytes)}` : 'Uninstall?'}
      danger
      onConfirm={remove}
      timeoutMs={null}
      context="GameActions.uninstall"
      fallback={failed}
    >
      {addedHere ? 'Remove' : 'Uninstall'}
    </ConfirmButton>
  )
}

export function GameActions({
  selected,
  onStatus,
}: {
  selected: Game
  onStatus: (msg: string | null, sticky?: boolean) => void
}) {
  return (
    <button
      type="button"
      className="exo-glass exo-game-tool exo-game-side is-icon"
      aria-label="Open folder"
      onClick={() => {
        void host.openFolder(selected.id).catch((error: unknown) => onStatus(errorText(error, 'Could not open the folder.'), true))
      }}
    >
      <FolderOpen size={15} />
    </button>
  )
}

/** A game that is not installed keeps its cover tools on the page: it has no Properties. */
export function ArtworkStrip({ selected, coverSource, locked, onArtwork, onStatus }: {
  selected: Game
  coverSource: string | null | undefined
  locked: boolean
  onArtwork: (game: Game) => void
  onStatus: (msg: string | null, sticky?: boolean) => void
}) {
  const artwork = useArtwork(selected, coverSource, onArtwork, onStatus)
  return (
    <div className="exo-game-tools exo-utility-row" role="group" aria-label="Artwork">
      <ArtworkTools artwork={artwork} disabled={locked} />
    </div>
  )
}

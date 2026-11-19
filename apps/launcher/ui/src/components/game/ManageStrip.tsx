import { useEffect, useState } from 'react'
import { Sliders } from '../../brand/icons'
import { host, type DlssStatus, type Game } from '../../lib/host'
import { formatSize } from '../../lib/utils'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'
import { ArtworkTools, useArtwork } from './Artwork'
import { PropertiesSheet } from './PropertiesSheet'
import { useSelectionGuard } from './useSelectionGuard'

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

export type GameActionsProps = {
  selected: Game
  /** The cover currently on screen; `coverSource === 'custom'` unlocks Reset. */
  coverSource: string | null | undefined
  /** Host action in flight elsewhere (launch, install, ...): Uninstall waits. */
  locked: boolean
  upscalers: DlssStatus | null
  onUpscalersChanged: () => void
  onStatus: (msg: string | null, sticky?: boolean) => void
  onArtwork: (game: Game) => void
  onUninstalled: () => void
  /** True while Uninstall has a host call in flight. */
  onBusyChange: (busy: boolean) => void
  /** Properties over the page: its button here, or the card menu's Properties. */
  propertiesOpen: boolean
  onPropertiesOpen: (open: boolean) => void
}

/**
 * Beside Play on an installed game: Properties, which holds everything else (folder, files, updates, DLC, artwork)
 * and Uninstall (it asks first; Remove for a game added from a folder, whose files Exo leaves where they are). The
 * uninstall runs here, so it outlives the sheet; its result closes the sheet, and the page says what happened.
 */
export function GameActions({
  selected,
  coverSource,
  locked,
  upscalers,
  onUpscalersChanged,
  onStatus,
  onArtwork,
  onUninstalled,
  onBusyChange,
  propertiesOpen,
  onPropertiesOpen,
}: GameActionsProps) {
  const key = `${selected.store}:${selected.id}`
  const guard = useSelectionGuard(key)
  // The game being uninstalled: another source picked shows no ring, with no effect to reset it.
  const [uninstallingKey, setUninstallingKey] = useState<string | null>(null)
  const uninstalling = uninstallingKey === key
  const artwork = useArtwork(selected, coverSource, onArtwork, onStatus)

  const busy = uninstalling || artwork.action !== null
  useEffect(() => onBusyChange(busy), [busy, onBusyChange])

  // A game added from a folder leaves the Library, its files where they are: that is a Remove.
  const addedHere = selected.store === 'local'
  const verb = addedHere ? 'remove' : 'uninstall'
  function remove() {
    const failed = `Exo could not ${verb} the game. Try again.`
    setUninstallingKey(key)
    onStatus(`${addedHere ? 'Removing' : 'Uninstalling'} ${selected.title}…`)
    void host.uninstall(selected.id)
      .then(guard((r) => {
        onPropertiesOpen(false)
        // A handoff opened the store's own app: neutral info, never a sticky error.
        const handoff = !!r.handoffOnly
        onStatus(r.message ?? (handoff ? 'Finish uninstalling it in the store app.' : r.ok ? (addedHere ? 'Removed.' : 'Uninstalled.') : failed), !r.ok && !handoff)
        if (r.ok && !r.handoffOnly && !r.queued) onUninstalled()
      }))
      .catch(guard((error: unknown) => { onPropertiesOpen(false); onStatus(errorText(error, failed), true) }))
      .finally(() => setUninstallingKey(current => current === key ? null : current))
  }

  return (
    <>
      <button type="button" className="exo-game-tool exo-game-side is-icon" aria-label="Properties" aria-haspopup="dialog" onClick={() => onPropertiesOpen(true)}>
        <Sliders size={16} />
      </button>
      {propertiesOpen && (
        <PropertiesSheet
          key={key}
          game={selected}
          artwork={artwork}
          upscalers={upscalers}
          onUpscalersChanged={onUpscalersChanged}
          uninstall={
            <ConfirmButton
              disabled={locked || uninstalling || !!selected.canStop}
              className="exo-set-btn is-danger"
              question={addedHere ? 'Remove? Files stay' : selected.sizeBytes ? `Uninstall? ${formatSize(selected.sizeBytes)}` : 'Uninstall?'}
              danger
              onConfirm={remove}
              lead={uninstalling ? <Spinner label={`${addedHere ? 'Removing' : 'Uninstalling'} ${selected.title}`} /> : null}
            >
              {addedHere ? 'Remove' : 'Uninstall'}
            </ConfirmButton>
          }
          onClose={() => onPropertiesOpen(false)}
          onStatus={onStatus}
        />
      )}
    </>
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

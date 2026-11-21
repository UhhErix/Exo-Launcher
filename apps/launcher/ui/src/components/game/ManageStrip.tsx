import { useEffect, useRef, useState } from 'react'
import { FolderOpen, Sliders } from '../../brand/icons'
import { host, type DlssStatus, type Game } from '../../lib/host'
import { formatSize } from '../../lib/utils'
import { ConfirmButton } from '../ui/ConfirmButton'
import { errorText } from '../../lib/errorWords'
import { SUCCESS_HOLD_MS } from '../../lib/actionState'
import { ArtworkTools, useArtwork } from './Artwork'
import { PropertiesSheet } from './PropertiesSheet'
import { useSelectionGuard } from './useSelectionGuard'

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
 * uninstall runs here, so it outlives the sheet; its button shows how it went, and then the sheet closes.
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
  const failed = `Exo couldn’t ${verb} the game. Try again.`
  // Whether the sheet is still open when the answer comes: a failure says so on the button there, else on the page.
  const open = useRef(propertiesOpen)
  useEffect(() => { open.current = propertiesOpen }, [propertiesOpen])
  /**
   * The button shows the uninstall (ConfirmButton: its ring, its check, or Bad with why beside it). It runs here, so it
   * outlives the sheet: the sheet closes and the game leaves once the check has shown, even if the sheet closed first.
   */
  async function remove() {
    setUninstallingKey(key)
    try {
      const r = await host.uninstall(selected.id)
      // A handoff opened the store's own app, and a queued one waits its turn: neutral info, never an error.
      const handoff = !!r.handoffOnly
      if (r.ok || handoff) {
        window.setTimeout(guard(() => {
          onPropertiesOpen(false)
          if (handoff || r.queued) onStatus(r.message ?? 'Finish uninstalling it in the store app.')
          else onUninstalled()
        }), SUCCESS_HOLD_MS)
        return { ...r, ok: true }
      }
      if (!open.current) guard(() => onStatus(errorText(r.message, failed), true))(undefined)
      return r
    } catch (error) {
      if (!open.current) guard(() => onStatus(errorText(error, failed), true))(undefined)
      throw error
    } finally {
      setUninstallingKey(current => current === key ? null : current)
    }
  }

  return (
    <>
      <button type="button" className="exo-glass exo-game-tool exo-game-side" aria-label="Properties" aria-haspopup="dialog" onClick={() => onPropertiesOpen(true)}>
        <Sliders size={15} />
        <span>Properties</span>
      </button>
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
      {propertiesOpen && (
        <PropertiesSheet
          key={key}
          game={selected}
          artwork={artwork}
          upscalers={upscalers}
          onUpscalersChanged={onUpscalersChanged}
          uninstall={
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
          }
          onClose={() => onPropertiesOpen(false)}
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

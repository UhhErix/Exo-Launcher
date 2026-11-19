import { useState, type ReactNode } from 'react'
import { ImageUpload, Refresh, ResetDefault } from '../../brand/icons'
import { host, type ArtworkMutationResponse, type Game } from '../../lib/host'
import { Spinner } from '../ui/Spinner'
import { useSelectionGuard } from './useSelectionGuard'

type ArtAction = 'replace' | 'reset' | 'refetch'

const FAILED = 'Exo couldn’t update the cover. Try again.'

function errorText(error: unknown, fallback: string): string {
  return errorText(error, fallback)
}

/** The cover's own tools: Replace, and Reset (a custom cover) or Refresh art (the store's). */
export type Artwork = { action: ArtAction | null; custom: boolean; replace: () => void; reset: () => void; refetch: () => void }

/** Replace, reset or fetch the cover again; the new cover is the confirmation, only a failure says anything. */
export function useArtwork(selected: Game, coverSource: string | null | undefined, onArtwork: (game: Game) => void,
  onStatus: (msg: string | null, sticky?: boolean) => void): Artwork {
  const key = `${selected.store}:${selected.id}`
  const guard = useSelectionGuard(key)
  // What runs, held with the game it runs for: another game shows none of it, with no effect to reset it.
  const [running, setRunning] = useState<{ key: string; name: ArtAction } | null>(null)
  const action = running?.key === key ? running.name : null
  const run = (name: ArtAction, operation: (id: string) => Promise<ArtworkMutationResponse>) => {
    if (action) return
    setRunning({ key, name })
    void operation(selected.id)
      .then(guard((result) => {
        if (result.cancelled) { onStatus(null); return }
        if (result.ok && result.game) onArtwork(result.game)
        onStatus(result.ok ? null : result.message ?? FAILED, !result.ok)
      }))
      .catch(guard((error: unknown) => onStatus(errorText(error, FAILED), true)))
      .finally(() => setRunning(current => current?.key === key && current.name === name ? null : current))
  }
  return {
    action,
    custom: coverSource === 'custom',
    replace: () => run('replace', host.artReplace),
    reset: () => run('reset', host.artReset),
    refetch: () => run('refetch', host.artRefetch),
  }
}

/**
 * Replace the cover, or put Exo's art back (reset a custom cover, or fetch the
 * store's art again). A busy button keeps its label: Refresh art turns its
 * glyph, the others show the ring in their glyph's place (DESIGN.md › Waits),
 * so the row never changes width.
 */
export function ArtworkTools({ artwork, disabled, className = 'exo-game-tool' }: { artwork: Artwork; disabled: boolean; className?: string }) {
  const { action } = artwork
  const busy = action !== null
  const glyph = (name: ArtAction, label: string, idle: ReactNode) => action === name ? <Spinner label={label} /> : idle
  return (
    <>
      <button type="button" className={className} disabled={disabled || busy} aria-busy={action === 'replace'} onClick={artwork.replace}>
        {glyph('replace', 'Replacing the cover', <ImageUpload size={14} />)}
        Replace cover
      </button>
      {artwork.custom ? (
        <button type="button" className={className} disabled={disabled || busy} aria-busy={action === 'reset'} onClick={artwork.reset}>
          {glyph('reset', 'Resetting the cover', <ResetDefault size={14} />)}
          Reset cover
        </button>
      ) : (
        <button type="button" className={className} disabled={disabled || busy} aria-busy={action === 'refetch'} onClick={artwork.refetch}>
          <Refresh size={14} className={action === 'refetch' ? 'exo-spin' : undefined} />
          Refresh art
        </button>
      )}
    </>
  )
}

import { useMemo } from 'react'
import { openCaptures, useCapture, type CaptureItem } from '../../../lib/capture'
import type { DiscordHome } from '../../../lib/discord'
import type { Game } from '../../../lib/host'
import { shareTargets, type Party } from '../../../lib/playRooms'
import { ErrorNote } from '../../ui/ErrorNote'
import { ClipTile } from './ClipTile'
import { PlayWait } from './parts'
import { useClips } from './useClips'

/** How many captures the room reads: the latest, since older ones are in Captures. */
const MOST_CLIPS = 60

/** The game a capture belongs to, for the share list: by its id, else the card named for its folder. */
export function gameIdOf(item: CaptureItem, games: readonly Game[]): string | null {
  if (item.gameId) return item.gameId
  const folder = item.game.trim().toLowerCase()
  return games.find(game => game.title.trim().toLowerCase() === folder)?.id ?? null
}

/**
 * Clips: your captures from Exo Capture, newest first, each one press from a friend. A press on a picture
 * opens it in Captures; Share asks where. No capture, and no Exo Capture, each say so in a line; a failed read says so and can
 * be read again.
 */
export function ClipsRoom({ games, parties, home }: { games: Game[]; parties: Party[]; home: DiscordHome | null }) {
  const { items, failed, reload } = useClips(null, MOST_CLIPS)
  const { state } = useCapture()
  const dms = home?.dms
  const targetsFor = useMemo(() => {
    const cache = new Map<string | null, ReturnType<typeof shareTargets>>()
    return (gameId: string | null) => {
      let found = cache.get(gameId)
      if (!found) cache.set(gameId, found = shareTargets({ gameId, parties, dms: dms ?? [] }))
      return found
    }
  }, [parties, dms])

  if (failed) return (
    <div className="exo-play-empty">
      <ErrorNote message="Your clips couldn't be read." />
      <button type="button" className="exo-set-btn is-compact" onClick={reload}>Try again</button>
    </div>
  )
  if (!items) return <PlayWait room="clips" />
  if (!items.length) return (
    <div className="exo-play-empty">
      <p className="exo-dc-lobby-none">{state && !state.present ? "Exo Capture isn't installed." : 'No clips yet.'}</p>
      {state && !state.present ? <button type="button" className="exo-set-btn is-compact" onClick={() => openCaptures()}>Open Captures</button> : null}
    </div>
  )
  return (
    <ul className="exo-play-grid is-clips exo-motion-arrive" aria-label="Clips">
      {items.map(item => <ClipTile key={item.id} item={item} targets={targetsFor(gameIdOf(item, games))} />)}
    </ul>
  )
}

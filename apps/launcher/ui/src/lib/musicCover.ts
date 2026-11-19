import type { MusicPlayback } from './music'
import type { PlayerState } from './player'

const norm = (value: string) => value.trim().toLowerCase()

function playerMatchesPlayback(player: PlayerState, playback: MusicPlayback): boolean {
  if (!player.title || !playback.title) return false
  if (norm(player.title) !== norm(playback.title)) return false
  const playbackArtist = norm(playback.artist || '')
  const playerArtists = (player.artists ?? []).map(a => norm(a.name)).filter(Boolean)
  if (!playbackArtist || !playerArtists.length) return true
  return playerArtists.some(name => playbackArtist.includes(name) || name.includes(playbackArtist))
}

/**
 * Resolves cover art without letting a lagging PlayerState flash the previous track's cover when host playback has
 * already moved to a new song. When tracks align, PlayerState's full-res image wins; otherwise host artworkDataUrl
 * or CSP-whitelisted HTTPS artworkUrl shows immediately.
 */
export function coverArtForTrack(player: PlayerState | null | undefined, playback: MusicPlayback | null | undefined): string | undefined {
  const playbackArt = playback?.artworkDataUrl || playback?.artworkUrl || undefined
  if (!playback || playback.status === 'none' || !playback.title) return player?.image || undefined
  if (player && playerMatchesPlayback(player, playback)) {
    return player.image || playbackArt
  }
  return playbackArt ?? (player && !player.title ? player.image || undefined : undefined)
}

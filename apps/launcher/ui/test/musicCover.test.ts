import { test } from 'node:test'
import assert from 'node:assert/strict'
import { coverArtForTrack } from '../src/lib/musicCover.ts'
import type { MusicPlayback } from '../src/lib/music.ts'
import type { PlayerState } from '../src/lib/player.ts'

const playerOf = (over: Partial<PlayerState> = {}): PlayerState => ({
  trackUri: 'spotify:track:1',
  contextUri: 'spotify:playlist:1',
  shuffle: false,
  repeat: 0,
  liked: false,
  canShuffle: true,
  canRepeat: true,
  title: 'Song A',
  artists: [{ uri: 'spotify:artist:1', name: 'Artist A' }],
  image: 'https://i.scdn.co/image/song-a',
  ...over,
})

const playbackOf = (over: Partial<MusicPlayback> = {}): MusicPlayback => ({
  provider: 'spotify',
  status: 'playing',
  title: 'Song A',
  artist: 'Artist A',
  artworkDataUrl: null,
  artworkUrl: null,
  canPlay: true,
  canPause: true,
  canPrev: true,
  canNext: true,
  canSeek: true,
  ...over,
})

test('uses full-resolution player image when player and playback match the same track', () => {
  const src = coverArtForTrack(
    playerOf(),
    playbackOf({ artworkDataUrl: 'data:image/jpeg;base64,abc', artworkUrl: 'https://i.scdn.co/image/thumb-a' }),
  )
  assert.equal(src, 'https://i.scdn.co/image/song-a')
})

test('never flashes stale player image when playback has advanced to a new song', () => {
  // Playback jumped to Song B with HTTPS artworkUrl ready while PlayerState is still lagging on Song A.
  const instant = coverArtForTrack(
    playerOf({ title: 'Song A', image: 'https://i.scdn.co/image/song-a' }),
    playbackOf({ title: 'Song B', artist: 'Artist B', artworkUrl: 'https://i.scdn.co/image/song-b', artworkDataUrl: null }),
  )
  assert.equal(instant, 'https://i.scdn.co/image/song-b')

  // Even if Song B has no artwork yet, never show Song A's cover under Song B's title.
  const waiting = coverArtForTrack(
    playerOf({ title: 'Song A', image: 'https://i.scdn.co/image/song-a' }),
    playbackOf({ title: 'Song B', artist: 'Artist B', artworkUrl: null, artworkDataUrl: null }),
  )
  assert.equal(waiting, undefined)
})

test('falls back to playback artworkDataUrl then artworkUrl when player has no image or has no title yet', () => {
  assert.equal(
    coverArtForTrack(playerOf({ title: 'Song B', image: '' }), playbackOf({ title: 'Song B', artworkDataUrl: 'data:image/png;base64,123', artworkUrl: 'https://i.scdn.co/image/song-b' })),
    'data:image/png;base64,123',
  )
  assert.equal(
    coverArtForTrack(null, playbackOf({ title: 'Song B', artworkDataUrl: null, artworkUrl: 'https://i.scdn.co/image/song-b' })),
    'https://i.scdn.co/image/song-b',
  )
})

test('matches featured artist lists and uses player image when playback has no track', () => {
  assert.equal(
    coverArtForTrack(
      playerOf({ title: 'Collab', artists: [{ uri: 'a', name: 'Lead' }], image: 'https://i.scdn.co/image/collab' }),
      playbackOf({ title: 'collab', artist: 'Lead, Guest', artworkUrl: 'https://i.scdn.co/image/thumb' }),
    ),
    'https://i.scdn.co/image/collab',
  )
  assert.equal(
    coverArtForTrack(playerOf({ image: 'https://i.scdn.co/image/song-a' }), playbackOf({ status: 'none', title: '', artist: '' })),
    'https://i.scdn.co/image/song-a',
  )
})

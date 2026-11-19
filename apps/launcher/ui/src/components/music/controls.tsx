import { useState, type ReactNode } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import type { MusicPlayback } from '../../lib/music'
import type { MusicAdapter } from '../../lib/musicAdapters'
import type { PlayerState } from '../../lib/player'
import { Heart, Next, Pause, Play, Previous, Repeat, RepeatOne, Shuffle, ThumbsDown } from '../../brand/icons'
import { usePlayer } from './playerContext'
import { errorText } from '../../lib/errorWords'

/* The song's own controls, shared by the player strip and Immersive (Immersive.tsx). */

export function IconToggle({ label, on, disabled, className, onPress, children }: {
  label: string; on?: boolean; disabled?: boolean; className?: string; onPress: () => void; children: ReactNode
}) {
  return <button type="button" className={`exo-set-btn exo-music-toggle${on ? ' is-on' : ''}${className ? ' ' + className : ''}`} aria-label={label} aria-pressed={on} disabled={disabled} onClick={onPress}>{children}</button>
}

/** The playing song's heart, and YouTube Music's thumbs down beside it. */
export function SongVerdict({ player, change }: { player: PlayerState; change: (...args: Parameters<MusicAdapter['act']>) => void }) {
  const { adapter, has } = usePlayer()
  return (
    <>
      <IconToggle label={player.liked ? `Remove from ${adapter.likedName}` : `Save to ${adapter.likedName}`} on={player.liked}
        onPress={() => change('like', { uri: player.trackUri, on: !player.liked })}><Heart size={16} filled={player.liked} /></IconToggle>
      {has('dislike') ? (
        <IconToggle label={player.disliked ? 'Remove dislike' : 'Dislike'} on={player.disliked}
          onPress={() => change('dislike', { uri: player.trackUri, on: !player.disliked })}><ThumbsDown size={16} filled={player.disliked} /></IconToggle>
      ) : null}
    </>
  )
}

export function BarControls({ playback, player, change, onFail }: {
  playback: MusicPlayback | null
  player: PlayerState | null
  change: (...args: Parameters<MusicAdapter['act']>) => void
  onFail: (message: string | null) => void
}) {
  const playing = playback?.status === 'playing'
  // Spotify plays on another device: this is that device's id, and Play here moves playback to the web player.
  const [elsewhere, setElsewhere] = useState<string | null>(null)
  const control = (action: 'toggle' | 'next' | 'previous') => {
    onFail(null)
    setElsewhere(null)
    void host.musicControl(action).then(
      result => {
        if (result.ok) return
        onFail(errorText(result.message, 'The player could not respond.'))
        if (result.cause === 'another-device' && result.deviceId) setElsewhere(result.deviceId)
      },
      error => { report(error, 'MusicPlayer.control'); onFail('The player could not respond.') })
  }
  const repeat = player?.repeat ?? 0
  return (
      <div className="exo-music-bar-controls">
        <IconToggle label="Shuffle" on={player?.shuffle} disabled={!player?.canShuffle} onPress={() => change('shuffle', { on: !player?.shuffle })}><Shuffle size={16} /></IconToggle>
        <IconToggle label="Previous" disabled={!playback?.canPrev} onPress={() => control('previous')}><Previous size={18} /></IconToggle>
        <button type="button" className="exo-music-bar-play" aria-label={playing ? 'Pause' : 'Play'} disabled={!playback || playback.status === 'none'} onClick={() => control('toggle')}>
          {playing ? <Pause size={18} /> : <Play size={18} />}
        </button>
        <IconToggle label="Next" disabled={!playback?.canNext} onPress={() => control('next')}><Next size={18} /></IconToggle>
        <IconToggle label={repeat === 2 ? 'Repeat this song' : repeat === 1 ? 'Repeat' : 'Repeat off'} on={repeat > 0} disabled={!player?.canRepeat}
          onPress={() => change('repeat', { index: (repeat + 1) % 3 })}>{repeat === 2 ? <RepeatOne size={16} /> : <Repeat size={16} />}</IconToggle>
        {elsewhere ? (
          <button type="button" className="exo-set-btn is-compact" onClick={() => {
            onFail(null)
            setElsewhere(null)
            change('transfer', { context: elsewhere })
          }}>Play here</button>
        ) : null}
      </div>
  )
}

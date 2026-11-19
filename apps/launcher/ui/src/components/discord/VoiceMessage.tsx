import { useEffect, useMemo, useRef, useState } from 'react'
import type { DiscordAttachment } from '../../lib/discord'
import { Play, Pause } from '../../brand/icons'

const BARS = 48

/** Discord's waveform (base64, a byte per sample) as BARS heights from 0 to 1. */
function bars(waveform?: string): number[] {
  let samples: number[] = []
  try { samples = waveform ? [...atob(waveform)].map(c => c.charCodeAt(0) / 255) : [] } catch { samples = [] }
  if (!samples.length) return Array.from({ length: BARS }, () => 0.25)
  return Array.from({ length: BARS }, (_, i) => {
    const from = Math.floor(i * samples.length / BARS)
    const to = Math.max(from + 1, Math.floor((i + 1) * samples.length / BARS))
    return Math.max(0.12, Math.max(...samples.slice(from, to)))
  })
}

const clock = (seconds: number) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`

/**
 * A voice message, as Discord plays it: play or pause, its waveform filling as it plays (click a
 * point to jump there) and the time left.
 */
export function VoiceMessage({ attachment }: { attachment: DiscordAttachment }) {
  const audio = useRef<HTMLAudioElement>(null)
  const [playing, setPlaying] = useState(false)
  const [at, setAt] = useState(0)
  const length = attachment.duration_secs ?? 0
  const heights = useMemo(() => bars(attachment.waveform), [attachment.waveform])
  // The waveform fills a bar at a time and the clock counts seconds: the player's own time updates (four a second) are
  // enough, where a frame loop redrew all 48 bars sixty times a second.
  useEffect(() => {
    const element = audio.current
    if (!element) return
    const tick = () => setAt(element.currentTime)
    const play = () => setPlaying(true)
    const stop = () => { setPlaying(false); setAt(element.currentTime) }
    const end = () => { stop(); element.currentTime = 0; setAt(0) }
    element.addEventListener('timeupdate', tick)
    element.addEventListener('play', play)
    element.addEventListener('pause', stop)
    element.addEventListener('ended', end)
    return () => {
      element.removeEventListener('timeupdate', tick); element.removeEventListener('play', play)
      element.removeEventListener('pause', stop); element.removeEventListener('ended', end)
    }
  }, [])
  const progress = length ? at / length : 0
  const seek = (to: number) => {
    const element = audio.current
    if (!element || !length) return
    element.currentTime = Math.min(length, Math.max(0, to))
    setAt(element.currentTime)
  }
  // The keys a slider takes: a twentieth (a second at least) either way, and its start and end.
  const keys = (event: React.KeyboardEvent) => {
    const step = Math.max(1, length / 20)
    const to = { ArrowLeft: at - step, ArrowDown: at - step, ArrowRight: at + step, ArrowUp: at + step, Home: 0, End: length }[event.key]
    if (to === undefined) return
    event.preventDefault()
    seek(to)
  }
  return (
    <div className="exo-dc-voice-msg">
      <button type="button" className="exo-dc-voice-msg-play" aria-label={playing ? 'Pause voice message' : 'Play voice message'}
        onClick={() => { const element = audio.current; if (!element) return; if (element.paused) void element.play().catch(() => { }); else element.pause() }}>
        {playing ? <Pause size={16} /> : <Play size={16} />}
      </button>
      <span className="exo-dc-voice-msg-wave" role="slider" tabIndex={0} aria-label="Voice message position" aria-valuemin={0} aria-valuemax={Math.round(length)} aria-valuenow={Math.round(at)}
        aria-valuetext={`${clock(at)} of ${clock(length)}`} onKeyDown={keys}
        onClick={event => {
          const box = event.currentTarget.getBoundingClientRect()
          seek((event.clientX - box.left) / box.width * length)
        }}>
        {heights.map((height, i) => <i key={i} className={i / BARS < progress ? 'is-played' : undefined} style={{ height: `${Math.round(height * 100)}%` }} />)}
      </span>
      <span className="exo-dc-voice-msg-time">{clock(playing || at ? Math.max(0, length - at) : length)}</span>
      <audio ref={audio} src={attachment.url} preload="none" />
    </div>
  )
}

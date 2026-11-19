import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { musicTime, playbackAnchor, type MusicPlayback } from '../../lib/music'
import type { MusicAdapter } from '../../lib/musicAdapters'
import type { PlayerState } from '../../lib/player'
import { anythingOpen } from '../../lib/dismiss'
import { ExitFullScreen } from '../../brand/icons'
import { MusicClock, MusicSeek } from '../shell/MusicMiniPlayer'
import { ErrorNote } from '../ui/ErrorNote'
import { Art } from './Art'
import { BarControls, IconToggle, SongVerdict } from './controls'
import { Lyrics, SongArtists } from './panels'
import { runAction, usePlayer } from './playerContext'
import { BAR_IDLE_MS, barMayHide, wakesBar } from './immersiveRules'

/**
 * Now playing, full window (DESIGN.md › Rooms › Music): the cover large in its own light (the art, far out of focus and
 * dimmed so white words keep 4.5:1), the song, its place and its controls, and its lyrics large beside it where the
 * service has them. The Bar fades away while nothing needs it and comes back with the pointer near the top or any key;
 * Esc and the exit control always leave. Still while it shows: only the sung line moves.
 */
export function Immersive({ player, live, active, onLeave, onChanged }: {
  player: PlayerState | null
  live: { playback: MusicPlayback; at: number } | null
  /** The room on screen with nothing over it: the Bar may go, and Esc is Immersive's. */
  active: boolean
  onLeave: () => void
  onChanged: () => void
}) {
  const playback = live?.playback ?? null
  const uri = player?.trackUri ?? ''
  const art = player?.image || playback?.artworkDataUrl || undefined
  // How many lines the song's lyrics have, once known; none centres the song alone.
  const [lyrics, setLyrics] = useState<{ uri: string; count: number | null }>({ uri: '', count: null })
  const onLines = useCallback((count: number | null) => setLyrics({ uri, count }), [uri])
  const withLyrics = !!uri && !(lyrics.uri === uri && lyrics.count === 0)
  useBarAway(active)
  useEscape(active, onLeave)
  // Focus comes in with it (the room under it is inert), so keys land here and a Bar tab left focused can let the Bar go.
  const section = useRef<HTMLElement>(null)
  useEffect(() => { section.current?.focus({ preventScroll: true }) }, [])
  return (
    <section ref={section} tabIndex={-1} className={`exo-music-immersive${withLyrics ? ' has-lyrics' : ''}`} aria-label="Now playing, full window">
      <AmbientLight src={art} />
      <div className="exo-music-immersive-stage">
        <Song player={player} live={live} art={art} onLeave={onLeave} onChanged={onChanged} />
        {withLyrics ? (
          <div className="exo-music-immersive-lyrics">
            <Lyrics uri={uri} playback={playback} receivedAt={live?.at ?? 0} onLines={onLines} />
          </div>
        ) : null}
      </div>
    </section>
  )
}

/** The song's column: its cover (what flies in and out), its name and artists, its place, and its controls with the exit. */
function Song({ player, live, art, onLeave, onChanged }: {
  player: PlayerState | null
  live: { playback: MusicPlayback; at: number } | null
  art?: string
  onLeave: () => void
  onChanged: () => void
}) {
  const { adapter } = usePlayer()
  const playback = live?.playback ?? null
  // A control the player refused reads in Bad on the artist line for a moment, as on the strip.
  const [failed, setFailed] = useState<string | null>(null)
  useEffect(() => {
    if (!failed) return
    const timer = window.setTimeout(() => setFailed(null), 4500)
    return () => window.clearTimeout(timer)
  }, [failed])
  const change = (...args: Parameters<MusicAdapter['act']>) => {
    setFailed(null)
    void runAction(adapter, ...args).then(ok => { if (!ok) setFailed(`${adapter.name} did not take that. Try again.`); onChanged() })
  }
  return (
    <div className="exo-music-immersive-song">
      <Art className="exo-music-immersive-art" src={art} />
      <div className="exo-music-immersive-text">
        <h2>{player?.title || playback?.title || 'Nothing playing'}</h2>
        {failed ? <ErrorNote as="p" className="exo-music-row-sub is-failed" message={failed} />
          : <p className="exo-music-row-sub"><SongArtists player={player} playback={playback} onOpen={onLeave} /></p>}
      </div>
      {live ? <Progress live={live} /> : null}
      <div className="exo-music-immersive-controls">
        <span className="exo-music-immersive-verdict">{player?.trackUri ? <SongVerdict player={player} change={change} /> : null}</span>
        <BarControls playback={playback} player={player} change={change} onFail={setFailed} />
        <span className="exo-music-immersive-leave">
          <IconToggle label="Leave full window" onPress={onLeave}><ExitFullScreen size={16} /></IconToggle>
        </span>
      </div>
    </div>
  )
}

/** The song's place: a seek line between the time gone and the song's length, ticking once a second while it plays. */
function Progress({ live }: { live: { playback: MusicPlayback; at: number } }) {
  const { playback, at } = live
  const clock = useMemo(() => ({ anchor: playbackAnchor(playback, at), floor: null }), [playback, at])
  const [preview, setPreview] = useState<number | null>(null)
  const key = `${playback.provider}:${playback.title}:${playback.artist}`
  return (
    <MusicClock playback={playback} clock={clock} preview={preview}>
      {position => (
        <div className="exo-music-immersive-progress">
          <span className="exo-music-immersive-time">{musicTime(position)}</span>
          <MusicSeek key={key} className="exo-music-immersive-seek" position={position} duration={playback.durationMs ?? null}
            enabled={playback.canSeek === true} onPreview={setPreview} />
          <span className="exo-music-immersive-time">{musicTime(playback.durationMs)}</span>
        </div>
      )}
    </MusicClock>
  )
}

/** The art's light is drawn once per cover, small (a 48px canvas, blurred there) and stretched to the window: the GPU
    scales a still picture instead of blurring the whole window on every frame the sung line redraws. */
const LIGHT_PX = 48

/**
 * The cover's own colours behind everything, far out of focus, at the art's opacity under the dim (tokens.css ›
 * Immersive). A new song's light fades in over the last on the move time; then it holds still.
 */
function AmbientLight({ src }: { src?: string }) {
  const [layers, setLayers] = useState<Array<{ src: string; id: number }>>([])
  const next = useRef(0)
  useEffect(() => {
    if (!src) { setLayers([]); return }
    setLayers(previous => previous[previous.length - 1]?.src === src ? previous : [...previous.slice(-1), { src, id: ++next.current }])
  }, [src])
  return (
    <div className="exo-music-immersive-light" aria-hidden="true">
      {layers.map((layer, index) => (
        <LightLayer key={layer.id} src={layer.src} onShown={index === layers.length - 1 && layers.length > 1
          ? () => setLayers(previous => previous.filter(other => other.id >= layer.id)) : undefined} />
      ))}
    </div>
  )
}

function LightLayer({ src, onShown }: { src: string; onShown?: () => void }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [drawn, setDrawn] = useState(false)
  useEffect(() => {
    let live = true
    const image = new Image()
    image.referrerPolicy = 'no-referrer'
    image.decoding = 'async'
    image.src = src
    // Drawing a picture from another site is allowed (it only stops the canvas being read, which this never does).
    image.decode().then(() => {
      const context = live ? canvas.current?.getContext('2d') : null
      if (!context) return
      context.filter = 'blur(5px) saturate(1.4)'
      // Drawn past the edges, so the blur never pulls black in from outside the picture.
      context.drawImage(image, -8, -8, LIGHT_PX + 16, LIGHT_PX + 16)
      setDrawn(true)
    }, () => { })
    return () => { live = false }
  }, [src])
  return <canvas ref={canvas} width={LIGHT_PX} height={LIGHT_PX} className={drawn ? 'is-drawn' : undefined} onTransitionEnd={onShown} />
}

/**
 * The Bar fades away over Immersive once nothing has needed it for BAR_IDLE_MS, and comes back when the pointer moves
 * near the top or a key is pressed (`html[data-exo-immersive]`, music-player.css). Off screen, it is always there.
 */
function useBarAway(active: boolean) {
  useEffect(() => {
    if (!active) return
    const root = document.documentElement
    let timer = 0
    const set = (state: 'bar' | 'away') => { if (root.dataset.exoImmersive !== state) root.dataset.exoImmersive = state }
    const away = () => {
      const bar = document.querySelector('.exo-titlebar')
      if (!barMayHide({ hovered: !!bar?.matches(':hover'), focused: !!bar?.matches(':focus-within'), open: anythingOpen() })) {
        timer = window.setTimeout(away, BAR_IDLE_MS)
        return
      }
      set('away')
    }
    const wake = () => {
      set('bar')
      window.clearTimeout(timer)
      timer = window.setTimeout(away, BAR_IDLE_MS)
    }
    const onPointer = (event: PointerEvent) => { if (wakesBar({ kind: 'pointer', y: event.clientY })) wake() }
    const onKey = () => wake()
    wake()
    window.addEventListener('pointermove', onPointer, { passive: true })
    window.addEventListener('pointerdown', onPointer, { passive: true })
    window.addEventListener('keydown', onKey, true)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('pointermove', onPointer)
      window.removeEventListener('pointerdown', onPointer)
      window.removeEventListener('keydown', onKey, true)
      delete root.dataset.exoImmersive
    }
  }, [active])
}

/** What answers Esc itself: a menu, a popover or a dialog closes first. */
const OWNS_ESCAPE = '[role="menu"], [role="listbox"], [popover]:popover-open, dialog, [aria-modal="true"]'

/** Esc leaves, from anywhere in the room (the seek line included), ahead of the service's own Esc (shortcuts.ts). */
function useEscape(active: boolean, onLeave: () => void) {
  const leave = useRef(onLeave)
  useEffect(() => { leave.current = onLeave })
  useEffect(() => {
    if (!active) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || anythingOpen()) return
      const target = event.target instanceof Element ? event.target : null
      if (target?.closest(OWNS_ESCAPE)) return
      event.preventDefault()
      leave.current()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [active])
}

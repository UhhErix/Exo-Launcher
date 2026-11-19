import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent, type RefObject } from 'react'
import { Movie, Repeat, Scissors } from '../../brand/icons'
import { host } from '../../lib/host'
import { clock, type CaptureExport, type CaptureItem } from '../../lib/capture'
import { timeAt } from '../../lib/timeline'
import { ErrorNote } from '../ui/ErrorNote'
import { EditorShell, PresetTile, type EditorTab } from './EditorShell'
import { TimelineStrip } from './TimelineStrip'

/** A capture plays at half volume, as the player's slider shows, until you move it; kept while Exo runs. */
export const playback = { volume: 0.5, muted: false }

/** A key for the player: not one going into a field, a trim point (its arrows move it) or anything over the sheet. */
export function playerKey(event: KeyboardEvent): boolean {
  if (event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey) return false
  const target = event.target instanceof HTMLElement ? event.target : null
  if (target && target !== document.body && !target.closest('.exo-captures')) return false
  return !target?.isContentEditable && !target?.closest('input, textarea, select, [role="slider"]')
}

/**
 * Space plays and pauses; , and . step a frame. Forward goes by the gap to the next frame, learned on the way (from
 * 120 fps, twice as far whenever a step stays on its frame); back, to just before it. `onFrame` sees each frame shown.
 */
export function useFrameKeys(video: RefObject<HTMLVideoElement | null>, enabled: boolean, onFrame?: (mediaTime: number, el: HTMLVideoElement) => void) {
  const frame = useRef({ at: -1, gap: 1 / 120, from: -1, stepped: 0 })
  const seen = useRef(onFrame)
  useEffect(() => { seen.current = onFrame })
  useEffect(() => {
    const el = video.current
    if (!el) return
    let handle = 0
    const shown = (_now: number, meta: VideoFrameCallbackMetadata) => {
      const f = frame.current
      if (f.from >= 0) {
        const from = f.from
        f.from = -1
        if (meta.mediaTime > from + 1e-6) f.gap = Math.min(meta.mediaTime - from, 1 / 24)
        else if (f.gap < 1) { f.gap *= 2; f.from = from; el.currentTime = from + f.gap + 0.0005 }
        else f.gap = 1 / 120 // the last frame: nothing comes after it
      }
      f.at = meta.mediaTime
      seen.current?.(meta.mediaTime, el)
      handle = el.requestVideoFrameCallback(shown)
    }
    handle = el.requestVideoFrameCallback(shown)
    return () => el.cancelVideoFrameCallback(handle)
  }, [video])
  useEffect(() => {
    if (!enabled) return
    const step = (el: HTMLVideoElement, by: -1 | 1) => {
      el.pause()
      if (el.seeking) return // the last step is still on its way
      const f = frame.current
      const at = f.at >= 0 ? f.at : el.currentTime
      // A step forward that stayed on this frame without the video saying so: further this time.
      if (by > 0 && f.from === at && performance.now() - f.stepped > 250) f.gap = Math.min(f.gap * 2, 1)
      f.from = by > 0 ? at : -1
      f.stepped = performance.now()
      el.currentTime = by > 0 ? at + f.gap + 0.0005 : Math.max(0, at - 0.001)
    }
    const onKey = (event: KeyboardEvent) => {
      const el = video.current
      if (!el || !playerKey(event)) return
      if (event.key === ' ') {
        // Space on a focused button presses it, as everywhere else.
        if (event.target instanceof Element && event.target.closest('button, a')) return
        if (el.paused) void el.play().catch(() => undefined)
        else el.pause()
      } else if (event.key === ',' || event.key === '.') step(el, event.key === ',' ? -1 : 1)
      else return
      event.preventDefault()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [video, enabled])
}

/** A trim point's keys: arrows move it a second (Shift: five), Home and End to as far as it goes. */
function trimKey(event: ReactKeyboardEvent, value: number, min: number, max: number, set: (ms: number) => void) {
  const step = event.shiftKey ? 5000 : 1000
  const next = event.key === 'ArrowLeft' || event.key === 'ArrowDown' ? value - step
    : event.key === 'ArrowRight' || event.key === 'ArrowUp' ? value + step
      : event.key === 'Home' ? min : event.key === 'End' ? max : null
  if (next === null) return
  event.preventDefault()
  set(Math.min(max, Math.max(min, next)))
}

/** In and out points on the clip's timeline; drag either, or click to seek. Each point is a slider for the keyboard too. */
function TrimBar({ id, src, duration, start, end, time, onChange, onSeek }: {
  id: string
  src: string
  duration: number
  start: number
  end: number
  time: number
  onChange: (start: number, end: number) => void
  onSeek: (ms: number) => void
}) {
  const bar = useRef<HTMLDivElement>(null)
  const drag = useRef<'start' | 'end' | 'seek' | null>(null)
  const at = (event: ReactPointerEvent) => {
    const box = bar.current!.getBoundingClientRect()
    return timeAt(event.clientX, box.left, box.width, duration)
  }
  const pct = (ms: number) => `${duration ? (ms / duration) * 100 : 0}%`
  return (
    <div
      ref={bar}
      className="exo-trim"
      role="group"
      aria-label="Trim"
      onPointerDown={event => {
        const ms = at(event)
        const near = Math.abs(ms - start) < Math.abs(ms - end) ? 'start' : 'end'
        const box = bar.current!.getBoundingClientRect()
        const handle = Math.abs(((near === 'start' ? start : end) / duration) * box.width - (event.clientX - box.left)) < 12
        if (handle) { drag.current = near; event.currentTarget.setPointerCapture(event.pointerId) }
        else { drag.current = 'seek'; event.currentTarget.setPointerCapture(event.pointerId); onSeek(ms) }
      }}
      onPointerMove={event => {
        if (!drag.current) return
        const ms = at(event)
        if (drag.current === 'seek') onSeek(ms)
        else if (drag.current === 'start') onChange(Math.min(ms, end - 500), end)
        else onChange(start, Math.max(ms, start + 500))
      }}
      onPointerUp={() => { drag.current = null }}
      onPointerCancel={() => { drag.current = null }}
      onLostPointerCapture={() => { drag.current = null }}
    >
      <TimelineStrip id={id} src={src} durationMs={duration} />
      <div className="exo-trim-dim" style={{ left: 0, width: pct(start) }} />
      <div className="exo-trim-dim" style={{ right: 0, width: `calc(100% - ${pct(end)})` }} />
      <div className="exo-trim-kept" style={{ left: pct(start), width: `calc(${pct(end)} - ${pct(start)})` }} />
      <div className="exo-trim-time" style={{ left: pct(time) }} />
      <span className="exo-trim-handle" style={{ left: pct(start) }} role="slider" tabIndex={0} aria-label="Start"
        aria-valuemin={0} aria-valuemax={Math.round(end - 500)} aria-valuenow={Math.round(start)} aria-valuetext={clock(start)}
        onKeyDown={event => trimKey(event, start, 0, end - 500, ms => onChange(ms, end))} />
      <span className="exo-trim-handle" style={{ left: pct(end) }} role="slider" tabIndex={0} aria-label="End"
        aria-valuemin={Math.round(start + 500)} aria-valuemax={Math.round(duration)} aria-valuenow={Math.round(end)} aria-valuetext={clock(end)}
        onKeyDown={event => trimKey(event, end, start + 500, duration, ms => onChange(start, ms))} />
    </div>
  )
}

/** The editor's speeds, slowest first; [ and ] step through them. */
const SPEEDS = [0.5, 1, 2] as const
type Speed = (typeof SPEEDS)[number]

type Tab = 'trim' | 'export'
const TABS: readonly EditorTab<Tab>[] = [
  { id: 'trim', label: 'Trim', icon: Scissors },
  { id: 'export', label: 'Export', icon: Movie },
]

/** The export sizes, as tiles: what each is for, and the shape it comes out in. */
const EXPORTS: { preset: Exclude<CaptureExport, 'trim'>; label: string; detail: string; shape: number }[] = [
  { preset: 'discord', label: 'Discord', detail: 'Under 10 MB', shape: 16 / 9 },
  { preset: 'youtube', label: '16:9', detail: 'Full quality', shape: 16 / 9 },
  { preset: 'vertical', label: '9:16', detail: 'Shorts and Reels', shape: 9 / 16 },
]

const SAVED: Record<CaptureExport, string> = {
  trim: 'Saved the trim as a new clip.',
  discord: 'Saved a copy for Discord, under 10 MB.',
  youtube: 'Saved a full-quality copy.',
  vertical: 'Saved a vertical copy.',
}

/**
 * A clip's editor (Captures › Edit): the clip and its trim on the left; the inspector's Trim (length, speed, Loop)
 * and Export (Discord, 16:9, 9:16 tiles, each saving a copy of the part kept). Save keeps the trim as a new clip
 * (a cut at key frames, no re-encode); the original stays.
 */
export function ClipEditor({ item, onDone, onAdded }: { item: CaptureItem; onDone: () => void; onAdded: (item: CaptureItem) => void }) {
  const video = useRef<HTMLVideoElement>(null)
  const [duration, setDuration] = useState(item.durationMs ?? 0)
  const [range, setRange] = useState<[number, number]>([0, item.durationMs ?? 0])
  const [time, setTime] = useState(0)
  const [tab, setTab] = useState<Tab>('trim')
  const [speed, setSpeed] = useState<Speed>(1)
  const [loop, setLoop] = useState(false)
  const [busy, setBusy] = useState<CaptureExport | null>(null)
  const [note, setNote] = useState<{ text: string; bad?: boolean } | null>(null)
  const trimmed = duration > 0 && (range[0] > 250 || range[1] < duration - 250)

  useEffect(() => {
    if (!video.current) return
    video.current.defaultPlaybackRate = speed
    video.current.playbackRate = speed
  }, [speed])

  // Plays only what stays after the trim: at its end, back to its start, paused unless Loop is on.
  useFrameKeys(video, true, (mediaTime, el) => {
    if (trimmed && !el.paused && mediaTime * 1000 >= range[1]) {
      el.currentTime = range[0] / 1000
      if (!loop) el.pause()
    }
  })
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (!playerKey(event)) return
      if (event.key === '[' || event.key === ']') {
        const by = event.key === '[' ? -1 : 1
        setSpeed(now => SPEEDS[Math.min(SPEEDS.length - 1, Math.max(0, SPEEDS.indexOf(now) + by))])
      } else if (event.key === 'l' || event.key === 'L') setLoop(on => !on)
      else return
      event.preventDefault()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  async function save(preset: CaptureExport) {
    if (busy) return
    setBusy(preset)
    setNote(null)
    const failed = 'Exo Capture could not save the copy. Try again.'
    try {
      const result = await host.captureEdit(item.id, preset, range[0], trimmed ? range[1] : 0)
      if (!result.ok) setNote({ text: result.message ?? failed, bad: true })
      else {
        setNote({ text: SAVED[preset] })
        if (result.item) onAdded(result.item)
      }
    } catch (error) { setNote({ text: error instanceof Error ? error.message : failed, bad: true }) }
    finally { setBusy(null) }
  }

  return (
    <EditorShell
      label="Edit clip"
      tabs={TABS}
      tab={tab}
      onTab={setTab}
      history={{ revert: trimmed || speed !== 1 || loop ? () => { setRange([0, duration]); setSpeed(1); setLoop(false) } : null }}
      save={{ label: 'Save trim', busy: busy === 'trim', busyLabel: 'Saving the trim', disabled: !trimmed || !!busy, run: () => void save('trim') }}
      onCancel={onDone}
      media={
        <div className="exo-clip-media">
          <div className="exo-capture-stage">
            <video
              ref={video}
              src={item.url}
              controls
              playsInline
              loop={loop && !trimmed}
              onLoadedMetadata={event => {
                event.currentTarget.volume = playback.volume
                event.currentTarget.muted = playback.muted
                const ms = event.currentTarget.duration * 1000
                if (Number.isFinite(ms) && ms > 0) { setDuration(ms); setRange(r => [r[0], r[1] > 0 && r[1] <= ms ? r[1] : ms]) }
              }}
              onPlay={event => {
                // Played from past the trim's end, it starts at the trim's start.
                if (trimmed && event.currentTarget.currentTime * 1000 >= range[1] - 50) event.currentTarget.currentTime = range[0] / 1000
              }}
              onTimeUpdate={event => setTime(event.currentTarget.currentTime * 1000)}
              onVolumeChange={event => { playback.volume = event.currentTarget.volume; playback.muted = event.currentTarget.muted }}
            />
          </div>
          {duration > 0 ? (
            <TrimBar id={item.id} src={item.url} duration={duration} start={range[0]} end={range[1]} time={time}
              onChange={(start, end) => { setRange([start, end]); if (video.current) video.current.currentTime = start / 1000 }}
              onSeek={ms => { if (video.current) video.current.currentTime = ms / 1000 }} />
          ) : null}
        </div>
      }
    >
      {tab === 'trim' ? (
        <>
          <dl className="exo-editor-facts tabular-nums">
            <dt>Kept</dt><dd>{clock(range[1] - range[0])} of {clock(duration)}</dd>
          </dl>
          <p className="exo-editor-hint">Drag the ends of the strip. Save keeps the trim as a new clip; the original stays.</p>
          <span className="exo-shelf-chips" role="group" aria-label="Speed">
            {SPEEDS.map(rate => (
              <button key={rate} type="button" className="exo-shelf-chip" aria-pressed={speed === rate} onClick={() => setSpeed(rate)}>{rate}×</button>
            ))}
          </span>
          <span className="exo-shelf-chips">
            <button type="button" className="exo-shelf-chip" aria-pressed={loop} aria-keyshortcuts="L" onClick={() => setLoop(on => !on)}>
              <Repeat size={14} />Loop
            </button>
          </span>
        </>
      ) : (
        <>
          <div className="exo-editor-tiles">
            {EXPORTS.map(entry => (
              <PresetTile key={entry.preset} label={entry.label} detail={entry.detail} shape={entry.shape} busy={busy === entry.preset}
                disabled={!!busy} onPress={() => void save(entry.preset)} />
            ))}
          </div>
          <p className="exo-editor-hint">{trimmed ? `A copy of the ${clock(range[1] - range[0])} you kept.` : 'A copy of the whole clip; trim it first to keep less.'}</p>
        </>
      )}
      {note ? note.bad ? <ErrorNote className="exo-capture-note is-bad" message={note.text} /> : <p className="exo-capture-note" role="status">{note.text}</p> : null}
    </EditorShell>
  )
}

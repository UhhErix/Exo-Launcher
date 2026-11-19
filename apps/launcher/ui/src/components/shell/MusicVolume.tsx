import { ErrorNote } from '../ui/ErrorNote'
import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type MusicVolumeSource } from '../../lib/host'
import type { MusicService } from '../../lib/music'
import { onPageActivityChanged, pageIsActive, pageIsVisible } from '../../lib/pageActivity'
import { volumeKeyStep } from '../../lib/musicVolume'
import { Slider } from '../ui/Slider'
import { Volume, VolumeLow, VolumeOff } from '../../brand/icons'

/** The app's own level in percent (null until it has answered) and where it came from. */
export type MusicLevel = { level: number | null; source: MusicVolumeSource | null }

/** The music app's volume, on its own clock: read at once and when the service changes, followed from the app's own
    events, and re-read while the player is on screen when it is a Windows session level or not known yet (every second
    on screen, every five seconds behind another window). `hidden`: the player is not on screen, so nothing is read. */
export function useMusicVolume(service: MusicService, hidden: boolean) {
  const [volume, setVolume] = useState<MusicLevel>({ level: null, source: null })
  const readVolume = useRef<() => void>(() => { })
  useEffect(() => {
    let live = true, reading = false, heard = 0
    setVolume({ level: null, source: null })
    const acceptVolume = (level: number, source: MusicVolumeSource | null) => {
      if (!live) return
      setVolume(previous => previous.level === level && previous.source === (source ?? previous.source) ? previous : { level, source: source ?? previous.source })
    }
    // A read that started before the app's own volume event is older than it and is dropped.
    const read = async () => {
      if (reading) return
      reading = true
      const since = heard
      try {
        const result = await host.musicVolume()
        if (live && since === heard && result.ok && typeof result.level === 'number') acceptVolume(result.level * 100, result.source ?? null)
      } catch { /* asked again while unknown */ }
      reading = false
    }
    readVolume.current = () => void read()
    void read()
    const offVolume = onHostEvent('music.volume', data => { if (typeof data?.level === 'number') { heard += 1; acceptVolume(data.level * 100, 'app') } })
    return () => { live = false; readVolume.current = () => { }; offVolume() }
  }, [service])

  const follow = !hidden && (volume.source === 'session' || volume.level === null)
  useEffect(() => {
    if (!follow) return
    let timer = 0
    let every = 0
    const stop = () => { if (timer) { window.clearInterval(timer); timer = 0 } }
    const start = () => {
      if (!pageIsVisible()) { stop(); return }
      const next = pageIsActive() ? 1000 : 5000
      if (timer && next === every) return
      if (!timer || next < every) readVolume.current()
      stop()
      every = next
      timer = window.setInterval(() => readVolume.current(), every)
    }
    start()
    const off = onPageActivityChanged(start)
    return () => { stop(); off() }
  }, [follow])
  return { volume, read: () => readVolume.current() }
}

export function MusicVolume({ volume, onRefresh, className }: { volume: number | null; onRefresh: () => void; className?: string }) {
  const [draft, setDraft] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const state = useRef({ queued: null as number | null, writing: false, alive: true, dragging: false, value: volume ?? 0, revision: 0, settle: 0, flush: 0, lastInput: 0, accepted: null as number | null })
  const slider = useRef<HTMLInputElement>(null)
  const lastAudible = useRef(volume && volume > 0 ? volume : 25)
  const value = draft ?? volume ?? 0
  // An unknown level is not a muted one: until the app answers, the control is
  // disabled and shows neither a level nor the muted icon.
  const silent = (draft ?? volume) === 0
  state.current.value = value
  const confirmed = useRef(volume)
  confirmed.current = volume
  const refresh = useRef(onRefresh)
  refresh.current = onRefresh
  useEffect(() => { const s = state.current; s.alive = true; return () => { s.alive = false; s.queued = null; clearTimeout(s.settle); clearTimeout(s.flush) } }, [])

  async function flush() {
    const s = state.current
    clearTimeout(s.flush)
    s.flush = 0
    if (s.writing || !s.alive) return
    s.writing = true
    try {
      while (s.queued !== null && s.alive) {
        const target = s.queued, revision = s.revision
        s.queued = null
        try {
          const result = await host.musicVolume(target)
          if (!s.alive || revision !== s.revision) continue
          if (!result.ok) throw Error(result.message || 'Music volume could not be changed.')
          // A provider response may arrive while the thumb is still moving.
          // Keep the user's position until the gesture ends and the latest value settles.
          const actual = typeof result.level === 'number' ? Math.round(result.level * 100) : target
          s.accepted = actual
          refresh.current()
          s.settle = window.setTimeout(() => {
            if (s.alive && revision === s.revision && !s.dragging) {
              setDraft(Math.abs((confirmed.current ?? actual) - actual) <= 1 ? null : actual)
            }
          }, 700)
        } catch (error) {
          // Roll back to the last level the session actually accepted; the
          // prop may still be a read from before the previous write landed.
          if (s.alive && revision === s.revision) { setDraft(s.accepted); setError(error instanceof Error ? error.message : 'Music volume could not be changed.') }
        }
      }
    } finally { s.writing = false }
  }
  const flushRef = useRef(flush)
  flushRef.current = flush
  function change(percent: number, immediate = false) {
    const s = state.current
    const next = Math.round(Math.max(0, Math.min(100, percent)))
    clearTimeout(s.settle)
    s.value = next; s.queued = next; s.revision++; s.lastInput = Date.now()
    if (next > 0) lastAudible.current = next
    setDraft(next); setError(null)
    if (immediate) void flushRef.current()
    else if (!s.flush) s.flush = window.setTimeout(() => void flushRef.current(), 24)
  }
  const changeRef = useRef(change)
  changeRef.current = change
  useEffect(() => {
    const input = slider.current
    if (!input) return
    const wheel = (event: WheelEvent) => {
      if (input.disabled || !event.deltaY || event.ctrlKey) return
      event.preventDefault()
      changeRef.current(state.current.value + (event.deltaY < 0 ? 1 : -1))
    }
    input.addEventListener('wheel', wheel, { passive: false })
    return () => input.removeEventListener('wheel', wheel)
  }, [])
  useEffect(() => {
    const s = state.current
    // Unmute returns to the app's own last level, also one that arrived late.
    if (volume !== null && volume > 0) lastAudible.current = volume
    if (s.writing || s.queued !== null || s.dragging || Date.now() - s.lastInput < 700) return
    // Once the gesture settles, external provider changes own the display again.
    setDraft(null)
  }, [volume])
  const endDrag = () => {
    const s = state.current
    s.dragging = false
    void flushRef.current()
    refresh.current()
    if (!s.writing && s.queued === null) {
      clearTimeout(s.settle)
      s.settle = window.setTimeout(() => { if (s.alive && !s.dragging && !s.writing && s.queued === null) setDraft(null) }, 700)
    }
  }
  return <div className={['exo-mini-volume', className].filter(Boolean).join(' ')} role="group" aria-label="Music volume">
    <button aria-label={silent ? 'Unmute music' : 'Mute music'} disabled={volume === null} onClick={() => change(silent ? lastAudible.current : 0, true)}>
      {silent ? <VolumeOff size={18} /> : value < 50 ? <VolumeLow size={18} /> : <Volume size={18} />}
    </button>
    <Slider compact ref={slider} aria-label="Music volume" aria-valuetext={`${Math.round(value)} percent`} min={0} max={100} step={1} disabled={volume === null} value={value}
      onChange={event => change(Number(event.currentTarget.value))}
      onKeyDown={event => { const step = volumeKeyStep(event.key); if (step === null) return; event.preventDefault(); change(state.current.value + step) }}
      onPointerDown={event => { state.current.dragging = true; event.currentTarget.setPointerCapture?.(event.pointerId) }} onPointerUp={endDrag} onPointerCancel={endDrag} onBlur={endDrag} />
    <output className="exo-mini-volume-value" aria-live="off">{volume === null ? '—' : `${Math.round(value)}%`}</output>
    {error && <ErrorNote as="span" className="exo-mini-error" message={error} />}
  </div>
}

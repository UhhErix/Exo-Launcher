import { useEffect, useRef } from 'react'
import { coverCrop, stripCount, stripTimes } from '../../lib/timeline'

/** Finished strips by clip and size, so stepping back to a clip does not decode it again. Few, and small (a few hundred KB each). */
const done = new Map<string, HTMLCanvasElement>()
const KEPT = 6

/** Resolves when the video has shown the frame it was asked for, or after `wait` ms (a frame that never comes is left blank). */
function seekTo(video: HTMLVideoElement, ms: number, wait = 4000): Promise<void> {
  return new Promise(resolve => {
    const finish = () => { clearTimeout(timer); video.removeEventListener('seeked', finish); resolve() }
    const timer = setTimeout(finish, wait)
    video.addEventListener('seeked', finish)
    video.currentTime = ms / 1000
  })
}

/**
 * The frames of a clip along its trim bar, taken locally by a second, hidden, muted video seeking one frame at a
 * time (a cheap seek per cell, not playback), drawn as they arrive. Purely a picture: if the clip will not decode,
 * the bar stays plain. It waits for the page to be visible and stops the moment the clip changes or the bar leaves.
 */
export function TimelineStrip({ id, src, durationMs }: { id: string; src: string; durationMs: number }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const target = canvas.current
    const box = target?.parentElement
    if (!target || !box || !src || !(durationMs > 0)) return
    const { width, height } = box.getBoundingClientRect()
    const count = stripCount(width, height)
    if (!count) return
    const scale = Math.min(2, window.devicePixelRatio || 1)
    const cellW = Math.round((width / count) * scale)
    const cellH = Math.round(height * scale)
    target.width = cellW * count
    target.height = cellH
    const draw = target.getContext('2d')
    if (!draw) return
    const key = `${id}:${count}:${target.width}`
    const kept = done.get(key)
    if (kept) { draw.drawImage(kept, 0, 0); return }

    let stopped = false
    const video = document.createElement('video')
    video.muted = true
    video.preload = 'auto'
    video.crossOrigin = 'anonymous'
    video.src = src
    void (async () => {
      await new Promise<void>(resolve => {
        video.addEventListener('loadeddata', () => resolve(), { once: true })
        video.addEventListener('error', () => resolve(), { once: true })
      })
      if (stopped || !video.videoWidth) return
      const crop = coverCrop(video.videoWidth, video.videoHeight, cellW, cellH)
      const times = stripTimes(Number.isFinite(video.duration) ? Math.min(durationMs, video.duration * 1000) : durationMs, count)
      for (let i = 0; i < times.length && !stopped; i++) {
        if (document.visibilityState !== 'visible') await new Promise<void>(resolve => document.addEventListener('visibilitychange', () => resolve(), { once: true }))
        await seekTo(video, times[i])
        if (stopped) return
        try { draw.drawImage(video, crop.sx, crop.sy, crop.sw, crop.sh, i * cellW, 0, cellW, cellH) } catch { return }
      }
      if (stopped) return
      const copy = document.createElement('canvas')
      copy.width = target.width
      copy.height = target.height
      copy.getContext('2d')?.drawImage(target, 0, 0)
      done.set(key, copy)
      while (done.size > KEPT) done.delete(done.keys().next().value!)
    })()
    return () => {
      stopped = true
      video.removeAttribute('src')
      video.load()
    }
  }, [id, src, durationMs])
  return <canvas ref={canvas} className="exo-trim-strip" aria-hidden="true" />
}

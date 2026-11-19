// The Captures timeline's arithmetic: where a pointer lands on a clip, and which frames the thumbnail strip takes.
// Plain numbers, no DOM, so `npm test` (test/timeline.test.ts) can run it.

/** The clip time (ms) under a pointer at `x`, on a bar from `left` that is `width` wide; clamped to the clip. */
export function timeAt(x: number, left: number, width: number, durationMs: number): number {
  if (!(width > 0) || !(durationMs > 0)) return 0
  return Math.min(durationMs, Math.max(0, ((x - left) / width) * durationMs))
}

/** How many thumbnails fill a bar: each about a 16:9 frame at the bar's height, never fewer than 1 or more than `max`. */
export function stripCount(width: number, height: number, max = 40): number {
  if (!(width > 0) || !(height > 0)) return 0
  return Math.min(max, Math.max(1, Math.round(width / (height * 16 / 9))))
}

/** The times (ms) of `count` frames, each the middle of its equal slice of the clip, so the first and last are never the black edges. */
export function stripTimes(durationMs: number, count: number): number[] {
  if (!(durationMs > 0) || !(count > 0)) return []
  return Array.from({ length: count }, (_, i) => ((i + 0.5) / count) * durationMs)
}

/** The source rectangle that fills a `cellW` by `cellH` cell from a `w` by `h` frame without stretching it (a centered crop). */
export function coverCrop(w: number, h: number, cellW: number, cellH: number): { sx: number; sy: number; sw: number; sh: number } {
  if (!(w > 0) || !(h > 0) || !(cellW > 0) || !(cellH > 0)) return { sx: 0, sy: 0, sw: Math.max(0, w), sh: Math.max(0, h) }
  const wide = w / h > cellW / cellH
  const sw = wide ? h * (cellW / cellH) : w
  const sh = wide ? h : w * (cellH / cellW)
  return { sx: (w - sw) / 2, sy: (h - sh) / 2, sw, sh }
}

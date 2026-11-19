// The Captures gallery's plain logic: which captures the chips leave, the days they are shown under, the months the
// scrubber jumps to, On this day, and the editor's crop shapes. No DOM, so `npm test` (test/gallery.test.ts) can run it.
import type { CaptureItem } from './capture'

/** What the chips ask for; each narrows the rest (a game's screenshots, its favorite clips). */
export type GalleryFilter = {
  /** A game id or folder key; null: every game. */
  game: string | null
  kind: 'all' | 'video' | 'image'
  favorites: boolean
}

export function filterCaptures(items: readonly CaptureItem[], filter: GalleryFilter, inGame: (item: CaptureItem) => boolean): CaptureItem[] {
  return items.filter(item => (filter.kind === 'all' || item.kind === filter.kind)
    && (!filter.favorites || item.favourite)
    && (filter.game === null || inGame(item)))
}

const dayKey = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
const monthKey = (date: Date) => dayKey(date).slice(0, 7)

/** One day's captures, as the grid shows them under its heading. */
export type Day = { key: string; month: string; title: string; items: CaptureItem[] }

/** Today, Yesterday, a weekday this week, else the date (with the year once it is not this one). */
export function dayTitle(date: Date, now: Date): string {
  const days = Math.round((new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime() - new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime()) / 86_400_000)
  if (days === 0) return 'Today'
  if (days === 1) return 'Yesterday'
  if (days > 1 && days < 7) return date.toLocaleDateString(undefined, { weekday: 'long' })
  return date.toLocaleDateString(undefined, date.getFullYear() === now.getFullYear()
    ? { weekday: 'short', day: 'numeric', month: 'long' } : { day: 'numeric', month: 'long', year: 'numeric' })
}

/** The captures by the day they were made, newest first, in the order the list has them within a day. */
export function byDay(items: readonly CaptureItem[], now: Date): Day[] {
  const days = new Map<string, Day>()
  for (const item of [...items].sort((a, b) => Date.parse(b.created) - Date.parse(a.created))) {
    const date = new Date(item.created)
    const key = dayKey(date)
    let day = days.get(key)
    if (!day) days.set(key, day = { key, month: monthKey(date), title: dayTitle(date, now), items: [] })
    day.items.push(item)
  }
  return [...days.values()]
}

/** A scrubber stop: a month with captures; the label is its short name, or the year where a year starts. */
export type Stop = { month: string; label: string; year: boolean; day: string }

/** The months the scrubber offers, newest first; each jumps to its newest day. */
export function scrubStops(days: readonly Day[], now: Date): Stop[] {
  const stops: Stop[] = []
  for (const day of days) {
    if (stops.at(-1)?.month === day.month) continue
    const [year, month] = day.month.split('-').map(Number)
    const previous = stops.at(-1)
    const newYear = previous ? previous.month.slice(0, 4) !== day.month.slice(0, 4) : year !== now.getFullYear()
    stops.push({
      month: day.month,
      day: day.key,
      year: newYear,
      label: newYear ? String(year) : new Date(year, month - 1, 1).toLocaleDateString(undefined, { month: 'short' }),
    })
  }
  return stops
}

/** "November 2026": the month a date chip names. */
export function monthTitle(month: string): string {
  const [year, m] = month.split('-').map(Number)
  return new Date(year, m - 1, 1).toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
}

/** On this day: captures from today's date in an earlier year, newest first (none from this year). */
export function onThisDay(items: readonly CaptureItem[], now: Date): CaptureItem[] {
  return items
    .filter(item => {
      const date = new Date(item.created)
      return date.getFullYear() < now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate()
    })
    .sort((a, b) => Date.parse(b.created) - Date.parse(a.created))
}

/** The editor's crop shapes, like Photos' templates: free, or a ratio a place wants. */
export const CROP_SHAPES = [
  { id: 'free', label: 'Free', ratio: null },
  { id: 'wide', label: '16:9', ratio: 16 / 9 },
  { id: 'tall', label: '9:16', ratio: 9 / 16 },
  { id: 'square', label: 'Square', ratio: 1 },
] as const
export type CropShape = (typeof CROP_SHAPES)[number]['id']

export type Rect = { x: number; y: number; w: number; h: number }

/**
 * A crop dragged from `from` to `to` with a shape held: the largest box of that ratio inside the drag, from the corner it
 * started at, kept inside the picture. Free keeps the drag as it is.
 */
export function shapedCrop(from: [number, number], to: [number, number], ratio: number | null, width: number, height: number): Rect {
  const cx = Math.min(Math.max(to[0], 0), width), cy = Math.min(Math.max(to[1], 0), height)
  let w = Math.abs(cx - from[0]), h = Math.abs(cy - from[1])
  if (ratio) {
    if (w / ratio > h) w = h * ratio
    else h = w / ratio
  }
  const x = cx < from[0] ? from[0] - w : from[0]
  const y = cy < from[1] ? from[1] - h : from[1]
  return { x, y, w, h }
}

/** The whole picture as a shape: the largest box of the ratio, centered (a tile pressed with nothing dragged). */
export function centeredCrop(ratio: number, width: number, height: number): Rect {
  const w = Math.min(width, height * ratio), h = w / ratio
  return { x: (width - w) / 2, y: (height - h) / 2, w, h }
}

/**
 * Blur's cell: as large as the box's shorter side (a chat line or a name is one or two cells tall), and never under
 * a few letters wide, so each cell averages several letters and no letter can be matched back from the blocks.
 */
export function blurCell(rect: Rect, unit: number): number {
  return Math.max(unit * 8, Math.min(rect.w, rect.h))
}

// The Captures gallery's chips, days, scrubber, On this day and the editor's crop shapes (src/lib/gallery.ts).
//
// How it could fail: two chips don't combine (Hades' screenshots shows every screenshot); a day splits in two or two days
// merge; the newest day isn't first; the scrubber offers a month twice, misses a year change, or jumps nowhere; On this day
// shows today's own captures or another date's; a 16:9 crop isn't 16:9, leaves the picture, or grows from the wrong
// corner; Blur's cells are small enough to read letters back.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { blurCell, byDay, centeredCrop, filterCaptures, onThisDay, scrubStops, shapedCrop } from '../src/lib/gallery.ts'
import type { CaptureItem } from '../src/lib/capture.ts'

const item = (id: string, created: string, extra: Partial<CaptureItem> = {}): CaptureItem => ({
  id, kind: 'video', game: 'Hades', name: id, created, size: 1, favourite: false, url: '', ...extra,
})
const ids = (items: readonly CaptureItem[]) => items.map(entry => entry.id)
const now = new Date(2026, 10, 19, 12, 0)
const local = (y: number, m: number, d: number, h = 12) => new Date(y, m - 1, d, h).toISOString()

test('the chips narrow each other: a game, a type and favorites together', () => {
  const items = [
    item('hades-clip', local(2026, 11, 19)),
    item('hades-shot', local(2026, 11, 19), { kind: 'image' }),
    item('hades-shot-star', local(2026, 11, 18), { kind: 'image', favourite: true }),
    item('rivals-shot-star', local(2026, 11, 18), { kind: 'image', favourite: true, game: 'Marvel Rivals' }),
  ]
  const hades = (entry: CaptureItem) => entry.game === 'Hades'
  assert.deepEqual(ids(filterCaptures(items, { game: 'hades', kind: 'image', favorites: false }, hades)), ['hades-shot', 'hades-shot-star'])
  assert.deepEqual(ids(filterCaptures(items, { game: 'hades', kind: 'image', favorites: true }, hades)), ['hades-shot-star'])
  assert.deepEqual(ids(filterCaptures(items, { game: null, kind: 'image', favorites: true }, hades)), ['hades-shot-star', 'rivals-shot-star'])
  assert.deepEqual(ids(filterCaptures(items, { game: null, kind: 'all', favorites: false }, hades)), ids(items))
})

test('captures are grouped by the day they were made, newest day first', () => {
  const days = byDay([
    item('old', local(2026, 9, 2)),
    item('today-morning', local(2026, 11, 19, 8)),
    item('yesterday', local(2026, 11, 18, 23)),
    item('today-noon', local(2026, 11, 19, 12)),
  ], now)
  assert.deepEqual(days.map(day => day.title.length > 0 && day.key), ['2026-11-19', '2026-11-18', '2026-09-02'])
  assert.deepEqual(ids(days[0].items), ['today-noon', 'today-morning'])
  assert.equal(days[0].title, 'Today')
  assert.equal(days[1].title, 'Yesterday')
})

test('the scrubber has one stop a month, a year where the year changes, each jumping to that month\'s newest day', () => {
  const days = byDay([
    item('a', local(2026, 11, 19)), item('b', local(2026, 11, 2)),
    item('c', local(2026, 9, 2)), item('d', local(2025, 12, 30)), item('e', local(2025, 12, 1)), item('f', local(2025, 3, 3)),
  ], now)
  const stops = scrubStops(days, now)
  assert.deepEqual(stops.map(stop => stop.month), ['2026-11', '2026-09', '2025-12', '2025-03'])
  assert.deepEqual(stops.map(stop => stop.year), [false, false, true, false])
  assert.equal(stops[2].label, '2025')
  assert.equal(stops[0].day, '2026-11-19')
  assert.equal(stops[2].day, '2025-12-30')
})

test('a gallery that starts in an earlier year says that year at its first stop', () => {
  const stops = scrubStops(byDay([item('a', local(2024, 5, 5))], now), now)
  assert.deepEqual(stops.map(stop => [stop.label, stop.year]), [['2024', true]])
})

test('On this day is today\'s date in earlier years only', () => {
  const items = [item('today', local(2026, 11, 19)), item('last-year', local(2025, 11, 19)), item('two-years', local(2024, 11, 19)), item('other-day', local(2025, 11, 18))]
  assert.deepEqual(ids(onThisDay(items, now)), ['last-year', 'two-years'])
})

test('a shaped crop keeps its ratio, stays in the picture and grows from where the drag began', () => {
  const wide = shapedCrop([100, 100], [900, 900], 16 / 9, 1920, 1080)
  assert.equal(Math.round((wide.w / wide.h) * 1000), Math.round((16 / 9) * 1000))
  assert.deepEqual([wide.x, wide.y], [100, 100])
  const back = shapedCrop([1000, 800], [0, 0], 1, 1920, 1080)
  assert.equal(back.w, back.h)
  assert.equal(back.x + back.w, 1000)
  assert.equal(back.y + back.h, 800)
  const past = shapedCrop([1800, 1000], [5000, 5000], null, 1920, 1080)
  assert.deepEqual(past, { x: 1800, y: 1000, w: 120, h: 80 })
})

test('a shape pressed with nothing dragged is the largest of it in the middle', () => {
  assert.deepEqual(centeredCrop(1, 1920, 1080), { x: 420, y: 0, w: 1080, h: 1080 })
  const tall = centeredCrop(9 / 16, 1920, 1080)
  assert.equal(tall.h, 1080)
  assert.ok(tall.x > 0 && tall.x + tall.w < 1920)
})

test('Blur\'s cells are as tall as the line they cover, and never smaller than a few letters', () => {
  assert.equal(blurCell({ x: 0, y: 0, w: 900, h: 40 }, 3), 40)
  assert.equal(blurCell({ x: 0, y: 0, w: 900, h: 10 }, 3), 24)
  assert.equal(blurCell({ x: 0, y: 0, w: 300, h: 300 }, 3), 300)
})

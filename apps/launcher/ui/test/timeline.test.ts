// The Captures timeline's arithmetic (src/lib/timeline.ts). `npm test` (Node's own runner, which reads TypeScript as it is).
//
// How it could fail: a pointer left of the bar or past its end seeks outside the clip; a bar with no width (not laid
// out yet) divides by zero; a clip with no known length yields NaN times; the strip asks for no frames, or hundreds
// on a wide screen; a thumbnail is taken at 0 or the last instant, which are black fades; two thumbnails are the same
// frame; a tall or wide frame is stretched into its cell instead of cropped.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { coverCrop, stripCount, stripTimes, timeAt } from '../src/lib/timeline.ts'

test('a pointer maps linearly and clamps to the clip', () => {
  assert.equal(timeAt(150, 100, 200, 60_000), 15_000)
  assert.equal(timeAt(50, 100, 200, 60_000), 0)
  assert.equal(timeAt(900, 100, 200, 60_000), 60_000)
})

test('a bar not laid out, or a clip of unknown length, gives 0', () => {
  assert.equal(timeAt(150, 100, 0, 60_000), 0)
  assert.equal(timeAt(150, 100, 200, 0), 0)
  assert.equal(timeAt(150, 100, 200, Number.NaN), 0)
})

test('the strip takes whole 16:9 frames, 1 to 40', () => {
  assert.equal(stripCount(640, 40), 9)
  assert.equal(stripCount(10, 40), 1)
  assert.equal(stripCount(8000, 40), 40)
  assert.equal(stripCount(0, 40), 0)
  assert.equal(stripCount(640, 0), 0)
})

test('frames sit mid-slice, increasing, inside the clip', () => {
  const times = stripTimes(60_000, 6)
  assert.deepEqual(times, [5000, 15_000, 25_000, 35_000, 45_000, 55_000])
  assert.ok(times[0] > 0 && times[times.length - 1] < 60_000)
  assert.deepEqual(stripTimes(0, 6), [])
  assert.deepEqual(stripTimes(60_000, 0), [])
  assert.deepEqual(stripTimes(Number.NaN, 4), [])
})

test('a frame is cropped to its cell, never stretched', () => {
  // 16:9 into 16:9: whole frame.
  assert.deepEqual(coverCrop(1920, 1080, 160, 90), { sx: 0, sy: 0, sw: 1920, sh: 1080 })
  // Ultrawide into 16:9: the sides go.
  const wide = coverCrop(3440, 1440, 160, 90)
  assert.ok(Math.abs(wide.sw / wide.sh - 160 / 90) < 1e-9 && wide.sh === 1440 && wide.sx > 0 && wide.sy === 0)
  // Portrait into 16:9: top and bottom go.
  const tall = coverCrop(1080, 1920, 160, 90)
  assert.ok(Math.abs(tall.sw / tall.sh - 160 / 90) < 1e-9 && tall.sw === 1080 && tall.sy > 0 && tall.sx === 0)
  // No frame yet: nothing to crop.
  assert.deepEqual(coverCrop(0, 0, 160, 90), { sx: 0, sy: 0, sw: 0, sh: 0 })
})

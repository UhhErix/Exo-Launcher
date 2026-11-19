// Listen along's clock (src/lib/listenSync.ts) on made-up positions. `npm test` (Node's own runner, which reads
// TypeScript as it is); `npm run check` runs it too.
//
// How it could fail: a listener whose player stalled or buffered stays behind for the rest of the song (no correction);
// a clock error between two PCs makes the player seek over and over (a seek storm that sounds like skipping); a seek
// that is still landing is judged as drift and sent again; a paused or unread player is moved; a seek in the last
// seconds jumps into the next song; the measured seek time is NaN, negative or one huge outlier and ruins the estimate;
// latency added to the wrong side puts the listener further behind instead of nearer.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { SYNC, expectedPosition, nextLatency, shouldCorrect } from '../src/lib/listenSync.ts'

const base = { source: 'call' as const, actual: 30_000, expected: 30_000, playing: true, sinceLastSeek: 60_000, endsIn: 120_000 }

test('the expected position runs from their start and adds the seek time', () => {
  assert.equal(expectedPosition(1_000, 31_000), 30_000)
  assert.equal(expectedPosition(1_000, 31_000, 250), 30_250)
  assert.equal(expectedPosition(5_000, 4_000), 0)
})

test('drift inside the tolerance is left alone, outside it is moved, either way', () => {
  assert.equal(shouldCorrect({ ...base, actual: 30_000 + SYNC.call.tolerance }), false)
  assert.equal(shouldCorrect({ ...base, actual: 30_000 - SYNC.call.tolerance - 1 }), true)
  assert.equal(shouldCorrect({ ...base, actual: 30_000 + SYNC.call.tolerance + 1 }), true)
})

test('a friend\'s Discord clock is trusted less than a call song\'s', () => {
  const behind = { ...base, actual: 30_000 - 3000 }
  assert.equal(shouldCorrect({ ...behind, source: 'call' }), true)
  assert.equal(shouldCorrect({ ...behind, source: 'presence' }), false)
  assert.equal(shouldCorrect({ ...base, source: 'presence', actual: 30_000 - 6000 }), true)
})

test('a seek is not repeated inside the cooldown', () => {
  const far = { ...base, actual: 10_000 }
  assert.equal(shouldCorrect({ ...far, sinceLastSeek: SYNC.call.cooldown - 1 }), false)
  assert.equal(shouldCorrect({ ...far, sinceLastSeek: SYNC.call.cooldown }), true)
  assert.equal(shouldCorrect({ ...far, source: 'presence', sinceLastSeek: SYNC.call.cooldown }), false)
})

test('a paused, unread or stopped player is never moved', () => {
  const far = { ...base, actual: 10_000 }
  assert.equal(shouldCorrect({ ...far, playing: false }), false)
  assert.equal(shouldCorrect({ ...far, actual: null }), false)
  assert.equal(shouldCorrect({ ...far, actual: Number.NaN }), false)
})

test('no seek in the last seconds of a song, and one of unknown length may', () => {
  const far = { ...base, actual: 10_000 }
  assert.equal(shouldCorrect({ ...far, endsIn: 2999 }), false)
  assert.equal(shouldCorrect({ ...far, endsIn: 3000 }), true)
  assert.equal(shouldCorrect({ ...far, endsIn: null }), true)
})

test('the seek time estimate starts at the first sample, moves a third toward each, and ignores junk', () => {
  assert.equal(nextLatency(null, 300), 300)
  assert.equal(nextLatency(300, 600), 400)
  assert.equal(nextLatency(300, 99_999), 700)
  assert.equal(nextLatency(300, Number.NaN), 300)
  assert.equal(nextLatency(300, -5), 300)
  assert.equal(nextLatency(null, Number.NaN), null)
})

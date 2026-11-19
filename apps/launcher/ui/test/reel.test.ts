// Which clips a highlight reel takes (src/lib/reel.ts). `npm test` (Node's own runner, which reads TypeScript as it is).
//
// How it could fail: a snip, a trim or an earlier reel joins a reel (a picture twice); a session reaches back to last week's clips; clips from two evenings are one reel; a
// single clip, or one starred clip, makes a reel of one; the starred highlights are ignored, or a lone star hides the rest of
// the session; a clip picked in the grid is dropped, or one not picked is taken.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { latestSession, reelOfGame, reelOfPicked, SESSION_GAP_MS } from '../src/lib/reel.ts'
import type { CaptureItem } from '../src/lib/capture.ts'

const HOUR = 3_600_000
const base = Date.parse('2026-10-28T20:00:00Z')
const clip = (id: string, hoursAfter: number, extra: Partial<CaptureItem> = {}): CaptureItem => ({
  id, kind: 'video', game: 'Hades', name: id, created: new Date(base + hoursAfter * HOUR).toISOString(), size: 1, favourite: false, url: '', ...extra,
})
const ids = (items: CaptureItem[]) => items.map(item => item.id)

test('a session is the newest clips with no gap longer than three hours between them', () => {
  const items = [clip('old', -30), clip('a', 0), clip('b', 1), clip('c', 3.9), clip('d', 6.5)]
  assert.equal(SESSION_GAP_MS, 3 * HOUR)
  assert.deepEqual(ids(latestSession(items)), ['d', 'c', 'b', 'a'])
})

test('two evenings are two sessions; the reel is the newer', () => {
  const items = [clip('mon1', 0), clip('mon2', 1), clip('tue1', 24), clip('tue2', 25)]
  assert.deepEqual(ids(reelOfGame(items)), ['tue2', 'tue1'])
})

test('a trim, an export or an earlier reel is left out of a session: its picture is in the original', () => {
  const items = [clip('a', 0), clip('a trim', 0.5, { derived: true }), clip('b', 1), clip('reel', 2, { derived: true })]
  assert.deepEqual(ids(latestSession(items)), ['b', 'a'])
  assert.deepEqual(ids(reelOfGame(items)), ['b', 'a'])
})

test('snips are left out of a session', () => {
  const items = [clip('a', 0), clip('snip', 0.5, { kind: 'image' }), clip('b', 1)]
  assert.deepEqual(ids(latestSession(items)), ['b', 'a'])
})

test('two or more starred clips of the session are the highlights; one star does not hide the rest', () => {
  const items = [clip('a', 0, { favourite: true }), clip('b', 1), clip('c', 2, { favourite: true }), clip('d', 3)]
  assert.deepEqual(ids(reelOfGame(items)), ['c', 'a'])
  const lone = [clip('a', 0, { favourite: true }), clip('b', 1), clip('c', 2)]
  assert.deepEqual(ids(reelOfGame(lone)), ['c', 'b', 'a'])
})

test('a star from an older session does not count for the newest', () => {
  const items = [clip('old1', -30, { favourite: true }), clip('old2', -29, { favourite: true }), clip('a', 0), clip('b', 1)]
  assert.deepEqual(ids(reelOfGame(items)), ['b', 'a'])
})

test('fewer than two clips is no reel', () => {
  assert.deepEqual(reelOfGame([]), [])
  assert.deepEqual(reelOfGame([clip('a', 0)]), [])
  assert.deepEqual(reelOfGame([clip('a', 0), clip('snip', 0.1, { kind: 'image' })]), [])
})

test('picked clips: the videos among them, none unpicked', () => {
  const items = [clip('a', 0), clip('snip', 1, { kind: 'image' }), clip('b', 2), clip('c', 3)]
  assert.deepEqual(ids(reelOfPicked(items, new Set(['a', 'snip', 'c']))), ['a', 'c'])
  assert.deepEqual(reelOfPicked(items, new Set()), [])
})

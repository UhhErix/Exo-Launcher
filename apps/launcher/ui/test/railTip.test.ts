import { test } from 'node:test'
import assert from 'node:assert/strict'
import { answerRailTip, emptyRailTip, endRailTip, railTipRooms, startRailTip, type RailTipBox } from '../src/lib/railTip.ts'

const box = (guildId: string | null, label = guildId ?? 'Home'): RailTipBox => ({ guildId, label, left: 84, top: 120 })
const rooms = (...names: string[]) => names.map(name => ({ name }))

test('hover, then click a server: no tip is left, and a late answer for it is dropped', () => {
  let s = emptyRailTip<{ name: string }>()
  s = startRailTip(s, box('A', 'Alpha'))
  const readA = s.ask
  s = endRailTip(s) // the click ends the tip; the pointer may still be over the icon
  assert.equal(s.box, null)
  s = answerRailTip(s, readA, 'A', rooms('Lobby'))
  assert.equal(s.box, null)
  assert.deepEqual(railTipRooms(s), [])
})

test('hover, click, then switch: the right server is shown and only its own answer lands', () => {
  let s = emptyRailTip<{ name: string }>()
  s = startRailTip(s, box('A', 'Alpha'))
  const readA = s.ask
  s = endRailTip(s) // clicked into A
  s = startRailTip(s, box('B', 'Beta')) // then the pointer rests on B
  const readB = s.ask
  // A's read answers late: dropped, B's tip shows none of A's rooms.
  s = answerRailTip(s, readA, 'A', rooms('Alpha lobby'))
  assert.deepEqual(railTipRooms(s), [])
  assert.equal(s.box?.guildId, 'B')
  // B's own answer lands and is shown for B only.
  s = answerRailTip(s, readB, 'B', rooms('Beta lobby'))
  assert.deepEqual(railTipRooms(s), [{ name: 'Beta lobby' }])
  assert.equal(s.box?.label, 'Beta')
})

test('an answer for the server the tip is on, from an older read, is dropped', () => {
  let s = emptyRailTip<{ name: string }>()
  s = startRailTip(s, box('A'))
  const first = s.ask
  s = endRailTip(s)
  s = startRailTip(s, box('A'))
  s = answerRailTip(s, first, 'A', rooms('stale'))
  assert.deepEqual(railTipRooms(s), [])
})

test('moving along the same server keeps its read, so its answer still lands', () => {
  let s = emptyRailTip<{ name: string }>()
  s = startRailTip(s, box('A'))
  const read = s.ask
  s = startRailTip(s, { ...box('A'), left: 90 })
  assert.equal(s.ask, read)
  s = answerRailTip(s, read, 'A', rooms('Lobby'))
  assert.deepEqual(railTipRooms(s), [{ name: 'Lobby' }])
})

test('a label-only tip (Home, Play, Add a server) has no voice rooms', () => {
  const s = startRailTip(emptyRailTip<{ name: string }>(), box(null, 'Home'))
  assert.equal(s.box?.label, 'Home')
  assert.deepEqual(railTipRooms(s), [])
})

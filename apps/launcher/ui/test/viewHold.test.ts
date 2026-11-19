// How long a music page read can stand in for a new read (src/components/music/viewHold.ts). `npm test`.
//
// How it could fail: live state (the queue, devices, the player's own state) is held, so it shows old values; a pick page
// (Home, Explore, Browse, Radio) reads again at once, so its shelves reorder under the pointer; a page holds past its window,
// so a playlist opened a minute later shows what it was; a page never holds at all, so every visit to it asks the service
// again (the 218 Apple Music view calls); a read from the future (a clock step) holds forever.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { PICK_HOLD_MS, PAGE_HOLD_MS, holdMs, standsIn } from '../src/components/music/viewHold.ts'

test('live state is never held: every read of it loads', () => {
  for (const view of ['player', 'queue', 'devices', 'friends', 'ratings'] as const) {
    assert.equal(holdMs(view), 0, view)
    assert.equal(standsIn(view, 0), false, view)
  }
})

test('a pick page holds for ten minutes, the rest of the pages for twenty seconds', () => {
  assert.equal(holdMs('home'), PICK_HOLD_MS)
  assert.equal(holdMs('radio'), PICK_HOLD_MS)
  assert.equal(holdMs('playlist'), PAGE_HOLD_MS)
  assert.equal(holdMs('album'), PAGE_HOLD_MS)
  assert.equal(PAGE_HOLD_MS, 20_000)
  assert.equal(PICK_HOLD_MS, 600_000)
})

test('a read stands in only inside its window, and never for a read from the future', () => {
  assert.equal(standsIn('playlist', 0), true)
  assert.equal(standsIn('playlist', PAGE_HOLD_MS - 1), true)
  assert.equal(standsIn('playlist', PAGE_HOLD_MS), false)
  assert.equal(standsIn('home', PICK_HOLD_MS - 1), true)
  assert.equal(standsIn('home', PICK_HOLD_MS), false)
  assert.equal(standsIn('playlist', -5), false)
})

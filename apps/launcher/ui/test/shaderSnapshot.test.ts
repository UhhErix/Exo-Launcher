// Which Exo Shaders state the board keeps when several arrive (src/components/shaders/shaderCall.ts).
//
// How it could fail: an older state replaces a newer one (the board goes back a revision); a starting engine's empty
// scan wipes the games the stopped engine left (the list blanks for a moment); a new engine's revs, counted from 1,
// lose to an old engine's higher ones (the board never updates after a restart).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { rebase, stale, type Snapshot } from '../src/components/shaders/snapshotOrder.ts'

const idle = { running: false, paused: false, canStart: false, waiting: [] }
const snap = (rev: number, games: Snapshot['games'] = [], app: Snapshot['app'] = {}): Snapshot =>
  ({ rev, games, queue: idle, app }) as Snapshot
const game = { id: 'g' } as Snapshot['games'][number]

test('a state older than the one on the board is stale; the same or newer is not', () => {
  assert.equal(stale(snap(5), snap(3)), true)
  assert.equal(stale(snap(5), snap(5)), false)
  assert.equal(stale(snap(5), snap(6)), false)
})

test('with nothing on the board yet, any state is taken', () => {
  assert.equal(stale(null, snap(1)), false)
})

test('an exited engine\'s rev-0 games stay against a starting engine that has listed nothing yet', () => {
  const kept = snap(0, [game])
  assert.equal(stale(kept, snap(1, [], { scanning: true })), true)
  // Once the new engine lists games, its state replaces the old list.
  assert.equal(stale(kept, snap(1, [game], { scanning: true })), false)
  // A scan with no games on an empty board is taken as it comes.
  assert.equal(stale(snap(0, []), snap(1, [], { scanning: true })), false)
})

test('rebase drops a board\'s rev to 0 so a new engine\'s revs (counted from 1) win; a rev-0 or empty board is untouched', () => {
  assert.equal(rebase(snap(7, [game])).rev, 0)
  assert.equal(rebase(snap(7, [game])).games.length, 1)
  assert.equal(rebase(snap(0, [game])).rev, 0)
  assert.equal(rebase(null), null)
})

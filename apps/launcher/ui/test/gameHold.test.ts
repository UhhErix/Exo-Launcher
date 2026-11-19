import { test } from 'node:test'
import assert from 'node:assert/strict'
import { pressOutcome, withHold, type GameHold, type Press } from '../src/lib/gameHold.ts'

// How it could fail: one game launching blocks Play and Stop for every other game, silently.
test('every row of the press table: free games go, held games say why', () => {
  const rows: Array<[GameHold | undefined, Press, string | null]> = [
    [undefined, 'play', null],
    [undefined, 'stop', null],
    ['starting', 'play', 'Portal is already starting.'],
    ['starting', 'stop', 'Portal is still starting.'],
    ['closing', 'play', 'Portal is closing.'],
    ['closing', 'stop', 'Portal is already closing.'],
  ]
  for (const [held, press, say] of rows) {
    const outcome = pressOutcome(held, press, 'Portal')
    if (say === null) assert.deepEqual(outcome, { go: true })
    else assert.deepEqual(outcome, { go: false, say })
  }
})

test('holding one game leaves every other game free', () => {
  const holds = withHold(new Map(), 'steam:1', 'starting')
  assert.equal(pressOutcome(holds.get('steam:1'), 'play', 'A').go, false)
  assert.equal(pressOutcome(holds.get('steam:2'), 'play', 'B').go, true)
  assert.equal(pressOutcome(holds.get('steam:2'), 'stop', 'B').go, true)
  const released = withHold(holds, 'steam:1', null)
  assert.equal(released.size, 0)
  assert.equal(withHold(released, 'steam:1', null), released, 'nothing changed, the same map')
})

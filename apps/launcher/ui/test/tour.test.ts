import assert from 'node:assert/strict'
import test from 'node:test'
import { isTourDue, placeTourCard, TOUR_STEPS, TOUR_VERSION } from '../src/lib/tour.ts'

test('TOUR_STEPS covers core rooms with unique ids and valid copy', () => {
  assert.ok(TOUR_STEPS.length >= 7)
  const ids = new Set(TOUR_STEPS.map((s) => s.id))
  assert.equal(ids.size, TOUR_STEPS.length)
  for (const step of TOUR_STEPS) {
    assert.ok(step.title.trim().length > 0)
    assert.ok(step.body.trim().length > 0)
  }
  assert.equal(TOUR_STEPS[0].selector, null)
})

test('isTourDue opens when unseen or forced via ?tour and stays off once seen', () => {
  assert.equal(isTourDue(undefined), true)
  assert.equal(isTourDue(0), true)
  assert.equal(isTourDue(TOUR_VERSION), false)
  assert.equal(isTourDue(TOUR_VERSION, '?signedin&tour'), true)
  assert.equal(isTourDue(TOUR_VERSION, '?signedin'), false)
})

test('placeTourCard centers when spot is null and flips cleanly inside viewport bounds', () => {
  const centered = placeTourCard(null, { width: 400, height: 240 }, { width: 1440, height: 900 })
  assert.equal(centered.centered, true)
  assert.equal(centered.left, 520)
  assert.equal(centered.top, 330)

  const belowTopBar = placeTourCard(
    { left: 600, top: 8, width: 110, height: 36 },
    { width: 400, height: 240 },
    { width: 1440, height: 900 },
  )
  assert.equal(belowTopBar.centered, false)
  assert.equal(belowTopBar.top, 60)
  assert.ok(belowTopBar.left >= 20 && belowTopBar.left + 400 <= 1420)

  const aboveBottom = placeTourCard(
    { left: 100, top: 760, width: 300, height: 80 },
    { width: 400, height: 240 },
    { width: 1440, height: 900 },
  )
  assert.equal(aboveBottom.centered, false)
  assert.equal(aboveBottom.top, 504)
})

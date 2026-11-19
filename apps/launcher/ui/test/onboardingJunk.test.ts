// When setup's junk scan runs and when Continue waits for it (src/components/onboarding/model.ts). `npm test`.
//
// How it could fail: the scan runs on mount (a PC that never reaches Clean up pays 4 to 13 s for it); it runs again on each
// step; it never runs at all (Clean up is then never offered for junk); Continue from Discord or Captures goes on before the
// scan answers, so Clean up is skipped; Continue waits on a step that cannot lead into Clean up.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { STEPS, junkScanDue, waitsForJunkScan, type StepId } from '../src/components/onboarding/model.ts'

const ids = STEPS.map(step => step.id) as StepId[]

test('the junk scan does not run while setup is on its first steps', () => {
  for (const step of ['welcome', 'stores', 'apps'] as const) assert.equal(junkScanDue(step, false), false, step)
})

test('the junk scan starts once, when setup reaches Discord or any step after it', () => {
  for (const step of ['discord', 'capture', 'cleanup', 'pc'] as const) {
    assert.equal(junkScanDue(step, false), true, step)
    assert.equal(junkScanDue(step, true), false, `${step} after it started`)
  }
})

test('Continue waits only on the step that leads straight into Clean up', () => {
  // Captures shown: Captures waits, Discord does not (its Continue goes to Captures, which is always there).
  assert.deepEqual(ids.filter(step => waitsForJunkScan(step, true)), ['capture'])
  // Captures left out: Discord's Continue goes straight to Clean up, so Discord waits instead.
  assert.equal(waitsForJunkScan('discord', false), true)
  assert.equal(waitsForJunkScan('discord', true), false)
})

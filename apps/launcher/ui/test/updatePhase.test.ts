// updatePhase: the update the page draws, from the host's state alone. Ways it could fail: a host state with no phase (the
// sheet shows a stale one); an update kept for after a game drawn as installing (a spinner for an hour), or an install's
// short Ready step drawn as kept (a notice that flashes); a check that could not answer (offline) drawn as a failed update;
// the update sheet left up after a check says Exo is up to date; a state the host sends that the page refuses.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isUpdateState, sheetAfterCheck, updatePhase, type UpdateState } from '../src/lib/updatePhase.ts'

const rows: [UpdateState, boolean, boolean, string][] = [
  ['idle', false, false, 'idle'],
  ['idle', true, false, 'available'],
  ['checking', false, false, 'checking'],
  ['downloading', true, true, 'installing'],
  ['waitingForGame', true, false, 'waiting'],
  ['waitingForGame', true, true, 'waiting'],
  ['ready', true, false, 'waiting'],
  ['ready', true, true, 'installing'],
  ['applying', true, true, 'installing'],
  ['restarting', true, true, 'restarting'],
  ['failed', true, false, 'failed'],
]

test('every host state has the phase the page draws', () => {
  for (const [state, offered, asking, phase] of rows)
    assert.equal(updatePhase(state, offered, asking), phase, `${state} offered=${offered} asking=${asking}`)
})

test('a start-up check that could not answer shows nothing', () => {
  // Offline, a captive portal or the address down: the host goes back to idle (UpdateFlow) and nothing is offered.
  const answer = { ok: false, updateAvailable: false, state: 'idle' }
  assert.equal(isUpdateState(answer.state), true)
  assert.equal(updatePhase(answer.state as UpdateState, false, false), 'idle')
})

test('a check that says up to date, or cannot answer, closes the update sheet; one that offers keeps it', () => {
  assert.equal(sheetAfterCheck(true, { ok: true, updateAvailable: false }), false)
  assert.equal(sheetAfterCheck(true, { ok: false }), false)
  assert.equal(sheetAfterCheck(true, { ok: true, updateAvailable: true }), true)
  assert.equal(sheetAfterCheck(false, { ok: true, updateAvailable: true }), false)
})

test('each of the host flow states is covered and accepted', () => {
  const host = ['idle', 'checking', 'downloading', 'waitingForGame', 'ready', 'applying', 'restarting', 'failed']
  for (const state of host) {
    assert.equal(isUpdateState(state), true, state)
    assert.ok(rows.some(row => row[0] === state), `${state} has no row`)
  }
  assert.equal(isUpdateState('dismissed'), false)
  assert.equal(isUpdateState(undefined), false)
})

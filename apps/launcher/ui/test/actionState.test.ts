// actionState: one press's life in an action button (idle, pending, success, error). Ways it could fail: a second press
// while it works starts the work twice; an answer to an older press (or one that came after the wait gave up) moves a
// newer one; a check settles after a newer press; an error never clears; a host refusal ({ ok: false }) or `false`
// reads as done; a wait that never answers leaves the control spinning forever.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { actionReducer, idleAction, refusal, settled, TIMED_OUT, type ActionState } from '../src/lib/actionState.ts'
import { plainError } from '../src/lib/errorWords.ts'

const error = plainError('Steam is not running.')

test('a press runs once: pending, then success, then rest after the hold', () => {
  let state: ActionState = actionReducer(idleAction, { type: 'start' })
  assert.equal(state.phase, 'pending')
  assert.equal(state.run, 1)
  assert.equal(actionReducer(state, { type: 'start' }), state, 'a press while it works does nothing')
  state = actionReducer(state, { type: 'resolve', run: 1 })
  assert.equal(state.phase, 'success')
  state = actionReducer(state, { type: 'settle', run: 1 })
  assert.equal(state.phase, 'idle')
})

test('a failure shows its reason, and the next press is Retry with the reason gone', () => {
  let state = actionReducer(actionReducer(idleAction, { type: 'start' }), { type: 'reject', run: 1, error })
  assert.equal(state.phase, 'error')
  assert.equal(state.error?.text, 'Steam is not running.')
  state = actionReducer(state, { type: 'start' })
  assert.equal(state.phase, 'pending')
  assert.equal(state.run, 2)
  assert.equal(state.error, null)
  assert.deepEqual(actionReducer(actionReducer(state, { type: 'reject', run: 2, error }), { type: 'reset' }), { phase: 'idle', run: 2, error: null })
})

test('an answer that belongs to an older press is dropped', () => {
  let state = actionReducer(idleAction, { type: 'start' })
  state = actionReducer(state, { type: 'reject', run: 1, error })
  state = actionReducer(state, { type: 'start' })
  assert.equal(actionReducer(state, { type: 'resolve', run: 1 }), state)
  assert.equal(actionReducer(state, { type: 'reject', run: 1, error }), state)
  const held = actionReducer(state, { type: 'resolve', run: 2 })
  const again = actionReducer(held, { type: 'start' })
  assert.equal(again.phase, 'pending', 'a press on a held check goes again at once')
  assert.equal(actionReducer(again, { type: 'settle', run: 2 }), again, 'the old check’s settle does not end the new run')
})

test('a refusal is a failure, whatever shape it comes in', () => {
  assert.deepEqual(refusal({ ok: false, message: 'No.' }), { message: 'No.' })
  assert.deepEqual(refusal({ ok: false, error: 'Nope' }), { message: 'Nope' })
  assert.deepEqual(refusal({ ok: false }), { message: null })
  assert.deepEqual(refusal(false), { message: null })
  for (const yes of [undefined, null, true, 0, '', { ok: true }, { message: 'done' }, []]) assert.equal(refusal(yes), null, JSON.stringify(yes))
})

test('settled gives the value, the plain reason, or says it took too long', async () => {
  assert.deepEqual(await settled(async () => 7), { ok: true, value: 7 })
  const refused = await settled(async () => ({ ok: false, message: 'Something went wrong' }), { fallback: 'Exo couldn’t save it.' })
  assert.equal(refused.ok, false)
  if (!refused.ok) assert.equal(refused.error.text, 'Exo couldn’t save it.')
  const thrown = await settled(() => { throw new Error('ENOSPC: no space left on device') })
  if (thrown.ok) assert.fail('a throw is a failure')
  else assert.match(thrown.error.text, /drive is full/)
  const started = Date.now()
  const slow = await settled(() => new Promise(() => undefined), { timeoutMs: 20 })
  assert.ok(Date.now() - started < 1000)
  if (slow.ok) assert.fail('a wait that never answers fails')
  else assert.equal(slow.error.text, TIMED_OUT)
  assert.deepEqual(await settled(() => new Promise(resolve => setTimeout(() => resolve('late'), 30)), { timeoutMs: null }), { ok: true, value: 'late' })
})

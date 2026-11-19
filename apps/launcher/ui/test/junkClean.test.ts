// The junk clean outcome (src/lib/junkClean.ts). `npm test`.
//
// How it could fail: a partial clean loses the holder line; nothing deleted reads as "Deleted"; a failed call loses its reason.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { cleanOutcome } from '../src/lib/junkClean.ts'

const size = (bytes: number) => (bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.round(bytes / 1e6)} MB`)

test('a full clean says what it deleted and nothing else', () => {
  assert.deepEqual(cleanOutcome({ ok: true, files: 40, bytes: 1.2e9, message: null }, size), { done: 'Deleted 1.2 GB', error: undefined })
})

test('a partial clean says what it deleted and keeps the holder line', () => {
  const outcome = cleanOutcome({ ok: true, files: 40, bytes: 1.2e9, message: '3 files kept. Those files are in use by X.' }, size)
  assert.equal(outcome.done, 'Deleted 1.2 GB')
  assert.equal(outcome.error, '3 files kept. Those files are in use by X.')
})

test('a clean that deleted nothing reports why', () => {
  assert.deepEqual(cleanOutcome({ ok: true, files: 0, bytes: 0, message: '2 files kept. Those files are in use by X.' }, size),
    { error: '2 files kept. Those files are in use by X.' })
  assert.deepEqual(cleanOutcome({ ok: true, files: 0, bytes: 0 }, size), { error: 'Nothing could be deleted: those files are in use.' })
})

test('a failed call keeps its own message', () => {
  assert.deepEqual(cleanOutcome({ ok: false, files: 0, bytes: 0, message: 'Exo is busy.' }, size), { error: 'Exo is busy.' })
  assert.deepEqual(cleanOutcome({ ok: false, files: 0, bytes: 0 }, size), { error: 'Nothing was deleted. Try again.' })
})

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { libraryAnswerParts } from '../src/lib/libraryAnswer.ts'

const answer = {
  games: [{ id: 'steam:1' }],
  stores: [{ store: 'steam' }],
  queuedGameIds: ['steam:2'],
  progress: { isActive: true, gameId: 'steam:2' },
  recent: ['steam:1'],
} as never

// How it could fail: a library push lands between the request and its answer and the answer is dropped whole, so the queue,
// the active download and the store matrix stay unset on a cold start.
test('a push after the request drops only the game list', () => {
  const parts = libraryAnswerParts(answer, true)
  assert.equal(parts.games, null)
  assert.deepEqual(parts.queuedGameIds, ['steam:2'])
  assert.deepEqual(parts.recent, ['steam:1'])
  assert.equal(parts.stores?.length, 1)
  assert.equal(parts.progress?.gameId, 'steam:2')
})

test('with no push in between everything is taken, and an idle progress or empty stores are not', () => {
  const parts = libraryAnswerParts(answer, false)
  assert.equal(parts.games?.length, 1)
  const quiet = libraryAnswerParts({ games: [], stores: [], progress: { isActive: false } } as never, false)
  assert.equal(quiet.stores, null)
  assert.equal(quiet.progress, null)
  assert.equal(quiet.queuedGameIds, null)
})

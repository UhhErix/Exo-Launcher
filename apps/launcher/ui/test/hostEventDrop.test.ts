import { test } from 'node:test'
import assert from 'node:assert/strict'
import { droppedEventLog, validHostEvent } from '../src/lib/hostContract.ts'

// How it could fail: an event that does not match the contract vanishes without a trace, or floods the log, or the log
// carries what the event held.
test('an invalid event is told once per name and never with its contents', () => {
  const said: string[] = []
  const drop = droppedEventLog(message => said.push(message))
  assert.equal(validHostEvent('install.progress', { secret: 'C:\\Users\\x' }), false)
  drop('install.progress')
  drop('install.progress')
  drop('library.updated')
  assert.equal(said.length, 2)
  assert.match(said[0], /install\.progress/)
  assert.doesNotMatch(said.join(' '), /secret|Users/)
})

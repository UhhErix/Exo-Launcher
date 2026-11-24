import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isUserNotice } from '../src/lib/userNotice.ts'

test('classifies expected running-game and call guard notices', () => {
  assert.equal(isUserNotice('Close your game first.'), true)
  assert.equal(isUserNotice('Close your game first'), true)
  assert.equal(isUserNotice('Close your game first. Exo fixes the clients once nothing is playing.'), true)
  assert.equal(isUserNotice('Close Call of Duty first.'), true)
  assert.equal(isUserNotice('A game is running, so the update waits.'), true)
  assert.equal(isUserNotice('A game is running, so the update waits'), true)
  assert.equal(isUserNotice('A Discord call is running, so the update waits.'), true)
  assert.equal(isUserNotice('Leave your Discord call first.'), true)
})

test('does not hide real errors', () => {
  assert.equal(isUserNotice("The chipset driver didn't update."), false)
  assert.equal(isUserNotice('Manifest verification failed.'), false)
  assert.equal(isUserNotice(''), false)
  assert.equal(isUserNotice(null), false)
  assert.equal(isUserNotice(undefined), false)
})

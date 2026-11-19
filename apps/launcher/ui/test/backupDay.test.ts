import { test } from 'node:test'
import assert from 'node:assert/strict'
import { backupDayLabel } from '../src/lib/backupDay.ts'

test('a backup day reads as a short date in words', () => {
  assert.equal(backupDayLabel('2026-11-08'), 'Nov 8, 2026')
})

test('a day that is not yyyy-MM-dd is shown as it came, never as Invalid Date', () => {
  assert.equal(backupDayLabel('today'), 'today')
})

// How it could fail: a backup taken today reads as a date (or yesterday's), not "Today".
test('the day a backup was taken reads as Today, and only that day', () => {
  const now = new Date('2026-11-09T23:30:00Z')
  assert.equal(backupDayLabel('2026-11-09', now), 'Today')
  assert.equal(backupDayLabel('2026-11-08', now), 'Nov 8, 2026')
})

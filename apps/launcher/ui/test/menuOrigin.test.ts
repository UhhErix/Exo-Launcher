// Which corner a popup grows from (src/lib/menuOrigin.ts). `npm test` (Node's own runner, which reads TypeScript as it is).
//
// How it could fail: a menu hanging below its trigger grows from the top (a bottom origin makes it rise out of the trigger);
// a menu right-aligned to its trigger grows from the right; a menu above the trigger grows from the bottom; a menu at the
// trigger's bottom edge counts as below it (no flicker between sides at a pixel's difference).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { menuOrigin } from '../src/lib/menuOrigin.ts'

const trigger = { left: 100, right: 180, top: 50, bottom: 80 }

test('a popup below its trigger, aligned to the left edge, grows from top-left', () => {
  assert.equal(menuOrigin({ x: 100, y: 84 }, trigger), 'top-left')
})

test('a popup below its trigger, aligned to the right edge, grows from top-right', () => {
  assert.equal(menuOrigin({ x: 20, y: 84 }, trigger), 'top-right')
})

test('a popup above its trigger grows from the bottom', () => {
  assert.equal(menuOrigin({ x: 100, y: 10 }, trigger), 'bottom-left')
  assert.equal(menuOrigin({ x: 20, y: 10 }, trigger), 'bottom-right')
})

// A submenu hangs from its row: the row's top is where it starts, inside the row's own span (not below the row).
test('a submenu that hangs from a row starts at the row top and grows from top', () => {
  const row = { left: 200, right: 300, top: 110, bottom: 130 }
  assert.equal(menuOrigin({ x: 300, y: 110 }, row), 'top-left')
})

test('a popup at the trigger edge counts as below it (one pixel of slack)', () => {
  assert.equal(menuOrigin({ x: 101, y: 79.5 }, trigger), 'top-left')
})

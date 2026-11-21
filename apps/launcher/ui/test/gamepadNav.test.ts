import test from 'node:test'
import assert from 'node:assert/strict'
import {
  detectGamepadKind,
  gamepadLabels,
  pickSpatialTarget,
  scoreDirectionCandidate,
  stepSliderValue,
  type SpatialRect,
} from '../src/lib/gamepadMath.ts'

const box = (left: number, top: number, width: number, height: number): SpatialRect => ({
  left,
  top,
  right: left + width,
  bottom: top + height,
  width,
  height,
})

test('detectGamepadKind identifies PlayStation, Nintendo, and Xbox controllers', () => {
  assert.equal(detectGamepadKind('DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)'), 'playstation')
  assert.equal(detectGamepadKind('Sony Interactive Entertainment Wireless Controller'), 'playstation')
  assert.equal(detectGamepadKind('Pro Controller (Vendor: 057e Product: 2009)'), 'nintendo')
  assert.equal(detectGamepadKind('Xbox 360 Controller (XInput STANDARD GAMEPAD)'), 'xbox')
  assert.equal(detectGamepadKind('Generic USB Gamepad'), 'xbox')
})

test('gamepadLabels returns native button legends for each family', () => {
  assert.equal(gamepadLabels('playstation').confirm, '✕')
  assert.equal(gamepadLabels('playstation').action, '◻')
  assert.equal(gamepadLabels('playstation').prevRoom, 'L1')
  assert.equal(gamepadLabels('nintendo').prevTab, 'ZL')
  assert.equal(gamepadLabels('xbox').confirm, 'A')
  assert.equal(gamepadLabels('xbox').nextRoom, 'RB')
})

test('pickSpatialTarget prefers aligned items on the same row or column', () => {
  const origin = box(100, 100, 160, 240)
  const candidates = [
    { item: 'right-same-row', rect: box(280, 100, 160, 240) },
    { item: 'right-farther', rect: box(460, 100, 160, 240) },
    { item: 'right-off-row', rect: box(270, 380, 160, 240) },
    { item: 'above', rect: box(100, 20, 120, 40) },
    { item: 'below', rect: box(110, 380, 160, 240) },
  ]
  assert.equal(pickSpatialTarget(origin, candidates, 'right'), 'right-same-row')
  assert.equal(pickSpatialTarget(origin, candidates, 'up'), 'above')
  assert.equal(pickSpatialTarget(origin, candidates, 'down'), 'below')
  assert.equal(pickSpatialTarget(origin, candidates, 'left'), null)
})

test('scoreDirectionCandidate rejects opposite-direction candidates', () => {
  const origin = box(300, 200, 100, 40)
  const leftOf = box(120, 200, 100, 40)
  assert.equal(scoreDirectionCandidate(origin, leftOf, 'right'), null)
  assert.notEqual(scoreDirectionCandidate(origin, leftOf, 'left'), null)
})

test('stepSliderValue increments cleanly and clamps within bounds', () => {
  assert.equal(stepSliderValue(0.1, 4.0, 0.1, 0.4, 1), 0.5)
  assert.equal(stepSliderValue(0.1, 4.0, 0.1, 0.1, -1), 0.1)
  assert.equal(stepSliderValue(0.1, 4.0, 0.1, 4.0, 1), 4.0)
  assert.equal(stepSliderValue(0, 100, null, 50, 1), 55)
})

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { allowHover, resetHoverGate, suppressHover } from '../src/lib/hoverGate.ts'

test('hover is allowed at rest and suppressed cleanly across room transitions until grace expires', () => {
  resetHoverGate()
  assert.equal(allowHover(undefined, 1000), true)

  suppressHover(450, 1000)
  assert.equal(allowHover(undefined, 1100), false)
  assert.equal(allowHover({ pointerType: 'mouse', clientX: 500, clientY: 20 }, 1200), false)
  assert.equal(allowHover(undefined, 1449), false)
  assert.equal(allowHover(undefined, 1450), true)
})

test('touch events never activate desktop hover previews', () => {
  resetHoverGate()
  assert.equal(allowHover({ pointerType: 'touch' }, 5000), false)
})

test('real pointer movement past jitter threshold clears suppression immediately', () => {
  resetHoverGate()
  const listeners = new Map<string, Array<(e: { clientX: number; clientY: number }) => void>>()
  const prevWindow = (globalThis as { window?: unknown }).window
  ;(globalThis as { window: unknown }).window = {
    addEventListener(type: string, fn: (e: { clientX: number; clientY: number }) => void) {
      const list = listeners.get(type) ?? []
      list.push(fn)
      listeners.set(type, list)
    },
  }
  try {
    // Seed initial pointer location at click (500, 20), then arm transition gate.
    suppressHover(450, 2000)
    for (const fn of listeners.get('pointerdown') ?? []) fn({ clientX: 500, clientY: 20 })

    // Sub-threshold stationary synthetic pointerenter stays blocked during grace window.
    assert.equal(allowHover({ pointerType: 'mouse', clientX: 500, clientY: 20 }, 2100), false)
    assert.equal(allowHover({ pointerType: 'mouse', clientX: 501, clientY: 20 }, 2100), false)

    // Genuine cursor move (>= 2px) unlocks immediately even within grace window.
    for (const fn of listeners.get('pointermove') ?? []) fn({ clientX: 512, clientY: 36 })
    assert.equal(allowHover({ pointerType: 'mouse', clientX: 512, clientY: 36 }, 2100), true)
  } finally {
    if (prevWindow === undefined) delete (globalThis as { window?: unknown }).window
    else (globalThis as { window: unknown }).window = prevWindow
  }
})

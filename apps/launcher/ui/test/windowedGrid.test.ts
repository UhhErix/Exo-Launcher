import { test } from 'node:test'
import assert from 'node:assert/strict'
import { calculateGridWindow, resolveGridCardLayout, resolveWindowedGridInput } from '../src/lib/gridWindow.ts'

const sampleGeometry = {
  containerWidth: 1200,
  viewportHeight: 800,
  cardWidth: 180,
  cardHeight: 270,
  columnGap: 10,
  rowGap: 14,
  paddingInline: 10,
  paddingTop: 12,
  paddingBottom: 24,
  offset: 360,
}

test('ignores zero-dimension measurements when the library room is hidden or collapsed', () => {
  assert.equal(resolveWindowedGridInput({ ...sampleGeometry, containerWidth: 0 }, 80, 0), null)
  assert.equal(resolveWindowedGridInput({ ...sampleGeometry, viewportHeight: 0 }, 80, 0), null)
  assert.equal(resolveWindowedGridInput({ ...sampleGeometry, containerWidth: -10 }, 80, 0), null)
})

test('clamps negative viewportStart when scrollTop is above grid offset', () => {
  const above = resolveWindowedGridInput(sampleGeometry, 80, 120)
  assert.ok(above)
  assert.equal(above.viewportStart, 0)

  const scrolled = resolveWindowedGridInput(sampleGeometry, 80, 1200)
  assert.ok(scrolled)
  assert.equal(scrolled.viewportStart, 840)
})

test('switching from a short hidden shelf back to full library keeps visible cards rendered across scroll', () => {
  const hiddenInput = resolveWindowedGridInput({ ...sampleGeometry, offset: 80 }, 2, 0)!
  const hiddenWin = calculateGridWindow(hiddenInput)
  assert.equal(hiddenWin.startIndex, 0)
  assert.equal(hiddenWin.endIndex, 2)

  // Return to All games (reset scrollTop=0 with updated offset=360, then scroll down to 1600):
  const topInput = resolveWindowedGridInput(sampleGeometry, 120, 0)!
  const topWin = calculateGridWindow(topInput)
  assert.equal(topWin.startIndex, 0)
  assert.ok(topWin.endIndex > 0)

  const midInput = resolveWindowedGridInput(sampleGeometry, 120, 1600)!
  const midWin = calculateGridWindow(midInput)
  assert.ok(midWin.startIndex > 0 && midWin.endIndex > midWin.startIndex)
  assert.ok(midWin.renderedHeight > 0)

  const layout = resolveGridCardLayout(sampleGeometry.containerWidth)
  assert.ok(layout.columnCount >= 1 && layout.cardWidth > 100)
})

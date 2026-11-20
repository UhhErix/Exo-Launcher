import { describe, it } from 'node:test'
import assert from 'node:assert/strict'
import { computeGlideGeometry, glideTransform } from '../src/lib/glide.ts'

describe('glide composite geometry & transforms', () => {
  it('computes unscaled local coordinates for the selected item', () => {
    const geometry = computeGlideGeometry({
      rect: { left: 220, top: 64, width: 96, height: 28 },
      origin: { left: 100, top: 50, width: 400, height: 32 },
      offsetWidth: 400,
      clientLeft: 0,
      clientTop: 0,
      scrollLeft: 0,
      scrollTop: 0,
      itemOffsetWidth: 96,
      itemOffsetHeight: 28,
    })
    assert.deepEqual(geometry, { x: 120, y: 14, w: 96, h: 28 })
  })

  it('compensates for parent panel scale during transitions and scroll offsets', () => {
    const geometry = computeGlideGeometry({
      rect: { left: 196, top: 60, width: 48, height: 24 },
      origin: { left: 100, top: 50, width: 200, height: 16 },
      offsetWidth: 400,
      clientLeft: 2,
      clientTop: 1,
      scrollLeft: 10,
      scrollTop: 4,
      itemOffsetWidth: 96,
      itemOffsetHeight: 28,
    })
    // scale = 200 / 400 = 0.5 -> (196 - 100) / 0.5 - 2 + 10 = 200; (60 - 50) / 0.5 - 1 + 4 = 23
    assert.deepEqual(geometry, { x: 200, y: 23, w: 96, h: 28 })
  })

  it('returns null when the selected item is hidden or zero-sized', () => {
    assert.equal(
      computeGlideGeometry({
        rect: { left: 0, top: 0, width: 0, height: 0 },
        origin: { left: 0, top: 0, width: 200, height: 32 },
        offsetWidth: 200,
        clientLeft: 0,
        clientTop: 0,
        scrollLeft: 0,
        scrollTop: 0,
        itemOffsetWidth: 0,
        itemOffsetHeight: 28,
      }),
      null,
    )
  })

  it('builds composite-only translate3d + scaleX/scaleY FLIP transforms without layout properties', () => {
    const target = { x: 140, y: 2, w: 100, h: 28 }
    assert.equal(glideTransform(target), 'translate3d(140px, 2px, 0) scaleX(1) scaleY(1)')

    const from = { x: 20, y: 2, w: 50, h: 14 }
    assert.equal(glideTransform(target, from), 'translate3d(20px, 2px, 0) scaleX(0.5) scaleY(0.5)')
  })
})

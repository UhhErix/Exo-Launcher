import assert from 'node:assert/strict'
import { test } from 'node:test'
import { hairlineWidth } from '../src/lib/hairline.ts'

const near = (a: number, b: number) => assert.ok(Math.abs(a - b) < 1e-6, `${a} is not ${b}`)

test('the line is one device pixel below 200%', () => {
  assert.equal(hairlineWidth(1), 1)
  near(hairlineWidth(1.25), 0.8)
  near(hairlineWidth(1.5), 2 / 3)
  near(hairlineWidth(1.75), 4 / 7)
})

test('the line is whole device pixels from 200%', () => {
  assert.equal(hairlineWidth(2), 1)
  near(hairlineWidth(2.5), 0.8)
  assert.equal(hairlineWidth(3), 1)
})

test('zoomed out, the line never goes under one device pixel', () => {
  near(hairlineWidth(0.8), 1.25)
  assert.equal(hairlineWidth(0.5), 2)
})

test('a bad ratio gives 1px', () => {
  assert.equal(hairlineWidth(0), 1)
  assert.equal(hairlineWidth(Number.NaN), 1)
})

import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { formatCauseLabel, formatPlacement, formatUs } from '../src/diagnose-format.ts'

describe('Diagnose formatting helpers', () => {
  it('formats microsecond durations across units', () => {
    assert.equal(formatUs(42), '42 µs')
    assert.equal(formatUs(640), '640 µs')
    assert.equal(formatUs(1850), '1.9 ms')
    assert.equal(formatUs(1_250_000), '1.25 s')
  })

  it('maps diagnostic causes to clear labels', () => {
    assert.equal(formatCauseLabel('steady'), 'Steady delivery')
    assert.equal(formatCauseLabel('pc_busy'), 'PC busy (DPC/ISR)')
    assert.equal(formatCauseLabel('usb_uneven'), 'USB or mouse uneven')
    assert.equal(formatCauseLabel('both'), 'PC busy and USB uneven')
    assert.equal(formatCauseLabel('insufficient'), 'Need more movement')
  })

  it('maps controller placement values', () => {
    assert.equal(formatPlacement('cpu'), 'CPU direct')
    assert.equal(formatPlacement('chipset'), 'Chipset')
    assert.equal(formatPlacement('addin'), 'Add-in')
    assert.equal(formatPlacement('dock'), 'Dock')
    assert.equal(formatPlacement('unknown'), 'Unknown')
  })
})

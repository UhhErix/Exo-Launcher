import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  buildKeyboardLayout,
  resolveFormFactor,
  resolveLayoutCode,
  type KeyCap,
  type LayoutCode,
} from '../src/components/devices/keyboardLayouts.ts'
import type { KeyboardFormFactor } from '../src/lib/host.ts'

function rowUnits(row: KeyCap[]): number {
  return Math.round(row.reduce((sum, key) => sum + key.u, 0) * 100) / 100
}

test('every Wooting form factor and physical layout keeps exact unit geometry and unique key ids', () => {
  const forms: KeyboardFormFactor[] = ['60', '80', 'tkl', 'full']
  const layouts: LayoutCode[] = ['ansi', 'iso', 'jis', 'ansi-split', 'iso-split']

  for (const form of forms) {
    for (const layout of layouts) {
      const spec = buildKeyboardLayout(form, layout)
      assert.equal(spec.form, form)
      assert.equal(spec.layoutCode, layout)
      assert.equal(spec.mainRows.length, form === '60' ? 5 : 6)

      for (const row of spec.mainRows) {
        assert.equal(rowUnits(row), 15, `${form}/${layout} main row width`)
      }
      for (const row of spec.navRows) {
        assert.equal(rowUnits(row), 3, `${form}/${layout} nav row width`)
      }
      for (const row of spec.numRows) {
        assert.equal(rowUnits(row), 4, `${form}/${layout} num row width`)
      }

      const ids = [...spec.mainRows.flat(), ...spec.navRows.flat(), ...spec.numRows.flat()].map(k => k.id)
      assert.equal(new Set(ids).size, ids.length, `${form}/${layout} duplicate key id`)
    }
  }
})

test('60HE v2 split spacebar renders 2.75u left, 1.25u center Fn, and 2.25u right spacebar', () => {
  for (const layout of ['ansi-split', 'iso-split'] as const) {
    const spec = buildKeyboardLayout('60', layout)
    const bottom = spec.mainRows[spec.mainRows.length - 1]
    const left = bottom.find(k => k.id === 'space-l')
    const mid = bottom.find(k => k.id === 'fn-mid')
    const right = bottom.find(k => k.id === 'space-r')
    assert.ok(left && mid && right)
    assert.equal(left.u, 2.75)
    assert.equal(mid.u, 1.25)
    assert.equal(right.u, 2.25)
    assert.equal(left.u + mid.u + right.u, 6.25)
    assert.equal(bottom.find(k => k.id === 'space'), undefined)
  }
})

test('80HE includes 10-segment LED bar and Mode key, while TKL omits LED bar and Full includes A1-A3', () => {
  const w80 = buildKeyboardLayout('80', 'ansi')
  assert.equal(w80.hasLedBar, true)
  assert.ok(w80.navRows.flat().some(k => k.id === 'led-bar' && k.kind === 'led-bar'))
  assert.ok(w80.navRows.flat().some(k => k.id === 'mode'))
  assert.equal(w80.numRows.length, 0)

  const tkl = buildKeyboardLayout('tkl', 'ansi')
  assert.equal(tkl.hasLedBar, false)
  assert.equal(tkl.navRows.flat().some(k => k.id === 'led-bar'), false)

  const full = buildKeyboardLayout('full', 'ansi')
  assert.equal(full.numRows.length, 6)
  for (const id of ['a1', 'a2', 'a3', 'mode']) {
    assert.ok(full.numRows.flat().some(k => k.id === id))
  }
})

test('ISO, JIS, and UwU 3-key keypad render their distinct physical keys', () => {
  const iso = buildKeyboardLayout('60', 'iso')
  const isoKeys = iso.mainRows.flat()
  assert.equal(isoKeys.find(k => k.id === 'lshift')?.u, 1.25)
  assert.ok(isoKeys.some(k => k.id === 'iso-lt'))
  assert.ok(isoKeys.some(k => k.id === 'iso-hash'))
  assert.ok(isoKeys.some(k => k.id === 'enter-top'))

  const jis = buildKeyboardLayout('80', 'jis')
  const jisKeys = jis.mainRows.flat()
  assert.ok(jisKeys.some(k => k.id === 'yen'))
  assert.ok(jisKeys.some(k => k.id === 'muhenkan'))
  assert.ok(jisKeys.some(k => k.id === 'henkan'))
  assert.ok(jisKeys.some(k => k.id === 'kana'))
  assert.equal(jisKeys.find(k => k.id === 'space')?.u, 3.75)

  const uwu = buildKeyboardLayout('keypad3', 'keypad')
  assert.equal(uwu.form, 'keypad3')
  assert.equal(uwu.layoutCode, 'keypad')
  assert.equal(uwu.mainRows.length, 2)
  assert.equal(uwu.mainRows[1].filter(k => k.kind === 'analog').length, 3)
  assert.equal(rowUnits(uwu.mainRows[0]), 4)
  assert.equal(rowUnits(uwu.mainRows[1]), 4)
})

test('every Wooting model name resolves to its form factor and layout labels normalize cleanly', () => {
  const expected: Array<[string, KeyboardFormFactor]> = [
    ['Wooting One', 'tkl'],
    ['Wooting Two', 'full'],
    ['Wooting Two Lekker Edition', 'full'],
    ['Wooting Two HE (AVR)', 'full'],
    ['Wooting Two HE', 'full'],
    ['Wooting 60HE (AVR)', '60'],
    ['Wooting 60HE', '60'],
    ['Wooting 60HE+', '60'],
    ['Wooting 60HE v2', '60'],
    ['Wooting 80HE', '80'],
    ['Wooting 80HE+', '80'],
    ['Wooting UwU', 'keypad3'],
    ['Wooting UwU RGB', 'keypad3'],
  ]
  for (const [model, form] of expected) {
    assert.equal(resolveFormFactor(null, model), form, model)
  }

  assert.equal(resolveLayoutCode(null, 'ANSI · Split spacebar', '60'), 'ansi-split')
  assert.equal(resolveLayoutCode(null, 'ISO · Split spacebar', '60'), 'iso-split')
  assert.equal(resolveLayoutCode(null, 'ISO', '80'), 'iso')
  assert.equal(resolveLayoutCode(null, 'JIS', '80'), 'jis')
  assert.equal(resolveLayoutCode(null, null, 'keypad3'), 'keypad')
})

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { normalizeRecoveryCode, recoveryCodeConfirmed } from '../src/components/onboarding/accountCode.ts'

const CODE = 'K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVC'

test('dashes, spaces and case do not matter', () => {
  assert.equal(recoveryCodeConfirmed('k7q29tma xb4r lz8p wh3d 6nvc', CODE), true)
  assert.equal(recoveryCodeConfirmed('K7Q29TMAXB4RLZ8PWH3D6NVC', CODE), true)
})

test('O reads as 0, and I or L reads as 1', () => {
  // A code with a 0 and a 1 in it, typed with O and I or L in their place.
  const withDigits = '07Q2-9TMA-XB4R-1Z8P-WH3D-6NVC'
  assert.equal(recoveryCodeConfirmed('O7Q2-9TMA-XB4R-IZ8P-WH3D-6NVC', withDigits), true)
  assert.equal(recoveryCodeConfirmed('07Q2-9TMA-XB4R-LZ8P-WH3D-6NVC', withDigits), true)
})

test('a code one character short or long never unlocks Continue', () => {
  assert.equal(recoveryCodeConfirmed('K7Q2-9TMA-XB4R-LZ8P-WH3D-6NV', CODE), false)
  assert.equal(recoveryCodeConfirmed(CODE + 'X', CODE), false)
})

test('a different code does not unlock Continue, and empty text does not', () => {
  assert.equal(recoveryCodeConfirmed('K7Q2-9TMA-XB4R-LZ8P-WH3D-6NVD', CODE), false)
  assert.equal(recoveryCodeConfirmed('', CODE), false)
})

test('normalizing only removes separators and maps the look-alikes', () => {
  assert.equal(normalizeRecoveryCode(' o-i-l '), '011')
})

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { VOLUME_KEY_STEP, volumeKeyStep } from '../src/lib/musicVolume.ts'

test('arrow keys step the music volume by 5 percent, up and right raising it', () => {
  assert.equal(VOLUME_KEY_STEP, 5)
  assert.equal(volumeKeyStep('ArrowUp'), 5)
  assert.equal(volumeKeyStep('ArrowRight'), 5)
  assert.equal(volumeKeyStep('ArrowDown'), -5)
  assert.equal(volumeKeyStep('ArrowLeft'), -5)
})

test('other keys leave the slider to its own handling', () => {
  assert.equal(volumeKeyStep('Home'), null)
  assert.equal(volumeKeyStep('a'), null)
  assert.equal(volumeKeyStep('PageUp'), null)
})

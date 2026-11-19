import { test } from 'node:test'
import assert from 'node:assert/strict'
import { heldSentences } from '../src/components/settings/accountWords.ts'

test('one large cover image reads as a sentence, with no internal key', () => {
  const text = heldSentences([{ key: 'cover-image/game-1', reason: 'too-large' }])
  assert.deepEqual(text, ['1 cover image is too large to sync. It stays on this PC.'])
  assert.ok(!text.join(' ').includes('cover-image/'))
})

test('several items of one kind group into one line', () => {
  const text = heldSentences([
    { key: 'cover-image/a', reason: 'too-large' },
    { key: 'cover-image/b', reason: 'too-large' },
  ])
  assert.deepEqual(text, ['2 cover images are too large to sync. They stay on this PC.'])
})

test('a refused item says the service did not take it', () => {
  assert.deepEqual(heldSentences([{ key: 'cover/g9', reason: 'refused' }]), ['1 cover choice was not accepted by the service. It stays on this PC.'])
})

test('no held items, no sentences', () => {
  assert.deepEqual(heldSentences([]), [])
})

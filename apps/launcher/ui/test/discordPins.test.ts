import { test } from 'node:test'
import assert from 'node:assert/strict'

class Storage {
  items = new Map<string, string>()
  getItem(key: string) { return this.items.get(key) ?? null }
  setItem(key: string, value: string) { this.items.set(key, value) }
  removeItem(key: string) { this.items.delete(key) }
}

;(globalThis as { window?: unknown }).window = { localStorage: new Storage(), sessionStorage: new Storage() }

const {
  MAX_PINNED_DMS,
  isDmPinned,
  partitionPinnedDms,
  readPinnedDms,
  sanitizePinnedDms,
  togglePinDm,
} = await import('../src/lib/discordPins.ts')

test('sanitizePinnedDms deduplicates, trims, and caps at MAX_PINNED_DMS', () => {
  const many = Array.from({ length: MAX_PINNED_DMS + 10 }, (_, i) => `dm-${i}`)
  const result = sanitizePinnedDms([' ', 'dm-1', '', 42, 'dm-1', ...many])
  assert.equal(result.length, MAX_PINNED_DMS)
  assert.equal(result[0], 'dm-1')
  assert.equal(new Set(result).size, result.length)
})

test('togglePinDm pins/unpins DM ids and partitionPinnedDms keeps pinned order at top', () => {
  assert.equal(togglePinDm('dm-b'), true)
  assert.equal(togglePinDm('dm-a'), true)
  assert.equal(isDmPinned('dm-b'), true)
  assert.deepEqual(readPinnedDms(), ['dm-a', 'dm-b'])

  const all = [
    { id: 'dm-c', name: 'C' },
    { id: 'dm-b', name: 'B' },
    { id: 'dm-a', name: 'A' },
    { id: 'dm-d', name: 'D' },
  ]
  const split = partitionPinnedDms(all, readPinnedDms())
  assert.deepEqual(split.pinned.map(d => d.id), ['dm-a', 'dm-b'])
  assert.deepEqual(split.unpinned.map(d => d.id), ['dm-c', 'dm-d'])

  assert.equal(togglePinDm('dm-a'), false)
  assert.equal(isDmPinned('dm-a'), false)
  assert.deepEqual(readPinnedDms(), ['dm-b'])
})

// The Settings > Game stores line for a store that is installed but showed no games (src/lib/stores.ts).
//
// How it could fail: the warning is missing for the flagged store (the person never learns the library is empty);
// it shows on a healthy store (noise); or it replaces a sign-in state the person must act on.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { storeLayoutNote } from '../src/lib/stores.ts'
import type { StoreStatus } from '../src/lib/host.ts'

const base: StoreStatus = { store: 'steam', displayName: 'Steam', clientPresent: true, checkCode: 'checked' }

test('a flagged store gets a quiet line that says the layout may have changed', () => {
  const note = storeLayoutNote({ ...base, checkCode: 'layout_unrecognized' })
  assert.match(note ?? '', /no games/)
  assert.match(note ?? "", /moved its library/)
})

test('a healthy store gets no line', () => {
  assert.equal(storeLayoutNote(base), undefined)
})

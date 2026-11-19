// Exo Mouse's presets kept by Exo (src/savedStore.ts), with a made-up host and frame storage.
//
// How it could fail: a failed read of Exo's file leaves an empty set and the next save rewrites the file whole, losing
// every preset; every save after a failed read fails without a word; presets the frame kept before are lost on the update, or removed from the frame before Exo saved them;
// a value Exo already has is overwritten by an older copy from the frame.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { makeSaved } from '../src/savedStore.ts'

function frame(items: Record<string, string>) {
  return { items, get: (key: string) => items[key] ?? null, remove: (key: string) => { delete items[key] } }
}

test('a failed read of Exo\'s file means no save writes it', async () => {
  const writes: unknown[] = []
  const saved = makeSaved({ read: () => Promise.resolve({ ok: false }), write: data => { writes.push(data); return Promise.resolve({ ok: true }) } }, frame({}))

  await saved.load()

  assert.equal(await saved.save('hits-presets', [{ name: 'Fast' }]), false)
  assert.deepEqual(writes, [])
})

test('after a failed read, the first save that cannot be kept says so, once', async () => {
  const said: string[] = []
  const saved = makeSaved({ read: () => Promise.resolve({ ok: false }), write: () => Promise.resolve({ ok: true }) }, frame({}), text => said.push(text))
  await saved.load()

  await saved.save('hits-presets', [])
  await saved.save('host-bunny-hop', {})

  assert.equal(said.length, 1)
  assert.match(said[0], /could not read your saved presets/)
})

test('the frame\'s old presets move to Exo once, and leave the frame only after Exo saved them', async () => {
  const writes: Record<string, unknown>[] = []
  let ok = false
  const old = frame({ 'exo-mouse-hits-presets': JSON.stringify([{ name: 'Old' }]), 'exo-mouse.host-bunny-hop': JSON.stringify({ m1: 50 }) })
  const host = { read: () => Promise.resolve({ ok: true, data: { 'host-bunny-hop': { m1: 80 } } }), write: (data: Record<string, unknown>) => { writes.push(data); return Promise.resolve({ ok }) } }

  await makeSaved(host, old).load()
  assert.deepEqual(writes, [{ 'host-bunny-hop': { m1: 80 }, 'hits-presets': [{ name: 'Old' }] }])
  assert.ok(old.items['exo-mouse-hits-presets'], 'kept while Exo has not saved it')

  ok = true
  const again = makeSaved(host, old)
  await again.load()
  assert.equal(old.items['exo-mouse-hits-presets'], undefined)
  assert.equal(old.items['exo-mouse.host-bunny-hop'], undefined)
  // Exo's own value beat the frame's older copy.
  assert.deepEqual(again.value('host-bunny-hop'), { m1: 80 })
})

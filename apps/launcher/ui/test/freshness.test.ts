// freshness: what a list shows while it loads, fails or is empty, and what makes a cached list read again. Ways it could
// fail: the empty line flashes before the first answer lands (loading read as empty); a failed read says "nothing here"
// (failure read as empty); a list on screen is replaced by an error because one refresh behind it failed; a return to
// the window (focus, visible and online at once) reads three times; a read starts while the page is hidden; an
// unsubscribed listener still reads; signing out leaves one cache behind, or one throwing cache stops the others; one
// account's caches are forgotten by another's sign-out; an account switch with no sign-out between is missed; the first
// account seen counts as a switch and throws away a cache just filled.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { accountWatch, forgetOnSignOut, readView, signedOut, whenBack, type ReturnEnv } from '../src/lib/freshness.ts'

test('loading is not empty: no answer yet shows the wait, an empty answer shows the empty line', () => {
  assert.equal(readView(null), 'loading')
  assert.equal(readView(undefined), 'loading')
  assert.equal(readView([]), 'empty')
  assert.equal(readView([1]), 'ready')
})

test('a failed read is not empty, before or after an empty answer', () => {
  assert.equal(readView(null, 'Discord did not answer.'), 'failed')
  assert.equal(readView([], true), 'failed')
  assert.equal(readView(null, null), 'loading')
  assert.equal(readView([], false), 'empty')
})

test('a list on screen stays through a failed refresh behind it', () => {
  assert.equal(readView(['kept'], 'offline'), 'ready')
})

function fakeEnv() {
  const listeners = new Map<string, Set<() => void>>()
  const target = {
    addEventListener: (type: string, fn: () => void) => { if (!listeners.has(type)) listeners.set(type, new Set()); listeners.get(type)!.add(fn) },
    removeEventListener: (type: string, fn: () => void) => { listeners.get(type)?.delete(fn) },
  }
  let clock = 0
  const doc = { ...target, hidden: false }
  const env: ReturnEnv = { win: target as never, doc: doc as never, now: () => clock }
  const fire = (type: string) => { for (const fn of [...(listeners.get(type) ?? [])]) fn() }
  return { env, doc, fire, tick: (ms: number) => { clock += ms }, count: () => [...listeners.values()].reduce((n, s) => n + s.size, 0) }
}

test('one return to the window reads once, however many of focus, visible and online it fires', () => {
  const f = fakeEnv()
  let reads = 0
  whenBack(() => { reads += 1 }, 2000, f.env)
  f.fire('focus'); f.fire('visibilitychange'); f.fire('online')
  assert.equal(reads, 1)
  f.tick(2500)
  f.fire('online')
  assert.equal(reads, 2)
})

test('nothing reads while the page is hidden; coming back on screen does', () => {
  const f = fakeEnv()
  let reads = 0
  whenBack(() => { reads += 1 }, 0, f.env)
  f.doc.hidden = true
  f.fire('visibilitychange')
  f.fire('online')
  assert.equal(reads, 0)
  f.doc.hidden = false
  f.fire('visibilitychange')
  assert.equal(reads, 1)
})

test('unsubscribed, a return reads nothing and no listener is left', () => {
  const f = fakeEnv()
  let reads = 0
  const off = whenBack(() => { reads += 1 }, 0, f.env)
  off()
  f.fire('focus')
  assert.equal(reads, 0)
  assert.equal(f.count(), 0)
})

test("signing out forgets every cache of that account, even past one that throws, and none of another's", () => {
  const forgot: string[] = []
  forgetOnSignOut('discord', () => { forgot.push('channels') })
  forgetOnSignOut('discord', () => { throw new Error('broken cache') })
  forgetOnSignOut('discord', () => { forgot.push('messages') })
  const offStore = forgetOnSignOut('steam', () => { forgot.push('store home') })
  signedOut('discord')
  assert.deepEqual(forgot, ['channels', 'messages'])
  offStore()
  signedOut('steam')
  assert.deepEqual(forgot, ['channels', 'messages'])
})

test('an account switch is seen without a sign-out between, and the first account is not a switch', () => {
  const watch = accountWatch()
  assert.equal(watch.changed('76561198000000001'), false)
  assert.equal(watch.changed('76561198000000001'), false)
  assert.equal(watch.changed('76561198000000002'), true)
  assert.equal(watch.changed(null), true)
  assert.equal(watch.changed(undefined), false)
})

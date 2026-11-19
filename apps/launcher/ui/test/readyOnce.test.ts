// readyOnce: how the page tells the host it reached a usable start (shell.ready). Ways it could fail: a call that fails is
// never sent again, so a working update is rolled back; a call that went through is sent again; calls made while one is
// under way send more than one; a host that keeps failing is asked forever.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readyOnce } from '../src/lib/readyOnce.ts'

const settle = () => new Promise(resolve => setImmediate(resolve))

test('a failed call is sent again until it goes through, then never again', async () => {
  const answers = [false, false, true]
  let sent = 0
  const ready = readyOnce(() => { sent += 1; return answers.shift() ? Promise.resolve() : Promise.reject(new Error('no')) }, 5, 0, (_, then) => { void settle().then(then) })
  ready()
  for (let i = 0; i < 10; i++) await settle()
  assert.equal(sent, 3)
  ready()
  ready()
  await settle()
  assert.equal(sent, 3)
})

test('calls while one is under way send nothing more', async () => {
  let sent = 0
  let finish: () => void = () => { }
  const ready = readyOnce(() => { sent += 1; return new Promise<void>(resolve => { finish = resolve }) })
  ready()
  ready()
  ready()
  assert.equal(sent, 1)
  finish()
  await settle()
  ready()
  assert.equal(sent, 1)
})

test('a host that keeps failing is asked a bounded number of times', async () => {
  let sent = 0
  const ready = readyOnce(() => { sent += 1; return Promise.reject(new Error('no')) }, 4, 0, (_, then) => { void settle().then(then) })
  ready()
  for (let i = 0; i < 20; i++) await settle()
  assert.equal(sent, 4)
})

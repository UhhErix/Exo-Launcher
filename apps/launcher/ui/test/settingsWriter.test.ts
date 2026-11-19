import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createSettingsWriter, readUntilSettled } from '../src/lib/settingsWriter.ts'

type S = { a: number, b: number }

function setup() {
  const sent: Array<{ patch: Partial<S>, done: (value: S) => void, fail: (error: Error) => void }> = []
  const applied: S[] = []
  const writer = createSettingsWriter<S, Partial<S>>({
    send: patch => new Promise<S>((done, fail) => { sent.push({ patch, done, fail }) }),
    apply: saved => { applied.push(saved) },
  })
  const tick = async () => { for (let i = 0; i < 5; i++) await Promise.resolve() }
  return { sent, applied, writer, tick }
}

// How it could fail: a hide saved first answers last and puts back the source chosen after it.
test('a late answer to an older save never replaces the newer one', async () => {
  const { sent, applied, writer, tick } = setup()
  const first = writer.save({ a: 1 })
  const second = writer.save({ b: 2 })
  await tick()
  assert.equal(sent.length, 1, 'saves go one at a time')
  sent[0].done({ a: 1, b: 0 })
  await first
  await tick()
  assert.equal(sent.length, 2)
  assert.deepEqual(applied, [], 'the older answer is not taken while a newer save is on its way')
  sent[1].done({ a: 1, b: 2 })
  await second
  assert.deepEqual(applied, [{ a: 1, b: 2 }])
})

test('the only save is applied, and idle says when nothing is on its way', async () => {
  const { sent, applied, writer, tick } = setup()
  assert.equal(writer.idle(), true)
  const only = writer.save({ a: 5 })
  assert.equal(writer.idle(), false)
  await tick()
  sent[0].done({ a: 5, b: 0 })
  await only
  assert.deepEqual(applied, [{ a: 5, b: 0 }])
  assert.equal(writer.idle(), true)
})

test('a failed save rejects to its caller, does not stop the next one, and applies nothing', async () => {
  const { sent, applied, writer, tick } = setup()
  const bad = writer.save({ a: 1 })
  const good = writer.save({ b: 3 })
  await tick()
  sent[0].fail(new Error('no'))
  await assert.rejects(bad, /no/)
  await tick()
  sent[1].done({ a: 0, b: 3 })
  await good
  assert.deepEqual(applied, [{ a: 0, b: 3 }])
  assert.equal(writer.idle(), true)
})

// A fake host that models what the real one does: it merges each patch into what it stores and answers with everything.
function hostModel() {
  let stored: S = { a: 0, b: 0 }
  const waiting: Array<{ answer: () => void, fail: (error: Error) => void }> = []
  const applied: S[] = []
  const writer = createSettingsWriter<S, Partial<S>>({
    send: patch => new Promise<S>((done, fail) => {
      waiting.push({
        answer: () => { stored = { ...stored, ...patch }; done({ ...stored }) },
        fail: error => fail(error),
      })
    }),
    apply: saved => { applied.push(saved) },
  })
  const tick = async () => { for (let i = 0; i < 5; i++) await Promise.resolve() }
  return { waiting, applied, writer, tick, stored: () => stored }
}

// How it could fail: save A succeeds, newer save B fails, and the page keeps showing neither (A's answer was held back for B).
test('when the newest save fails, the page takes the answer of the save that did work and the failure rejects', async () => {
  const { waiting, applied, writer, tick } = hostModel()
  const first = writer.save({ a: 1 })
  const second = writer.save({ b: 2 })
  await tick()
  waiting[0].answer()
  await first
  await tick()
  assert.deepEqual(applied, [], 'held back while B is on its way')
  waiting[1].fail(new Error('refused'))
  await assert.rejects(second, /refused/)
  assert.deepEqual(applied, [{ a: 1, b: 0 }], 'A is what the host stores')
})

test('a failing only save applies nothing and a later success is applied normally', async () => {
  const { waiting, applied, writer, tick } = hostModel()
  const bad = writer.save({ a: 1 })
  await tick()
  waiting[0].fail(new Error('no'))
  await assert.rejects(bad)
  assert.deepEqual(applied, [])
  const good = writer.save({ b: 2 })
  await tick()
  waiting[1].answer()
  await good
  assert.deepEqual(applied, [{ a: 0, b: 2 }])
})

// How it could fail: two saves sent together answer in any order and the page ends with the one that does not hold the other's change.
test('two patches in a row never clobber each other: the host stores both and the page ends with both', async () => {
  const { waiting, applied, writer, tick, stored } = hostModel()
  const first = writer.save({ a: 1 })
  const second = writer.save({ b: 2 })
  await tick()
  assert.equal(waiting.length, 1, 'the second patch waits for the first answer')
  waiting[0].answer()
  await first
  await tick()
  waiting[1].answer()
  await second
  assert.deepEqual(stored(), { a: 1, b: 2 })
  assert.deepEqual(applied, [{ a: 1, b: 2 }])
})

// How it could fail: a read of the settings is in flight, a save is made and answered, and then the older read answers and
// puts back what the save changed (the guard that looked only at the moment of the answer let this through).
test('a read answered after a newer save, or while one is on its way, is dropped', async () => {
  const { sent, applied, writer, tick } = setup()
  let answerRead: (value: S) => void = () => undefined
  const read = writer.read(() => new Promise<S>(done => { answerRead = done }))
  const save = writer.save({ a: 7 })
  await tick()
  sent[0].done({ a: 7, b: 0 })
  await save
  answerRead({ a: 0, b: 0 })
  assert.equal(await read, null, 'a save was made after the read began')
  assert.deepEqual(applied, [{ a: 7, b: 0 }])

  let answerSecond: (value: S) => void = () => undefined
  const second = writer.read(() => new Promise<S>(done => { answerSecond = done }))
  const pending = writer.save({ b: 1 })
  answerSecond({ a: 7, b: 0 })
  assert.equal(await second, null, 'a save is on its way when the read answers')
  await tick()
  sent[1].done({ a: 7, b: 1 })
  await pending
})

test('a read with no save around it is returned, and a failed read rejects', async () => {
  const { writer } = setup()
  assert.deepEqual(await writer.read(() => Promise.resolve({ a: 1, b: 2 })), { a: 1, b: 2 })
  await assert.rejects(writer.read(() => Promise.reject(new Error('down'))), /down/)
})

// How it could fail: the page asks for its settings while a queue of saves is open, every read is dropped, and the page is left
// with nothing and no error.
test('a read asked for during a queue of saves waits for them and returns the saved state', async () => {
  const { sent, writer, tick } = setup()
  const first = writer.save({ a: 1 })
  const second = writer.save({ b: 2 })
  let fetched = 0
  const read = writer.read(() => { fetched++; return Promise.resolve({ a: 1, b: 2 }) })
  await tick()
  assert.equal(fetched, 0, 'nothing is read while saves are open')
  sent[0].done({ a: 1, b: 0 })
  await first
  await tick()
  assert.equal(fetched, 0, 'one save is still open')
  sent[1].done({ a: 1, b: 2 })
  await second
  assert.deepEqual(await read, { a: 1, b: 2 })
  assert.equal(fetched, 1)
})

test('a read waiting for saves that fail still goes on', async () => {
  const { sent, writer, tick } = setup()
  const save = writer.save({ a: 1 })
  const read = writer.read(() => Promise.resolve({ a: 0, b: 0 }))
  await tick()
  sent[0].fail(new Error('refused'))
  await assert.rejects(save, /refused/)
  assert.deepEqual(await read, { a: 0, b: 0 })
})

test('the load path throws when no read stands, and takes the first read that does', async () => {
  let asked = 0
  await assert.rejects(readUntilSettled<S>(() => { asked++; return Promise.resolve(null) }), /could not be loaded/)
  assert.equal(asked, 4, 'the first read and three more')
  let calls = 0
  const got = await readUntilSettled<S>(() => Promise.resolve(++calls < 3 ? null : { a: 1, b: 1 }))
  assert.deepEqual(got, { a: 1, b: 1 })
})

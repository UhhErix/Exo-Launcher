/**
 * The relay's own tests: the real worker.js under the local runtime (../local/runtime.mjs), through real WebSockets
 * (Node's own client). Each runs twice, the second time with the object thrown away after every event as hibernation
 * does, so state kept only in memory fails here.
 *
 * How the relay could fail: a message reaches its sender, another topic or nobody; a latecomer misses what was said
 * before it came, or gets an old message instead of the newest, or one from someone who left; nobody hears that
 * someone left (their song would stay on screen); its age is wrong (positions drift); limits that don't hold (size,
 * rate, members, text), or a closed-by-the-relay connection whose leaving isn't told; bad paths reach an object.
 *   node --test relay/test
 */
import assert from 'node:assert/strict'
import { after, before, describe, test } from 'node:test'
import { serve } from '../local/runtime.mjs'

const worker = await import('../src/worker.js')
const TOPIC = 'a'.repeat(32)

for (const evict of [false, true]) {
  describe(evict ? 'evicted after every event' : 'kept in memory', () => {
    let relay
    before(async () => { relay = await serve(worker, { evict }) })
    after(() => relay.close())
    const url = (topic = TOPIC) => `ws://127.0.0.1:${relay.port}/v1/${topic}`

    test('a message reaches everyone else on the topic, at once, and not its sender', async () => {
      const topic = fresh()
      const [a, b, c] = await Promise.all([open(url(topic)), open(url(topic)), open(url(topic))])
      a.ws.send(bytes(1, 2, 3))
      const [toB, toC] = await Promise.all([b.next(), c.next()])
      for (const got of [toB, toC]) {
        assert.equal(got.kind, 1)
        assert.equal(got.age, 0)
        assert.deepEqual([...got.payload], [1, 2, 3])
      }
      assert.equal(toB.conn, toC.conn)
      await quiet(a)
      ;[a, b, c].forEach(s => s.ws.close())
    })

    test('a latecomer gets each member\'s newest message, with how long the relay held it', async () => {
      const topic = fresh()
      const [a, b] = await Promise.all([open(url(topic)), open(url(topic))])
      a.ws.send(bytes(1))
      a.ws.send(bytes(2))
      await b.next(); await b.next()
      b.ws.send(bytes(9))
      await a.next()
      await delay(120)
      const c = await open(url(topic))
      const got = [await c.next(), await c.next()].sort((x, y) => x.payload[0] - y.payload[0])
      assert.deepEqual(got.map(g => [...g.payload]), [[2], [9]])
      for (const g of got) assert.ok(g.age >= 100 && g.age < 5000, `age ${g.age}`)
      await quiet(c)
      ;[a, b, c].forEach(s => s.ws.close())
    })

    test('leaving is told to the rest, and a leaver\'s message is not replayed', async () => {
      const topic = fresh()
      const [a, b] = await Promise.all([open(url(topic)), open(url(topic))])
      b.ws.send(bytes(7))
      const said = await a.next()
      b.ws.close()
      const gone = await a.next()
      assert.equal(gone.kind, 2)
      assert.equal(gone.conn, said.conn)
      const c = await open(url(topic))
      await quiet(c)
      ;[a, c].forEach(s => s.ws.close())
    })

    test('topics are apart', async () => {
      const [a, b] = await Promise.all([open(url(fresh())), open(url(fresh()))])
      a.ws.send(bytes(1))
      await quiet(b)
      ;[a, b].forEach(s => s.ws.close())
    })

    test('too big, text and too many close the connection, and the rest hear it went', async () => {
      const topic = fresh()
      const watcher = await open(url(topic))
      for (const [send, code] of [
        [ws => ws.send(new Uint8Array(2049)), 1009],
        [ws => ws.send('hello'), 1003],
        [ws => { for (let i = 0; i < 21; i++) ws.send(bytes(i)) }, 1008],
      ]) {
        const s = await open(url(topic))
        send(s.ws)
        assert.equal(await s.closed, code)
        let frame
        do frame = await watcher.next()
        while (frame.kind === 1)
        assert.equal(frame.kind, 2)
      }
      const ok = await open(url(topic))
      ok.ws.send(new Uint8Array(2048))
      assert.equal((await watcher.next()).payload.length, 2048)
      ;[watcher, ok].forEach(s => s.ws.close())
    })

    test('a topic holds 32 connections', async () => {
      const topic = fresh()
      const all = await Promise.all(Array.from({ length: 32 }, () => open(url(topic))))
      await assert.rejects(open(url(topic)))
      all.forEach(s => s.ws.close())
    })

    test('only /v1/<32 lowercase hex> as a WebSocket', async () => {
      const base = `http://127.0.0.1:${relay.port}`
      assert.equal((await fetch(`${base}/v1/${TOPIC}`)).status, 426)
      for (const path of ['/', '/v1/', `/v1/${'A'.repeat(32)}`, `/v1/${'a'.repeat(31)}`, `/v1/${TOPIC}/x`, `/v2/${TOPIC}`])
        assert.equal((await fetch(base + path)).status, 404, path)
      await assert.rejects(open(`ws://127.0.0.1:${relay.port}/v1/nope`))
    })
  })
}

let topics = 0
const fresh = () => (++topics).toString(16).padStart(32, '0')
const bytes = (...values) => new Uint8Array(values)
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))

/** A connection with its frames queued: next() is the next one (2 s at most), closed the close code. */
function open(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url)
    ws.binaryType = 'arraybuffer'
    const frames = []
    const waiting = []
    let closedWith
    const closed = new Promise((done, fail) => {
      closedWith = done
      setTimeout(() => fail(new Error('not closed')), 3000).unref()
    })
    closed.catch(() => {})
    ws.onmessage = event => {
      const data = new Uint8Array(event.data)
      const view = new DataView(data.buffer)
      const frame = { kind: data[0], conn: view.getUint32(1), age: data[0] === 1 ? view.getUint32(5) : 0, payload: data.subarray(data[0] === 1 ? 9 : 5) }
      const waiter = waiting.shift()
      if (waiter) waiter(frame)
      else frames.push(frame)
    }
    ws.onclose = event => closedWith(event.code)
    ws.onerror = () => reject(new Error('refused'))
    ws.onopen = () => resolve({
      ws, closed, frames,
      next: () => frames.length ? Promise.resolve(frames.shift()) : new Promise((done, fail) => {
        const timer = setTimeout(() => fail(new Error('no frame')), 2000)
        waiting.push(frame => { clearTimeout(timer); done(frame) })
      }),
    })
  })
}

/** Nothing more comes to s for a moment. */
async function quiet(s) {
  await delay(150)
  assert.deepEqual(s.frames, [])
}

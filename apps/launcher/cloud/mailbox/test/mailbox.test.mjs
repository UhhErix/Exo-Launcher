/**
 * The mailbox's own tests: the real worker.js under the local runtime (../local/runtime.mjs), through real WebSockets
 * (Node's own client). Each runs twice, the second time with every object thrown away after each event as hibernation
 * does, so state kept only in memory fails here. With MAILBOX_URL set (ws://127.0.0.1:8787 from `wrangler dev`), the
 * ones that need no clock or limit changes run against workerd instead.
 *
 * How the mailbox could fail: someone without a box's key reads or writes it (a wrong key, another box's key, a
 * changed signature, the high-s twin of a good one, a stale challenge, a frame before login); a stored message is lost, duplicated, reordered or
 * reaches the wrong box or its own writer; a reader catching up misses rows or never hears there's more; trim or a
 * cap restarts seq numbers or takes the newest row; a row goes by age while its box is used (a PC away for months, or a
 * new PC after the only one was lost, must find the account whole), or a box nobody used for two years keeps its storage; the limits
 * don't hold (size, frames, puts, connections) or a connection that never logs in holds a slot forever. The
 * rendezvous: a nameplate handed out twice at once, a second joiner not crowding the session out (the new PC must
 * hear it), a PC forging the rendezvous' own frames, a session outliving ten minutes or its other side (the PC left
 * behind must be told), nameplate guesses not limited per address.
 *   node --test cloud/mailbox/test
 */
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { get as httpGet } from 'node:http'
import { after, afterEach, before, describe, test } from 'node:test'
import { bindings, ratelimits } from '../local/config.mjs'
import { serve } from '../local/runtime.mjs'
import { limits } from '../src/limits.js'

const worker = await import('../src/worker.js')
const remote = process.env.MAILBOX_URL
const defaults = structuredClone(limits)
const CHALLENGE = 0x10, READY = 0x11, MESSAGE = 0x12, STORED = 0x13, CAUGHT_UP = 0x14
const AUTH = 0x20, PUT = 0x21, SINCE = 0x22, TRIM = 0x23
const NAMEPLATE = 0x30, JOINED = 0x31, GONE = 0x32, CROWDED = 0x33
const ORDER = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551n
const DAY = 24 * 60 * 60 * 1000

for (const evict of remote ? [false] : [false, true]) {
  describe(remote ? `against ${remote}` : evict ? 'evicted after every event' : 'kept in memory', () => {
    let mailbox
    let base
    const local = remote ? test.skip : test
    before(async () => {
      if (remote) base = remote
      else {
        mailbox = await serve(worker, { evict, bindings, ratelimits })
        base = `ws://127.0.0.1:${mailbox.port}`
      }
    })
    after(() => mailbox?.close())
    afterEach(() => {
      Object.assign(limits, structuredClone(defaults))
      Date.now = realNow
    })

    test('the box key logs in; the ready frame says the box is empty', async () => {
      const key = await boxKey()
      const s = await login(base, key)
      assert.deepEqual(s.ready, { first: 1, last: 0 })
      s.ws.close()
    })

    test('a wrong key, another box\'s key, a changed or high-s signature or a frame before login is shut out', async () => {
      const key = await boxKey()
      const other = await boxKey()
      for (const [answer, code] of [
        [async c => auth(other, c), 1008],
        [async c => { const a = await auth(key, c); a[100] ^= 1; return a }, 1008],
        [async c => { const a = await auth(key, c); a.set(twinS(a.subarray(66)), 66); return a }, 1008],
        [async c => { const a = await auth(key, c); a[1] = 2; return a }, 1008],
        [async c => auth(key, c.map(b => b ^ 1)), 1008],
        [async () => new Uint8Array([PUT, 1, 2, 3]), 1008],
        [async c => (await auth(key, c)).slice(0, 129), 1008],
      ]) {
        const s = await open(`${base}/v1/box/${key.id}`)
        const challenge = await s.next()
        assert.equal(challenge.kind, CHALLENGE)
        s.ws.send(await answer(challenge.body))
        assert.equal(await s.closed, code)
      }
      // And the other box's key opens its own box only.
      const theirs = await login(base, other)
      theirs.ws.close()
      const text = await open(`${base}/v1/box/${key.id}`)
      await text.next()
      text.ws.send('hello')
      assert.equal(await text.closed, 1003)
    })

    test('a put is stored once, in order, and reaches the box\'s other readers but not its writer', async () => {
      const key = await boxKey()
      const [a, b] = [await login(base, key), await login(base, key)]
      const outsider = await login(base, await boxKey())
      const lurker = await open(`${base}/v1/box/${key.id}`)
      await lurker.next()
      a.ws.send(put(bytes(1, 2, 3)))
      a.ws.send(put(bytes(4)))
      assert.deepEqual(await a.next(), frameOf(STORED, 1))
      assert.deepEqual(await a.next(), frameOf(STORED, 2))
      const first = await b.next()
      assert.equal(first.kind, MESSAGE)
      assert.equal(first.seq, 1)
      assert.ok(Math.abs(first.at - Date.now() / 1000) < 60)
      assert.deepEqual([...first.blob], [1, 2, 3])
      assert.equal((await b.next()).seq, 2)
      await quiet(a)
      await quiet(outsider)
      await quiet(lurker)
      const later = await login(base, key)
      assert.deepEqual(later.ready, { first: 1, last: 2 })
      ;[a, b, outsider, lurker, later].forEach(s => s.ws.close())
    })

    local('a reader catches up from its last seq, a page at a time', async () => {
      limits.pageRows = 3
      const key = await boxKey()
      const s = await login(base, key)
      for (let i = 1; i <= 5; i++) {
        s.ws.send(put(bytes(i)))
        await s.next()
      }
      s.ws.send(since(1))
      const page = [await s.next(), await s.next(), await s.next(), await s.next()]
      assert.deepEqual(page.slice(0, 3).map(f => f.seq), [2, 3, 4])
      assert.deepEqual(page[3], { kind: CAUGHT_UP, last: 4, more: true })
      s.ws.send(since(4))
      assert.equal((await s.next()).seq, 5)
      assert.deepEqual(await s.next(), { kind: CAUGHT_UP, last: 5, more: false })
      s.ws.send(since(5))
      assert.deepEqual(await s.next(), { kind: CAUGHT_UP, last: 5, more: false })
      s.ws.close()
    })

    test('trim keeps the newest row, so seq numbers carry on', async () => {
      const key = await boxKey()
      const s = await login(base, key)
      for (let i = 1; i <= 3; i++) {
        s.ws.send(put(bytes(i)))
        await s.next()
      }
      s.ws.send(trim(99))
      s.ws.send(since(0))
      assert.equal((await s.next()).seq, 3)
      assert.deepEqual(await s.next(), { kind: CAUGHT_UP, last: 3, more: false })
      s.ws.send(put(bytes(4)))
      assert.deepEqual(await s.next(), frameOf(STORED, 4))
      s.ws.close()
      const again = await login(base, key)
      assert.deepEqual(again.ready, { first: 3, last: 4 })
      again.ws.close()
    })

    local('over the row or byte cap the oldest rows go, never the seq count', async () => {
      limits.rows = 3
      const key = await boxKey()
      const s = await login(base, key)
      for (let i = 1; i <= 5; i++) {
        s.ws.send(put(bytes(i)))
        await s.next()
      }
      s.ws.close()
      const after = await login(base, key)
      assert.deepEqual(after.ready, { first: 3, last: 5 })
      after.ws.close()

      limits.rows = 100
      limits.boxBytes = 2500
      const bigKey = await boxKey()
      const b = await login(base, bigKey)
      for (let i = 1; i <= 4; i++) {
        b.ws.send(put(new Uint8Array(1000).fill(i)))
        await b.next()
      }
      b.ws.send(since(0))
      const kept = [await b.next(), await b.next()]
      assert.deepEqual(kept.map(f => [f.seq, f.blob[0], f.blob.length]), [[3, 3, 1000], [4, 4, 1000]])
      assert.equal((await b.next()).kind, CAUGHT_UP)
      b.ws.close()
    })

    local('rows go 30 days behind the newest, so a quiet box keeps its last month; two years untouched and it goes whole', async () => {
      const key = await boxKey()
      const s = await login(base, key)
      s.ws.send(put(bytes(1)))
      await s.next()
      Date.now = () => realNow() + 20 * DAY
      s.ws.send(put(bytes(2)))
      await s.next()
      s.ws.close()

      // Months away (a PC off, or lost): the box is as it was.
      Date.now = () => realNow() + 400 * DAY
      await mailbox.runAlarms()
      assert.deepEqual(mailbox.env.BOXES.rows(key.id, 'rows').map(r => r.seq), [1, 2])
      // A PC that only reads keeps it in use.
      const reader = await login(base, key)
      assert.deepEqual(reader.ready, { first: 1, last: 2 })
      reader.ws.close()
      Date.now = () => realNow() + 760 * DAY
      await mailbox.runAlarms()
      assert.deepEqual(mailbox.env.BOXES.rows(key.id, 'rows').map(r => r.seq), [1, 2])

      // Writes again: what is 30 days older than the newest row goes, as before.
      const w = await login(base, key)
      w.ws.send(put(bytes(3)))
      await w.next()
      w.ws.close()
      Date.now = () => realNow() + 762 * DAY
      await mailbox.runAlarms()
      assert.deepEqual(mailbox.env.BOXES.rows(key.id, 'rows').map(r => r.seq), [3])

      // Two years with no login and no write: the box goes whole.
      Date.now = () => realNow() + 1500 * DAY
      await mailbox.runAlarms()
      assert.deepEqual(mailbox.env.BOXES.rows(key.id, 'rows'), [])
      const back = await login(base, key)
      assert.deepEqual(back.ready, { first: 1, last: 0 })
      back.ws.close()
    })

    test('a message over 64 KiB is refused; 64 KiB is stored', async () => {
      const key = await boxKey()
      const s = await login(base, key)
      s.ws.send(put(new Uint8Array(65536)))
      assert.deepEqual(await s.next(), frameOf(STORED, 1))
      s.ws.send(put(new Uint8Array(65537)))
      assert.equal(await s.closed, 1009)
    })

    local('puts and frames past their buckets close the connection', async () => {
      limits.puts = [3, 60_000]
      const key = await boxKey()
      const s = await login(base, key)
      for (let i = 0; i < 4; i++) s.ws.send(put(bytes(i)))
      assert.equal(await s.closed, 1008)

      limits.frames = [5, 60_000]
      const f = await login(base, key)
      for (let i = 0; i < 6; i++) f.ws.send(since(0))
      assert.equal(await f.closed, 1008)
    })

    local('a box takes 16 connections; one that never logs in gives its place up after 10 seconds', async () => {
      const key = await boxKey()
      const idle = await open(`${base}/v1/box/${key.id}`)
      const rest = await Promise.all(Array.from({ length: 15 }, () => login(base, key)))
      await assert.rejects(open(`${base}/v1/box/${key.id}`))
      Date.now = () => realNow() + 11_000
      const late = await login(base, key)
      assert.equal(await idle.closed, 1008)
      ;[...rest, late].forEach(s => s.ws.close())
    })

    test('a new PC gets a nameplate, the linked PC joins it, and frames pass both ways as they are', async () => {
      const n = await open(`${base}/v1/link`)
      const plate = await n.next()
      assert.equal(plate.kind, NAMEPLATE)
      assert.match(plate.nameplate, /^[0-9]{8}$/)
      const e = await open(`${base}/v1/link/${plate.nameplate}`)
      assert.equal((await n.next()).kind, JOINED)
      assert.equal((await e.next()).kind, JOINED)
      e.ws.send(bytes(0x01, ...new Array(32).fill(7)))
      const commit = await n.raw()
      assert.equal(commit.length, 33)
      n.ws.send(bytes(0x02, 9, 9))
      assert.deepEqual([...await e.raw()], [0x02, 9, 9])
      // When one side goes, the session is over for the other too, and it's told so.
      e.ws.close()
      assert.equal((await n.next()).kind, GONE)
      await ended(n)
      await assert.rejects(open(`${base}/v1/link/${plate.nameplate}`))
    })

    test('a second joiner crowds the nameplate: both PCs hear it and the session ends', async () => {
      const n = await open(`${base}/v1/link`)
      const { nameplate } = await n.next()
      const e = await open(`${base}/v1/link/${nameplate}`)
      await n.next()
      await e.next()
      await assert.rejects(open(`${base}/v1/link/${nameplate}`))
      assert.equal((await n.next()).kind, CROWDED)
      assert.equal((await e.next()).kind, CROWDED)
      await ended(n)
      await ended(e)
    })

    test('a PC can\'t send the rendezvous\' own frames', async () => {
      const n = await open(`${base}/v1/link`)
      const { nameplate } = await n.next()
      const e = await open(`${base}/v1/link/${nameplate}`)
      await n.next()
      await e.next()
      e.ws.send(bytes(CROWDED))
      assert.equal(await e.closed, 1008)
      assert.equal((await n.next()).kind, GONE)
      await ended(n)
    })

    local('a nameplate lasts ten minutes, and nobody joins a nameplate that isn\'t out', async () => {
      const n = await open(`${base}/v1/link`)
      const { nameplate } = await n.next()
      await assert.rejects(open(`${base}/v1/link/${nameplate === '00000000' ? '00000001' : '00000000'}`))
      Date.now = () => realNow() + 10 * 60 * 1000 + 1
      await assert.rejects(open(`${base}/v1/link/${nameplate}`))
      n.ws.send(bytes(1))
      assert.equal(await n.closed, 1000)
    })

    local('nameplate guesses are limited per address', async () => {
      const fresh = await serve(worker, { evict, bindings, ratelimits })
      try {
        const url = `ws://127.0.0.1:${fresh.port}/v1/link/12345678`
        const answers = []
        for (let i = 0; i < 11; i++) answers.push(await status(url))
        assert.deepEqual(answers, [...new Array(10).fill(404), 429])
      } finally { await fresh.close() }
    })

    test('only /v1/box/<32 hex>, /v1/link and /v1/link/<8 digits>, as WebSockets', async () => {
      const http = base.replace(/^ws/, 'http')
      assert.equal((await fetch(`${http}/v1/box/${'a'.repeat(32)}`)).status, 426)
      for (const path of ['/', '/v1/box/', `/v1/box/${'A'.repeat(32)}`, `/v1/box/${'a'.repeat(31)}`, '/v1/link/1234567', '/v1/link/new/12345678', '/v1/link/x'])
        assert.equal((await fetch(http + path)).status, 404, path)
    })
  })
}

test('the local runtime binds what wrangler.toml declares', () => {
  const toml = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')
  for (const [binding, className] of Object.entries(bindings))
    assert.match(toml, new RegExp(`name = "${binding}"\\s+class_name = "${className}"`))
  assert.match(toml, new RegExp(`new_sqlite_classes = \\[${Object.values(bindings).map(c => `"${c}"`).join(', ')}\\]`))
  const { limit, period } = ratelimits.LINK_GUESSES
  assert.match(toml, new RegExp(`name = "LINK_GUESSES"[\\s\\S]*simple = \\{ limit = ${limit}, period = ${period} \\}`))
  assert.match(toml, /\[observability\]\s+enabled = false/)
  assert.match(toml, /send_metrics = false/)
})

const realNow = Date.now.bind(Date)
const bytes = (...values) => new Uint8Array(values)
const delay = ms => new Promise(resolve => setTimeout(resolve, ms))
const u32 = value => { const out = new Uint8Array(4); new DataView(out.buffer).setUint32(0, value); return out }
const put = blob => new Uint8Array([PUT, ...blob])
const since = seq => new Uint8Array([SINCE, ...u32(seq)])
const trim = seq => new Uint8Array([TRIM, ...u32(seq)])
const frameOf = (kind, seq) => ({ kind, seq })
const hex = data => [...data].map(b => b.toString(16).padStart(2, '0')).join('')

/** A box key as Exo makes one: P-256, its box id the first 16 bytes of SHA-256 of the raw public key. */
async function boxKey() {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify'])
  const publicKey = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey))
  const id = hex(new Uint8Array(await crypto.subtle.digest('SHA-256', publicKey)).subarray(0, 16))
  return { ...pair, raw: publicKey, id }
}

/** The login answer as Exo signs it: low s (WebCrypto signs either, as CNG does). */
async function auth(key, challenge) {
  const id = Uint8Array.from(key.id.match(/../g).map(h => parseInt(h, 16)))
  const signed = new Uint8Array([...new TextEncoder().encode('exo-box-auth/1'), ...id, ...challenge])
  const signature = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key.privateKey, signed))
  const s = BigInt('0x' + hex(signature.subarray(32)))
  if (s > ORDER >> 1n) signature.set(twinS(signature).subarray(32), 32)
  return new Uint8Array([AUTH, ...key.raw, ...signature])
}

/** (r, n - s): verifies for the same key and message, so only the low one of the two is taken. */
function twinS(signature) {
  const s = ORDER - BigInt('0x' + hex(signature.subarray(32, 64)))
  const out = new Uint8Array(signature)
  out.set(Uint8Array.from(s.toString(16).padStart(64, '0').match(/../g).map(h => parseInt(h, 16))), 32)
  return out
}

async function login(base, key) {
  const s = await open(`${base}/v1/box/${key.id}`)
  const challenge = await s.next()
  s.ws.send(await auth(key, challenge.body))
  const ready = await s.next()
  assert.equal(ready.kind, READY)
  return { ...s, ready: { first: ready.first, last: ready.last } }
}

/** The status an upgrade to url gets, without keeping the connection. */
function status(url) {
  const { hostname, port, pathname } = new URL(url)
  return new Promise((resolve, reject) => {
    const request = httpGet({ hostname, port, path: pathname, headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Key': 'dGhlIH' + 'NhbXBs' + 'ZSBub2' + '5jZQ==', 'Sec-WebSocket-Version': '13' } })
    request.on('response', response => { response.resume(); resolve(response.statusCode) })
    request.on('upgrade', (_, socket) => { socket.destroy(); resolve(101) })
    request.on('error', reject)
  })
}

function parse(data) {
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength)
  switch (data[0]) {
    case CHALLENGE: return { kind: CHALLENGE, body: data.slice(1) }
    case READY: return { kind: READY, first: view.getUint32(1), last: view.getUint32(5) }
    case MESSAGE: return { kind: MESSAGE, seq: view.getUint32(1), at: view.getUint32(5), blob: data.slice(9) }
    case STORED: return { kind: STORED, seq: view.getUint32(1) }
    case CAUGHT_UP: return { kind: CAUGHT_UP, last: view.getUint32(1), more: data[5] === 1 }
    case NAMEPLATE: return { kind: NAMEPLATE, nameplate: new TextDecoder().decode(data.slice(1)) }
    default: return { kind: data[0], body: data.slice(1) }
  }
}

/** A connection with its frames queued: next() is the next one parsed, raw() as bytes (2 s at most), closed its code. */
function open(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url)
    ws.binaryType = 'arraybuffer'
    const frames = []
    const waiting = []
    let closedWith
    const closed = new Promise((done, fail) => {
      closedWith = done
      setTimeout(() => fail(new Error('not closed')), 5000).unref()
    })
    closed.catch(() => {})
    ws.onmessage = event => {
      const data = new Uint8Array(event.data)
      const waiter = waiting.shift()
      if (waiter) waiter(data)
      else frames.push(data)
    }
    ws.onclose = event => closedWith(event.code)
    ws.onerror = () => reject(new Error('refused'))
    const raw = () => frames.length ? Promise.resolve(frames.shift()) : new Promise((done, fail) => {
      const timer = setTimeout(() => fail(new Error('no frame')), 2000)
      waiting.push(data => { clearTimeout(timer); done(data) })
    })
    ws.onopen = () => resolve({ ws, closed, frames, raw, next: async () => parse(await raw()) })
  })
}

/**
 * The server closed s: its close frame came (CLOSING) or the connection is gone (CLOSED). Under workerd a close sent to
 * the far side of an event can keep its TCP connection open for a while, so this doesn't wait for that.
 */
async function ended(s) {
  for (let i = 0; i < 100 && s.ws.readyState < 2; i++) await delay(20)
  assert.ok(s.ws.readyState >= 2, `still open (${s.ws.readyState})`)
}

/** Nothing more comes to s for a moment. */
async function quiet(s) {
  await delay(150)
  assert.deepEqual(s.frames, [])
}

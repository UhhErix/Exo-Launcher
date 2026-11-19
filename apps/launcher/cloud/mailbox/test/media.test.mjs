/**
 * The profile's media parts in R2 (src/media.js), through the local runtime over real HTTP. How it could fail:
 * someone without a box's key reads, writes or deletes its parts (another box's key, a stale time, a signature over
 * another body or method, the high-s twin of a good one); a part comes back changed; the size or the box's quota
 * doesn't hold; a Worker without its bucket throws instead of saying so.
 *   node --test cloud/mailbox/test
 */
import assert from 'node:assert/strict'
import { createHash, generateKeyPairSync, sign } from 'node:crypto'
import { after, before, test } from 'node:test'
import { bindings, buckets, ratelimits } from '../local/config.mjs'
import { serve } from '../local/runtime.mjs'
import { limits } from '../src/limits.js'

const worker = await import('../src/worker.js')
const ORDER = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551n
let mailbox, base
before(async () => {
  mailbox = await serve(worker, { bindings, ratelimits, buckets })
  base = `http://127.0.0.1:${mailbox.port}`
})
after(() => mailbox?.close())

const b64url = bytes => Buffer.from(bytes).toString('base64url')
const sha = bytes => createHash('sha256').update(bytes).digest()

function boxKey() {
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' })
  const jwk = publicKey.export({ format: 'jwk' })
  const pub = Buffer.concat([Buffer.from([4]), Buffer.from(jwk.x, 'base64url'), Buffer.from(jwk.y, 'base64url')])
  return { privateKey, pub, box: sha(pub).subarray(0, 16).toString('hex') }
}

/** A low-s signature; `high` gives its high-s twin instead. */
function signed(key, method, box, name, time, body, high = false) {
  const message = Buffer.concat([Buffer.from('exo-media/1'), Buffer.from(method), Buffer.from(box, 'hex'), Buffer.from(name), Buffer.from(String(time)), sha(body)])
  const sig = sign('sha256', message, { key: key.privateKey, dsaEncoding: 'ieee-p1363' })
  let s = BigInt('0x' + sig.subarray(32).toString('hex'))
  if ((s > ORDER / 2n) !== high) s = ORDER - s
  return Buffer.concat([sig.subarray(0, 32), Buffer.from(s.toString(16).padStart(64, '0'), 'hex')])
}

async function call(method, key, name, body = Buffer.alloc(0), { box = key.box, time = Math.floor(Date.now() / 1000), signer = key, high = false, signedBody = body } = {}) {
  return fetch(`${base}/v1/media/${box}/${name}`, {
    method,
    headers: { 'x-exo-key': b64url(signer.pub), 'x-exo-time': String(time), 'x-exo-sig': b64url(signed(signer, method, box, name, time, signedBody, high)) },
    body: method === 'PUT' ? body : undefined,
  })
}

const name = (n = 1) => n.toString(16).padStart(64, '0')

test('a part put with the box key comes back as it went, and goes when deleted', async () => {
  const key = boxKey()
  const body = Buffer.from(Array.from({ length: 70_000 }, (_, i) => i % 251))
  assert.equal((await call('PUT', key, name(1), body)).status, 201)
  const got = await call('GET', key, name(1))
  assert.equal(got.status, 200)
  assert.deepEqual(Buffer.from(await got.arrayBuffer()), body)
  assert.equal((await call('DELETE', key, name(1))).status, 204)
  assert.equal((await call('GET', key, name(1))).status, 404)
})

test('only the box key reaches its parts, fresh and over what it sends', async () => {
  const key = boxKey()
  const other = boxKey()
  const body = Buffer.from('ciphertext')
  assert.equal((await call('PUT', key, name(2), body)).status, 201)
  assert.equal((await call('GET', key, name(2), Buffer.alloc(0), { signer: other })).status, 401, 'another box key')
  assert.equal((await call('GET', key, name(2), Buffer.alloc(0), { time: Math.floor(Date.now() / 1000) - 400 })).status, 401, 'a stale time')
  assert.equal((await call('PUT', key, name(2), Buffer.from('changed'), { signedBody: body })).status, 401, 'a signature over another body')
  assert.equal((await call('GET', key, name(2), Buffer.alloc(0), { high: true })).status, 401, 'the high-s twin')
  assert.equal((await call('DELETE', other, name(2), Buffer.alloc(0), { box: key.box })).status, 401, 'another key deleting')
  assert.equal((await call('GET', key, name(2))).status, 200)
})

test('a part is at most its size, and a box at most its quota', async () => {
  const key = boxKey()
  const saved = { ...limits }
  try {
    limits.mediaPart = 1000
    limits.mediaBox = 2500
    assert.equal((await call('PUT', key, name(3), Buffer.alloc(1001))).status, 413)
    assert.equal((await call('PUT', key, name(3), Buffer.alloc(1000))).status, 201)
    assert.equal((await call('PUT', key, name(4), Buffer.alloc(1000))).status, 201)
    assert.equal((await call('PUT', key, name(5), Buffer.alloc(1000))).status, 507)
  } finally {
    Object.assign(limits, saved)
  }
})

test('a Worker with no MEDIA bucket answers a signed request 503, not an error', async () => {
  const key = boxKey()
  const time = Math.floor(Date.now() / 1000)
  for (const method of ['PUT', 'GET', 'DELETE']) {
    const body = method === 'PUT' ? Buffer.from('x') : Buffer.alloc(0)
    const request = new Request(`http://mailbox/v1/media/${key.box}/${name(1)}`, {
      method,
      headers: { 'x-exo-key': b64url(key.pub), 'x-exo-time': String(time), 'x-exo-sig': b64url(signed(key, method, key.box, name(1), time, body)) },
      body: method === 'PUT' ? body : undefined,
    })
    assert.equal((await worker.default.fetch(request, {})).status, 503, method)
  }
})

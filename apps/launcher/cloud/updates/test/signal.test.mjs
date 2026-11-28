import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import worker from '../src/worker.js'
import { SAMPLE_EVERY, classOf, counted, record, routeIn } from '../src/signal.js'

const sink = () => {
  const sent = []
  return { sent, SIGNAL: { writeDataPoint: (point) => sent.push(point) } }
}

const randomIs = async (value, run) => {
  const real = Math.random
  Math.random = () => value
  try {
    return await run()
  } finally {
    Math.random = real
  }
}

test('a status becomes its class, and anything that is not a status becomes other', () => {
  assert.deepEqual([200, 206, 304, 404, 429, 500, 503].map(classOf), ['2xx', '2xx', '3xx', '4xx', '4xx', '5xx', '5xx'])
  assert.deepEqual([0, 99, 600, 200.5, NaN, undefined, '200'].map(classOf), Array(7).fill('other'))
})

test('a route is the first name whose pattern matches, and any other path is other, never the path itself', () => {
  const routeOf = routeIn([['latest', /^\/v1\/latest$/], ['cache', /^\/v1\/cache\//]])
  assert.equal(routeOf('/v1/latest'), 'latest')
  assert.equal(routeOf('/v1/cache/a/b/c'), 'cache')
  assert.equal(routeOf('/wp-login.php?x=1'), 'other')
  assert.equal(routeOf('/v1/latest/extra'), 'other')
})

test('every 5xx is written, with the weight 1 and only the Worker, route and class', () => {
  const env = sink()
  randomIs(0.999, () => record(env, 'updates', 'latest', 503))
  assert.deepEqual(env.sent, [{ blobs: ['updates', 'latest', '5xx'], doubles: [1], indexes: ['updates'] }])
})

test('successes and client errors are sampled, each point carrying the weight it stands for', () => {
  assert.deepEqual(SAMPLE_EVERY, { '2xx': 100, '3xx': 100, '4xx': 5 })
  const env = sink()
  randomIs(0, () => {
    record(env, 'w', 'r', 200)
    record(env, 'w', 'r', 304)
    record(env, 'w', 'r', 404)
  })
  assert.deepEqual(env.sent.map((point) => [point.blobs[2], point.doubles[0]]), [['2xx', 100], ['3xx', 100], ['4xx', 5]])
  const skipped = sink()
  randomIs(0.5, () => {
    record(skipped, 'w', 'r', 200)
    record(skipped, 'w', 'r', 404)
  })
  assert.deepEqual(skipped.sent, [])
})

test('the wrapper returns the answer untouched and counts its route and status', async () => {
  const env = sink()
  const answer = new Response('body', { status: 500 })
  const wrapped = counted('w', [['latest', /^\/latest$/]], { fetch: async () => answer, scheduled: async () => 'ran' })
  const response = await wrapped.fetch(new Request('https://x.test/latest?token=secret', { headers: { 'CF-Connecting-IP': '203.0.113.9', 'user-agent': 'Exo/3.31.4' } }), env, {})
  assert.equal(response, answer)
  assert.equal(await wrapped.scheduled(), 'ran')
  assert.deepEqual(env.sent, [{ blobs: ['w', 'latest', '5xx'], doubles: [1], indexes: ['w'] }])
  const written = JSON.stringify(env.sent)
  for (const secret of ['203.0.113.9', 'Exo/3.31.4', 'token', 'secret']) assert.equal(written.includes(secret), false, secret)
})

test('a Worker that throws is counted as 5xx and the error still reaches the caller', async () => {
  const env = sink()
  const wrapped = counted('w', [], { fetch: async () => { throw new Error('boom') } })
  await assert.rejects(wrapped.fetch(new Request('https://x.test/anything'), env, {}), /boom/)
  assert.deepEqual(env.sent, [{ blobs: ['w', 'other', '5xx'], doubles: [1], indexes: ['w'] }])
})

test('a missing or failing counter never changes the answer', async () => {
  const wrapped = counted('w', [], { fetch: async () => new Response('ok') })
  assert.equal(await (await wrapped.fetch(new Request('https://x.test/'), {}, {})).text(), 'ok')
  const broken = { SIGNAL: { writeDataPoint: () => { throw new Error('quota') } } }
  assert.equal(await (await wrapped.fetch(new Request('https://x.test/'), broken, {})).text(), 'ok')
})

test('the four Workers carry the same signal.js', () => {
  const copy = (name) => readFileSync(new URL(`../../${name}/src/signal.js`, import.meta.url), 'utf8').replace(/\r\n/g, '\n')
  for (const name of ['reports', 'covers', 'riot']) assert.equal(copy(name), copy('updates'), name)
})

test('the update address counts its routes: the cache, health and any other path', async () => {
  const env = { ...sink(), MIRROR: undefined }
  const real = Math.random
  Math.random = () => 0
  try {
    await worker.fetch(new Request('https://updates.test/v1/cache/a/b/c'), env, {})
    await worker.fetch(new Request('https://updates.test/health'), env, {})
    await worker.fetch(new Request('https://updates.test/wp-admin/'), env, {})
  } finally {
    Math.random = real
  }
  assert.deepEqual(env.sent.map((point) => point.blobs), [['updates', 'cache', '5xx'], ['updates', 'health', '5xx'], ['updates', 'other', '4xx']])
})

test('/health answers 200 when the bucket is bound, 503 when it is not, and is never kept', async () => {
  const bound = await worker.fetch(new Request('https://updates.test/health'), { MIRROR: {} }, {})
  assert.equal(bound.status, 200)
  assert.deepEqual(await bound.json(), { ok: true })
  assert.equal(bound.headers.get('cache-control'), 'no-store')
  const unbound = await worker.fetch(new Request('https://updates.test/health'), {}, {})
  assert.equal(unbound.status, 503)
  assert.deepEqual(await unbound.json(), { ok: false })
})

import assert from 'node:assert/strict'
import { test } from 'node:test'
import worker from '../src/worker.js'
import { TARGETS, selfTest } from '../src/selftest.js'

const sink = () => {
  const sent = []
  return { sent, SIGNAL: { writeDataPoint: (point) => sent.push(point) } }
}

test('the self-test asks the health address of each Worker and writes one point each with the class of the answer', async () => {
  const env = sink()
  const asked = []
  const answers = { updates: 200, reports: 503, covers: 429, riot: 200 }
  const fetchImpl = async (url, init) => {
    asked.push([url, init.redirect, init.headers['user-agent']])
    const name = TARGETS.find(([, target]) => target === url)[0]
    return new Response(null, { status: answers[name] })
  }
  await selfTest(env, fetchImpl)
  assert.deepEqual(asked.map(([url]) => url), [
    'https://updates.getexo.app/health',
    'https://reports.getexo.app/health',
    'https://covers.getexo.app/health',
    'https://riot.getexo.app/health',
  ])
  assert.deepEqual(env.sent.map((point) => point.blobs), [
    ['selftest', 'updates', '2xx'], ['selftest', 'reports', '5xx'], ['selftest', 'covers', '4xx'], ['selftest', 'riot', '2xx'],
  ])
  assert.deepEqual(env.sent.map((point) => point.doubles), Array(4).fill([1]))
})

test('a Worker that does not answer is written as err and the others are still asked', async () => {
  const env = sink()
  let calls = 0
  await selfTest(env, async () => {
    calls++
    if (calls === 2) throw new TypeError('network')
    return new Response(null, { status: 200 })
  })
  assert.equal(calls, 4)
  assert.deepEqual(env.sent.map((point) => point.blobs[2]), ['2xx', 'err', '2xx', '2xx'])
})

test('without a counter binding nothing is asked', async () => {
  await selfTest({}, async () => {
    throw new Error('asked')
  })
})

test('/health answers 200 when the bucket and the database are bound, 503 when not, and refuses other methods', async () => {
  const bound = await worker.fetch(new Request('https://reports.test/health'), { REPORTS: {}, TELEMETRY: {} }, {})
  assert.equal(bound.status, 200)
  assert.deepEqual(await bound.json(), { ok: true })
  assert.equal(bound.headers.get('cache-control'), 'no-store')
  assert.equal((await worker.fetch(new Request('https://reports.test/health'), { REPORTS: {} }, {})).status, 503)
  assert.equal((await worker.fetch(new Request('https://reports.test/health', { method: 'POST' }), { REPORTS: {}, TELEMETRY: {} }, {})).status, 405)
})

test('the reports Worker counts its routes by name', async () => {
  const env = sink()
  const real = Math.random
  Math.random = () => 0
  try {
    await worker.fetch(new Request('https://reports.test/v1/report'), env, {})
    await worker.fetch(new Request('https://reports.test/v1/telemetry', { method: 'GET' }), env, {})
    await worker.fetch(new Request('https://reports.test/v1/secret-probe?install=abc'), env, {})
  } finally {
    Math.random = real
  }
  assert.deepEqual(env.sent.map((point) => point.blobs), [['reports', 'report', '4xx'], ['reports', 'telemetry', '4xx'], ['reports', 'other', '4xx']])
  assert.equal(JSON.stringify(env.sent).includes('abc'), false)
})

test('the daily job runs the self-test even when GitHub is set up', async () => {
  const env = { ...sink(), TELEMETRY: { prepare: () => ({ bind: () => ({ run: async () => ({}), first: async () => null, all: async () => ({ results: [] }) }), run: async () => ({}), first: async () => null, all: async () => ({ results: [] }) }), batch: async () => [] } }
  const real = globalThis.fetch
  globalThis.fetch = async () => new Response(null, { status: 200 })
  try {
    await worker.scheduled({}, env)
  } finally {
    globalThis.fetch = real
  }
  assert.deepEqual(env.sent.filter((point) => point.blobs[0] === 'selftest').map((point) => point.blobs[1]), ['updates', 'reports', 'covers', 'riot'])
})

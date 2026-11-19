/**
 * Admission to the caps on a PUT (README.md › Limits): only growth is refused. An edit that shrinks an account goes through
 * even on an account over a cap, a write that will lose (its base is older than the stored version) counts for nothing,
 * and a second PUT for the same account while one is in flight in this isolate is answered 503 busy.
 *
 * How it could fail: a shrinking edit refused on an account over its cap, so it can never get back under; a losing write
 * refused as if it landed; two PUTs for one account landing on one counter read.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { limits } from '../src/limits.js'
import { account, call, resetClock } from './harness.mjs'

const EMAIL = 'admit@example.com'
const PASSWORD = 'correct horse battery'
const DEVICE = '0123456789abcdef0123456789abcdef'

afterEach(resetClock)

async function signedUp(env) {
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  return { token: made.body.token, id: made.body.account.id }
}
const put = (env, token, items) => call(env, 'PUT', '/v1/sync', { token, body: { items } })
const insert = (env, id, app, type, itemId, version, value) => env.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, ?, 0, 0, ?)')
  .run(id, app, type, itemId, version, value)

test('an edit that shrinks an account over its byte cap goes through', async () => {
  const env = account()
  const { token, id } = await signedUp(env)
  insert(env, id, 'cinema', 'history', 'huge', 1, new Uint8Array(limits.accountBytes + 2000))
  insert(env, id, 'cinema', 'history', 'other', 2, new Uint8Array(limits.accountBytes))
  const shrink = await put(env, token, [{ app: 'cinema', type: 'history', id: 'huge', value: '{}', base: 1 }])
  assert.equal(shrink.status, 200)
  assert.equal(shrink.body.written.length, 1)
  assert.equal(shrink.body.conflicts.length, 0)
})

test('a write that will lose is not counted: it can\'t be refused for bytes it would never add', async () => {
  const env = account()
  const { token, id } = await signedUp(env)
  // 'small' is written by a real PUT, so its stored value is sealed and the conflict can read it back.
  const small = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'small', value: '{}', base: 0 }])
  insert(env, id, 'launcher', 'settings', 'big', 1000, new Uint8Array(limits.accountBytes - 100))
  const losing = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'small', value: 'a'.repeat(60000), base: 0 }])
  assert.equal(small.body.written.length, 1)
  assert.equal(losing.status, 200)
  assert.equal(losing.body.written.length, 0)
  assert.equal(losing.body.conflicts.length, 1, 'the stored value comes back as the conflict')
  const growing = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'fresh', value: 'a'.repeat(60000), base: 0 }])
  assert.equal(growing.body.error, 'account-full', 'a write that lands and grows is still refused')
})

test('a second PUT for the same account while one is in flight is answered 503 busy with Retry-After: 1', async () => {
  const env = account()
  const { token } = await signedUp(env)
  let open
  const gate = new Promise(resolve => { open = resolve })
  let held = 0
  // The first PUT stops at its read of the stored items, until the gate opens.
  const realBatch = env.DB.batch.bind(env.DB)
  env.DB.batch = async statements => {
    if (statements.length && statements[0].sql.includes('SELECT version, length(value)')) {
      held++
      await gate
    }
    return realBatch(statements)
  }
  const first = put(env, token, [{ app: 'launcher', type: 'settings', id: 'a', value: '{}', base: 0 }])
  while (held < 1) await new Promise(resolve => setImmediate(resolve))
  const second = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'b', value: '{}', base: 0 }])
  assert.equal(second.status, 503)
  assert.equal(second.body.error, 'busy')
  open()
  assert.equal((await first).status, 200)
  assert.equal((await put(env, token, [{ app: 'launcher', type: 'settings', id: 'c', value: '{}', base: 0 }])).status, 200, 'the guard is given back')
})

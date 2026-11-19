/**
 * The per-account caps (README.md › Limits): the items an account holds and the sealed bytes they take. The account row
 * keeps both as counters, which triggers on items move with each write (migration 0005), so a PUT checks the caps without
 * counting the rows. An edit or a tombstone that makes an account smaller always goes through.
 *
 * How the caps could fail: an edit, a tombstone or a delete refused at the cap, so a full account can't shrink; a new item
 * let in past the cap; the counters drifting from the rows after a write, an edit, a tombstone or a delete.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { limits } from '../src/limits.js'
import { account, call, resetClock } from './harness.mjs'

const EMAIL = 'caps@example.com'
const PASSWORD = 'correct horse battery'
const DEVICE = '0123456789abcdef0123456789abcdef'

afterEach(resetClock)

async function signedUp(env) {
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  return { token: made.body.token, id: made.body.account.id }
}
const put = (env, token, items) => call(env, 'PUT', '/v1/sync', { token, body: { items } })

/** Adds tombstones (no value, so no bytes) until the account holds count items: the rows are written directly. */
function fill(env, id, count) {
  const row = env.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, ?, 0, 0, ?)')
  for (let i = 0; i < count; i++) row.run(id, 'launcher', 'settings', `filler-${i}`, i + 1, null)
}

/** The counters the caps read, and what the rows add up to: they must agree. */
function counters(env, id) {
  const held = env.DB.db.prepare('SELECT item_count, item_bytes FROM accounts WHERE id = ?').get(id)
  const rows = env.DB.db.prepare('SELECT count(*) AS n, COALESCE(SUM(length(value)), 0) AS bytes FROM items WHERE account = ?').get(id)
  return { held: [held.item_count, held.item_bytes], rows: [rows.n, rows.bytes] }
}

test('at the item cap: an edit and a tombstone of an item the account holds go through, and a new item is refused', async () => {
  const env = account()
  const { token, id } = await signedUp(env)
  fill(env, id, limits.accountItems - 1)
  const made = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'theme', value: '{"dark":true}', base: 0 }])
  assert.equal(made.status, 200)
  assert.equal(counters(env, id).held[0], limits.accountItems, 'exactly at the cap')
  const edit = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'theme', value: '{"dark":false}', base: made.body.written[0].version }])
  assert.equal(edit.status, 200)
  assert.equal(edit.body.written.length, 1)
  const gone = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'theme', deleted: true, base: edit.body.written[0].version }])
  assert.equal(gone.status, 200)
  assert.equal(gone.body.written.length, 1)
  const fresh = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'one-more', value: '{}', base: 0 }])
  assert.equal(fresh.status, 413)
  assert.equal(fresh.body.error, 'account-full')
})

test('at the byte cap: an edit to a smaller value goes through, and a new item past the cap is refused', async () => {
  const env = account()
  const { token, id } = await signedUp(env)
  env.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, 1, 0, 0, ?)')
    .run(id, 'cinema', 'history', 'big', new Uint8Array(limits.accountBytes))
  assert.deepEqual(counters(env, id).held, [1, limits.accountBytes])
  const refused = await put(env, token, [{ app: 'cinema', type: 'history', id: 'tiny', value: '{}', base: 0 }])
  assert.equal(refused.body.error, 'account-full')
  const shrink = await put(env, token, [{ app: 'cinema', type: 'history', id: 'big', value: '{}', base: 1 }])
  assert.equal(shrink.status, 200)
  assert.equal(shrink.body.written.length, 1)
  assert.equal((await put(env, token, [{ app: 'cinema', type: 'history', id: 'tiny', value: '{}', base: 0 }])).status, 200)
})

test('the counters on the account row match its rows after every kind of write, and a stale write changes neither', async () => {
  const env = account()
  const { token, id } = await signedUp(env)
  const agree = () => {
    const { held, rows } = counters(env, id)
    assert.deepEqual(held, rows)
  }
  const first = await put(env, token, [
    { app: 'launcher', type: 'settings', id: 'a', value: '{"x":1}', base: 0 },
    { app: 'launcher', type: 'settings', id: 'b', value: '{"y":22}', base: 0 },
  ])
  agree()
  const grown = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'a', value: '{"x":333333}', base: first.body.written[0].version }])
  assert.equal(grown.status, 200)
  agree()
  await put(env, token, [{ app: 'launcher', type: 'settings', id: 'b', deleted: true, base: first.body.written[1].version }])
  agree()
  const stale = await put(env, token, [{ app: 'launcher', type: 'settings', id: 'a', value: '{}', base: 0 }])
  assert.equal(stale.body.written.length, 0)
  agree()
  assert.equal(counters(env, id).held[0], 2, 'a tombstone keeps its row')
})

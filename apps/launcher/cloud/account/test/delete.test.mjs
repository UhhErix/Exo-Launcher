/**
 * Deleting a full account (README.md › Limits): its items go in chunks of 1,000 rows, each its own statement, before the
 * account row goes, so a 20,000-item account can't outgrow D1's per-batch limits and abort half way.
 *
 * How it could fail: one statement over every item of a full account is cut short by D1, leaving rows and a password that
 * still signs in; the chunks stop early and the account keeps items.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { limits } from '../src/limits.js'
import { account, call, resetClock } from './harness.mjs'

const EMAIL = 'full@example.com'
const PASSWORD = 'correct horse battery'
const DEVICE = '0123456789abcdef0123456789abcdef'

afterEach(resetClock)

test('a full account (20,000 items) is deleted in chunks of 1,000, and its password stops working', async () => {
  const env = account()
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const id = made.body.account.id
  const row = env.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, ?, 0, 0, ?)')
  for (let i = 0; i < limits.accountItems; i++) row.run(id, 'launcher', 'settings', `item-${i}`, i + 1, null)
  const prepare = env.DB.prepare.bind(env.DB)
  const chunks = []
  env.DB.prepare = sql => {
    const statement = prepare(sql)
    if (!sql.includes('rowid IN')) return statement
    return {
      bind: (...args) => {
        const bound = statement.bind(...args)
        return {
          run: async () => {
            const result = await bound.run()
            chunks.push(result.meta.changes)
            return result
          },
        }
      },
    }
  }
  const gone = await call(env, 'DELETE', '/v1/account', { token: made.body.token, body: { password: PASSWORD } })
  assert.equal(gone.status, 204)
  assert.equal(env.DB.db.prepare('SELECT count(*) AS n FROM items WHERE account = ?').get(id).n, 0)
  assert.equal(env.DB.rows('accounts').length, 0)
  assert.deepEqual(chunks, [...Array(limits.accountItems / 1000).fill(1000), 0], 'twenty chunks of 1,000, then an empty pass')
  assert.equal((await call(env, 'POST', '/v1/session', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })).body.error, 'wrong-credentials')
})

test('a PUT that lands after the chunked delete doesn\'t fail the account\'s delete: its items go with the account', async () => {
  const env = account()
  const made = await call(env, 'POST', '/v1/account', { body: { email: EMAIL, password: PASSWORD, device: DEVICE } })
  const id = made.body.account.id
  const realBatch = env.DB.batch.bind(env.DB)
  let raced = false
  env.DB.batch = async statements => {
    // Just before the final batch, a PUT that the chunks did not see lands (the race the final batch must absorb).
    if (!raced && statements[0]?.sql?.startsWith('DELETE FROM items WHERE account')) {
      raced = true
      env.DB.db.prepare('INSERT INTO items (account, app, type, id, version, updated, written, value) VALUES (?, ?, ?, ?, 1, 0, 0, NULL)').run(id, 'launcher', 'settings', 'late')
    }
    return realBatch(statements)
  }
  const gone = await call(env, 'DELETE', '/v1/account', { token: made.body.token, body: { password: PASSWORD } })
  assert.equal(gone.status, 204)
  assert.ok(raced)
  assert.equal(env.DB.db.prepare('SELECT count(*) AS n FROM items WHERE account = ?').get(id).n, 0)
})

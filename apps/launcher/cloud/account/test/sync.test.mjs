/**
 * Item sync (README.md › Sync): the Worker over the D1 stand-in. The server orders the writes. Each write lands with the
 * account's next version, and only when the item's stored version is no newer than the version the client last saw;
 * a stale write to the same item loses and is answered with the current value. A device's clock is metadata: it never
 * decides an order.
 *
 * How sync could fail: a write on a stale view of an item overwrites a newer one; a stale write to a different item is
 * refused; a device whose clock is a day behind wins or loses by its clock; a deletion that doesn't stick, or an old
 * deletion that wipes a newer value; a log entry from one PC dropped by another's; an app or type the server must know
 * first (it must not); a pull that skips or repeats changes around its cursor; an item readable in the database, or
 * readable once moved to another item; an account reading another's items; a value or batch of any size; an export
 * that can't be paged.
 *   node --test cloud/account/test
 */
import assert from 'node:assert/strict'
import { afterEach, test } from 'node:test'
import { limits } from '../src/limits.js'
import { account, call, resetClock } from './harness.mjs'

const EMAIL = 'sync@example.com'
const PASSWORD = 'correct horse battery'
const DEVICE = '0123456789abcdef0123456789abcdef'
const OTHER = 'fedcba9876543210fedcba9876543210'
const DAY_MS = 24 * 60 * 60 * 1000

afterEach(resetClock)

async function setup(env, email = EMAIL) {
  const made = await call(env, 'POST', '/v1/account', { body: { email, password: PASSWORD, device: DEVICE } })
  const laptop = await call(env, 'POST', '/v1/session', { body: { email, password: PASSWORD, device: OTHER } })
  return { phone: made.body.token, laptop: laptop.body.token }
}

const put = (env, token, items) => call(env, 'PUT', '/v1/sync', { token, body: { items } })
const pull = (env, token, after = 0) => call(env, 'GET', `/v1/sync?after=${after}`, { token })
/** An item to write: base is the version the writer last saw of it (0 for none), updated is its own clock. */
const item = (app, type, id, value, base = 0, updated = Date.now()) => ({ app, type, id, value, base, updated })
const gone = (app, type, id, base = 0) => ({ app, type, id, deleted: true, base })
/** The version a pull shows for an item (0 when it has none): the base the next write on it must carry. */
const versionOf = (items, id) => items.find(i => i.id === id)?.version ?? 0

test('the server orders the writes: a write on the version it saw lands; a write on an older version loses and gets the current value back', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  const first = await put(env, phone, [item('launcher', 'settings', 'theme', '{"dark":true}')])
  const v1 = first.body.written[0].version
  assert.ok(v1 > 0)
  const stale = await put(env, laptop, [item('launcher', 'settings', 'theme', '{"dark":false}', 0)])
  assert.deepEqual(stale.body.written, [])
  assert.deepEqual(stale.body.conflicts.map(c => [c.id, c.version, c.value]), [['theme', v1, '{"dark":true}']])
  const onTop = await put(env, laptop, [item('launcher', 'settings', 'theme', '{"dark":false}', v1)])
  assert.equal(onTop.body.written[0].version > v1, true)
  assert.deepEqual(onTop.body.conflicts, [])
  const v2 = onTop.body.written[0].version
  const late = await put(env, phone, [item('launcher', 'settings', 'theme', '{"dark":"late"}', v1)])
  assert.deepEqual(late.body.conflicts.map(c => [c.version, c.value]), [[v2, '{"dark":false}']])
  assert.equal((await pull(env, phone)).body.items[0].value, '{"dark":false}')
})

test('a stale write to a different item still lands: the check is per item', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  await put(env, phone, [item('launcher', 'settings', 'a', '{"x":1}'), item('launcher', 'settings', 'b', '{"y":1}')])
  const { body } = await put(env, laptop, [item('launcher', 'settings', 'a', '{"x":2}', 0), item('launcher', 'settings', 'c', '{"z":1}', 0)])
  assert.deepEqual(body.written.map(w => w.id), ['c'])
  assert.deepEqual(body.conflicts.map(c => c.id), ['a'])
})

test('a device whose clock is a day behind cannot take a write that has seen the newer one, and a day ahead is no problem either', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  const now = Date.now()
  await put(env, phone, [item('cinema', 'history', 'tt1', '{"pos":10}', 0, now)])
  const seen = versionOf((await pull(env, laptop)).body.items, 'tt1')
  // The laptop's clock says yesterday. Its write is on the version it saw, so it lands, and the clock is kept as metadata.
  const behind = await put(env, laptop, [item('cinema', 'history', 'tt1', '{"pos":20}', seen, now - DAY_MS)])
  assert.equal(behind.body.written.length, 1)
  const pulled = (await pull(env, phone)).body.items[0]
  assert.equal(pulled.value, '{"pos":20}')
  assert.equal(pulled.updated, now - DAY_MS)
  // The same clock with no base: it saw nothing newer, so the server answers with the current value instead.
  const stale = await put(env, laptop, [item('cinema', 'history', 'tt1', '{"pos":5}', 0, now - 2 * DAY_MS)])
  assert.deepEqual(stale.body.conflicts.map(c => c.value), ['{"pos":20}'])
  // A phone whose clock is a day ahead writes on its base: accepted, nothing refused for its time.
  const ahead = await put(env, phone, [item('cinema', 'history', 'tt1', '{"pos":30}', pulled.version, now + DAY_MS)])
  assert.equal(ahead.status, 200)
  assert.equal(ahead.body.written.length, 1)
})

test('an app or type the server has never heard of syncs the same way: no server change for a new item type', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  await put(env, phone, [item('cinema', 'watch-history', 'tt0111161', '{"pos":12}'), item('browser', 'tabs', 'tab-1', '{"url":"x"}')])
  const { body } = await pull(env, laptop)
  assert.deepEqual(body.items.map(i => `${i.app}/${i.type}/${i.id}`).sort(), ['browser/tabs/tab-1', 'cinema/watch-history/tt0111161'])
})

test('a log type merges by entry id: two PCs append different entries and both keep them; the same entry sent again changes nothing', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  await put(env, phone, [item('launcher', 'riot-ledger', 'session-a', '{"minutes":40}')])
  await put(env, laptop, [item('launcher', 'riot-ledger', 'session-b', '{"minutes":25}')])
  const again = await put(env, laptop, [item('launcher', 'riot-ledger', 'session-a', '{"minutes":40}')])
  assert.deepEqual(again.body.written, [])
  assert.equal(again.body.conflicts[0].value, '{"minutes":40}')
  const first = await pull(env, phone)
  assert.deepEqual(first.body.items.map(i => i.id), ['session-a', 'session-b'])
  assert.deepEqual((await pull(env, phone, first.body.cursor)).body.items, [])
})

test('deletions are tombstones with a version: a current deletion removes the item, a stale one loses, a later write brings it back', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  await put(env, phone, [item('launcher', 'collections', 'c1', '{"name":"RPG"}')])
  const v1 = versionOf((await pull(env, laptop)).body.items, 'c1')
  const stale = await put(env, laptop, [gone('launcher', 'collections', 'c1', 0)])
  assert.equal(stale.body.conflicts[0].value, '{"name":"RPG"}')
  assert.equal((await put(env, laptop, [gone('launcher', 'collections', 'c1', v1)])).body.written.length, 1)
  const { body } = await pull(env, phone)
  assert.equal(body.items[0].value, null)
  const v2 = body.items[0].version
  assert.equal((await put(env, phone, [item('launcher', 'collections', 'c1', '{"name":"back"}', v2)])).body.written.length, 1)
  assert.equal((await pull(env, laptop)).body.items[0].value, '{"name":"back"}')
})

test('pulls are in version order: a cursor returns only what changed after it; a page of 500 says more, and the rest follows', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  let firstWritten
  for (let batch = 0; batch < 6; batch++) {
    const items = Array.from({ length: 100 }, (_, i) => item('launcher', 'riot-ledger', `e-${batch}-${i}`, '{}'))
    const { body } = await put(env, phone, items)
    firstWritten ??= body.written[0]
  }
  const page = await pull(env, laptop)
  assert.equal(page.body.items.length, 500)
  assert.equal(page.body.more, true)
  const rest = await pull(env, laptop, page.body.cursor)
  assert.equal(rest.body.items.length, 100)
  assert.equal(rest.body.more, false)
  await put(env, phone, [item('launcher', 'riot-ledger', 'e-0-0', '{"edited":true}', firstWritten.version)])
  const later = await pull(env, laptop, rest.body.cursor)
  assert.deepEqual(later.body.items.map(i => [i.id, i.value]), [['e-0-0', '{"edited":true}']])
  assert.equal((await pull(env, laptop, later.body.cursor)).body.cursor, later.body.cursor)
})

test('pulls and exports page by bytes as well as count: 100 items of 64 KiB stop well short of 500, and every item still comes', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  const big = 'x'.repeat(64 * 1024 - 2)
  await put(env, phone, Array.from({ length: 100 }, (_, i) => item('launcher', 'riot-ledger', `b-${i}`, `"${big}"`)))
  const first = await pull(env, laptop)
  assert.ok(first.body.items.length < 500)
  assert.ok(first.body.items.reduce((sum, i) => sum + i.value.length, 0) <= limits.syncPageBytes + 64 * 1024)
  assert.equal(first.body.more, true)
  let cursor = first.body.cursor
  let seen = first.body.items.length
  for (let page = 0; page < 10; page++) {
    const next = await pull(env, laptop, cursor)
    seen += next.body.items.length
    cursor = next.body.cursor
    if (!next.body.more) break
  }
  assert.equal(seen, 100)
  const exported = await call(env, 'GET', '/v1/export?after=0', { token: laptop })
  assert.ok(exported.body.items.length < 500)
  assert.equal(exported.body.more, true)
})

test('an item deleted while an export pages shows as a tombstone on a later page, so the export never reads as if it still held it', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  for (let batch = 0; batch < 6; batch++) {
    await put(env, phone, Array.from({ length: 100 }, (_, i) => item('launcher', 'riot-ledger', `d-${batch * 100 + i}`, '{}')))
  }
  const first = await call(env, 'GET', '/v1/export?after=0', { token: laptop })
  assert.equal(first.body.items.length, 500)
  // d-550 is on the second page, which is not read yet: delete it now.
  const seen = (await pull(env, laptop, first.body.cursor)).body.items.find(i => i.id === 'd-550')
  assert.equal((await put(env, phone, [gone('launcher', 'riot-ledger', 'd-550', seen.version)])).body.written.length, 1)
  const second = await call(env, 'GET', `/v1/export?after=${first.body.cursor}`, { token: laptop })
  const tombstone = second.body.items.find(i => i.id === 'd-550')
  assert.ok(tombstone, 'the deletion is on the second page')
  assert.equal(tombstone.value, null)
  assert.equal(second.body.items.filter(i => i.value !== null).length, 99)
})

test('the 8 MiB PUT cap is measured on the body as sent: a control character takes 6 bytes, so 25 items of 64 KiB of them are too big', async () => {
  const env = account()
  const { phone } = await setup(env)
  const control = '\u0001'.repeat(64 * 1024)
  assert.equal((await put(env, phone, [item('launcher', 'settings', 'one', control)])).status, 200)
  const batch = Array.from({ length: 25 }, (_, i) => item('launcher', 'settings', `c-${i}`, control))
  const refused = await put(env, phone, batch)
  assert.equal(refused.status, 413)
  assert.equal(refused.body.error, 'too-big')
})

test('what is sent is checked: names, ids, versions, clocks, sizes, batch size, the body and the token', async () => {
  const env = account()
  const { phone } = await setup(env)
  const bad = items => put(env, phone, items)
  assert.equal((await bad([item('Launcher', 'settings', 'x', '{}')])).body.error, 'bad-item')
  assert.equal((await bad([item('launcher', 'settings', 'has space', '{}')])).body.error, 'bad-item')
  assert.equal((await bad([{ ...item('launcher', 'settings', 'x', '{}'), base: -1 }])).body.error, 'bad-item')
  assert.equal((await bad([{ ...item('launcher', 'settings', 'x', '{}'), base: 1.5 }])).body.error, 'bad-item')
  assert.equal((await bad([{ ...item('launcher', 'settings', 'x', '{}'), updated: -5 }])).body.error, 'bad-item')
  assert.equal((await bad([item('launcher', 'settings', 'x', 42)])).body.error, 'bad-item')
  assert.equal((await bad([item('launcher', 'settings', 'x', 'x'.repeat(64 * 1024 + 1))])).status, 413)
  assert.equal((await bad(Array.from({ length: 101 }, (_, i) => item('launcher', 'settings', `x${i}`, '{}')))).body.error, 'bad-request')
  assert.equal((await call(env, 'PUT', '/v1/sync', { token: phone, body: { items: 'nope' } })).body.error, 'bad-request')
  assert.equal((await call(env, 'PUT', '/v1/sync', { body: { items: [] } })).body.error, 'signed-out')
  assert.equal((await bad([item('launcher', 'settings', 'ok', 'x'.repeat(64 * 1024))])).status, 200)
})

test('items are sealed at rest, and a sealed value moved to another item does not open', async () => {
  const env = account()
  const { phone } = await setup(env)
  await put(env, phone, [item('launcher', 'settings', 'secret', '{"token":"plain-secret-text"}')])
  const [row] = env.DB.rows('items')
  assert.ok(!Buffer.from(row.value).toString('latin1').includes('plain-secret-text'))
  await env.DB.prepare("UPDATE items SET id = 'moved'").run()
  const moved = await pull(env, phone)
  assert.equal(moved.status, 500)
  assert.equal(moved.body.error, 'server-error')
  assert.ok(!JSON.stringify(moved.body).includes('plain-secret-text'))
})

test('each account sees only its own items, even with the same app, type and id', async () => {
  const env = account()
  const mine = await setup(env, 'one@example.com')
  const theirs = await setup(env, 'two@example.com')
  await put(env, mine.phone, [item('launcher', 'settings', 'theme', '{"who":"one"}')])
  await put(env, theirs.phone, [item('launcher', 'settings', 'theme', '{"who":"two"}')])
  assert.equal((await pull(env, mine.laptop)).body.items[0].value, '{"who":"one"}')
  assert.equal((await pull(env, theirs.laptop)).body.items[0].value, '{"who":"two"}')
  assert.equal((await pull(env, mine.phone)).body.items.length, 1)
})

test('export is paged: every item decrypted across pages, a deleted one as a tombstone, the account in each page', async () => {
  const env = account()
  const { phone, laptop } = await setup(env)
  let at = 0
  for (let batch = 0; batch < 6; batch++) {
    await put(env, phone, Array.from({ length: 100 }, () => item('launcher', 'riot-ledger', `e-${at++}`, `{"n":${at}}`)))
  }
  const made = await put(env, phone, [item('launcher', 'settings', 'gone', '{}')])
  await put(env, phone, [gone('launcher', 'settings', 'gone', made.body.written[0].version)])
  const first = await call(env, 'GET', '/v1/export', { token: laptop })
  assert.equal(first.status, 200)
  assert.match(first.headers.get('Content-Disposition'), /^attachment;/)
  assert.equal(first.body.items.length, 500)
  assert.equal(first.body.more, true)
  const second = await call(env, 'GET', `/v1/export?after=${first.body.cursor}`, { token: laptop })
  assert.equal(second.body.more, false)
  const all = [...first.body.items, ...second.body.items]
  assert.equal(all.length, 601)
  assert.deepEqual(all.filter(i => i.value === null).map(i => i.id), ['gone'])
  assert.equal(all[0].value, '{"n":1}')
  assert.equal(second.body.account.id, first.body.account.id)
  assert.equal((await call(env, 'GET', '/v1/export?after=x', { token: laptop })).body.error, 'bad-request')
})

test('account deletion takes the items too', async () => {
  const env = account()
  const { phone } = await setup(env)
  await put(env, phone, [item('cinema', 'settings', 'main', '{"subs":"on"}')])
  assert.equal((await call(env, 'DELETE', '/v1/account', { token: phone, body: { password: PASSWORD } })).status, 204)
  assert.equal(env.DB.rows('items').length, 0)
  assert.equal(env.DB.rows('accounts').length, 0)
})

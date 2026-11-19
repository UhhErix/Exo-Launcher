/**
 * The telemetry endpoints' tests: the real worker.js, telemetry.js and telemetry-store.js, the real migration SQL, on an
 * in-memory SQLite that answers D1's calls (prepare, bind, run, all, first, and batch as one transaction).
 *
 * How it could fail: a malformed envelope is stored; a refusal names the field or leaks a detail; an IP address, user agent
 * or header reaches a row or a limiter key; a personal-looking prop is stored, or an event that scrubs empty is stored; a
 * delete leaves rows, or removes another install's; the rate limit or the per-install hour cap is ignored; retention keeps
 * raw rows past 90 days, aggregates them twice, writes an install ID into an aggregate, or leaves half a day done on failure.
 *
 *   npm test   (node --test test; no packages)
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DatabaseSync } from 'node:sqlite'
import { readFileSync } from 'node:fs'
import worker from '../src/worker.js'
import { installKey, parseBatch, parseDelete, scrubProps, TelemetryError } from '../src/telemetry.js'
import { insertEvents, runRetention, utcDay } from '../src/telemetry-store.js'
import { telemetryLimits as L } from '../src/limits.js'
import { configuredLimiters, configuredLimits, limiters } from './limiters.mjs'

const MIGRATION = readFileSync(new URL('../migrations/0002_telemetry.sql', import.meta.url), 'utf8')
const CRASH_INDEX_MIGRATION = readFileSync(new URL('../migrations/0003_crash_index.sql', import.meta.url), 'utf8')
const DAY = 24 * 60 * 60 * 1000
const INSTALL = '5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13'
const OTHER = '0f1e2d3c-4b5a-4968-8778-6a5b4c3d2e1f'

// The draft tables as they were live on 2026-11-06 (wrangler d1 execute, exo-telemetry), for the migration test.
const LEGACY_DRAFT = `
CREATE TABLE raw (app TEXT NOT NULL, day TEXT NOT NULL, rid TEXT NOT NULL, app_version TEXT NOT NULL, os TEXT NOT NULL,
  gpu_class TEXT NOT NULL, ram_class TEXT NOT NULL, events TEXT NOT NULL, received_day TEXT NOT NULL, PRIMARY KEY (app, day, rid));
CREATE INDEX raw_received_day ON raw (received_day);
CREATE TABLE aggregates (day TEXT NOT NULL, app TEXT NOT NULL, app_version TEXT NOT NULL, os TEXT NOT NULL, gpu_class TEXT NOT NULL,
  ram_class TEXT NOT NULL, event TEXT NOT NULL, prop_key TEXT NOT NULL, prop_value TEXT NOT NULL, sum_n INTEGER NOT NULL,
  sum_value REAL NOT NULL, batches INTEGER NOT NULL, PRIMARY KEY (app, day, app_version, os, gpu_class, ram_class, event, prop_key, prop_value));
CREATE INDEX aggregates_day ON aggregates (day);`

/**
 * D1's calls over node:sqlite. A batch runs in one transaction, as D1's does. `failOn` makes any statement whose text
 * contains it throw, so a failure can be placed inside a batch.
 */
function fakeD1({ failOn = null, legacy = false } = {}) {
  const sql = new DatabaseSync(':memory:')
  if (legacy) sql.exec(LEGACY_DRAFT)
  sql.exec(MIGRATION)
  sql.exec(CRASH_INDEX_MIGRATION)
  sql.exec(readFileSync(new URL('../migrations/0004_crash_days.sql', import.meta.url), 'utf8'))
  sql.exec(readFileSync(new URL('../migrations/0005_alert_days.sql', import.meta.url), 'utf8'))
  sql.exec(readFileSync(new URL('../migrations/0006_github_issues.sql', import.meta.url), 'utf8'))
  const statement = (text) => {
    let args = []
    const check = () => {
      if (failOn && text.includes(failOn)) throw new Error('simulated D1 failure')
    }
    const s = {
      bind(...values) {
        args = values
        return s
      },
      async run() {
        check()
        const r = sql.prepare(text).run(...args)
        return { meta: { changes: r.changes } }
      },
      async all() {
        check()
        return { results: sql.prepare(text).all(...args) }
      },
      async first() {
        check()
        return sql.prepare(text).get(...args) ?? null
      },
    }
    return s
  }
  return {
    sql,
    prepare: statement,
    async batch(statements) {
      sql.exec('BEGIN')
      try {
        const out = []
        for (const st of statements) out.push(await st.run())
        sql.exec('COMMIT')
        return out
      } catch (error) {
        sql.exec('ROLLBACK')
        throw error
      }
    },
  }
}

/** The Worker's env: the D1 binding, a limiter that allows or refuses, and an R2 binding that telemetry must never touch. */
function makeEnv({ allow = true, db = fakeD1(), withDb = true, limiter = limiters({ allow }) } = {}) {
  const limited = limiter.calls
  const env = {
    ...limiter.bindings,
    REPORTS: {
      async put() {
        throw new Error('telemetry must not write R2')
      },
      // A delete of an install's crash reports is the only R2 call telemetry makes; it has no keys in these tests.
      async delete(keys) {
        if (keys.length > 0) throw new Error('no crash reports are indexed in these tests')
      },
    },
  }
  if (withDb) env.TELEMETRY = db
  return { env, db, limited }
}

const envelope = (over = {}) => ({
  schema: 1,
  app: 'launcher',
  version: '3.25.0',
  channel: 'stable',
  install_id: INSTALL,
  session_id: 'a1b2c3d4e5f60718',
  ts: new Date().toISOString(),
  event: 'perf.frame',
  props: { p50_ms: 6.9, p95_ms: 11.2, p99_ms: 16.8, frames: 1200 },
  ...over,
})

const post = (path, body, headers = {}) =>
  new Request(`https://reports.getexo.app${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: typeof body === 'string' || body instanceof Uint8Array ? body : JSON.stringify(body),
  })

const count = (db, where = '1 = 1') => db.sql.prepare(`SELECT COUNT(*) AS n FROM events WHERE ${where}`).get().n

/** A raw row as the retention and cap tests need it (insertEvents takes these). */
const row = (install, event = 'perf.frame', props_json = '{}') => ({
  app: 'launcher',
  version: '3.25.0',
  channel: 'stable',
  install_id: install,
  session_id: 'a1b2c3d4e5f60718',
  ts: new Date().toISOString(),
  event,
  props_json,
})

test('a valid batch is stored, one row per event, with the scrubbed props as JSON', async () => {
  const { env, db } = makeEnv()
  const response = await worker.fetch(post('/v1/telemetry', [envelope(), envelope({ event: 'app.exit', props: { session_s: 61, clean: true } })]), env)
  assert.equal(response.status, 202)
  const rows = db.sql.prepare('SELECT app, version, channel, install_id, session_id, event, props_json, received_day FROM events ORDER BY id').all()
  assert.equal(rows.length, 2)
  assert.deepEqual({ ...rows[0] }, {
    app: 'launcher',
    version: '3.25.0',
    channel: 'stable',
    install_id: INSTALL,
    session_id: 'a1b2c3d4e5f60718',
    event: 'perf.frame',
    props_json: JSON.stringify({ p50_ms: 6.9, p95_ms: 11.2, p99_ms: 16.8, frames: 1200 }),
    received_day: utcDay(Date.now()),
  })
  assert.deepEqual(JSON.parse(rows[1].props_json), { session_s: 61, clean: true })
})

test('the spec wrapper {schema, events} is accepted as well as a bare array', async () => {
  const { env, db } = makeEnv()
  assert.equal((await worker.fetch(post('/v1/telemetry', { schema: 1, events: [envelope()] }), env)).status, 202)
  assert.equal(count(db), 1)
})

test('every invalid batch is refused with 400, an empty body (no detail), and stores nothing', async () => {
  const cases = {
    'a schema other than 1': [envelope({ schema: 2 })],
    'a wrapper with another schema': { schema: 2, events: [envelope()] },
    'an app outside the set': [envelope({ app: 'steam' })],
    'an app that is not in the set': [envelope({ app: 'store' })],
    'a version that is not semver': [envelope({ version: '3.25' })],
    'a four-part version': [envelope({ version: '3.25.0.1.2' })],
    'a channel outside the set': [envelope({ channel: 'nightly' })],
    'an install id that is not 128-bit hex': [envelope({ install_id: 'abc' })],
    'a 31-digit install id': [envelope({ install_id: INSTALL.replace(/-/g, '').slice(1) })],
    'a session id that is not hex': [envelope({ session_id: 'xyz123xyz123xyz1' })],
    'a short session id': [envelope({ session_id: 'a1b2' })],
    'a ts without milliseconds': [envelope({ ts: '2026-11-06T08:46:57Z' })],
    'a ts 8 days in the past': [envelope({ ts: new Date(Date.now() - 8 * DAY).toISOString() })],
    'a ts 8 days in the future': [envelope({ ts: new Date(Date.now() + 8 * DAY).toISOString() })],
    'an event name with capitals': [envelope({ event: 'Perf.Frame' })],
    'an event name with a space': [envelope({ event: 'perf frame' })],
    'an event name of 65 characters': [envelope({ event: 'a'.repeat(65) })],
    'an empty event name': [envelope({ event: '' })],
    'props that are an array': [envelope({ props: [1, 2] })],
    'props that are a string': [envelope({ props: 'x' })],
    'props with 33 keys': [envelope({ props: Object.fromEntries(Array.from({ length: 33 }, (_, i) => [`k${i}`, i])) })],
    'a prop key with a dash': [envelope({ props: { 'bad-key': 1 } })],
    'a string prop of 257 characters': [envelope({ props: { note: 'word '.repeat(51) + 'ab' } })],
    'a nested prop': [envelope({ props: { nested: { a: 1 } } })],
    'a null prop': [envelope({ props: { gone: null } })],
    'an event without props': [{ ...envelope(), props: undefined }],
    'an event that is not an object': ['perf.frame'],
    'an empty batch': [],
    'a wrapper with no events': { schema: 1, events: [] },
    'a body that is not JSON': '{not json',
    'a body that is not UTF-8': new Uint8Array([0xff, 0xfe, 0x7b]),
  }
  for (const [what, body] of Object.entries(cases)) {
    const { env, db } = makeEnv()
    const response = await worker.fetch(post('/v1/telemetry', body), env)
    assert.equal(response.status, 400, `${what} should be refused`)
    assert.equal(await response.text(), '', `${what} must not say why`)
    assert.equal(count(db), 0, `${what} must not be stored`)
  }
})

test('a batch of more than 500 events is refused in code (and 501 small events are over the 64 KB body cap on the wire)', async () => {
  const five = Array.from({ length: 500 }, () => envelope())
  assert.equal(parseBatch(five, Date.now()).length, 500)
  assert.throws(() => parseBatch([...five, envelope()], Date.now()), TelemetryError)
  const { env, db } = makeEnv()
  const wire = Array.from({ length: 501 }, () => envelope({ props: {} }))
  assert.equal((await worker.fetch(post('/v1/telemetry', wire), env)).status, 413)
  assert.equal(count(db), 0)
})

test('a body over 64 KB is refused with 413, by its declared length or once it is read', async () => {
  const { env, db } = makeEnv()
  const padded = JSON.stringify([envelope()]) + ' '.repeat(L.bodyBytes)
  assert.equal((await worker.fetch(post('/v1/telemetry', padded), env)).status, 413)
  const declared = await worker.fetch(post('/v1/telemetry', [envelope()], { 'Content-Length': String(L.bodyBytes + 1) }), env)
  assert.equal(declared.status, 413)
  assert.equal(count(db), 0)
})

test('any Origin header is refused with 403, so a browser page cannot post', async () => {
  for (const origin of ['https://getexo.app', 'null', 'https://evil.example']) {
    const { env, db } = makeEnv()
    const response = await worker.fetch(post('/v1/telemetry', [envelope()], { Origin: origin }), env)
    assert.equal(response.status, 403, `Origin ${origin} should be refused`)
    assert.equal(count(db), 0)
  }
})

test('only POST to the telemetry paths; other paths are 404', async () => {
  const { env } = makeEnv()
  assert.equal((await worker.fetch(new Request('https://reports.getexo.app/v1/telemetry'), env)).status, 405)
  assert.equal((await worker.fetch(new Request('https://reports.getexo.app/v1/telemetry/delete'), env)).status, 405)
  assert.equal((await worker.fetch(post('/v1/telemetry/other', {}), env)).status, 404)
})

test('the install id is normalised, so the same 128 bits in any form are one install', () => {
  assert.equal(installKey(INSTALL.replace(/-/g, '')), INSTALL)
  assert.equal(installKey(INSTALL.toUpperCase()), INSTALL)
  assert.equal(installKey('5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b1'), null)
  assert.equal(installKey(42), null)
})

test('a personal-looking string prop is dropped; the rest of the event is kept', () => {
  const personal = {
    'a Windows path': 'C:\\Users\\Erix\\Games\\Riot.exe',
    'a UNC path': '\\\\DESKTOP-K3J9\\share',
    'an environment path': '%USERPROFILE%\\Games',
    'a Unix home': '/home/erix/x',
    'a macOS home': '/Users/erix/x',
    'a URL': 'https://example.com/a',
    'a bare www host': 'www.example.com',
    'an email': 'erix@example.com',
    'an IPv4 address': '203.0.113.9',
    'an IPv6 address': '2001:db8::1',
    'a GUID': '5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13',
    'a JWT': 'eyJhbG' + 'ciOiJI' + 'UzI1Ni' + 'J9.eyJ' + 'zdWIiO' + 'iIxMjM' + '0NTY3O' + 'DkwIn0' + '.abcde' + 'fghijk' + 'lmnopq' + 'rstuvw' + 'x',
    'a bearer token': 'Bearer abc.def',
    'a secret assignment': 'token=abc',
    'a long alphanumeric run': '0123456789abcdef0123456789abcdef',
    'a long number as text': '76561198012345678',
    'a media file name': 'Movie.mkv',
    'a control character': 'line\nbreak',
  }
  for (const [what, value] of Object.entries(personal)) {
    assert.deepEqual(scrubProps({ bad: value, ok: 1 }), { ok: 1 }, `${what} should be dropped`)
  }
  assert.deepEqual(scrubProps({ bad: 76561198012345678 }), {}, 'a 17-digit number should be dropped')
})

test('values that look like versions, build numbers, times and ids are kept', () => {
  const kept = { os_build: '10.0.26300.1', version: '3.24.1', game_id: '1245620', time: '19:42', gpu: 'Intel(R) UHD Graphics 770', driver: '31.0.15.4601', flag: true, n: 1048576 }
  assert.deepEqual(scrubProps(kept), kept)
})

test('an event whose props all scrub away is not stored; one with no props at all is', async () => {
  const { env, db } = makeEnv()
  const body = [envelope({ event: 'error.caught', props: { component: 'C:\\Users\\Erix\\x' } }), envelope({ event: 'app.exit', props: {} })]
  assert.equal((await worker.fetch(post('/v1/telemetry', body), env)).status, 202)
  assert.deepEqual(db.sql.prepare('SELECT event FROM events').all().map((r) => r.event), ['app.exit'])
})

test('an event over 1 KB is dropped whole; the others in the batch are kept', async () => {
  const { env, db } = makeEnv()
  const big = envelope({ event: 'perf.memory', props: Object.fromEntries(Array.from({ length: 30 }, (_, i) => [`k${i}`, 'word '.repeat(40)])) })
  assert.equal((await worker.fetch(post('/v1/telemetry', [big, envelope()]), env)).status, 202)
  assert.deepEqual(db.sql.prepare('SELECT event FROM events').all().map((r) => r.event), ['perf.frame'])
})

test('a delete removes every event of that install, keeps the others, and answers 204', async () => {
  const { env, db } = makeEnv()
  await worker.fetch(post('/v1/telemetry', [envelope(), envelope({ event: 'app.exit', props: {} })]), env)
  await worker.fetch(post('/v1/telemetry', [envelope({ install_id: OTHER })]), env)
  assert.equal(count(db), 3)
  const response = await worker.fetch(post('/v1/telemetry/delete', { schema: 1, app: 'launcher', install_id: INSTALL }), env)
  assert.equal(response.status, 204)
  assert.equal(await response.text(), '')
  assert.equal(count(db, `install_id = '${INSTALL}'`), 0)
  assert.equal(count(db, `install_id = '${OTHER}'`), 1)
})

test('a delete takes an install id, and refuses a malformed one or an unknown app or schema with 400', async () => {
  const { env, db } = makeEnv()
  await worker.fetch(post('/v1/telemetry', [envelope()]), env)
  assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), env)).status, 204)
  assert.equal(count(db), 0)
  for (const body of [{}, { install_id: 'abc' }, { install_id: INSTALL, app: 'store' }, { install_id: INSTALL, schema: 2 }, []]) {
    assert.equal((await worker.fetch(post('/v1/telemetry/delete', body), env)).status, 400)
  }
  assert.throws(() => parseDelete({ install_id: 42 }), TelemetryError)
})

test('the rate limit is asked per sender network and per install in the batch, keyed apart from crash reports, and a refusal stores nothing (429)', async () => {
  const allowed = makeEnv()
  const ip = { 'CF-Connecting-IP': '198.51.100.7' }
  await worker.fetch(post('/v1/telemetry', [envelope(), envelope({ app: 'mouse', install_id: OTHER }), envelope()], ip), allowed.env)
  assert.deepEqual(allowed.limited, [
    { binding: 'REPORTS_PER_NETWORK', key: 'telemetry:198.51.100.7' },
    { binding: 'REPORTS_PER_INSTALL', key: `telemetry:${INSTALL}` },
    { binding: 'REPORTS_PER_INSTALL', key: `telemetry:${OTHER}` },
    { binding: 'REPORTS_CEILING', key: 'telemetry' },
  ])
  await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }, ip), allowed.env)
  assert.deepEqual(allowed.limited.slice(4), [
    { binding: 'REPORTS_PER_NETWORK', key: 'delete:198.51.100.7' },
    { binding: 'REPORTS_PER_INSTALL', key: `delete:${INSTALL}` },
    { binding: 'REPORTS_CEILING', key: 'delete' },
  ])

  for (const refusing of ['REPORTS_PER_NETWORK', 'REPORTS_PER_INSTALL', 'REPORTS_CEILING']) {
    const refused = makeEnv({ limiter: limiters({ allow: (binding) => binding !== refusing }) })
    await insertEvents(refused.db, [row(INSTALL)], Date.now())
    assert.equal((await worker.fetch(post('/v1/telemetry', [envelope()]), refused.env)).status, 429, refusing)
    assert.equal(count(refused.db), 1, `${refusing}: nothing more is stored`)
    assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), refused.env)).status, 429, refusing)
    assert.equal(count(refused.db), 1, `${refusing}: nothing is deleted`)
  }
})

test('10,000 installs sending a batch in the same minute are all stored, and one install past its count does not hold back the next', async () => {
  const { env, db } = makeEnv({ limiter: configuredLimiters() })
  const id = (n) => `${n.toString(16).padStart(8, '0')}-6a2d-4f7e-9c1b-2d8e4a7f0b13`
  for (let n = 0; n < 10_000; n++) {
    const response = await worker.fetch(post('/v1/telemetry', [envelope({ install_id: id(n) })], { 'CF-Connecting-IP': `198.18.${n >> 8}.${n & 255}` }), env)
    assert.equal(response.status, 202, `install ${n}`)
  }
  assert.equal(count(db), 10_000)

  const perInstall = configuredLimits().REPORTS_PER_INSTALL
  const home = { 'CF-Connecting-IP': '203.0.113.50' }
  // A new minute: the counts start again.
  Object.assign(env, configuredLimiters().bindings)
  for (let i = 0; i < perInstall; i++) assert.equal((await worker.fetch(post('/v1/telemetry', [envelope({ install_id: id(0) })], home), env)).status, 202)
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope({ install_id: id(0) })], home), env)).status, 429)
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope({ install_id: id(1) })], home), env)).status, 202)
})

test('a per-install cap of events per hour is enforced in code (429), counted from received time', async () => {
  const now = Date.now()
  const { env, db } = makeEnv()
  const full = Array.from({ length: L.eventsPerInstallHour }, () => row(INSTALL))
  await insertEvents(db, full, now)
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope()]), env)).status, 429)
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope({ install_id: OTHER })]), env)).status, 202, 'another install is not capped')

  // Events received more than an hour ago do not count.
  const old = makeEnv()
  await insertEvents(old.db, Array.from({ length: L.eventsPerInstallHour }, () => row(INSTALL)), now - 2 * 60 * 60 * 1000)
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope()]), old.env)).status, 202)
})

test('with no D1 binding (the dev copy) telemetry answers 503', async () => {
  const { env } = makeEnv({ withDb: false })
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope()]), env)).status, 503)
  assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), env)).status, 503)
})

test('a D1 failure answers 503 (the apps keep the batch and retry) and does not say why', async () => {
  const { env } = makeEnv({ db: fakeD1({ failOn: 'INSERT INTO events' }) })
  const response = await worker.fetch(post('/v1/telemetry', [envelope()]), env)
  assert.equal(response.status, 503)
  assert.equal(await response.text(), '')
})

test('no IP address, user agent, cookie or client header reaches a row or a response, and the address reaches only the network limiter as its key', async () => {
  const { env, db, limited } = makeEnv()
  const headers = { 'CF-Connecting-IP': '203.0.113.9', 'X-Forwarded-For': '203.0.113.9', 'User-Agent': 'ExoLauncher/3.25.0 (SECRET-UA-7f3)', Cookie: 'session=abc123', 'X-Client-Id': 'client-42' }
  const response = await worker.fetch(post('/v1/telemetry', [envelope()], headers), env)
  assert.equal(response.status, 202)
  const everything = JSON.stringify([db.sql.prepare('SELECT * FROM events').all(), db.sql.prepare('SELECT * FROM aggregates').all(), [...response.headers.entries()]])
  for (const secret of ['203.0.113.9', 'SECRET-UA-7f3', 'abc123', 'client-42']) assert.ok(!everything.includes(secret), `${secret} leaked`)
  const keys = JSON.stringify(limited)
  for (const secret of ['SECRET-UA-7f3', 'abc123', 'client-42']) assert.ok(!keys.includes(secret), `${secret} leaked into a limiter key`)
  for (const call of limited) if (call.key.includes('203.0.113.9')) assert.equal(call.binding, 'REPORTS_PER_NETWORK')
})

test('the retention job writes each old day to aggregates (no install id) and removes its raw rows, once', async () => {
  const { db } = makeEnv()
  const now = Date.now()
  const old = now - 91 * DAY
  const kept = now - 89 * DAY
  await insertEvents(db, [row(INSTALL), row(INSTALL), row(OTHER), row(OTHER, 'app.exit')], old)
  await insertEvents(db, [row(INSTALL)], kept)
  await insertEvents(db, [row(OTHER)], now)

  assert.equal(await runRetention(db, now), 1, 'only the day 91 days back is past 90 days')
  assert.equal(count(db, `received_day = '${utcDay(old)}'`), 0)
  assert.equal(count(db, `received_day = '${utcDay(kept)}'`), 1)
  assert.equal(count(db), 2)

  const aggregates = db.sql.prepare('SELECT day, app, version, event, count, installs FROM aggregates ORDER BY event').all().map((r) => ({ ...r }))
  assert.deepEqual(aggregates, [
    { day: utcDay(old), app: 'launcher', version: '3.25.0', event: 'app.exit', count: 1, installs: 1 },
    { day: utcDay(old), app: 'launcher', version: '3.25.0', event: 'perf.frame', count: 3, installs: 2 },
  ])
  const columns = db.sql.prepare('PRAGMA table_info(aggregates)').all().map((c) => c.name)
  assert.deepEqual(columns, ['day', 'app', 'version', 'event', 'count', 'installs'], 'aggregates hold counts only, no install id')

  // A second run finds nothing old and adds nothing: no double count.
  assert.equal(await runRetention(db, now), 0)
  assert.equal(db.sql.prepare('SELECT SUM(count) AS n FROM aggregates').get().n, 4)
})

test('a retention day that fails half way is rolled back: no aggregate without its delete, no delete without its aggregate', async () => {
  const db = fakeD1({ failOn: 'DELETE FROM events WHERE received_day' })
  const now = Date.now()
  await insertEvents(db, [row(INSTALL)], now - 91 * DAY)
  await assert.rejects(runRetention(db, now))
  assert.equal(count(db), 1, 'the raw row survives a failed day')
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM aggregates').get().n, 0, 'no aggregate is left behind')
})

test('the scheduled job runs the retention and removes old rows', async () => {
  const { env, db } = makeEnv()
  await insertEvents(db, [row(INSTALL, 'app.exit')], Date.now() - 100 * DAY)
  await worker.scheduled({}, env)
  assert.equal(count(db), 0)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM aggregates').get().n, 1)
})

test('the migration replaces the empty draft tables and creates the new ones', () => {
  const db = fakeD1({ legacy: true })
  const tables = db.sql.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name").all().map((t) => t.name)
  assert.deepEqual(tables, ['aggregates', 'alert_days', 'crash_days', 'crash_groups', 'crash_index', 'events', 'issue_days', 'issue_queue'])
  const columns = db.sql.prepare('PRAGMA table_info(events)').all().map((c) => c.name).sort()
  assert.deepEqual(columns, ['app', 'channel', 'event', 'id', 'install_id', 'props_json', 'received_at', 'received_day', 'session_id', 'ts', 'version'])
})

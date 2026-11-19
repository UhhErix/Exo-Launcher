/**
 * Crash reports by install: the real worker.js, validate.js, crash-store.js and the real migrations, on an in-memory SQLite
 * for D1 and a Map for R2.
 *
 * How it could fail: a report with an install ID is stored without its index row, so a delete leaves it behind; a report
 * without one is refused or indexed; a malformed install ID is stored or the report is refused without saying why; the
 * index keeps another install's objects; a delete answers 204 while objects or rows remain; a failed R2 delete loses the
 * index so a retry cannot finish; the index is written but the report is not (or the reverse) when one of them fails; the
 * stored report carries the install ID; the daily job keeps index rows past 30 days or drops recent ones.
 *
 *   npm test   (node --test test; no packages)
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DatabaseSync } from 'node:sqlite'
import { readFileSync } from 'node:fs'
import worker from '../src/worker.js'
import { limiters } from './limiters.mjs'
import { parseReport, reportInstallId, ReportError } from '../src/validate.js'
import { limits } from '../src/limits.js'
import { utcDay } from '../src/telemetry-store.js'

const MIGRATIONS = ['0002_telemetry.sql', '0003_crash_index.sql', '0004_crash_days.sql', '0005_alert_days.sql', '0006_github_issues.sql'].map((name) => readFileSync(new URL(`../migrations/${name}`, import.meta.url), 'utf8'))
const DAY = 24 * 60 * 60 * 1000
const INSTALL = '5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13'
const OTHER = '0f1e2d3c-4b5a-4968-8778-6a5b4c3d2e1f'

const sample = {
  app: 'launcher',
  version: '3.24.1',
  osBuild: '10.0.26300.1',
  errorType: 'SteamGameException',
  excerpt: '[2026-11-05T19:42:11.001Z] CRASH: unexpected end of stream',
}

/** D1 over node:sqlite, as telemetry.test.mjs has it; `failOn` makes a statement containing that text throw. */
function fakeD1({ failOn = null } = {}) {
  const sql = new DatabaseSync(':memory:')
  for (const migration of MIGRATIONS) sql.exec(migration)
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

/** R2 as a Map: put and delete, with `failDelete` making delete throw (a failure part-way through a delete). */
function fakeR2({ failDelete = false } = {}) {
  const objects = new Map()
  return {
    objects,
    async put(key, body) {
      objects.set(key, body)
    },
    async delete(keys) {
      if (failDelete) throw new Error('simulated R2 failure')
      for (const key of keys) objects.delete(key)
    },
  }
}

/** The Worker's env. `withDb` false is the dev copy (no D1 binding). */
function makeEnv({ db = fakeD1(), r2 = fakeR2(), withDb = true } = {}) {
  const env = {
    REPORTS: r2,
    ...limiters().bindings,
  }
  if (withDb) env.TELEMETRY = db
  return { env, db, r2 }
}

const post = (path, body, headers = {}) =>
  new Request(`https://reports.getexo.app${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: typeof body === 'string' ? body : JSON.stringify(body),
  })

const indexRows = (db, install) => db.sql.prepare('SELECT install_id, r2_key, received_day FROM crash_index WHERE install_id = ? ORDER BY r2_key').all(install).map((r) => ({ ...r }))

test('a report with an install ID is accepted, stored, and indexed under that install', async () => {
  const { env, db, r2 } = makeEnv()
  const response = await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL }), env)
  assert.equal(response.status, 202)
  assert.equal(r2.objects.size, 1)
  const [[key, body]] = [...r2.objects]
  assert.match(key, /^reports\/\d{4}-\d{2}-\d{2}\/[0-9a-f-]{36}\.json$/)
  const rows = indexRows(db, INSTALL)
  assert.deepEqual(rows, [{ install_id: INSTALL, r2_key: key, received_day: utcDay(Date.now()) }])
  // The stored report is still exactly the five fields; the install ID lives in the index only.
  assert.deepEqual(Object.keys(JSON.parse(body)).sort(), ['app', 'errorType', 'excerpt', 'osBuild', 'version'])
})

test('a report without an install ID (an old client) is accepted and not indexed', async () => {
  const { env, db, r2 } = makeEnv()
  assert.equal((await worker.fetch(post('/v1/report', sample), env)).status, 202)
  assert.equal(r2.objects.size, 1)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM crash_index').get().n, 0)
})

test('a malformed install ID refuses the report with 400 and stores nothing', async () => {
  const bad = [null, 42, '', 'abc', INSTALL.slice(0, -1), INSTALL + '0', `{${INSTALL}}`, INSTALL.replace(/-/g, 'g'), ['x'], { id: INSTALL }, true]
  for (const installId of bad) {
    const { env, db, r2 } = makeEnv()
    const response = await worker.fetch(post('/v1/report', { ...sample, installId }), env)
    assert.equal(response.status, 400, `${JSON.stringify(installId)} should be refused`)
    assert.equal(r2.objects.size, 0, `${JSON.stringify(installId)} must not be stored`)
    assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM crash_index').get().n, 0)
  }
  assert.throws(() => reportInstallId({ ...sample, installId: 'nope' }), ReportError)
  assert.throws(() => parseReport({ ...sample, installId: 7 }), /Bad install ID/)
})

test('an install ID is accepted in the telemetry forms (hyphenated or 32 hex, any case) and stored in one form', async () => {
  assert.equal(reportInstallId({ ...sample, installId: INSTALL.toUpperCase() }), INSTALL)
  assert.equal(reportInstallId({ ...sample, installId: INSTALL.replace(/-/g, '') }), INSTALL)
  assert.equal(reportInstallId(sample), null)
  const { env, db } = makeEnv()
  await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL.replace(/-/g, '').toUpperCase() }), env)
  assert.equal(indexRows(db, INSTALL).length, 1)
})

test('other unknown fields are still refused, with an install ID present', async () => {
  const { env, r2 } = makeEnv()
  assert.equal((await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL, userName: 'Erix' }), env)).status, 400)
  assert.equal(r2.objects.size, 0)
})

test('a report with an install ID answers 503 when there is no index (the dev copy) and stores nothing', async () => {
  const { env, r2 } = makeEnv({ withDb: false })
  assert.equal((await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL }), env)).status, 503)
  assert.equal(r2.objects.size, 0)
  assert.equal((await worker.fetch(post('/v1/report', sample), env)).status, 202)
})

test('an index failure answers 503 and stores no report', async () => {
  const { env, r2 } = makeEnv({ db: fakeD1({ failOn: 'INSERT INTO crash_index' }) })
  const response = await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL }), env)
  assert.equal(response.status, 503)
  assert.equal(r2.objects.size, 0)
})

test('a failed R2 write removes the index row it just wrote, so nothing points at a missing object', async () => {
  const { env, db } = makeEnv()
  env.REPORTS.put = async () => {
    throw new Error('simulated R2 write failure')
  }
  await assert.rejects(worker.fetch(post('/v1/report', { ...sample, installId: INSTALL }), env), /simulated R2 write failure/)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM crash_index').get().n, 0)
})

test('delete removes that install’s events and crash objects, keeps the others, and answers 204', async () => {
  const { env, db, r2 } = makeEnv()
  await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL }), env)
  await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL, excerpt: 'second' }), env)
  await worker.fetch(post('/v1/report', { ...sample, installId: OTHER }), env)
  await worker.fetch(post('/v1/report', sample), env)
  assert.equal(r2.objects.size, 4)
  const mine = indexRows(db, INSTALL).map((r) => r.r2_key)
  assert.equal(mine.length, 2)
  const theirs = indexRows(db, OTHER).map((r) => r.r2_key)

  // Events for the same install, from the telemetry endpoint (the same D1).
  const envelope = { schema: 1, app: 'launcher', version: '3.25.0', channel: 'stable', install_id: INSTALL, session_id: 'a1b2c3d4e5f60718', ts: new Date().toISOString(), event: 'app.exit', props: {} }
  assert.equal((await worker.fetch(post('/v1/telemetry', [envelope]), env)).status, 202)

  const response = await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), env)
  assert.equal(response.status, 204)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM events WHERE install_id = ?').get(INSTALL).n, 0)
  assert.equal(indexRows(db, INSTALL).length, 0)
  for (const key of mine) assert.equal(r2.objects.has(key), false, `${key} must be gone`)
  assert.equal(indexRows(db, OTHER).length, 1)
  for (const key of theirs) assert.equal(r2.objects.has(key), true, 'another install’s report stays')
  assert.equal(r2.objects.size, 2)
})

test('a delete for an install with no crash reports still answers 204', async () => {
  const { env } = makeEnv()
  assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), env)).status, 204)
})

test('a delete matches the install ID in any form the report used', async () => {
  const { env, db, r2 } = makeEnv()
  await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL.replace(/-/g, '') }), env)
  assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL.toUpperCase() }), env)).status, 204)
  assert.equal(indexRows(db, INSTALL).length, 0)
  assert.equal(r2.objects.size, 0)
})

test('a failed R2 delete answers 503 and keeps the index, so a retry removes the objects', async () => {
  const r2 = fakeR2()
  const { env, db } = makeEnv({ r2 })
  await worker.fetch(post('/v1/report', { ...sample, installId: INSTALL }), env)
  r2.delete = async () => {
    throw new Error('simulated R2 failure')
  }
  assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), env)).status, 503)
  assert.equal(indexRows(db, INSTALL).length, 1, 'the index keeps the key the retry needs')
  assert.equal(r2.objects.size, 1)

  r2.delete = async (keys) => {
    for (const key of keys) r2.objects.delete(key)
  }
  assert.equal((await worker.fetch(post('/v1/telemetry/delete', { install_id: INSTALL }), env)).status, 204)
  assert.equal(r2.objects.size, 0)
  assert.equal(indexRows(db, INSTALL).length, 0)
})

test('the daily job drops index rows older than 30 days and keeps recent ones', async () => {
  const { env, db } = makeEnv()
  const insert = db.sql.prepare('INSERT INTO crash_index (install_id, r2_key, received_day) VALUES (?, ?, ?)')
  insert.run(INSTALL, 'reports/old.json', utcDay(Date.now() - (limits.keepDays + 1) * DAY))
  insert.run(INSTALL, 'reports/edge.json', utcDay(Date.now() - (limits.keepDays - 1) * DAY))
  await worker.scheduled({}, env)
  const keys = db.sql.prepare('SELECT r2_key FROM crash_index ORDER BY r2_key').all().map((r) => r.r2_key)
  assert.deepEqual(keys, ['reports/edge.json'])
})

test('the index is keyed by install and key, so one report is not indexed twice', () => {
  const db = fakeD1()
  const insert = db.sql.prepare('INSERT INTO crash_index (install_id, r2_key, received_day) VALUES (?, ?, ?)')
  insert.run(INSTALL, 'reports/a.json', '2026-11-06')
  assert.throws(() => insert.run(INSTALL, 'reports/a.json', '2026-11-06'), /UNIQUE|constraint/i)
})

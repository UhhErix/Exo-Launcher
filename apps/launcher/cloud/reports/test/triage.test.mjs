/**
 * Triage's tests (src/triage.js, README.md "Triage"): the real Worker against node:sqlite as D1, a Map as R2, and a mail
 * binding and a Discord that only record. Nothing reaches Cloudflare, a mailbox or Discord.
 *
 * How it could fail: a new error in a version tells nobody, or every report of it does (a flood); a failed count or a
 * failed send loses the report or fails its answer; the dev copy (no D1) breaks; the digest names the wrong day or
 * misses a group; an alert goes to a host that is not Discord; a group keeps something about the sender.
 *   node --test test
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DatabaseSync } from 'node:sqlite'
import { readFileSync } from 'node:fs'
import worker from '../src/worker.js'
import { limiters } from './limiters.mjs'
import { ALERT_AT, ALERTS_PER_DAY, claimAlert, dailyMessage, digestOf, discordBody, isDiscordWebhook, pruneAlertDays } from '../src/triage.js'
import { utcDay } from '../src/telemetry-store.js'

const MIGRATIONS = ['0002_telemetry.sql', '0003_crash_index.sql', '0004_crash_days.sql', '0005_alert_days.sql', '0006_github_issues.sql'].map((name) => readFileSync(new URL(`../migrations/${name}`, import.meta.url), 'utf8'))
const DAY = 24 * 60 * 60 * 1000
const HOOK = 'https://discord.com/api/webhooks/123456789012345678/tok_En-1'
const sample = {
  app: 'launcher',
  version: '3.26.0',
  osBuild: '10.0.26300.1',
  errorType: 'HostCallFailed',
  excerpt: '[2026-11-09T19:42:11.001Z] WARN: RPC library.get: NullReferenceException',
}

function fakeD1({ failOn = null, migrations = MIGRATIONS } = {}) {
  const sql = new DatabaseSync(':memory:')
  for (const migration of migrations) sql.exec(migration)
  const statement = (text) => {
    let args = []
    const check = () => { if (failOn && text.includes(failOn)) throw new Error('simulated D1 failure') }
    const s = {
      bind(...values) { args = values; return s },
      async run() { check(); return { meta: { changes: sql.prepare(text).run(...args).changes } } },
      async all() { check(); return { results: sql.prepare(text).all(...args) } },
      async first() { check(); return sql.prepare(text).get(...args) ?? null },
    }
    return s
  }
  return {
    sql,
    prepare: statement,
    async batch(statements) { const out = []; for (const st of statements) out.push(await st.run()); return out },
  }
}

/** The Worker's env with a mail binding and a Discord webhook that record what they were given. */
function makeEnv({ db = fakeD1(), withDb = true, hook = HOOK, hookFails = false } = {}) {
  const objects = new Map()
  const mails = []
  const posts = []
  const env = {
    REPORTS: { async put(key, body) { objects.set(key, body) }, async delete() {} },
    ...limiters().bindings,
    CONTACT_TO: 'team@example.test',
    CONTACT_MAIL: { async send(message) { mails.push(message) } },
    EmailMessage: class { constructor(from, to, raw) { Object.assign(this, { from, to, raw }) } },
    fetcher: async (url, init) => {
      if (hookFails) throw new Error('discord is down')
      posts.push({ url, body: JSON.parse(init.body) })
      return new Response(null, { status: 204 })
    },
  }
  if (withDb) env.TELEMETRY = db
  if (hook) env.DISCORD_WEBHOOK_REPORTS = hook
  return { env, db, objects, mails, posts }
}

const send = (env, body = sample) => worker.fetch(new Request('https://reports.getexo.app/v1/report', {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
}), env)

const contentOf = (post) => post.body.content

test('the first report of an error in a version tells the team once in Discord only (never emails), with what broke and its excerpt', async () => {
  const { env, mails, posts, objects } = makeEnv()
  assert.equal((await send(env)).status, 202)
  assert.equal(objects.size, 1)
  assert.equal(mails.length, 0)
  assert.equal(posts.length, 1)
  assert.equal(posts[0].url, HOOK)
  assert.match(contentOf(posts[0]), /^\*\*Exo Launcher 3\.26\.0: HostCallFailed \(new error\)\*\*/)
  assert.match(contentOf(posts[0]), /RPC library\.get: NullReferenceException/)
  assert.match(contentOf(posts[0]), /Windows 10\.0\.26300\.1/)
})

test('later reports of the same error stay quiet until the 10th', async () => {
  const { env, mails, posts } = makeEnv()
  for (let i = 0; i < 9; i++) await send(env)
  assert.equal(posts.length, 1, 'reports 2 to 9 tell nobody')
  await send(env)
  assert.equal(posts.length, 2)
  assert.match(contentOf(posts[1]), /^\*\*Exo Launcher 3\.26\.0: HostCallFailed \(10 reports\)\*\*/)
  assert.equal(mails.length, 0)
  assert.deepEqual(ALERT_AT, [1, 10, 100, 1000])
})

test('the same error in another version, and another error in this one, are new groups', async () => {
  const { env, mails, posts } = makeEnv()
  await send(env)
  await send(env, { ...sample, version: '3.26.1' })
  await send(env, { ...sample, errorType: 'UiError' })
  assert.equal(mails.length, 0)
  assert.deepEqual(posts.map((p) => contentOf(p).split('\n')[0]), [
    '**Exo Launcher 3.26.0: HostCallFailed (new error)**',
    '**Exo Launcher 3.26.1: HostCallFailed (new error)**',
    '**Exo Launcher 3.26.0: UiError (new error)**',
  ])
})

test('a group is a count and a sample key: nothing about the sender', async () => {
  const { env, db } = makeEnv()
  await send(env, { ...sample, installId: '5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13' })
  await send(env)
  const rows = db.sql.prepare('SELECT * FROM crash_days').all().map((row) => ({ ...row }))
  assert.equal(rows.length, 1)
  assert.deepEqual(Object.keys(rows[0]).sort(), ['app', 'count', 'day', 'error_type', 'sample_key', 'version'])
  assert.equal(rows[0].count, 2)
  assert.equal(rows[0].day, utcDay(Date.now()))
  assert.match(rows[0].sample_key, /^reports\/\d{4}-\d{2}-\d{2}\/[0-9a-f-]{36}\.json$/)
})

test('the dev copy (no D1), a failed count and a failed Discord send all still store the report and answer 202', async () => {
  const dev = makeEnv({ withDb: false })
  assert.equal((await send(dev.env)).status, 202)
  assert.equal(dev.posts.length, 0)
  assert.equal(dev.mails.length, 0)
  assert.equal(dev.objects.size, 1)

  const broken = makeEnv({ db: fakeD1({ failOn: 'crash_days' }) })
  assert.equal((await send(broken.env)).status, 202)
  assert.equal(broken.objects.size, 1)
  assert.equal(broken.posts.length, 0)
  assert.equal(broken.mails.length, 0)

  const noHook = makeEnv({ hookFails: true })
  assert.equal((await send(noHook.env)).status, 202)
  assert.equal(noHook.objects.size, 1)
  assert.equal(noHook.mails.length, 0)
})

test('with a Discord webhook the alert is posted there only; anything that is not a Discord webhook gets nothing and never emails', async () => {
  const withHook = makeEnv({ hook: HOOK })
  await send(withHook.env)
  assert.equal(withHook.posts.length, 1)
  assert.equal(withHook.mails.length, 0)
  assert.equal(withHook.posts[0].url, HOOK)
  assert.match(withHook.posts[0].body.content, /^\*\*Exo Launcher 3\.26\.0: HostCallFailed \(new error\)\*\*/)
  assert.deepEqual(withHook.posts[0].body.allowed_mentions, { parse: [] })

  const elsewhere = makeEnv({ hook: 'https://evil.example/api/webhooks/1/tok' })
  await send(elsewhere.env)
  assert.equal(elsewhere.posts.length, 0)
  assert.equal(elsewhere.mails.length, 0, 'never sends email for error reports')
  for (const url of ['http://discord.com/api/webhooks/1/tok', 'https://discord.com.evil.example/api/webhooks/1/tok', '', undefined]) assert.equal(isDiscordWebhook(url), false, String(url))
})

test('a Discord alert fits in one message and cannot close its own code block', () => {
  const body = discordBody({ subject: 'Exo Launcher 3.26.0: UiError (new error)', text: 'x'.repeat(5000) + '```end' })
  assert.ok(body.content.length <= 2000, String(body.content.length))
  assert.equal(body.content.split('```').length, 3, 'one code block')
})

test('alertOf and discordBody always keep the Error line at the top even when the log is long', async () => {
  const { env, posts } = makeEnv()
  const lines = Array.from({ length: 35 }, (_, i) => `[2026-11-23T07:00:${String(i % 60).padStart(2, '0')}.000Z] WARN Store layout not recognized: entry ${i} ` + 'x'.repeat(60))
  const longExcerpt = [
    'Exo Launcher 3.29.0 · Windows 10.0.26200.0 · 2026-11-23 07:27 UTC',
    "Error (exo ai): Its installer didn't start.",
    '',
    `Exo's log (${lines.length} entries):`,
    ...lines,
  ].join('\n')
  assert.equal((await send(env, { ...sample, version: '3.29.0', errorType: 'AppError', excerpt: longExcerpt })).status, 202)
  const text = contentOf(posts[0])
  assert.ok(text.length <= 2000, String(text.length))
  assert.match(text, /Error \(exo ai\): Its installer didn't start\./)
  assert.match(text, /entry 34/)
})

test("the digest lists yesterday's groups, most reports first, and marks the ones that are new", async () => {
  const db = fakeD1()
  const now = Date.UTC(2026, 10, 10, 4, 20)
  const add = (version, type, day, count) => db.sql.prepare('INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES (?, ?, ?, ?, ?, ?)').run('launcher', version, type, day, count, 'reports/x.json')
  add('3.26.0', 'UiError', '2026-11-09', 3)
  add('3.26.0', 'HostCallFailed', '2026-11-09', 12)
  add('3.26.0', 'HostCallFailed', '2026-11-08', 5)
  add('3.25.0', 'Hang', '2026-11-08', 40)
  const digest = await digestOf(db, now)
  assert.equal(digest.subject, 'Exo errors, 2026-11-09: 15 in 2 groups')
  assert.deepEqual(digest.text.trim().split('\n').map((line) => line.trim()), [
    '12  Exo Launcher 3.26.0  HostCallFailed',
    '3  Exo Launcher 3.26.0  UiError  (new)',
  ])
  assert.equal(await digestOf(db, now + 2 * DAY), null, 'a day with no reports sends no digest')
})

test('the daily job posts the digest to Discord (never emails) and removes counts older than 90 days', async () => {
  const { env, db, mails, posts } = makeEnv()
  const day = (ago) => utcDay(Date.now() - ago * DAY)
  const add = (d, count) => db.sql.prepare('INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES (?, ?, ?, ?, ?, ?)').run('cinema', '2.0.0', 'Hang', d, count, 'reports/x.json')
  add(day(1), 4)
  add(day(120), 9)
  await worker.scheduled({}, env)
  assert.equal(mails.length, 0)
  assert.equal(posts.length, 1)
  assert.match(contentOf(posts[0]), /^\*\*Exo errors, \d{4}-\d{2}-\d{2}: 4 in 1 group\*\*/)
  assert.deepEqual(db.sql.prepare('SELECT day FROM crash_days').all().map((row) => row.day), [day(1)])
})

test('invented error types cannot alert without end: at most ALERTS_PER_DAY alerts a UTC day, and the digest still counts every group', async () => {
  const { env, db, mails, posts } = makeEnv()
  for (let i = 0; i < ALERTS_PER_DAY + 25; i++) assert.equal((await send(env, { ...sample, errorType: `Invented${i}Error` })).status, 202)
  assert.equal(mails.length, 0)
  assert.equal(posts.length, ALERTS_PER_DAY + 1, 'the cap, then one message that alerts are paused')
  assert.match(contentOf(posts.at(-1)), /alerts paused for today/)
  const groups = db.sql.prepare('SELECT count(*) AS n FROM crash_days').get().n
  assert.equal(groups, ALERTS_PER_DAY + 25)
  // The next UTC day starts a new count; the daily job removes tallies older than 90 days.
  const tomorrow = new Date(Date.now() + DAY).toISOString().slice(0, 10)
  db.sql.prepare('INSERT INTO alert_days (day, sent) VALUES (?, ?)').run('2000-01-01', 5)
  await pruneAlertDays(db, Date.now())
  assert.equal(db.sql.prepare("SELECT count(*) AS n FROM alert_days WHERE day = '2000-01-01'").get().n, 0)
  assert.equal(db.sql.prepare('SELECT count(*) AS n FROM alert_days WHERE day = ?').get(tomorrow).n, 0)
})
test('claimAlert walks its table: send to the cap, pause once, then hold; a missing tally table always sends', async () => {
  const db = fakeD1()
  const seen = []
  for (let i = 0; i < ALERTS_PER_DAY + 3; i++) seen.push(await claimAlert(db, '2026-11-10'))
  assert.deepEqual(seen.slice(0, ALERTS_PER_DAY), Array(ALERTS_PER_DAY).fill('send'))
  assert.deepEqual(seen.slice(ALERTS_PER_DAY), ['pause', 'hold', 'hold'])
  assert.equal(await claimAlert(db, '2026-11-11'), 'send', 'a new day starts again')
  const without = fakeD1({ migrations: MIGRATIONS.slice(0, 3) })
  assert.equal(await claimAlert(without, '2026-11-10'), 'send')
})

test('with migrations 0005 and 0006 not applied, a new error still alerts Discord and the daily digest still goes, naming the failed steps', async () => {
  const { env, mails, posts } = makeEnv({ db: fakeD1({ migrations: MIGRATIONS.slice(0, 3) }) })
  assert.equal((await send(env)).status, 202)
  assert.equal(posts.length, 1, 'the alert went to Discord')
  await worker.scheduled({}, env)
  assert.equal(posts.length, 2, 'the daily digest went to Discord')
  assert.equal(mails.length, 0)
  assert.match(contentOf(posts[1]), /These steps of the daily job failed: alert tally pruning, issue tally pruning\./)
})

test('a daily step that throws stops nothing else: the digest still goes to Discord, with the step named', async () => {
  const db = fakeD1({ failOn: 'DELETE FROM crash_index' })
  const yesterday = utcDay(Date.now() - DAY)
  db.sql.prepare("INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES ('launcher', '3.26.0', 'UiError', ?, 4, 'k')").run(yesterday)
  const { env, mails, posts } = makeEnv({ db })
  await worker.scheduled({}, env)
  assert.equal(mails.length, 0)
  assert.equal(posts.length, 1)
  assert.match(contentOf(posts[0]), /^\*\*Exo errors, .*: 4 in 1 group\*\*/)
  assert.match(contentOf(posts[0]), /UiError/)
  assert.match(contentOf(posts[0]), /These steps of the daily job failed: crash index pruning\./)
})

test('the digest says how many alerts were held back yesterday', async () => {
  const db = fakeD1()
  const yesterday = utcDay(Date.now() - DAY)
  db.sql.prepare("INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES ('launcher', '3.26.0', 'UiError', ?, 1, 'k')").run(yesterday)
  db.sql.prepare('INSERT INTO alert_days (day, sent) VALUES (?, ?)').run(yesterday, ALERTS_PER_DAY + 7)
  const { env, mails, posts } = makeEnv({ db })
  await worker.scheduled({}, env)
  assert.equal(mails.length, 0)
  assert.match(contentOf(posts[0]), /7 alerts were held back by the daily cap/)
})

test('the daily message: nothing to say is no alert; a failure alone still alerts', () => {
  assert.equal(dailyMessage(null, [], 0, '2026-11-09'), null)
  const failure = dailyMessage(null, ['digest'], 0, '2026-11-09')
  assert.equal(failure.subject, 'Exo errors, 2026-11-09: the daily job had a problem')
  assert.match(failure.text, /failed: digest\./)
})
/**
 * The GitHub issues' tests (src/issues.js, src/fingerprint.js, src/user-report.js, README.md "Issues"): the real Worker
 * against node:sqlite as D1, a Map as R2 and a fake GitHub API that keeps issues in memory. Nothing reaches GitHub.
 *
 * How it could fail: one bug opens an issue per report (spam) or two bugs share one; a rebuild's new line numbers split a
 * group; later reports comment on every report instead of editing the count; a closed issue never reopens when the error
 * comes back in a newer version, or reopens for an old version still in use; thresholds comment twice or never; GitHub
 * down loses the report, fails its answer or loses the issue for good; no token breaks the Worker; invented errors open
 * issues without end; a Report a problem message mentions people, holds personal data in its log, or never gets filed.
 *   node --test test
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DatabaseSync } from 'node:sqlite'
import { readFileSync } from 'node:fs'
import worker from '../src/worker.js'
import { limiters } from './limiters.mjs'
import { fingerprintOf, legacyFingerprintOf, normalizeFrame, signatureOf } from '../src/fingerprint.js'
import { DAILY, compareVersions, dueNow, safeTitle, withStats } from '../src/issues.js'
import { parseUserReport } from '../src/user-report.js'

const MIGRATIONS = ['0002_telemetry.sql', '0003_crash_index.sql', '0004_crash_days.sql', '0005_alert_days.sql', '0006_github_issues.sql']
  .map((name) => readFileSync(new URL(`../migrations/${name}`, import.meta.url), 'utf8'))
const INSTALL = '5b0f3c9e-6a2d-4f7e-9c1b-2d8e4a7f0b13'

const stack = (line = 42) => [
  'Exo Launcher 3.26.0 · Windows 10.0.26300.1 · 2026-11-19 10:00 UTC',
  'Error (library): Steam did not answer',
  '',
  "Exo's log (2 entries):",
  '[2026-11-19T09:59:58.001Z] CRASH: System.NullReferenceException: Object reference not set to an instance of an object.',
  `   at ExoLauncher.Services.SteamService.<RefreshAsync>b__12_0(Game game) in <path>:line ${line}`,
  `   at ExoLauncher.Services.LibraryService.LoadAsync\`1(CancellationToken ct) in <path>:line ${line + 7}`,
  '   at ExoLauncher.App.OnLaunched(LaunchActivatedEventArgs e)',
  '   at System.Threading.Tasks.Task.Run()',
].join('\n')

const crash = { app: 'launcher', version: '3.26.0', osBuild: '10.0.26300.1', errorType: 'NullReferenceException', excerpt: stack() }

function fakeD1() {
  const sql = new DatabaseSync(':memory:')
  for (const migration of MIGRATIONS) sql.exec(migration)
  const statement = (text) => {
    let args = []
    const s = {
      bind(...values) { args = values; return s },
      async run() { return { meta: { changes: sql.prepare(text).run(...args).changes } } },
      async all() { return { results: sql.prepare(text).all(...args) } },
      async first() { return sql.prepare(text).get(...args) ?? null },
    }
    return s
  }
  return { sql, prepare: statement, async batch(list) { const out = []; for (const st of list) out.push(await st.run()); return out } }
}

/** GitHub's issues API for one repository, in memory. `down` answers 503 (or, as 'network', throws) to everything. */
function fakeGitHub() {
  const gh = { issues: new Map(), comments: [], labels: new Set(['app:launcher', 'app:cinema', 'app:browser', 'kind:crash', 'kind:freeze', 'kind:user-report', 'new', 'regression', 'count-10', 'count-100', 'count-1000']), calls: [], down: false, tokens: new Set() }
  gh.fetcher = async (url, init) => {
    const { pathname } = new URL(url)
    const method = init.method
    gh.calls.push(`${method} ${pathname.replace('/repos/UhhErix/Exo-Reports', '')}`)
    gh.tokens.add(init.headers.Authorization)
    if (gh.down === 'network') throw new TypeError('fetch failed')
    if (gh.down) return new Response('unavailable', { status: 503 })
    assert.ok(pathname.startsWith('/repos/UhhErix/Exo-Reports/'), `only the reports repository: ${pathname}`)
    const body = init.body ? JSON.parse(init.body) : null
    const path = pathname.replace('/repos/UhhErix/Exo-Reports', '')
    let m
    if (method === 'POST' && path === '/labels') {
      if (gh.labels.has(body.name)) return Response.json({ message: 'already_exists' }, { status: 422 })
      gh.labels.add(body.name)
      return Response.json(body, { status: 201 })
    }
    if (method === 'POST' && path === '/issues') {
      const number = gh.issues.size + 1
      for (const label of body.labels) assert.ok(gh.labels.has(label), `label ${label} exists before it is used`)
      gh.issues.set(number, { number, state: 'open', title: body.title, body: body.body, labels: [...body.labels] })
      return Response.json({ number }, { status: 201 })
    }
    if ((m = /^\/issues\/(\d+)$/.exec(path))) {
      const issue = gh.issues.get(Number(m[1]))
      if (method === 'GET') return Response.json(issue)
      Object.assign(issue, body)
      return Response.json(issue)
    }
    if ((m = /^\/issues\/(\d+)\/comments$/.exec(path))) {
      gh.comments.push({ number: Number(m[1]), body: body.body })
      return Response.json({}, { status: 201 })
    }
    if ((m = /^\/issues\/(\d+)\/labels$/.exec(path))) {
      const issue = gh.issues.get(Number(m[1]))
      for (const label of body.labels) if (!issue.labels.includes(label)) issue.labels.push(label)
      return Response.json(issue.labels)
    }
    throw new Error(`unexpected ${method} ${path}`)
  }
  return gh
}

const HOOK = 'https://discord.com/api/webhooks/123456789012345678/tok_En-1'

/** The Worker's env: fake R2, D1 and GitHub, and a mail binding and a Discord webhook that only record (the alerts before GitHub). */
function makeEnv({ token = 'github_pat_test', db = fakeD1() } = {}) {
  const objects = new Map()
  const github = fakeGitHub()
  const mails = []
  const posts = []
  const env = {
    CONTACT_TO: 'team@example.test',
    CONTACT_MAIL: { async send(mail) { mails.push(mail) } },
    EmailMessage: class { constructor(from, to, raw) { Object.assign(this, { from, to, raw }) } },
    DISCORD_WEBHOOK_REPORTS: HOOK,
    REPORTS: {
      async put(key, body) { objects.set(key, body) },
      async get(key) { return objects.has(key) ? { text: async () => objects.get(key) } : null },
      async delete(keys) { for (const key of [keys].flat()) objects.delete(key) },
    },
    ...limiters().bindings,
    TELEMETRY: db,
    fetcher: async (url, init) => {
      if (url === HOOK) { posts.push(JSON.parse(init.body)); return new Response(null, { status: 204 }) }
      return github.fetcher(url, init)
    },
  }
  if (token) env.GITHUB_REPORTS_TOKEN = token
  return { env, db, objects, github, mails, posts }
}

const post = (env, path, body) => worker.fetch(new Request(`https://reports.getexo.app${path}`, {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
}), env)
const send = (env, body = crash) => post(env, '/v1/report', body)
const groups = (db) => db.sql.prepare('SELECT * FROM crash_groups ORDER BY first_day, fingerprint').all()

// ---- Grouping ----

test('frames are normalised: line numbers, lambda numbering, generic arity and arguments do not split a group', async () => {
  assert.equal(normalizeFrame('ExoLauncher.Services.SteamService.<RefreshAsync>b__12_0(Game game) in <path>:line 42'), 'ExoLauncher.Services.SteamService.<RefreshAsync>')
  assert.equal(normalizeFrame('ExoLauncher.Services.LibraryService.LoadAsync`1(CancellationToken ct)'), 'ExoLauncher.Services.LibraryService.LoadAsync')
  assert.equal(normalizeFrame('render (<url>:12:34)'), 'render')
  assert.deepEqual(signatureOf(crash).frames, [
    'ExoLauncher.Services.SteamService.<RefreshAsync>',
    'ExoLauncher.Services.LibraryService.LoadAsync',
    'ExoLauncher.App.OnLaunched',
  ])
  assert.equal(await fingerprintOf(crash), await fingerprintOf({ ...crash, excerpt: stack(99), version: '3.27.0', osBuild: '10.0.22631.1' }))
})

test('another app, error type or top frame is another group; a frame below the top three is not', async () => {
  const base = await fingerprintOf(crash)
  assert.notEqual(await fingerprintOf({ ...crash, app: 'cinema' }), base)
  assert.notEqual(await fingerprintOf({ ...crash, errorType: 'InvalidOperationException' }), base)
  assert.notEqual(await fingerprintOf({ ...crash, excerpt: crash.excerpt.replace('OnLaunched', 'OnResumed') }), base)
  assert.equal(await fingerprintOf({ ...crash, excerpt: crash.excerpt.replace('Task.Run', 'Task.Wait') }), base)
})

test('a report with no stack is grouped by its error line, numbers and quoted values taken out', async () => {
  const ui = (message) => ({ ...crash, errorType: 'UiError', excerpt: `Exo Launcher 3.26.0 · Windows 10.0.26300.1 · 2026-11-19 10:00 UTC\nError (ui): ${message}\n` })
  assert.equal(await fingerprintOf(ui('Cannot read "name" of undefined at row 12')), await fingerprintOf(ui('Cannot read "title" of undefined at row 7')))
  assert.notEqual(await fingerprintOf(ui('Cannot read "name" of undefined')), await fingerprintOf(ui('Network request failed')))
})

test('async CLR and WinUI plumbing frames are skipped before picking top frames', async () => {
  const asyncTrace = (caller) => ({
    ...crash,
    excerpt: [
      'Exo Launcher 3.30.2 · Windows 10.0.26300.1 · 2026-11-24 10:00 UTC',
      'Error (rpc): NullReferenceException',
      '   at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()',
      '   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)',
      '   at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task)',
      `   at ${caller}()`,
      '   at ExoLauncher.Services.WebHostBridge.HandleAsync()',
    ].join('\n'),
  })
  const a = asyncTrace('ExoLauncher.Services.StoreService.HomeAsync')
  const b = asyncTrace('ExoLauncher.Services.DiscordSession.ConnectOnceAsync')
  assert.deepEqual(signatureOf(a).frames, [
    'ExoLauncher.Services.StoreService.HomeAsync',
    'ExoLauncher.Services.WebHostBridge.HandleAsync',
  ])
  assert.notEqual(await fingerprintOf(a), await fingerprintOf(b))
})

test('a report for an existing legacy plumbing-frame group attaches to it and does not open a duplicate issue', async () => {
  const { env, db, github } = makeEnv()
  const report = {
    ...crash,
    version: '3.30.3',
    excerpt: [
      'Exo Launcher 3.30.3 · Windows 10.0.26300.1 · 2026-11-24 10:00 UTC',
      'Error (rpc): NullReferenceException',
      '   at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()',
      '   at System.Runtime.CompilerServices.TaskAwaiter.GetResult()',
      '   at Microsoft.UI.Dispatching.DispatcherQueue.TryEnqueue()',
      '   at ExoLauncher.Services.StoreService.HomeAsync()',
      '   at ExoLauncher.Services.WebHostBridge.HandleAsync()',
    ].join('\n'),
  }
  const modern = await fingerprintOf(report)
  const legacy = await legacyFingerprintOf(report)
  assert.notEqual(modern, legacy)

  github.issues.set(14, {
    number: 14,
    state: 'open',
    title: '[launcher] NullReferenceException: System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw',
    body: `<!-- exo:group ${legacy} -->\n<!-- exo:stats -->\n**Seen 1 time**\n<!-- /exo:stats -->`,
    labels: ['app:launcher', 'kind:crash', 'v3.30.2'],
  })
  db.sql.prepare(
    'INSERT INTO crash_groups (fingerprint, app, error_type, count, versions, newest_version, first_day, last_day, os_build, sample_key, issue_number, synced_count, synced_version, pending) '
    + 'VALUES (?, ?, ?, 1, ?, ?, ?, ?, ?, ?, 14, 1, ?, 0)',
  ).run(legacy, report.app, report.errorType, '["3.30.2"]', '3.30.2', '2026-11-20', '2026-11-20', report.osBuild, 'reports/2026-11-20/old.json', '3.30.2')

  assert.equal((await send(env, report)).status, 202)
  assert.equal(github.issues.size, 1)
  assert.equal(github.calls.filter((call) => call === 'POST /issues').length, 0)
  assert.match(github.issues.get(14).body, /\*\*Seen 2 times\*\*/)
  assert.equal(groups(db).length, 1)
  assert.equal(groups(db)[0].fingerprint, legacy)
  assert.equal(groups(db)[0].count, 2)
  assert.equal(groups(db)[0].newest_version, '3.30.3')
})

// ---- Create versus update ----

test('the first report opens one issue with the excerpt, version, Windows build and labels; later ones edit its count', async () => {
  const { env, db, github } = makeEnv()
  assert.equal((await send(env)).status, 202)
  assert.equal(github.issues.size, 1)
  const issue = github.issues.get(1)
  assert.match(issue.title, /^\[launcher\] NullReferenceException: ExoLauncher\.Services\.SteamService\.<RefreshAsync>$/)
  assert.match(issue.body, /3\.26\.0 on Windows 10\.0\.26300\.1/)
  assert.match(issue.body, /Object reference not set/)
  assert.match(issue.body, /\*\*Seen 1 time\*\*/)
  assert.deepEqual(issue.labels.sort(), ['app:launcher', 'kind:crash', 'new', 'v3.26.0'])
  assert.equal(groups(db)[0].issue_number, 1)

  for (let i = 0; i < 4; i++) await send(env, { ...crash, excerpt: stack(50 + i) })
  assert.equal(github.issues.size, 1, 'one issue for the group')
  assert.match(github.issues.get(1).body, /\*\*Seen 5 times\*\*/)
  assert.equal(github.comments.length, 0, 'no comment below 10')
  assert.equal(groups(db)[0].pending, 0)
})

test('the stats block is replaced in place, so what people wrote in the issue stays', () => {
  const group = { count: 3, versions: '["3.26.0","3.26.1"]', first_day: '2026-11-19', last_day: '2026-11-20', newest_version: '3.26.1' }
  const body = 'Intro\n<!-- exo:stats -->\nold\n<!-- /exo:stats -->\nNotes by the team'
  const next = withStats(body, group)
  assert.match(next, /^Intro\n<!-- exo:stats -->\n\*\*Seen 3 times\*\* · versions 3\.26\.0, 3\.26\.1 .*newest version 3\.26\.1\n<!-- \/exo:stats -->\nNotes by the team$/)
  assert.match(withStats('no block', group), /^<!-- exo:stats -->[\s\S]*no block$/)
})

test('a flood of one error is a few edits, not one per report, and the daily job writes the final count', async () => {
  const { env, db, github } = makeEnv()
  for (let i = 0; i < 60; i++) await send(env)
  const edits = github.calls.filter((call) => call === 'PATCH /issues/1').length
  assert.ok(edits < 25, `${edits} edits for 60 reports`)
  assert.equal(groups(db)[0].count, 60)
  await worker.scheduled({}, env)
  assert.match(github.issues.get(1).body, /\*\*Seen 60 times\*\*/)
  assert.equal(groups(db)[0].pending, 0)
  assert.equal(dueNow({ issue_number: 1, count: 30, synced_count: 28, noted_threshold: 10 }, false), false)
  assert.equal(dueNow({ issue_number: 1, count: 35, synced_count: 28, noted_threshold: 10 }, false), true)
})

// ---- Thresholds ----

test('10, 100 and 1000 reports each comment once and add their label; nothing else comments', async () => {
  const { env, db, github } = makeEnv()
  for (let i = 0; i < 100; i++) await send(env)
  assert.deepEqual(github.comments.map((c) => c.body.match(/^Seen (\d+) times/)?.[1]), ['10', '100'])
  assert.ok(github.issues.get(1).labels.includes('count-10'))
  assert.ok(github.issues.get(1).labels.includes('count-100'))
  db.sql.prepare('UPDATE crash_groups SET count = 999').run()
  await send(env)
  assert.equal(github.comments.length, 3)
  assert.match(github.comments[2].body, /^Seen 1000 times/)
  await send(env)
  await worker.scheduled({}, env)
  assert.equal(github.comments.length, 3, 'never twice')
})

// ---- Regressions ----

test('a closed issue reopens as a regression when the error comes back in a newer version, not for an old one', async () => {
  const { env, github } = makeEnv()
  await send(env)
  await send(env, { ...crash, version: '3.26.1' })
  github.issues.get(1).state = 'closed'

  await send(env, { ...crash, version: '3.26.0' })
  await send(env, { ...crash, version: '3.26.1' })
  await worker.scheduled({}, env)
  assert.equal(github.issues.get(1).state, 'closed', 'old versions still in use do not reopen it')
  assert.match(github.issues.get(1).body, /\*\*Seen 4 times\*\*/, 'the count still moves')

  await send(env, { ...crash, version: '3.27.0' })
  const issue = github.issues.get(1)
  assert.equal(issue.state, 'open')
  assert.equal(issue.state_reason, 'reopened')
  assert.ok(issue.labels.includes('regression'))
  assert.ok(issue.labels.includes('v3.27.0'))
  assert.match(github.comments.at(-1).body, /Seen again in 3\.27\.0, newer than 3\.26\.1/)
  assert.ok(github.labels.has('v3.27.0'), 'the version label is made on the fly')
})

test('versions compare part by part as numbers', () => {
  assert.equal(compareVersions('3.10.0', '3.9.9'), 1)
  assert.equal(compareVersions('3.26.0', '3.26.0'), 0)
  assert.equal(compareVersions('2.0.0', '10.0.0'), -1)
})

// ---- GitHub down, no token, the cap ----

test('GitHub down never fails a report; the group waits and the daily job files it once GitHub is back', async () => {
  for (const down of [true, 'network']) {
    const { env, db, objects, github } = makeEnv()
    github.down = down
    assert.equal((await send(env)).status, 202)
    assert.equal(objects.size, 1)
    assert.equal(groups(db)[0].issue_number, null)
    assert.equal(groups(db)[0].pending, 1)
    await worker.scheduled({}, env)
    assert.equal(groups(db)[0].pending, 1, 'still waiting while GitHub is down')
    github.down = false
    await worker.scheduled({}, env)
    assert.equal(github.issues.size, 1)
    assert.equal(groups(db)[0].issue_number, 1)
    assert.equal(groups(db)[0].pending, 0)
  }
})

test('without the token the Worker stores and counts only, and files the backlog once the token is set', async () => {
  const { env, db, objects, github } = makeEnv({ token: null })
  for (let i = 0; i < 12; i++) assert.equal((await send(env)).status, 202)
  assert.equal(objects.size, 12)
  assert.equal(github.calls.length, 0, 'nothing reaches GitHub')
  await worker.scheduled({}, env)
  assert.equal(github.calls.length, 0)
  env.GITHUB_REPORTS_TOKEN = 'github_pat_now'
  await worker.scheduled({}, env)
  assert.equal(github.issues.size, 1)
  assert.match(github.issues.get(1).body, /\*\*Seen 12 times\*\*/)
  assert.ok(github.issues.get(1).labels.includes('count-10'), 'a backlog past a threshold is labelled, not commented')
  assert.equal(github.comments.length, 0)
  assert.equal(groups(db)[0].pending, 0)
  assert.deepEqual([...github.tokens], ['Bearer github_pat_now'])
})

test('until the token is set the team is told in Discord only (never emailed); once it is set, only on GitHub', async () => {
  const { env, db, github, mails, posts } = makeEnv({ token: null })
  assert.equal((await send(env)).status, 202)
  assert.equal(mails.length, 0, 'never emails the team on errors')
  assert.equal(posts.length, 1, 'posts the new error to the Discord channel')
  assert.match(posts[0].content, /NullReferenceException \(new error\)/)
  assert.equal((await post(env, '/v1/user-report', message)).status, 202)
  assert.equal(mails.length, 0, 'never emails the team on user reports')
  assert.equal(posts.length, 2, 'posts the Report a problem message to Discord')
  assert.match(posts[1].content, /Report a problem/)
  assert.match(posts[1].content, /The library went blank/)
  assert.deepEqual(posts[1].allowed_mentions, { parse: [] }, "the player's words mention nobody")
  const yesterday = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
  db.sql.prepare("INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES ('launcher', '3.25.0', 'UiError', ?, 4, 'k')").run(yesterday)
  await worker.scheduled({}, env)
  assert.equal(mails.length, 0, 'never emails the digest')
  assert.equal(posts.length, 3, "yesterday's digest goes to Discord")
  assert.equal(github.calls.length, 0)

  env.GITHUB_REPORTS_TOKEN = 'github_pat_now'
  await worker.scheduled({}, env)
  assert.equal(github.issues.size, 2, 'the backlog is filed: the error group and the message')
  assert.equal(mails.length, 0, 'no email once GitHub tells the team')
  for (let i = 0; i < 9; i++) await send(env)
  await post(env, '/v1/user-report', { ...message, message: 'Again' })
  assert.equal(mails.length, 0, 'still no email')
  assert.equal(posts.length, 3, 'nothing more on Discord')
  assert.ok(github.comments.some((comment) => /Seen 10 times/.test(comment.body)), 'the 10th report comments on GitHub instead')
})

test(`invented errors open at most ${DAILY.crash} issues a day; the rest wait for the next day's job`, async () => {
  const { env, db, github } = makeEnv()
  for (let i = 0; i < DAILY.crash + 5; i++) assert.equal((await send(env, { ...crash, errorType: `Invented${i}Error` })).status, 202)
  assert.equal(github.issues.size, DAILY.crash)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM crash_groups WHERE issue_number IS NULL AND pending = 1').get().n, 5)
  db.sql.prepare('DELETE FROM issue_days').run()
  await worker.scheduled({}, env)
  assert.equal(github.issues.size, DAILY.crash + 5)
})

test('two reports racing for a new group open one issue', async () => {
  const { env, github } = makeEnv()
  await Promise.all([send(env), send(env), send(env)])
  await worker.scheduled({}, env)
  assert.equal(github.issues.size, 1)
})

test('a group holds counts, versions and days: no install ID, no excerpt', async () => {
  const { env, db } = makeEnv()
  await send(env, { ...crash, installId: INSTALL })
  const row = groups(db)[0]
  assert.ok(!Object.values(row).some((value) => String(value).includes(INSTALL)))
  assert.ok(!Object.values(row).some((value) => String(value).includes('Object reference')))
})

// ---- Report a problem ----

const message = { app: 'launcher', version: '3.26.0', osBuild: '10.0.26300.1', message: 'The library went blank after @someone said #12 was fixed.', excerpt: 'Error (library): Steam did not answer', contact: 'player@example.com', installId: INSTALL }

test('a Report a problem message is filed at once as its own kind:user-report issue and answers its number', async () => {
  const { env, objects, github } = makeEnv()
  const answer = await post(env, '/v1/user-report', message)
  assert.equal(answer.status, 202)
  assert.deepEqual(await answer.json(), { issue: 1 })
  const issue = github.issues.get(1)
  assert.deepEqual(issue.labels.sort(), ['app:launcher', 'kind:user-report', 'new', 'v3.26.0'])
  assert.ok(!/[@#]/.test(issue.title), 'no mention or reference in the title')
  assert.match(issue.body, /```+text\nThe library went blank after @someone/, 'the message sits in a code block, so it mentions nobody')
  assert.match(issue.body, /\*\*Contact:\*\* `player@example\.com`/)
  assert.match(issue.body, /Steam did not answer/)
  assert.equal([...objects.keys()].filter((key) => /^reports\/\d{4}-\d{2}-\d{2}\/user-/.test(key)).length, 1, 'stored under reports/, the 30-day rule')
  await post(env, '/v1/user-report', { ...message, message: 'Second', contact: '' })
  assert.equal(github.issues.size, 2, 'one issue per message')
  assert.match(github.issues.get(2).body, /\*\*Contact:\*\* none left/)
})

test('with GitHub down or no token a message is stored, answers a reference and is filed by the daily job', async () => {
  const { env, db, github } = makeEnv()
  github.down = true
  const answer = await post(env, '/v1/user-report', { ...message, excerpt: '' })
  assert.equal(answer.status, 202)
  const { ref } = await answer.json()
  assert.match(ref, /^[0-9a-f]{8}$/)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM issue_queue').get().n, 1)
  github.down = false
  await worker.scheduled({}, env)
  assert.equal(github.issues.size, 1)
  assert.match(github.issues.get(1).body, new RegExp(`reference ${ref}`))
  assert.match(github.issues.get(1).body, /Logs: not attached\./)
  assert.equal(db.sql.prepare('SELECT COUNT(*) AS n FROM issue_queue').get().n, 0)

  const none = makeEnv({ token: null })
  assert.match((await (await post(none.env, '/v1/user-report', message)).json()).ref, /^[0-9a-f]{8}$/)
  assert.equal(none.github.calls.length, 0)
})

test(`messages open at most ${DAILY.user} issues a day; past it they still store and wait`, async () => {
  const { env, db, github } = makeEnv()
  db.sql.prepare("INSERT INTO issue_days (day, kind, used) VALUES (?, 'user', ?)").run(new Date().toISOString().slice(0, 10), DAILY.user)
  const answer = await post(env, '/v1/user-report', message)
  assert.equal(answer.status, 202)
  assert.ok((await answer.json()).ref)
  assert.equal(github.issues.size, 0)
})

test('a message is refused for a log with personal data, a bad shape or a browser origin; its own words are not', async () => {
  const { env } = makeEnv()
  assert.equal((await post(env, '/v1/user-report', { ...message, excerpt: 'at C:\\Users\\ada\\x.cs' })).status, 422)
  assert.equal((await post(env, '/v1/user-report', { ...message, message: '   ' })).status, 400)
  assert.equal((await post(env, '/v1/user-report', { ...message, extra: 1 })).status, 400)
  assert.equal((await post(env, '/v1/user-report', { ...message, contact: 'a\nb' })).status, 400)
  assert.equal((await post(env, '/v1/user-report', { ...message, message: 'x'.repeat(4001) })).status, 400)
  const fromPage = await worker.fetch(new Request('https://reports.getexo.app/v1/user-report', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'https://evil.example' }, body: JSON.stringify(message) }), env)
  assert.equal(fromPage.status, 403)
  assert.deepEqual(parseUserReport({ ...message, message: 'Mail me at ada@example.com\r\nplease' }).message, 'Mail me at ada@example.com\nplease')
})

test('Delete my data removes a stored message too', async () => {
  const { env, objects } = makeEnv()
  await post(env, '/v1/user-report', message)
  assert.equal(objects.size, 1)
  const deleted = await post(env, '/v1/telemetry/delete', { install_id: INSTALL })
  assert.equal(deleted.status, 204)
  assert.equal(objects.size, 0)
})

test('titles from untrusted words are one line with no mentions or references', () => {
  assert.equal(safeTitle('a\n@b  #3 c'), 'a b 3 c')
  assert.equal(safeTitle('x'.repeat(200)).length, 100)
})

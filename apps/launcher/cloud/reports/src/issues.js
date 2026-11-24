import { GitHubError, gitHubOf } from './github.js'
import { fingerprintOf, legacyFingerprintOf, signatureOf } from './fingerprint.js'
import { utcDay } from './telemetry-store.js'
import { trimExcerpt } from './triage.js'

export const THRESHOLDS = [10, 100, 1000]
export const DAILY = { crash: 50, user: 100, sync: 2000 }
export const QUEUE_MAX = 500
export const FLUSH_MAX = 100
export const VERSIONS_KEPT = 20
export const KEEP_DAYS = 90
const DAY_MS = 24 * 60 * 60 * 1000
const APP_NAMES = { launcher: 'Exo Launcher', cinema: 'Exo Cinema', browser: 'Exo Browser' }
const FILING = -1
const DIAGNOSIS_MODEL = '@cf/meta/llama-3.3-70b-instruct-fp8-fast'

export function compareVersions(a, b) {
  const x = String(a).split('.').map(Number)
  const y = String(b).split('.').map(Number)
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] ?? 0) - (y[i] ?? 0)
    if (d !== 0) return d > 0 ? 1 : -1
  }
  return 0
}

export const reached = (count) => THRESHOLDS.filter((at) => count >= at).at(-1) ?? 0

export async function claim(db, day, kind) {
  try {
    const row = await db.prepare(
      'INSERT INTO issue_days (day, kind, used) VALUES (?, ?, 1) ON CONFLICT (day, kind) DO UPDATE SET used = used + 1 RETURNING used',
    ).bind(day, kind).first()
    return Number(row?.used) <= DAILY[kind]
  } catch {
    return false
  }
}

export async function recordGroup(db, report, key, day) {
  let fingerprint = await fingerprintOf(report)
  let before = await groupOf(db, fingerprint)
  if (!before) {
    const legacy = await legacyFingerprintOf(report)
    if (legacy !== fingerprint) {
      const existing = await groupOf(db, legacy)
      if (existing) {
        fingerprint = legacy
        before = existing
      }
    }
  }
  if (!before) {
    const inserted = await db.prepare(
      'INSERT INTO crash_groups (fingerprint, app, error_type, count, versions, newest_version, first_day, last_day, os_build, sample_key) '
      + 'VALUES (?, ?, ?, 1, ?, ?, ?, ?, ?, ?) ON CONFLICT (fingerprint) DO NOTHING',
    ).bind(fingerprint, report.app, report.errorType, JSON.stringify([report.version]), report.version, day, day, report.osBuild, key).run()
    if (inserted.meta?.changes === 1) return { group: await groupOf(db, fingerprint), newVersion: true }
    before = await groupOf(db, fingerprint)
  }
  const versions = JSON.parse(before.versions)
  const newVersion = !versions.includes(report.version)
  if (newVersion) {
    versions.push(report.version)
    versions.sort(compareVersions)
  }
  const newest = compareVersions(report.version, before.newest_version) > 0 ? report.version : before.newest_version
  await db.prepare(
    'UPDATE crash_groups SET count = count + 1, versions = ?, newest_version = ?, last_day = ?, pending = 1, '
    + 'sample_key = CASE WHEN issue_number IS NULL THEN ? ELSE sample_key END, os_build = CASE WHEN issue_number IS NULL THEN ? ELSE os_build END '
    + 'WHERE fingerprint = ?',
  ).bind(JSON.stringify(versions.slice(-VERSIONS_KEPT)), newest, day, key, report.osBuild, fingerprint).run()
  return { group: await groupOf(db, fingerprint), newVersion }
}

const groupOf = (db, fingerprint) => db.prepare('SELECT * FROM crash_groups WHERE fingerprint = ?').bind(fingerprint).first()

export function dueNow(group, newVersion) {
  if (group.issue_number === null || newVersion) return true
  if (group.count <= THRESHOLDS[0] || reached(group.count) > group.noted_threshold) return true
  return group.count >= group.synced_count * 1.25
}

export const versionLabel = (version) => `v${version}`

function fenced(text, info = 'text') {
  const longest = Math.max(2, ...[...text.matchAll(/`+/g)].map((m) => m[0].length))
  const fence = '`'.repeat(longest + 1)
  return `${fence}${info}\n${text}\n${fence}`
}

export function safeTitle(text, max = 100) {
  const line = String(text).replace(/\s+/g, ' ').replace(/[@#]/g, '').trim()
  return line.length > max ? `${line.slice(0, max - 1)}…` : line
}

export function statsBlock(group) {
  const versions = JSON.parse(group.versions)
  const times = group.count === 1 ? '1 time' : `${group.count} times`
  return `<!-- exo:stats -->\n**Seen ${times}** · ${versions.length === 1 ? 'version' : 'versions'} ${versions.join(', ')} · `
    + `first seen ${group.first_day} · last seen ${group.last_day} · newest version ${group.newest_version}\n<!-- /exo:stats -->`
}

export function withStats(body, group) {
  const block = statsBlock(group)
  const marked = /<!-- exo:stats -->[\s\S]*?<!-- \/exo:stats -->/
  return marked.test(body ?? '') ? body.replace(marked, block) : `${block}\n\n${body ?? ''}`
}

export function kindOf(errorType) {
  return /^(Hang|Freeze)/.test(errorType) ? 'freeze' : 'crash'
}

export function crashTitle(group, sample) {
  const { frames, message } = signatureOf(sample)
  const where = frames[0] ?? message
  return safeTitle(`[${group.app}] ${group.error_type}${where ? `: ${where}` : ''}`)
}

export function crashBody(group, sample, diagnosis) {
  const { frames } = signatureOf(sample)
  return [
    `<!-- exo:group ${group.fingerprint} -->`,
    `**${APP_NAMES[group.app] ?? group.app}** · \`${group.error_type}\` · first reported by ${sample.version} on Windows ${group.os_build}`,
    '',
    statsBlock(group),
    '',
    frames.length ? `Top frames (the group's fingerprint):\n\n${fenced(frames.join('\n'))}` : 'No stack in the report: grouped by its error line.',
    '',
    diagnosis ? `Likely cause (Workers AI, from the log; may be wrong):\n\n> ${diagnosis.replace(/\n/g, '\n> ')}\n` : null,
    `<details><summary>The first report's log excerpt (scrubbed on the PC and by the Worker)</summary>\n\n${fenced(sample.excerpt)}\n\n</details>`,
    '',
    `Stored 30 days in R2 at \`${group.sample_key}\`; \`node triage.mjs\` in cloud/reports lists the groups.`,
  ].filter((part) => part !== null).join('\n')
}

async function diagnosisOf(env, report) {
  if (!env.AI) return null
  let timer
  try {
    const answer = await Promise.race([
      env.AI.run(DIAGNOSIS_MODEL, {
        max_tokens: 180,
        messages: [
          { role: 'system', content: 'You triage crash reports for Exo, a Windows 11 app (a .NET host with a WebView2 React page). From the error type and the end of the log, say in at most three short plain lines: what most likely broke, which part of the app, and how bad it is for the user. Only what the log supports; say "not clear from the log" otherwise. No preamble.' },
          { role: 'user', content: `${report.app} ${report.version}, Windows ${report.osBuild}\n${report.errorType}\n\n${trimExcerpt(report.excerpt, 3000)}` },
        ],
      }),
      new Promise((resolve) => { timer = setTimeout(() => resolve(null), 8000) }),
    ])
    const raw = answer?.response ?? answer?.choices?.[0]?.message?.content
    return typeof raw === 'string' && raw.trim() ? raw.trim().slice(0, 600) : null
  } catch {
    return null
  } finally {
    clearTimeout(timer)
  }
}

async function sampleOf(env, group) {
  const object = await env.REPORTS.get(group.sample_key)
  return object ? JSON.parse(await object.text()) : null
}

export async function syncGroup(env, gh, group, day, sample = null) {
  const db = env.TELEMETRY
  if (group.issue_number === null) return openGroup(env, gh, group, day, sample)
  if (group.issue_number === FILING) return 'capped'
  if (!(await claim(db, day, 'sync'))) return 'capped'

  const number = group.issue_number
  const issue = await gh.getIssue(number)
  let closedVersion = null
  let reopen = false
  if (issue.state === 'closed') {
    closedVersion = group.closed_version ?? group.synced_version ?? group.newest_version
    reopen = compareVersions(group.newest_version, closedVersion) > 0
  }
  await gh.updateIssue(number, { body: withStats(issue.body, group), ...(reopen ? { state: 'open', state_reason: 'reopened' } : {}) })

  const labels = []
  if (reopen) {
    await gh.ensureLabel(versionLabel(group.newest_version))
    labels.push('regression', versionLabel(group.newest_version))
    await gh.comment(number, `Seen again in ${group.newest_version}, newer than ${closedVersion}, the newest version this issue showed when it was closed: reopened as a regression.`)
  }
  const threshold = reached(group.count)
  if (threshold > group.noted_threshold) {
    labels.push(`count-${threshold}`)
    await gh.comment(number, `Seen ${threshold} times (${group.count} now), newest version ${group.newest_version}.`)
  }
  if (labels.length) await gh.addLabels(number, labels)
  await settle(db, group, { closedVersion: reopen ? null : closedVersion, threshold: Math.max(threshold, group.noted_threshold) })
  return 'updated'
}

async function openGroup(env, gh, group, day, sample) {
  const db = env.TELEMETRY
  const taken = await db.prepare('UPDATE crash_groups SET issue_number = ? WHERE fingerprint = ? AND issue_number IS NULL').bind(FILING, group.fingerprint).run()
  if (taken.meta?.changes !== 1) return 'capped'
  try {
    if (!(await claim(db, day, 'crash'))) {
      await release(db, group)
      return 'capped'
    }
    const report = sample ?? await sampleOf(env, group)
    if (!report) {
      await db.prepare('UPDATE crash_groups SET issue_number = NULL, pending = 0 WHERE fingerprint = ?').bind(group.fingerprint).run()
      return 'gone'
    }
    const threshold = reached(group.count)
    await gh.ensureLabel(versionLabel(report.version))
    const issue = await gh.createIssue({
      title: crashTitle(group, report),
      body: crashBody(group, report, await diagnosisOf(env, report)),
      labels: [`app:${group.app}`, `kind:${kindOf(group.error_type)}`, versionLabel(report.version), 'new', ...(threshold ? [`count-${threshold}`] : [])],
    })
    await db.prepare('UPDATE crash_groups SET issue_number = ? WHERE fingerprint = ?').bind(issue.number, group.fingerprint).run()
    await settle(db, group, { closedVersion: null, threshold })
    return 'filed'
  } catch (error) {
    await release(db, group)
    throw error
  }
}

const release = (db, group) => db.prepare('UPDATE crash_groups SET issue_number = NULL WHERE fingerprint = ? AND issue_number = ?').bind(group.fingerprint, FILING).run()

function settle(db, group, { closedVersion, threshold }) {
  return db.prepare(
    'UPDATE crash_groups SET synced_count = ?, synced_version = ?, closed_version = ?, noted_threshold = ?, '
    + 'pending = CASE WHEN count = ? AND newest_version = ? THEN 0 ELSE 1 END WHERE fingerprint = ?',
  ).bind(group.count, group.newest_version, closedVersion, threshold, group.count, group.newest_version, group.fingerprint).run()
}

export async function fileCrash(env, report, key, day) {
  if (!env.TELEMETRY) return
  try {
    const { group, newVersion } = await recordGroup(env.TELEMETRY, report, key, day)
    const gh = gitHubOf(env)
    if (!gh || !dueNow(group, newVersion)) return
    await syncGroup(env, gh, group, day, group.sample_key === key ? report : null)
  } catch {}
}

export function userTitle(report) {
  const first = report.message.split('\n').find((line) => line.trim()) ?? 'Report'
  return safeTitle(`[${report.app} ${report.version}] ${first}`)
}

export function userBody(report) {
  const contact = report.contact ? `\`${report.contact.replace(/`/g, "'")}\`` : 'none left'
  return [
    `<!-- exo:user-report ${report.ref} -->`,
    `**${APP_NAMES[report.app] ?? report.app} ${report.version}** on Windows ${report.osBuild} · sent from Report a problem · reference ${report.ref}`,
    '',
    '**What happened**',
    '',
    fenced(report.message),
    '',
    `**Contact:** ${contact}`,
    '',
    report.excerpt
      ? `<details><summary>Log excerpt (scrubbed on the PC and by the Worker)</summary>\n\n${fenced(report.excerpt)}\n\n</details>`
      : 'Logs: not attached.',
  ].join('\n')
}

export async function fileUserReport(env, gh, report, day) {
  if (!(await claim(env.TELEMETRY, day, 'user'))) return null
  await gh.ensureLabel(versionLabel(report.version))
  const issue = await gh.createIssue({
    title: userTitle(report),
    body: userBody(report),
    labels: [`app:${report.app}`, 'kind:user-report', versionLabel(report.version), 'new'],
  })
  return issue.number
}

export async function queueUserReport(db, ref, key, day) {
  const row = await db.prepare('SELECT COUNT(*) AS waiting FROM issue_queue').first()
  if (Number(row?.waiting ?? 0) >= QUEUE_MAX) return false
  await db.prepare('INSERT OR IGNORE INTO issue_queue (ref, r2_key, day) VALUES (?, ?, ?)').bind(ref, key, day).run()
  return true
}

export async function flush(env, now) {
  const db = env.TELEMETRY
  const gh = gitHubOf(env)
  const done = { groups: 0, messages: 0 }
  if (!gh) return done
  const day = utcDay(now)
  await db.prepare('UPDATE crash_groups SET issue_number = NULL WHERE issue_number = ?').bind(FILING).run()

  const { results: groups } = await db.prepare('SELECT * FROM crash_groups WHERE pending = 1 ORDER BY last_day DESC LIMIT ?').bind(FLUSH_MAX).all()
  for (const group of groups) {
    const result = await syncGroup(env, gh, group, day)
    if (result === 'capped') break
    done.groups++
  }

  const { results: queued } = await db.prepare('SELECT * FROM issue_queue ORDER BY day, ref LIMIT ?').bind(FLUSH_MAX).all()
  for (const item of queued) {
    const object = await env.REPORTS.get(item.r2_key)
    if (!object) {
      await db.prepare('DELETE FROM issue_queue WHERE ref = ?').bind(item.ref).run()
      continue
    }
    let number
    try {
      number = await fileUserReport(env, gh, JSON.parse(await object.text()), day)
    } catch (error) {
      await db.prepare('UPDATE issue_queue SET tries = tries + 1 WHERE ref = ?').bind(item.ref).run()
      throw error
    }
    if (number === null) break
    await db.prepare('DELETE FROM issue_queue WHERE ref = ?').bind(item.ref).run()
    done.messages++
  }
  return done
}

export async function pruneIssueDays(db, now) {
  await db.prepare('DELETE FROM issue_days WHERE day < ?').bind(utcDay(now - KEEP_DAYS * DAY_MS)).run()
}

export { GitHubError }

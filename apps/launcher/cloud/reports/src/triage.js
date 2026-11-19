import { utcDay } from './telemetry-store.js'

/**
 * Triage (README.md, "Triage"): so a crash is worked on the day it happens, not when someone thinks to look. Every stored
 * crash report is counted in D1 (`crash_days`, migrations/0004_crash_days.sql) by app, version, error type and UTC day. The
 * first report of an error in a version, and its 10th, 100th and 1000th, tell the team at once; a digest follows the daily
 * job. A group holds a count and one sample's key: no install ID, no time finer than the day.
 *
 * The alerts and the digest (mail, and the private Discord channel when its webhook is set) are the fallback until the
 * secret GITHUB_REPORTS_TOKEN is set (README.md › Issues): with the token the team is told through GitHub issues
 * (src/issues.js) and nothing here is mailed; the counts are kept either way, for `node triage.mjs`.
 */

const DAY_MS = 24 * 60 * 60 * 1000
/** A group's totals that tell the team: a new error in a version, then each tenfold. */
export const ALERT_AT = [1, 10, 100, 1000]
/** Counts older than this are removed by the daily job (the raw events' own age). */
export const KEEP_DAYS = 90
const APP_NAMES = { launcher: 'Exo Launcher', cinema: 'Exo Cinema', browser: 'Exo Browser' }
const EXCERPT_CHARS = 1500
const DIGEST_ROWS = 30
/** Alert mails a UTC day at most (migrations/0005_alert_days.sql); past it the digest still lists every group. */
export const ALERTS_PER_DAY = 50

/** Counts one stored report. Returns the group's total for its version, across days. */
export async function countReport(db, report, key, day) {
  await db.prepare(
    'INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES (?, ?, ?, ?, 1, ?) '
    + 'ON CONFLICT (app, version, error_type, day) DO UPDATE SET count = count + 1, sample_key = excluded.sample_key',
  ).bind(report.app, report.version, report.errorType, day, key).run()
  const row = await db.prepare('SELECT SUM(count) AS total FROM crash_days WHERE app = ? AND version = ? AND error_type = ?')
    .bind(report.app, report.version, report.errorType).first()
  return Number(row?.total ?? 0)
}

/**
 * What one alert that is due does today (migrations/0005_alert_days.sql), counted up in one statement so racing reports
 * cannot both take the last place:
 *
 * | the day's alerts so far, this one included | does                                                        |
 * | ------------------------------------------ | ----------------------------------------------------------- |
 * | 1 to ALERTS_PER_DAY                        | 'send': the alert goes                                      |
 * | ALERTS_PER_DAY + 1                         | 'pause': one mail says alerts are paused for today          |
 * | more                                       | 'hold': nothing goes; the digest counts what was held back  |
 * | the count cannot be read or written        | 'send': a missing table (0005 not applied) or a failed      |
 * |                                            | write never silences an alert                               |
 */
export async function claimAlert(db, day) {
  let sent
  try {
    const row = await db.prepare(
      'INSERT INTO alert_days (day, sent) VALUES (?, 1) ON CONFLICT (day) DO UPDATE SET sent = sent + 1 RETURNING sent',
    ).bind(day).first()
    sent = Number(row?.sent)
  } catch {
    return 'send'
  }
  if (!Number.isFinite(sent) || sent <= ALERTS_PER_DAY) return 'send'
  return sent === ALERTS_PER_DAY + 1 ? 'pause' : 'hold'
}

/** The one mail at the first alert past the day's cap. */
export function pausedAlert(day) {
  return {
    subject: `Exo errors, ${day}: alerts paused for today`,
    text: `${ALERTS_PER_DAY} alerts went out today. New errors are still stored and counted; tomorrow's digest lists every group.\n`,
  }
}

/** How many alerts the day held back past its cap; 0 when none, or when the tally cannot be read. */
export async function heldBack(db, day) {
  try {
    const row = await db.prepare('SELECT sent FROM alert_days WHERE day = ?').bind(day).first()
    return Math.max(0, Number(row?.sent ?? 0) - ALERTS_PER_DAY)
  } catch {
    return 0
  }
}

/**
 * The daily mail: the digest (or a line that it could not be made), what was held back, and every step of the daily job
 * that failed, so a broken step is heard about instead of stopping the mail. Null only when there is nothing to say.
 */
export function dailyMessage(digest, failed, held, day) {
  const lines = []
  if (held > 0) lines.push(`${held} ${held === 1 ? 'alert was' : 'alerts were'} held back by the daily cap; every group is listed above.`)
  if (failed.length > 0) lines.push(`These steps of the daily job failed: ${failed.join(', ')}.`)
  if (!digest && lines.length === 0) return null
  return {
    subject: digest?.subject ?? `Exo errors, ${day}: the daily job had a problem`,
    text: `${digest?.text ?? ''}${lines.length ? (digest ? '\n' : '') + lines.join('\n') + '\n' : ''}`,
  }
}
/** The message for a group that reached `total`: what broke, where, and the end of its (already scrubbed) excerpt. */
export function alertOf(report, total) {
  const app = APP_NAMES[report.app] ?? report.app
  const what = total === 1 ? 'new error' : `${total} reports`
  const excerpt = report.excerpt.length > EXCERPT_CHARS ? '…' + report.excerpt.slice(-EXCERPT_CHARS) : report.excerpt
  return {
    subject: `${app} ${report.version}: ${report.errorType} (${what})`,
    text: `${app} ${report.version} on Windows ${report.osBuild}\n${report.errorType}: ${what} in this version\n\n${excerpt}\n`,
  }
}

/**
 * The message for one Report a problem message before GitHub (src/user-report.js): who sent what, in the player's own
 * words cut to EXCERPT_CHARS, the contact when given, and the reference and R2 key to read the rest (its log included).
 */
export function userAlertOf(report, key) {
  const app = APP_NAMES[report.app] ?? report.app
  const message = report.message.length > EXCERPT_CHARS ? report.message.slice(0, EXCERPT_CHARS) + '…' : report.message
  return {
    subject: `${app} ${report.version}: Report a problem (${report.ref})`,
    text: `${app} ${report.version} on Windows ${report.osBuild}\nContact: ${report.contact ?? 'none left'}\n`
      + `Logs: ${report.excerpt ? 'attached' : 'not attached'} · stored at ${key}\n\n${message}\n`,
  }
}

/** Yesterday's groups, most reports first, as the digest's text; null when there were none. */
export async function digestOf(db, now) {
  const day = utcDay(now - DAY_MS)
  const { results } = await db.prepare(
    'SELECT d.app, d.version, d.error_type, d.count, '
    + '(SELECT SUM(count) FROM crash_days t WHERE t.app = d.app AND t.version = d.version AND t.error_type = d.error_type) AS total '
    + 'FROM crash_days d WHERE d.day = ? ORDER BY d.count DESC, d.app, d.version, d.error_type LIMIT ?',
  ).bind(day, DIGEST_ROWS).all()
  if (!results.length) return null
  const sum = results.reduce((all, row) => all + row.count, 0)
  const lines = results.map((row) => `${String(row.count).padStart(5)}  ${APP_NAMES[row.app] ?? row.app} ${row.version}  ${row.error_type}${row.total === row.count ? '  (new)' : ''}`)
  return { subject: `Exo errors, ${day}: ${sum} in ${results.length} ${results.length === 1 ? 'group' : 'groups'}`, text: `${lines.join('\n')}\n` }
}

/** The daily job: counts older than KEEP_DAYS go. */
export async function pruneCrashDays(db, now) {
  await db.prepare('DELETE FROM crash_days WHERE day < ?').bind(utcDay(now - KEEP_DAYS * DAY_MS)).run()
}

/** The daily job: alert tallies older than KEEP_DAYS go (a step of its own, so a missing 0005 stops nothing else). */
export async function pruneAlertDays(db, now) {
  await db.prepare('DELETE FROM alert_days WHERE day < ?').bind(utcDay(now - KEEP_DAYS * DAY_MS)).run()
}

/** True only for an https Discord webhook on the default port, with no user info. */
export function isDiscordWebhook(url) {
  if (typeof url !== 'string') return false
  let parsed
  try {
    parsed = new URL(url)
  } catch {
    return false
  }
  return parsed.protocol === 'https:' && parsed.port === '' && !parsed.username && !parsed.password
    && ['discord.com', 'discordapp.com'].includes(parsed.hostname) && /^\/api\/webhooks\/\d+\/[\w-]+\/?$/.test(parsed.pathname)
}

/** The Discord message for an alert: its subject in bold and the text in a code block, inside Discord's 2000 characters. */
export function discordBody({ subject, text }) {
  const room = 1900 - subject.length
  const body = text.length > room ? '…' + text.slice(-room) : text
  return { content: `**${subject}**\n\`\`\`\n${body.replaceAll('```', "'''")}\n\`\`\``, allowed_mentions: { parse: [] } }
}

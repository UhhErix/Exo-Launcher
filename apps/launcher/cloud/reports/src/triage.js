import { utcDay } from './telemetry-store.js'

const DAY_MS = 24 * 60 * 60 * 1000
export const ALERT_AT = [1, 10, 100, 1000]
export const KEEP_DAYS = 90
const APP_NAMES = { launcher: 'Exo Launcher', cinema: 'Exo Cinema', browser: 'Exo Browser' }
const EXCERPT_CHARS = 1500
const DIGEST_ROWS = 30
export const ALERTS_PER_DAY = 50

export async function countReport(db, report, key, day) {
  await db.prepare(
    'INSERT INTO crash_days (app, version, error_type, day, count, sample_key) VALUES (?, ?, ?, ?, 1, ?) '
    + 'ON CONFLICT (app, version, error_type, day) DO UPDATE SET count = count + 1, sample_key = excluded.sample_key',
  ).bind(report.app, report.version, report.errorType, day, key).run()
  const row = await db.prepare('SELECT SUM(count) AS total FROM crash_days WHERE app = ? AND version = ? AND error_type = ?')
    .bind(report.app, report.version, report.errorType).first()
  return Number(row?.total ?? 0)
}

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

export function pausedAlert(day) {
  return {
    subject: `Exo errors, ${day}: alerts paused for today`,
    text: `${ALERTS_PER_DAY} alerts went out today. New errors are still stored and counted; tomorrow's digest lists every group.\n`,
  }
}

export async function heldBack(db, day) {
  try {
    const row = await db.prepare('SELECT sent FROM alert_days WHERE day = ?').bind(day).first()
    return Math.max(0, Number(row?.sent ?? 0) - ALERTS_PER_DAY)
  } catch {
    return 0
  }
}

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
export function trimExcerpt(text, maxChars) {
  if (typeof text !== 'string' || text.length <= maxChars) return text ?? ''
  const lines = text.split('\n')
  const headCap = Math.max(64, Math.floor(maxChars / 2))
  const headLines = []
  let idx = 0
  let headLen = 0
  let sawError = false
  while (idx < lines.length) {
    const line = lines[idx]
    if (/^\[\d/.test(line)) break
    if (headLen + line.length + 1 > headCap && headLines.length > 0) break
    headLines.push(line)
    headLen += line.length + 1
    idx++
    if (/^Error \(/.test(line)) {
      sawError = true
      while (idx < lines.length && lines[idx].trim() !== '' && !/^\[\d/.test(lines[idx])) {
        if (headLen + lines[idx].length + 1 > headCap) break
        headLines.push(lines[idx])
        headLen += lines[idx].length + 1
        idx++
      }
      break
    }
  }
  const head = headLines.join('\n').trimEnd()
  if (!sawError || !head || idx >= lines.length) {
    return '…' + text.slice(-(maxChars - 1))
  }
  const marker = '\n…\n'
  const tailBudget = maxChars - head.length - marker.length
  if (tailBudget <= 0) return head.slice(0, maxChars)
  const tail = lines.slice(idx).join('\n')
  if (tail.length <= tailBudget) return `${head}\n${tail}`
  const cut = tail.length - tailBudget
  const nl = tail.indexOf('\n', cut)
  const kept = nl >= 0 && nl + 1 < tail.length ? tail.slice(nl + 1) : tail.slice(cut)
  return `${head}${marker}${kept}`
}

export function alertOf(report, total) {
  const app = APP_NAMES[report.app] ?? report.app
  const what = total === 1 ? 'new error' : `${total} reports`
  const excerpt = trimExcerpt(report.excerpt, EXCERPT_CHARS)
  return {
    subject: `${app} ${report.version}: ${report.errorType} (${what})`,
    text: `${app} ${report.version} on Windows ${report.osBuild}\n${report.errorType}: ${what} in this version\n\n${excerpt}\n`,
  }
}

export function userAlertOf(report, key) {
  const app = APP_NAMES[report.app] ?? report.app
  const message = report.message.length > EXCERPT_CHARS ? report.message.slice(0, EXCERPT_CHARS) + '…' : report.message
  return {
    subject: `${app} ${report.version}: Report a problem (${report.ref})`,
    text: `${app} ${report.version} on Windows ${report.osBuild}\nContact: ${report.contact ?? 'none left'}\n`
      + `Logs: ${report.excerpt ? 'attached' : 'not attached'} · stored at ${key}\n\n${message}\n`,
  }
}

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

export async function pruneCrashDays(db, now) {
  await db.prepare('DELETE FROM crash_days WHERE day < ?').bind(utcDay(now - KEEP_DAYS * DAY_MS)).run()
}

export async function pruneAlertDays(db, now) {
  await db.prepare('DELETE FROM alert_days WHERE day < ?').bind(utcDay(now - KEEP_DAYS * DAY_MS)).run()
}

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

export function discordBody({ subject, text }) {
  const room = 1900 - subject.length
  const body = text.length > room ? trimExcerpt(text, room) : text
  return { content: `**${subject}**\n\`\`\`\n${body.replaceAll('```', "'''")}\n\`\`\``, allowed_mentions: { parse: [] } }
}

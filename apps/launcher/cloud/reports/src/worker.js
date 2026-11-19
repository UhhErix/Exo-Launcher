// Exo's reports service (README.md). POST /v1/report stores one crash report the Launcher, Cinema or Browser sent, in R2
// under the UTC date. POST /v1/telemetry stores scrubbed app events in D1, and /v1/telemetry/delete removes one install's
// events; a daily cron job writes aggregates and removes raw events after 90 days. Nothing about the sender is kept: no IP
// address is written (CF-Connecting-IP is read only as a rate limiter's key, `admitted` below), no user agent, no cookie, no
// id the client makes beyond the install ID the telemetry envelope carries. Logs are off (wrangler.toml).
import { limits, telemetryLimits } from './limits.js'
import { ReportError, parseReport, reportInstallId } from './validate.js'
import { TelemetryError, parseBatch, parseDelete } from './telemetry.js'
import { deleteInstall, insertEvents, installEventsSince, runRetention, utcDay } from './telemetry-store.js'
import { addCrashIndex, deleteCrashReports, pruneCrashIndex, removeCrashIndex } from './crash-store.js'
import { ContactError, contactLimits, parseContact } from './contact.js'
import { ALERT_AT, alertOf, claimAlert, countReport, dailyMessage, digestOf, discordBody, heldBack, isDiscordWebhook, pausedAlert, pruneAlertDays, pruneCrashDays } from './triage.js'

const HOUR_MS = 60 * 60 * 1000
// The contact form's page is the only browser that may post (src/contact.js, README.md › Contact).
const SITE = 'https://getexo.app'
const CONTACT_FROM = 'contact@getexo.app'
const APP_NAMES = { launcher: 'Launcher', cinema: 'Cinema', browser: 'Browser', other: 'Other' }

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url)
    if (url.pathname === '/v1/contact') return contact(request, env)
    if (url.pathname === '/v1/report') {
      if (request.method !== 'POST') return new Response('POST only', { status: 405, headers: { Allow: 'POST' } })
      return receive(request, env, ctx)
    }
    if (url.pathname === '/v1/telemetry' || url.pathname === '/v1/telemetry/delete') {
      if (request.method !== 'POST') return new Response(null, { status: 405, headers: { Allow: 'POST' } })
      return telemetry(request, env, url.pathname)
    }
    return new Response('Not found', { status: 404 })
  },

  // The daily job (wrangler.toml [triggers]): aggregates and removes raw events older than 90 days, and crash index rows
  // older than the 30-day lifecycle of their objects; then yesterday's crash digest goes to the team (src/triage.js).
  // Each step runs on its own: one that throws is named in the mail and stops nothing else, and the mail always goes.
  async scheduled(controller, env) {
    if (!env.TELEMETRY) return
    const now = Date.now()
    const db = env.TELEMETRY
    const failed = []
    const steps = [
      ['retention', () => runRetention(db, now)],
      ['crash index pruning', () => pruneCrashIndex(db, now)],
      ['crash count pruning', () => pruneCrashDays(db, now)],
      ['alert tally pruning', () => pruneAlertDays(db, now)],
    ]
    for (const [name, step] of steps) {
      try { await step() } catch { failed.push(name) }
    }
    let digest = null
    try { digest = await digestOf(db, now) } catch { failed.push('digest') }
    const yesterday = utcDay(now - 24 * HOUR_MS)
    const message = dailyMessage(digest, failed, await heldBack(db, yesterday), yesterday)
    if (message) await tellTeam(env, message)
  },
}

/**
 * Counts a stored report and tells the team when its group is new in its version or has grown tenfold (src/triage.js). It
 * runs after the answer and never fails a report: with no D1 (the dev copy) or a failed send, the report is still stored.
 */
async function triage(env, report, key, day) {
  if (!env.TELEMETRY) return
  try {
    const total = await countReport(env.TELEMETRY, report, key, day)
    if (!ALERT_AT.includes(total)) return
    const does = await claimAlert(env.TELEMETRY, day)
    if (does === 'send') await tellTeam(env, alertOf(report, total))
    else if (does === 'pause') await tellTeam(env, pausedAlert(day))
  } catch {
    // Logs are off; the report is in R2 and the daily digest reads the counts that did land.
  }
}

/** One message to the team: by mail (the contact form's own binding and address), and to Discord when its webhook is set. */
async function tellTeam(env, { subject, text }) {
  const sends = [mail(env, { subject, text })]
  if (isDiscordWebhook(env.DISCORD_WEBHOOK_REPORTS)) {
    const post = env.fetcher ?? ((...args) => fetch(...args))
    sends.push(post(env.DISCORD_WEBHOOK_REPORTS, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(discordBody({ subject, text })) }))
  }
  await Promise.allSettled(sends)
}

async function receive(request, env, ctx) {
  if (!isJson(request.headers.get('Content-Type'))) return new Response('JSON only', { status: 415 })
  const declared = request.headers.get('Content-Length')
  if (declared !== null && !(Number(declared) <= limits.bodyBytes)) return new Response('Too large', { status: 413 })
  const text = await readCapped(request, limits.bodyBytes)
  if (text === null) return new Response('Too large', { status: 413 })

  let report
  let installId
  try {
    const value = JSON.parse(text)
    report = parseReport(value)
    installId = reportInstallId(value)
  } catch (error) {
    if (error instanceof ReportError) return new Response(error.message, { status: error.status })
    return new Response('Not JSON', { status: 400 })
  }

  if (!(await admitted(env, request, 'report', installId === null ? [] : [installId]))) return new Response('Slow down', { status: 429 })

  const day = utcDay(Date.now())
  const key = `${limits.prefix}${day}/${crypto.randomUUID()}.json`

  // A report with an install ID is indexed first, so a delete can always find it; if the index is down, nothing is stored.
  if (installId !== null) {
    if (!env.TELEMETRY) return new Response('Unavailable', { status: 503 })
    try {
      await addCrashIndex(env.TELEMETRY, installId, key, day)
    } catch {
      return new Response('Unavailable', { status: 503 })
    }
  }
  try {
    await env.REPORTS.put(key, JSON.stringify(report), { httpMetadata: { contentType: 'application/json' } })
  } catch (error) {
    if (installId !== null) await removeCrashIndex(env.TELEMETRY, installId, key).catch(() => {})
    throw error
  }
  const counted = triage(env, report, key, day)
  if (ctx?.waitUntil) ctx.waitUntil(counted)
  else await counted
  return new Response(null, { status: 202 })
}

/**
 * The telemetry endpoints. Every refusal is an empty answer with its status (no field named, no detail): a bad batch 400,
 * a browser Origin 403, too big 413, over a limit 429, D1 unavailable 503 (the apps keep the batch and retry on 5xx).
 */
async function telemetry(request, env, path) {
  if (request.headers.has('Origin')) return empty(403)
  if (!env.TELEMETRY) return empty(503)
  const deleting = path === '/v1/telemetry/delete'
  const max = deleting ? telemetryLimits.deleteBodyBytes : telemetryLimits.bodyBytes
  const declared = request.headers.get('Content-Length')
  if (declared !== null && !(Number(declared) <= max)) return empty(413)
  const text = await readCapped(request, max)
  if (text === null) return empty(413)

  try {
    const value = JSON.parse(text)
    return deleting ? await deleteTelemetry(env, request, value) : await receiveTelemetry(env, request, value, Date.now())
  } catch (error) {
    if (error instanceof TelemetryError || error instanceof SyntaxError) return empty(400)
    return empty(503)
  }
}

async function receiveTelemetry(env, request, value, now) {
  const rows = parseBatch(value, now)
  if (rows.length === 0) return empty(202)

  if (!(await admitted(env, request, 'telemetry', new Set(rows.map((row) => row.install_id))))) return empty(429)

  // The per-install cap per hour, in code. Best effort: the count and the insert are two statements, so a burst can pass it.
  const perInstall = new Map()
  for (const row of rows) perInstall.set(row.install_id, (perInstall.get(row.install_id) ?? 0) + 1)
  for (const [installId, incoming] of perInstall) {
    const sent = await installEventsSince(env.TELEMETRY, installId, now - HOUR_MS)
    if (sent + incoming > telemetryLimits.eventsPerInstallHour) return empty(429)
  }

  await insertEvents(env.TELEMETRY, rows, now)
  return empty(202)
}

async function deleteTelemetry(env, request, value) {
  const installId = parseDelete(value)
  if (!(await admitted(env, request, 'delete', [installId]))) return empty(429)
  await deleteInstall(env.TELEMETRY, installId)
  // The install's crash reports: their R2 objects, then their index rows. A failure answers 503 and a retry finishes it.
  await deleteCrashReports(env.TELEMETRY, env.REPORTS, installId)
  return empty(204)
}

const empty = (status) => new Response(null, { status })

/**
 * The rate limits of /v1/report, /v1/telemetry and /v1/telemetry/delete (wrangler.toml [[ratelimits]]), counted by
 * Cloudflare at the edge; the Worker stores neither key. Each install has its own count per route, so any number of players
 * online at once never share one; the sender's network has a larger count per route as the backstop, so one sender making
 * up install IDs is still stopped. A crash report from an old client carries no install ID and meets the network's alone.
 * A high cost ceiling per route comes last (REPORTS_CEILING), so senders spread over many addresses cannot run up writes,
 * deletes and alert mails without bound, and a request refused by its own install or network never spends it. The network is
 * asked first, so a batch padded with made-up IDs costs one refused call; the first refusal stops, and a refused request
 * stores nothing.
 */
async function admitted(env, request, route, installIds) {
  if (!(await env.REPORTS_PER_NETWORK.limit({ key: `${route}:${networkOf(request.headers.get('CF-Connecting-IP'))}` })).success) return false
  for (const id of installIds) {
    if (!(await env.REPORTS_PER_INSTALL.limit({ key: `${route}:${id}` })).success) return false
  }
  // Last, the cost ceiling for everyone on this route: a request one of the counts above refused never spends it.
  return (await env.REPORTS_CEILING.limit({ key: route })).success
}

/**
 * The sender's network as a limiter key: an IPv4 address as it is, an IPv6 address as its /64 (one home or phone gets a
 * whole /64, so a single address would let one sender rotate past the limit), an IPv4-mapped one as its IPv4. No address
 * (a local run): one shared key.
 */
export function networkOf(ip) {
  if (!ip) return 'unknown'
  if (!ip.includes(':')) return ip
  if (ip.includes('.')) return ip.slice(ip.lastIndexOf(':') + 1)
  const [head, tail = ''] = ip.toLowerCase().split('::')
  const left = head ? head.split(':') : []
  const right = ip.includes('::') && tail ? tail.split(':') : []
  const groups = ip.includes('::') ? [...left, ...Array(8 - left.length - right.length).fill('0'), ...right] : left
  return `${groups.slice(0, 4).map((g) => g.replace(/^0+(?=.)/, '')).join(':')}::/64`
}

/**
 * POST /v1/contact: one message from the help page. Stored in R2 under contact/<UTC date>/<id>.json (the address only
 * when one was given), then emailed to the owner if the send_email binding and CONTACT_TO are set. The rate limit is
 * keyed by the sender's IP address, the one place it is read: the key is held by Cloudflare's limiter and nowhere else.
 * Answers: 202 stored (or a honeypot hit, which looks the same), 400 with { field, problem } naming a rule, 403 for any
 * origin but getexo.app, 405, 413, 415, 429, 503 when R2 is absent (the dev copy).
 */
async function contact(request, env) {
  if (request.headers.get('Origin') !== SITE) return empty(403)
  const cors = { 'Access-Control-Allow-Origin': SITE, Vary: 'Origin' }
  if (request.method === 'OPTIONS') {
    return new Response(null, { status: 204, headers: { ...cors, 'Access-Control-Allow-Methods': 'POST', 'Access-Control-Allow-Headers': 'Content-Type', 'Access-Control-Max-Age': '600' } })
  }
  if (request.method !== 'POST') return new Response(null, { status: 405, headers: { ...cors, Allow: 'POST, OPTIONS' } })
  if (!isJson(request.headers.get('Content-Type'))) return new Response(null, { status: 415, headers: cors })
  const declared = request.headers.get('Content-Length')
  if (declared !== null && !(Number(declared) <= contactLimits.bodyBytes)) return new Response(null, { status: 413, headers: cors })
  const text = await readCapped(request, contactLimits.bodyBytes)
  if (text === null) return new Response(null, { status: 413, headers: cors })

  let message
  try {
    message = parseContact(JSON.parse(text))
  } catch (error) {
    if (error instanceof ContactError) return Response.json({ field: error.field, problem: error.problem }, { status: 400, headers: cors })
    return new Response(null, { status: 400, headers: cors })
  }
  // A bot that filled the honeypot gets the same answer as a stored message, and nothing is kept or sent.
  if (message.spam) return new Response(null, { status: 202, headers: cors })

  const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown'
  if (!(await env.CONTACT_PER_IP.limit({ key: ip })).success) return Response.json({ field: null, problem: 'Slow down: try again in a minute.' }, { status: 429, headers: cors })
  if (!env.REPORTS) return new Response(null, { status: 503, headers: cors })

  const { spam, ...stored } = message
  await env.REPORTS.put(`contact/${utcDay(Date.now())}/${crypto.randomUUID()}.json`, JSON.stringify(stored), { httpMetadata: { contentType: 'application/json' } })
  // The message is kept before the email is tried: an email that fails loses nothing, so the answer is still 202.
  await notify(env, stored).catch(() => {})
  return new Response(null, { status: 202, headers: cors })
}

/** Emails the owner through the send_email binding (CONTACT_MAIL) to CONTACT_TO. Plain text; the subject holds the app's name only. */
function notify(env, { app, email, message }) {
  return mail(env, {
    subject: `Exo contact: ${APP_NAMES[app]}`,
    text: `App: ${APP_NAMES[app]}\nReply to: ${email ?? 'nobody (no address given)'}\n\n${message}\n`,
    replyTo: email,
  })
}

/** One plain-text mail to the owner (CONTACT_MAIL to CONTACT_TO); nothing is sent where either is absent (the dev copy). */
async function mail(env, { subject, text, replyTo }) {
  if (!env.CONTACT_MAIL || !env.CONTACT_TO) return
  const EmailMessage = env.EmailMessage ?? (await import('cloudflare:email')).EmailMessage
  const headers = [
    `From: Exo <${CONTACT_FROM}>`,
    `To: ${env.CONTACT_TO}`,
    `Subject: ${subject.replace(/[\r\n]+/g, ' ')}`,
    'MIME-Version: 1.0',
    'Content-Type: text/plain; charset=utf-8',
    'Content-Transfer-Encoding: base64',
  ]
  if (replyTo) headers.push(`Reply-To: ${replyTo}`)
  const raw = `${headers.join('\r\n')}\r\n\r\n${base64(new TextEncoder().encode(text))}`
  await env.CONTACT_MAIL.send(new EmailMessage(CONTACT_FROM, env.CONTACT_TO, raw))
}

function base64(bytes) {
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/.{76}/g, '$&\r\n')
}

function isJson(type) {
  return type !== null && /^application\/json(;|$)/i.test(type.trim())
}

/** The body as text, or null once it passes `max` bytes (the rest is never read). */
async function readCapped(request, max) {
  if (!request.body) return ''
  const reader = request.body.getReader()
  const chunks = []
  let total = 0
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    total += value.byteLength
    if (total > max) {
      await reader.cancel()
      return null
    }
    chunks.push(value)
  }
  const bytes = new Uint8Array(total)
  let at = 0
  for (const chunk of chunks) {
    bytes.set(chunk, at)
    at += chunk.byteLength
  }
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch {
    return ''
  }
}

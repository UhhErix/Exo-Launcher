import { limits, telemetryLimits } from './limits.js'
import { ReportError, parseReport, reportInstallId } from './validate.js'
import { TelemetryError, parseBatch, parseDelete } from './telemetry.js'
import { deleteInstall, insertEvents, installEventsSince, runRetention, utcDay } from './telemetry-store.js'
import { addCrashIndex, deleteCrashReports, pruneCrashIndex, removeCrashIndex } from './crash-store.js'
import { ContactError, contactLimits, parseContact } from './contact.js'
import { ALERT_AT, alertOf, claimAlert, countReport, dailyMessage, digestOf, discordBody, heldBack, isDiscordWebhook, pausedAlert, pruneAlertDays, pruneCrashDays, userAlertOf } from './triage.js'
import { isTestRun, isVersionSkew } from './fingerprint.js'
import { fileCrash, fileUserReport, flush, pruneIssueDays, queueUserReport } from './issues.js'
import { gitHubOf } from './github.js'
import { parseUserReport, userLimits } from './user-report.js'
import { counted } from './signal.js'
import { selfTest } from './selftest.js'

const HOUR_MS = 60 * 60 * 1000
const SITE = 'https://getexo.app'
const CONTACT_FROM = 'contact@getexo.app'
const APP_NAMES = { launcher: 'Launcher', cinema: 'Cinema', browser: 'Browser', other: 'Other' }

const ROUTES = [
  ['contact', /^\/v1\/contact$/], ['report', /^\/v1\/report$/], ['user-report', /^\/v1\/user-report$/],
  ['telemetry', /^\/v1\/telemetry$/], ['telemetry-delete', /^\/v1\/telemetry\/delete$/], ['health', /^\/health$/],
]

export default counted('reports', ROUTES, {
  async fetch(request, env, ctx) {
    const url = new URL(request.url)
    if (url.pathname === '/health') {
      if (request.method !== 'GET') return new Response('GET only', { status: 405, headers: { Allow: 'GET' } })
      const ok = Boolean(env.REPORTS && env.TELEMETRY)
      return new Response(JSON.stringify({ ok }), { status: ok ? 200 : 503, headers: { 'content-type': 'application/json', 'cache-control': 'no-store' } })
    }
    if (url.pathname === '/v1/contact') return contact(request, env)
    if (url.pathname === '/v1/report') {
      if (request.method !== 'POST') return new Response('POST only', { status: 405, headers: { Allow: 'POST' } })
      return receive(request, env, ctx)
    }
    if (url.pathname === '/v1/user-report') {
      if (request.method !== 'POST') return new Response('POST only', { status: 405, headers: { Allow: 'POST' } })
      return receiveUserReport(request, env)
    }
    if (url.pathname === '/v1/telemetry' || url.pathname === '/v1/telemetry/delete') {
      if (request.method !== 'POST') return new Response(null, { status: 405, headers: { Allow: 'POST' } })
      return telemetry(request, env, url.pathname)
    }
    return new Response('Not found', { status: 404 })
  },

  async scheduled(controller, env) {
    if (!env.TELEMETRY) return
    const now = Date.now()
    const db = env.TELEMETRY
    const gh = gitHubOf(env)
    const failed = []
    const steps = [
      ['retention', () => runRetention(db, now)],
      ['crash index pruning', () => pruneCrashIndex(db, now)],
      ['crash count pruning', () => pruneCrashDays(db, now)],
      ['alert tally pruning', () => pruneAlertDays(db, now)],
      ['issue tally pruning', () => pruneIssueDays(db, now)],
      ...(gh ? [['GitHub', () => flush(env, now)]] : []),
    ]
    for (const [name, step] of steps) {
      try { await step() } catch { failed.push(name) }
    }
    await selfTest(env)
    if (gh) return
    let digest = null
    try { digest = await digestOf(db, now) } catch { failed.push('digest') }
    const yesterday = utcDay(now - 24 * HOUR_MS)
    const message = dailyMessage(digest, failed, await heldBack(db, yesterday), yesterday)
    if (message) await tellTeam(env, message)
  },
})

async function triage(env, report, key, day) {
  if (!env.TELEMETRY) return
  const withGitHub = gitHubOf(env) !== null
  const testRun = isTestRun(report)
  const suppress = testRun || isVersionSkew(report)
  try {
    const total = await countReport(env.TELEMETRY, report, key, day)
    if (!suppress && !withGitHub && ALERT_AT.includes(total)) {
      const does = await claimAlert(env.TELEMETRY, day)
      if (does === 'send') await tellTeam(env, alertOf(report, total))
      else if (does === 'pause') await tellTeam(env, pausedAlert(day))
    }
  } catch {}
  if (!testRun) await fileCrash(env, report, key, day)
}

async function tellTeam(env, { subject, text }) {
  if (!isDiscordWebhook(env.DISCORD_WEBHOOK_REPORTS)) return
  const post = env.fetcher ?? ((...args) => fetch(...args))
  await Promise.allSettled([
    post(env.DISCORD_WEBHOOK_REPORTS, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(discordBody({ subject, text })) }),
  ])
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

async function receiveUserReport(request, env) {
  if (request.headers.has('Origin')) return empty(403)
  if (!isJson(request.headers.get('Content-Type'))) return new Response('JSON only', { status: 415 })
  const declared = request.headers.get('Content-Length')
  if (declared !== null && !(Number(declared) <= userLimits.bodyBytes)) return new Response('Too large', { status: 413 })
  const text = await readCapped(request, userLimits.bodyBytes)
  if (text === null) return new Response('Too large', { status: 413 })

  let report
  let installId
  try {
    const value = JSON.parse(text)
    report = parseUserReport(value)
    installId = reportInstallId(value)
  } catch (error) {
    if (error instanceof ReportError) return new Response(error.message, { status: error.status })
    return new Response('Not JSON', { status: 400 })
  }
  if (!(await admitted(env, request, 'user-report', installId === null ? [] : [installId]))) return new Response('Slow down', { status: 429 })

  const day = utcDay(Date.now())
  const ref = crypto.randomUUID().replace(/-/g, '').slice(0, 8)
  const key = `${limits.prefix}${day}/user-${crypto.randomUUID()}.json`
  const stored = { ...report, ref }
  if (installId !== null) {
    if (!env.TELEMETRY) return new Response('Unavailable', { status: 503 })
    try {
      await addCrashIndex(env.TELEMETRY, installId, key, day)
    } catch {
      return new Response('Unavailable', { status: 503 })
    }
  }
  try {
    await env.REPORTS.put(key, JSON.stringify(stored), { httpMetadata: { contentType: 'application/json' } })
  } catch (error) {
    if (installId !== null) await removeCrashIndex(env.TELEMETRY, installId, key).catch(() => {})
    throw error
  }

  const gh = gitHubOf(env)
  let issue = null
  if (gh && env.TELEMETRY) {
    try { issue = await fileUserReport(env, gh, stored, day) } catch { issue = null }
  }
  if (!gh) await tellTeam(env, userAlertOf(stored, key)).catch(() => {})
  if (issue === null && env.TELEMETRY) await queueUserReport(env.TELEMETRY, ref, key, day).catch(() => {})
  return Response.json(issue === null ? { ref } : { issue }, { status: 202 })
}

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
  await deleteCrashReports(env.TELEMETRY, env.REPORTS, installId)
  return empty(204)
}

const empty = (status) => new Response(null, { status })

async function admitted(env, request, route, installIds) {
  if (!(await env.REPORTS_PER_NETWORK.limit({ key: `${route}:${networkOf(request.headers.get('CF-Connecting-IP'))}` })).success) return false
  for (const id of installIds) {
    if (!(await env.REPORTS_PER_INSTALL.limit({ key: `${route}:${id}` })).success) return false
  }
  return (await env.REPORTS_CEILING.limit({ key: route })).success
}

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
  if (message.spam) return new Response(null, { status: 202, headers: cors })

  const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown'
  if (!(await env.CONTACT_PER_IP.limit({ key: ip })).success) return Response.json({ field: null, problem: 'Slow down: try again in a minute.' }, { status: 429, headers: cors })
  if (!env.REPORTS) return new Response(null, { status: 503, headers: cors })

  const { spam, ...stored } = message
  await env.REPORTS.put(`contact/${utcDay(Date.now())}/${crypto.randomUUID()}.json`, JSON.stringify(stored), { httpMetadata: { contentType: 'application/json' } })
  await notify(env, stored).catch(() => {})
  return new Response(null, { status: 202, headers: cors })
}

function notify(env, { app, email, message }) {
  return mail(env, {
    subject: `Exo contact: ${APP_NAMES[app]}`,
    text: `App: ${APP_NAMES[app]}\nReply to: ${email ?? 'nobody (no address given)'}\n\n${message}\n`,
    replyTo: email,
  })
}

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

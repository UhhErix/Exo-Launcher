// The no-model crawler (misc/app-audit/U01-c.md › 5, tier A): the shipped ui/ on the dev mock host in headless Chromium,
// every room and every state one or two clicks away, plus the mock's start states (signed out, What's New, an update,
// a call and a song live), the failure modes (?fail, ?empty, ?stress, ?slow) and the two other appearances Windows can
// ask for (more contrast, forced colours). Deterministic: document order, fixed caps, no randomness, no model.
//
// FAIL (exit 1): an uncaught exception, a console error, a failed request or an HTTP error, the page scrolling
// sideways, words past the window, words under 3:1 against the pixels really behind them, an error screen.
// WARN (reported, exit unaffected): words under 4.5:1, an unnamed control, a target under 28 px.
//
//   node tools/ui-pass/crawl.mjs [--quick | --no-walk] [--out <dir>]
//   The full walk replays each path from a fresh page, so it takes hours; --quick (one click deep, one look) about
//   six minutes; --no-walk (every start, failure mode and look, no clicks) a few minutes.
//   (from ui/: npm run crawl)   Needs Playwright's Chromium (or EXO_UIPASS_BROWSER). Starts its own Vite server and
//   browser on free ports; nothing under ui/src is edited. Writes crawl.json and crawl-summary.md (and a picture of
//   each failing state) to tools/ui-pass/out/crawl/ unless --out says otherwise.
import { spawn } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import net from 'node:net'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { deflateSync, crc32 } from 'node:zlib'

const argv = process.argv.slice(2)
const quick = argv.includes('--quick')
const outDir = resolve(argv.includes('--out') ? argv[argv.indexOf('--out') + 1] : join(import.meta.dirname, 'out', 'crawl'))
const PER_STATE = quick ? 8 : 24 // controls clicked in one state
const PER_ROOM = quick ? 12 : 45 // new states looked at in one room
// --no-walk: every start, mode and look, without the clicks from each room (minutes instead of hours).
const DEPTH = argv.includes('--no-walk') ? 0 : quick ? 1 : 2

const freePort = () => new Promise((res, rej) => { const s = net.createServer(); s.unref(); s.on('error', rej); s.listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => res(port)) }) })
const uiPort = await freePort()
process.env.EXO_UIPASS_CDP ??= String(await freePort())
process.env.EXO_UIPASS_ORIGIN = `http://127.0.0.1:${uiPort}/`
const { launch, Page, sleep, ORIGIN } = await import('./cdp.mjs')
const A = await import('./audit.mjs')

// What a click must never do, even on the mock: leave, delete, buy, start or stop something, sign out, post.
const RISKY = /\b(delete|remove|uninstall|sign out|log out|leave|send|post|buy|purchase|install|update now|launch|play|stop|erase|reset|restore|clear|quit|exit|close exo|restart|shut ?down|sleep|block|kick|ban|report|redeem|activate|download|record|go live|stream|call|join|hang up|disconnect)\b/i
// Reviewed console lines and requests that are the mock's, not the app's (each with why).
const KNOWN = [
  { re: /mouse\.exo-launcher\.local/, why: "Exo Mouse's frame is served by the host's own origin, which the browser mock has no server for" },
  { re: /Mock: no shader add-on/, why: 'the browser mock has no Exo Shaders engine (a host add-on)' },
]
const known = (text) => KNOWN.find((k) => k.re.test(text))

// ---- fake pictures: every external image is a small two-tone PNG (no art is real); nothing fails on purpose ----
const chunk = (type, data) => { const t = Buffer.from(type); const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const c = Buffer.alloc(4); c.writeUInt32BE(crc32(Buffer.concat([t, data])) >>> 0); return Buffer.concat([len, t, data, c]) }
function png(seed) {
  let h = 2166136261; for (let i = 0; i < seed.length; i++) { h ^= seed.charCodeAt(i); h = Math.imul(h, 16777619) }
  const W = 48, H = 72, raw = Buffer.alloc((W * 4 + 1) * H), r = (h >>> 0) % 160 + 40, g = (h >>> 8) % 160 + 40, b = (h >>> 16) % 160 + 40
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) { const i = y * (W * 4 + 1) + 1 + x * 4, f = 1 - y / H * 0.6; raw[i] = r * f; raw[i + 1] = g * f; raw[i + 2] = b * f; raw[i + 3] = 255 }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 6
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]).toString('base64')
}

async function newPage(view) {
  const p = await Page.create()
  await p.view(1920, 1080, view)
  await p.send('Page.addScriptToEvaluateOnNewDocument', { source: `(${A.installObservers.toString()})();` })
  p.requests = new Map(); p.failed = []; p.faked = new Set()
  await p.send('Fetch.enable', { patterns: [{ urlPattern: '*', resourceType: 'Image' }, { urlPattern: '*', resourceType: 'Media' }] })
  p.handlers.push((m) => {
    if (m.method === 'Fetch.requestPaused') {
      const { requestId, request, resourceType } = m.params
      const u = new URL(request.url)
      if (u.hostname === '127.0.0.1' || u.protocol === 'data:' || u.protocol === 'blob:') return void p.send('Fetch.continueRequest', { requestId }).catch(() => {})
      // A clip has nothing to play here: it is refused and counted as the mock's, not as a failure.
      if (resourceType === 'Media') { p.faked.add(request.url); return void p.send('Fetch.failRequest', { requestId, errorReason: 'Aborted' }).catch(() => {}) }
      return void p.send('Fetch.fulfillRequest', { requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'image/png' }, { name: 'Access-Control-Allow-Origin', value: '*' }], body: png(request.url) }).catch(() => {})
    }
    if (m.method === 'Network.requestWillBeSent') p.requests.set(m.params.requestId, m.params.request.url)
    if (m.method === 'Network.loadingFailed' && !m.params.canceled) {
      const url = p.requests.get(m.params.requestId) ?? '?'
      if (!p.faked.has(url)) p.failed.push(`${m.params.errorText} ${url}`)
    }
    if (m.method === 'Network.responseReceived' && m.params.response.status >= 400) p.failed.push(`HTTP ${m.params.response.status} ${m.params.response.url}`)
  })
  return p
}

// ---- in the page: what state we are in, and what can be clicked from it ----
function stateKey() {
  const room = document.querySelector('.exo-titlebar [aria-current="page"]')?.textContent.trim().replace(/\s*\d+$/, '') ?? ''
  const dialogs = [...document.querySelectorAll('[role="dialog"],[role="alertdialog"]')].filter((d) => d.checkVisibility()).map((d) => d.getAttribute('aria-label') || d.querySelector('h1,h2,h3')?.textContent.trim() || 'dialog').join('+')
  const heads = [...document.querySelectorAll('main h1, main h2, [role="dialog"] h2')].filter((h) => h.checkVisibility()).slice(0, 3).map((h) => h.textContent.trim().slice(0, 30)).join('/')
  const menu = [...document.querySelectorAll('[role="menu"],[role="listbox"]')].some((m) => m.checkVisibility()) ? '+menu' : ''
  const tab = [...document.querySelectorAll('[role="tab"][aria-selected="true"]')].filter((t) => t.checkVisibility()).map((t) => t.textContent.trim().slice(0, 20)).join(',')
  return [room, dialogs, heads, tab].join(' | ') + menu
}
function candidates(risky, max) {
  const INTER = 'button,[role=button],[role=tab],[role=menuitem],[role=menuitemradio],[role=option],a[href],[role=switch]'
  const vis = (e) => { const b = e.getBoundingClientRect(); if (!(b.width > 0 && b.height > 0 && b.bottom > 0 && b.top < innerHeight && b.right > 0 && b.left < innerWidth && e.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }))) return false; const t = document.elementFromPoint(Math.min(innerWidth - 1, Math.max(0, b.left + b.width / 2)), Math.min(innerHeight - 1, Math.max(0, b.top + b.height / 2))); return !!t && (e === t || e.contains(t) || t.contains(e)) }
  const name = (e) => (e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 60)
  const re = new RegExp(risky, 'i')
  const seen = new Map(), out = []
  // A menu or a modal open on top takes the clicks: what is behind it is not reachable by a person either.
  const top = [...document.querySelectorAll('[role="menu"],[role="listbox"],[aria-modal="true"],[role="alertdialog"]')].filter((m) => m.checkVisibility()).pop() ?? document
  for (const e of top.querySelectorAll(INTER)) {
    if (out.length >= max) break
    if (e.closest('.exo-titlebar, [data-crawl-skip], [aria-disabled="true"]') || e.disabled || !vis(e)) continue
    const n = name(e); if (!n || re.test(n)) continue
    if (e.tagName === 'A' && /^(https?:|mailto:)/.test(e.getAttribute('href') || '') ) continue
    const sig = (e.getAttribute('role') || e.tagName.toLowerCase()) + '|' + n
    const nth = seen.get(sig) ?? 0; seen.set(sig, nth + 1)
    if (nth > 0) continue // one of a kind: the tenth game card says nothing the first did not
    out.push(sig)
  }
  return out
}
function clickSig(sig) {
  const [role, n] = [sig.slice(0, sig.indexOf('|')), sig.slice(sig.indexOf('|') + 1)]
  const INTER = 'button,[role=button],[role=tab],[role=menuitem],[role=menuitemradio],[role=option],a[href],[role=switch]'
  const e = [...document.querySelectorAll(INTER)].find((x) => (x.getAttribute('role') || x.tagName.toLowerCase()) === role && (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 60) === n && x.checkVisibility() && !x.closest('.exo-titlebar'))
  if (!e) return null
  e.scrollIntoView({ block: 'nearest' })
  const b = e.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }
}

// ---- one look at one state ----
const results = []
let shots = 0
async function look(p, where) {
  await sleep(250)
  const rec = { where, key: await p.fn(stateKey).catch(() => '?'), fail: [], warn: [] }
  const consoleErrors = p.console.filter((c) => c.type === 'error').map((c) => c.text)
  for (const e of p.errors) rec.fail.push(`exception: ${e.split('\n')[0]}`)
  for (const t of [...consoleErrors, ...p.failed]) {
    // With ?fail the mock refuses every call on purpose, and the page reporting each refusal is what it should do.
    const k = known(t) ?? (p.failMode && /Host refused/.test(t) ? { why: 'the mock refused this call on purpose (?fail)' } : null)
    if (k) rec.warn.push(`known (${k.why}): ${t.slice(0, 160)}`); else rec.fail.push(`${p.failed.includes(t) ? 'request' : 'console'}: ${t.slice(0, 200)}`)
  }
  p.console.length = 0; p.errors.length = 0; p.failed.length = 0
  try {
    const L = await p.fn(A.layoutAudit)
    if (L.overflowX > 0) rec.fail.push(`the page scrolls sideways by ${L.overflowX} px`)
    for (const c of L.clipped.filter((c) => c.pastWindow).slice(0, 3)) rec.fail.push(`words past the window: ${c.sel} "${c.text}"`)
    if (L.unnamed.length) rec.warn.push(`${L.unnamed.length} unnamed control(s): ${L.unnamed.slice(0, 2).map((u) => u.sel).join(', ')}`)
    if (L.small.length) rec.warn.push(`${L.small.length} target(s) under 28 px: ${L.small.slice(0, 2).map((s) => `${s.sel} ${s.w}x${s.h}`).join(', ')}`)
    // While a modal is open the room behind it is dimmed on purpose (the one scrim): only the modal's words count.
    const modals = await p.fn(() => [...document.querySelectorAll('[aria-modal="true"],[role="alertdialog"]')].filter((d) => d.checkVisibility()).map((d) => { const r = d.getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom] }))
    // Under forced colours Chromium draws system colours but still reports the page's own: the measure would compare
    // the wrong colour with the pixels, so contrast is left to Windows there.
    let all = []
    if (p.forced) rec.warn.push('contrast not measured under forced colours')
    else {
      const items = (await p.fn(A.textItems)).filter((it) => !modals.length || modals.some(([l, t, r, b]) => it.rect[0] >= l - 1 && it.rect[1] >= t - 1 && it.rect[2] <= r + 1 && it.rect[3] <= b + 1))
      await p.fn(A.hideText, true); await sleep(120)
      const b64 = await p.pixels()
      await p.fn(A.hideText, false)
      all = await p.eval(`(${A.contrast.toString()})(${JSON.stringify(items)}, ${JSON.stringify(b64)})`)
    }
    const seen = new Set()
    for (const c of all.filter((x) => x.p5 < 3).sort((a, b) => a.p5 - b.p5)) { if (seen.has(c.sel)) continue; seen.add(c.sel); if (seen.size <= 4) rec.fail.push(`contrast ${c.p5}:1 (under 3:1): ${c.sel} "${c.text}"`) }
    const soft = all.filter((x) => x.p5 >= 3 && x.p5 < x.need)
    if (soft.length) rec.warn.push(`${soft.length} text run(s) under ${soft[0].need}:1, lowest ${Math.min(...soft.map((s) => s.p5))}:1 (${soft[0].sel})`)
    if (await p.fn(() => /something went wrong|unexpected error|this room stopped/i.test(document.body.innerText))) rec.fail.push('an error screen shows')
  } catch (error) { rec.fail.push(`the audit could not run: ${String(error.message).slice(0, 160)}`) }
  if (rec.fail.length) { rec.shot = `fail-${++shots}.png`; await p.shot(join(outDir, rec.shot)).catch(() => { rec.shot = null }) }
  results.push(rec)
  process.stdout.write(`${rec.fail.length ? 'FAIL' : rec.warn.length ? 'warn' : 'ok  '} ${where}${rec.fail.length ? ' — ' + rec.fail.slice(0, 2).join('; ') : ''}\n`)
  return rec
}

// ---- getting to a state: open the mock, go to a room, replay the clicks ----
async function reach(p, start, path) {
  try { return await reachOrThrow(p, start, path) } catch (error) { p.reachError = String(error.message); return false }
}
async function reachOrThrow(p, start, path) {
  await p.goto(ORIGIN + '?' + ['signedin', start.query].filter(Boolean).join('&'))
  await sleep(900)
  // The page that ran with ?fail still logs its late refusals after the next one loads: those are not this page's.
  if (p.wasFail && !/(^|&)fail/.test(start.query ?? '')) { for (let i = p.console.length - 1; i >= 0; i--) if (/Host refused/.test(p.console[i].text)) p.console.splice(i, 1); p.wasFail = false }
  if (start.room) {
    if (start.room === 'Devices') {
      const pc = await p.fn(() => { const b = [...document.querySelectorAll('.exo-titlebar button, nav button')].find((x) => (x.getAttribute('aria-label') || x.textContent || '').trim().replace(/\s*\d+$/, '') === 'PC'); b?.click(); return !!b })
      if (!pc) throw new Error('no PC tab in the Bar')
      await sleep(1100)
      const dev = await p.fn(() => { const b = [...document.querySelectorAll('button.exo-pc-chip')].find((x) => (x.getAttribute('aria-label') || x.textContent || '').trim() === 'Devices'); b?.click(); return !!b })
      if (!dev) throw new Error('no Devices chip in PC')
      await sleep(1300)
    } else {
      const ok = await p.fn((r) => { const b = [...document.querySelectorAll('.exo-titlebar button, nav button')].find((x) => (x.getAttribute('aria-label') || x.textContent || '').trim().replace(/\s*\d+$/, '') === r); b?.click(); return !!b }, start.room)
      if (!ok) throw new Error(`no ${start.room} tab in the Bar`)
      await sleep(1300)
    }
  }
  if (start.open) await start.open(p)
  for (const sig of path) {
    const at = await p.fn(clickSig, sig)
    if (!at) return false
    await p.click(at.x, at.y); await sleep(800)
  }
  return true
}

async function explore(p, start, label) {
  if (!await reach(p, start, [])) { results.push({ where: label, fail: [`could not be opened: ${p.reachError}`], warn: [] }); console.log(`FAIL ${label} — could not be opened: ${p.reachError}`); return }
  const root = await look(p, label)
  const seen = new Set([root.key])
  const queue = [[]]
  let looked = 0
  while (queue.length && looked < PER_ROOM) {
    const path = queue.shift()
    if (path.length >= DEPTH) continue
    if (!await reach(p, start, path)) continue
    const here = await p.fn(stateKey)
    const sigs = await p.fn(candidates, RISKY.source, PER_STATE)
    for (const sig of sigs) {
      if (looked >= PER_ROOM) break
      if (await p.fn(stateKey) !== here && !await reach(p, start, path)) break
      const at = await p.fn(clickSig, sig)
      if (!at) continue
      await p.click(at.x, at.y); await sleep(900)
      const key = await p.fn(stateKey).catch(() => '?')
      if (seen.has(key)) { p.console.length = 0; continue }
      seen.add(key); looked++
      await look(p, `${label} › ${[...path, sig].map((s) => s.split('|')[1]).join(' › ')}`)
      queue.push([...path, sig])
    }
  }
}

const ROOMS = ['Library', 'Store', 'Discord', 'Music', 'PC', 'Devices']
const EXTRAS = [
  { label: 'Settings', start: { open: async (p) => { await p.clickSel('.exo-settings-btn'); await sleep(900) } } },
  { label: 'Ctrl+K', start: { open: async (p) => { await p.key('k', { ctrl: true }); await sleep(600); await p.key('a'); await sleep(600) } } },
  // The capture button is a placeholder until Exo Capture's state arrives.
  { label: 'Capture menu', start: { open: async (p) => { await p.waitFor(() => !!document.querySelector('button.exo-capture-btn')); await p.clickSel('button.exo-capture-btn'); await sleep(600) } } },
]
const STARTS = [['signed out', 'signedout'], ["What's New", 'whatsnew'], ['an update', 'update'], ['a song and a call live', 'live']]
const MODES = quick ? [] : [['every host call fails', 'fail'], ['no games', 'empty'], ['560 games', 'stress'], ['a slow host', 'slow=1500']]
const VIEWS = quick ? [['', {}]] : [['', {}], [' (more contrast)', { contrast: true }], [' (forced colours)', { forced: true }]]

mkdirSync(outDir, { recursive: true })
const viteCache = mkdtempSync(join(tmpdir(), 'exo-crawl-vite-'))
const server = spawn(process.execPath, [join(import.meta.dirname, 'serve.mjs')], { env: { ...process.env, EXO_UIPASS_PORT: String(uiPort), EXO_UIPASS_CACHE_DIR: viteCache }, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true })
let serverLog = ''
server.stdout.on('data', (d) => { serverLog += d }); server.stderr.on('data', (d) => { serverLog += d })
let browser
const started = Date.now()
try {
  for (let i = 0; ; i++) {
    try { if ((await fetch(ORIGIN)).ok) break } catch { /* not yet */ }
    if (i > 240 || server.exitCode != null) throw new Error('the Vite server did not start: ' + serverLog.slice(-400))
    await sleep(250)
  }
  browser = await launch()
  for (const [suffix, view] of VIEWS) {
    const p = await newPage(view)
    p.forced = !!view.forced
    const full = suffix === ''
    for (const room of ROOMS) full ? await explore(p, { room }, room) : (await reach(p, { room }, []), await look(p, room + suffix))
    for (const extra of EXTRAS) full ? await explore(p, extra.start, extra.label) : (await reach(p, extra.start, []), await look(p, extra.label + suffix))
    if (full) for (const [label, query] of STARTS) { await reach(p, { query: query === 'signedout' ? '' : query }, []); if (query === 'signedout') await p.goto(ORIGIN); await sleep(900); await look(p, label) }
    if (full) for (const [label, query] of MODES) for (const room of ROOMS) { p.failMode = query === 'fail'; await reach(p, { room, query }, []); await sleep(query.startsWith('slow') ? 2500 : 300); await look(p, `${room} with ${label}`); p.wasFail = p.failMode; p.failMode = false }
    await p.close()
  }
} finally {
  try { browser?.proc.kill() } catch { /* gone */ }
  server.kill()
  await sleep(500)
  try { rmSync(viteCache, { recursive: true, force: true }) } catch { /* in use */ }
  try { if (browser?.dir) rmSync(browser.dir, { recursive: true, force: true }) } catch { /* in use */ }
}

const failing = results.filter((r) => r.fail.length)
const byKind = {}
for (const r of failing) for (const f of r.fail) { const kind = f.split(/[:(]/)[0].replace(/\d+(\.\d+)?/g, 'N').trim(); (byKind[kind] ??= []).push(`${r.where}: ${f}`) }
writeFileSync(join(outDir, 'crawl.json'), JSON.stringify({ when: new Date().toISOString(), quick, seconds: Math.round((Date.now() - started) / 1000), states: results.length, failing: failing.length, results }, null, 1))
const lines = [
  `# UI crawl${quick ? ' (quick)' : ''}`, '',
  `${results.length} states looked at in ${Math.round((Date.now() - started) / 1000)} s; **${failing.length} failing**, ${results.filter((r) => !r.fail.length && r.warn.length).length} with warnings only.`, '',
  ...Object.entries(byKind).sort((a, b) => b[1].length - a[1].length).flatMap(([kind, list]) => [`## ${kind} (${list.length})`, '', ...[...new Set(list)].slice(0, 30).map((l) => `- ${l}`), '']),
]
writeFileSync(join(outDir, 'crawl-summary.md'), lines.join('\n'))
console.log(`\n${results.length} states, ${failing.length} failing. Report: ${join(outDir, 'crawl-summary.md')}`)
process.exit(failing.length ? 1 : 0)

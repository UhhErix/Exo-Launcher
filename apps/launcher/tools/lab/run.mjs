#!/usr/bin/env node
// Exo Lab: a repeatable performance meter for the installed Exo (or any Exo app with a CDP port), so a performance
// change is accepted on before/after numbers rather than on feel. tools/lab/README.md has the metrics and the rule.
//
//   node tools/lab/run.mjs --label before [--rounds 3] [--rooms Library,Store] [--presses 5] [--warmup 15] [--etw]
//   node tools/lab/run.mjs --attach [--port 9339]               # measure an Exo already running with CDP; no restart
//   node tools/lab/run.mjs --app <name> --exe <path> --port <port> --page <url part> --attach   # another app
//   node tools/lab/run.mjs --compare <reportA> <reportB>        # A then B; exit 1 when B is worse on a key metric
//
// The meter runs at Idle priority (its PowerShell children inherit it), never focuses, clicks or moves a real window
// (CDP input only), and keeps tools/e2e's rules: no run during a Discord call or a game, guarded files put back, Exo
// closed normally and started again normally. The report lands in lab-reports/<yyyyMMdd-HHmmss>/.
import os from 'node:os'
import { spawn, execFile } from 'node:child_process'
import { mkdir, readFile, writeFile, readdir } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { join, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { openPage, sleep } from '../e2e/cdp.mjs'
import * as exo from '../e2e/lib/exo.mjs'
import { win } from '../e2e/lib/win.mjs'
import { kit, ROOMS, roomTab } from '../e2e/lib/kit.mjs'
import { frameStart, frameStop } from '../e2e/lib/page.mjs'

const { PRIORITY_LOW, PRIORITY_NORMAL } = os.constants.priority
// PRIORITY_LOW is IDLE_PRIORITY_CLASS on Windows; processes this one starts inherit it.
os.setPriority(0, PRIORITY_LOW)

const here = dirname(fileURLToPath(import.meta.url))
const repo = join(here, '..', '..')
const argv = process.argv.slice(2)
const flag = (name) => argv.includes('--' + name)
const opt = (name, fallback) => { const i = argv.indexOf('--' + name); return i >= 0 && argv[i + 1] !== undefined ? argv[i + 1] : fallback }

const median = (a) => { const s = a.filter(Number.isFinite).sort((x, y) => x - y); if (!s.length) return null; const m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2 }
const pct = (a, p) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.min(s.length - 1, Math.ceil(s.length * p) - 1)] : null }
const round = (v, d = 2) => v == null ? null : Math.round(v * 10 ** d) / 10 ** d
const MB = 1048576
const home = os.homedir()
const mask = (s) => String(s ?? '').split(home).join('%USERPROFILE%').replace(/[\w.+-]+@[\w-]+\.[\w.]+/g, '<email>')

// ---------------------------------------------------------------------------------------------------------- compare

/** Lower is better for every metric except these, which only describe the run. */
const INFO = /(^|\.)frame\.count$|\.procs$/
/** The metrics a batch is accepted or reverted on (across rooms, the median of the room medians). */
const KEY = ['all.frame.p95', 'all.frame.dropped', 'all.press.frameMs', 'all.mem.gpu.privateMB', 'all.mem.renderer.privateMB', 'all.mem.host.privateMB', 'all.cpu.host', 'all.cpu.webview']
/** Noise floor per kind: a change smaller than both the share and the absolute amount is "same". */
const floor = (key) => key.includes('.mem.') || key.includes('heap') ? { rel: 0.05, abs: 5 }
  : key.includes('.cpu.') ? { rel: 0.10, abs: 0.3 }
    : key.includes('.cs.') ? { rel: 0.10, abs: 50 }
      : key.endsWith('dropped') ? { rel: 0.10, abs: 1 }
        : { rel: 0.05, abs: 0.5 }

function flatten(report) {
  const out = {}
  for (const [room, r] of Object.entries(report.rooms ?? {})) for (const [k, v] of Object.entries(r.median ?? {})) out[`${room}.${k}`] = v
  for (const [k, v] of Object.entries(report.overall ?? {})) out[`all.${k}`] = v
  return out
}

async function compare(pathA, pathB) {
  const load = async (p) => { const f = existsSync(join(p, 'report.json')) ? join(p, 'report.json') : p; return JSON.parse(await readFile(f, 'utf8')) }
  const [A, B] = await Promise.all([load(pathA), load(pathB)])
  const a = flatten(A), b = flatten(B)
  const warn = []
  if (A.machine?.gpus?.[0]?.name !== B.machine?.gpus?.[0]?.name || A.machine?.cpu !== B.machine?.cpu) warn.push('different machines: the numbers do not compare')
  if (A.app?.name !== B.app?.name) warn.push(`different apps (${A.app?.name} / ${B.app?.name})`)
  if (A.options?.rounds !== B.options?.rounds || A.options?.presses !== B.options?.presses || (A.options?.rooms ?? []).join() !== (B.options?.rooms ?? []).join()) warn.push('different scripts (rounds, presses or rooms differ)')
  if (A.options?.lab !== B.options?.lab) warn.push(`different meter versions (${A.options?.lab} / ${B.options?.lab})`)
  const rows = []
  for (const key of [...new Set([...Object.keys(a), ...Object.keys(b)])].sort((x, y) => (x.startsWith('all.') ? 0 : 1) - (y.startsWith('all.') ? 0 : 1) || x.localeCompare(y))) {
    const va = a[key], vb = b[key]
    let mark = '', delta = null
    if (va != null && vb != null) {
      delta = va === 0 ? (vb === 0 ? 0 : null) : (vb - va) / Math.abs(va) * 100
      const f = floor(key), diff = vb - va
      mark = INFO.test(key) ? 'info' : Math.abs(diff) <= f.abs || Math.abs(diff) <= Math.abs(va) * f.rel ? 'same' : diff < 0 ? 'better' : 'worse'
    }
    rows.push({ key, a: va, b: vb, delta, mark, key_: KEY.includes(key) })
  }
  const w = Math.max(...rows.map(r => r.key.length)) + 2
  const cell = (v) => v == null ? '-' : String(round(v, 2))
  const lines = [`A: ${A.label} ${A.startedAt} (${A.app?.version})`, `B: ${B.label} ${B.startedAt} (${B.app?.version})`, ...warn.map(x => 'warning: ' + x), '',
    `${'metric'.padEnd(w)}${'A'.padStart(10)}${'B'.padStart(10)}${'delta %'.padStart(10)}  mark`]
  for (const r of rows) lines.push(`${(r.key_ ? '* ' : '  ') + r.key.padEnd(w - 2)}${cell(r.a).padStart(10)}${cell(r.b).padStart(10)}${(r.delta == null ? '-' : (r.delta > 0 ? '+' : '') + r.delta.toFixed(1)).padStart(10)}  ${r.mark}`)
  const keyRows = rows.filter(r => r.key_)
  const worse = keyRows.filter(r => r.mark === 'worse'), better = keyRows.filter(r => r.mark === 'better')
  const verdict = worse.length ? `WORSE: B is worse on ${worse.map(r => r.key.slice(4)).join(', ')}${better.length ? ` (better on ${better.map(r => r.key.slice(4)).join(', ')})` : ''} - revert` : better.length ? `BETTER: B is better on ${better.map(r => r.key.slice(4)).join(', ')} and worse on no key metric - accept` : 'SAME: no key metric moved beyond noise - accept only if the change has another reason'
  lines.push('', '* key metric (lower is better; "same" is within the noise floor)', `verdict: ${verdict}`)
  console.log(lines.join('\n'))
  return worse.length ? 1 : 0
}

if (flag('compare')) {
  const i = argv.indexOf('--compare')
  const [pa, pb] = [argv[i + 1], argv[i + 2]]
  if (!pa || !pb) { console.error('usage: node tools/lab/run.mjs --compare <reportA> <reportB>'); process.exit(2) }
  process.exit(await compare(resolve(pa), resolve(pb)))
}

// ------------------------------------------------------------------------------------------------------------ setup

const LAB_VERSION = 1
const appName = opt('app', 'exo')
const isExo = appName === 'exo'
const app = isExo
  ? { name: 'exo', exe: opt('exe', exo.EXE), port: Number(opt('port', 9339)), page: opt('page', 'app.exo-launcher.local'), rooms: opt('rooms', '') ? opt('rooms').split(',') : ROOMS }
  : { name: appName, exe: opt('exe', ''), port: Number(opt('port', 0)), page: opt('page', ''), rooms: ['page'], press: opt('press', '') }
const attach = flag('attach')
if (!isExo && (!app.exe || !app.port || !app.page || !attach)) {
  console.error('another app needs --exe <path> --port <n> --page <url part> --attach (start it with its CDP port yourself)')
  process.exit(2)
}
const rounds = Number(opt('rounds', 3))
const presses = Number(opt('presses', 5))
const warmup = Number(opt('warmup', attach ? 0 : 15))
const settleSec = 5, idleSec = 10
const label = opt('label', 'unlabeled')
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, '').replace('T', '-')
const out = resolve(opt('out', join(repo, 'lab-reports', stamp)))

/** tools/lab/proc.ps1, kept running: one JSON line per command. */
function sampler(exe) {
  const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', join(here, 'proc.ps1'), '-Exe', exe], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  try { os.setPriority(child.pid, PRIORITY_LOW) } catch { /* inherited anyway */ }
  let buffer = '', err = ''
  const waiting = []
  child.stdout.on('data', (d) => {
    buffer += d
    let i
    while ((i = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, i).trim(); buffer = buffer.slice(i + 1)
      if (line.startsWith('{')) waiting.shift()?.(JSON.parse(line))
    }
  })
  child.stderr.on('data', (d) => { err += d })
  return {
    ask: (command, timeoutMs = 60000) => new Promise((ok, fail) => {
      const timer = setTimeout(() => fail(new Error(`proc.ps1 ${command}: no answer ${err.slice(0, 300)}`)), timeoutMs)
      waiting.push((r) => { clearTimeout(timer); r.ok ? ok(r) : fail(new Error(`proc.ps1 ${command}: ${r.error}`)) })
      child.stdin.write(command + '\n')
    }),
    close: () => child.stdin.end(),
  }
}

const groupOf = (p, root) => p.pid === root ? 'host'
  : /^msedgewebview2\.exe$/i.test(p.name) ? ({ browser: 'browser', 'gpu-process': 'gpu', renderer: 'renderer' }[p.type] ?? 'utility')
    : 'other'
const GROUPS = ['host', 'browser', 'gpu', 'renderer', 'utility', 'other']

function memory(sample) {
  const m = {}
  for (const g of [...GROUPS, 'total']) m[g] = { privateMB: 0, wsMB: 0, procs: 0 }
  for (const p of sample.procs) for (const g of [groupOf(p, sample.root), 'total']) {
    m[g].privateMB += p.private / MB; m[g].wsMB += p.ws / MB; m[g].procs++
  }
  for (const g of Object.values(m)) { g.privateMB = round(g.privateMB, 1); g.wsMB = round(g.wsMB, 1) }
  return m
}

/** CPU (% of one core) and context switches per second between two samples, for the host and the WebView2 tree. */
function activity(a, b) {
  const seconds = (b.at - a.at) / 1000
  const before = new Map(a.procs.map(p => [p.pid, p]))
  const sum = { host: { cpu: 0, cs: 0 }, webview: { cpu: 0, cs: 0 }, other: { cpu: 0, cs: 0 } }
  for (const p of b.procs) {
    const q = before.get(p.pid)
    if (!q) continue
    const g = groupOf(p, b.root)
    const into = g === 'host' ? sum.host : g === 'other' ? sum.other : sum.webview
    into.cpu += p.cpuMs - q.cpuMs
    // Per thread: a thread that started in the window counts whole, one that ended is lost (a slight undercount).
    const was = new Map()
    for (let i = 0; i < q.cs.length; i += 2) was.set(q.cs[i], q.cs[i + 1])
    for (let i = 0; i < p.cs.length; i += 2) into.cs += p.cs[i + 1] - (was.get(p.cs[i]) ?? 0)
  }
  return Object.fromEntries(Object.entries(sum).map(([k, v]) => [k, { cpu: round(v.cpu / seconds / 10, 2), cs: round(v.cs / seconds, 0) }]))
}

/** Installed in the page before one press: from the pointerdown's timeStamp to the next frame, and to the frame after the DOM's first change. */
function pressProbe() {
  window.__labPress = new Promise(resolve => {
    const on = (e) => {
      removeEventListener('pointerdown', on, true)
      const t0 = e.timeStamp
      let frame = null, done = false
      requestAnimationFrame(() => { frame = performance.now() - t0 })
      const mo = new MutationObserver(() => {
        mo.disconnect()
        requestAnimationFrame(() => { if (!done) { done = true; resolve({ frame, change: performance.now() - t0 }) } })
      })
      mo.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true })
      setTimeout(() => { if (!done) { done = true; mo.disconnect(); resolve({ frame, change: null }) } }, 150)
    }
    addEventListener('pointerdown', on, true)
  })
  return true
}

/** The largest visible element that scrolls vertically (the room's main scroller), or a large iframe (Exo Mouse). */
function mainScroller() {
  let best = null
  for (const el of document.querySelectorAll('*')) {
    if (el.scrollHeight <= el.clientHeight + 40 || el.clientWidth < 200 || el.clientHeight < 150) continue
    const r = el.getBoundingClientRect()
    if (r.bottom <= 0 || r.top >= innerHeight || r.right <= 0 || r.left >= innerWidth) continue
    const oy = getComputedStyle(el).overflowY
    if (oy !== 'auto' && oy !== 'scroll' && el !== document.scrollingElement) continue
    if (!el.checkVisibility()) continue
    const x = r.left + r.width / 2, y = Math.min(innerHeight - 40, r.top + Math.min(r.height / 2, 300))
    if (r.width < 200 || r.height < 150 || !el.contains(document.elementFromPoint(x, y))) continue
    const area = r.width * r.height
    if (!best || area > best.area) best = { el, area, x, y }
  }
  if (best) {
    window.__labScroller = best.el
    const el = best.el, room = el.scrollHeight - el.clientHeight
    return { x: Math.round(best.x), y: Math.round(best.y), what: (el.className?.toString() || el.tagName).split(' ')[0].slice(0, 40), down: el.scrollTop < room / 2, top: el.scrollTop }
  }
  const frame = [...document.querySelectorAll('iframe')].map(f => [f, f.getBoundingClientRect()]).filter(([f, r]) => r.width > 300 && r.height > 200 && f.checkVisibility()).sort((x, y) => y[1].width * y[1].height - x[1].width * x[1].height)[0]
  window.__labScroller = null
  return frame ? { x: Math.round(frame[1].left + frame[1].width / 2), y: Math.round(frame[1].top + frame[1].height / 2), what: 'iframe', down: true, top: 0 } : null
}

function frameStats(gaps, refreshMs) {
  if (!gaps.length) return { 'frame.count': 0 }
  return {
    'frame.count': gaps.length,
    'frame.p50': round(pct(gaps, 0.5)), 'frame.p95': round(pct(gaps, 0.95)), 'frame.p99': round(pct(gaps, 0.99)), 'frame.max': round(Math.max(...gaps)),
    // A gap of k refresh intervals means k - 1 frames the screen showed without a new one from the page.
    'frame.dropped': gaps.reduce((n, g) => n + Math.max(0, Math.round(g / refreshMs) - 1), 0),
  }
}

// ------------------------------------------------------------------------------------------------------- PresentMon

async function presentMon() {
  const candidates = [process.env.EXO_PRESENTMON, ...(existsSync(join(here, 'bin')) ? (await readdir(join(here, 'bin'))).filter(f => /^presentmon.*\.exe$/i.test(f)).map(f => join(here, 'bin', f)) : [])].filter(Boolean)
  const exe = candidates.find(existsSync)
  if (!exe) return null
  const v2 = await new Promise(ok => execFile(exe, ['--version'], { windowsHide: true, timeout: 10000 }, (e) => ok(!e)))
  return { exe, v2 }
}

/** Presents of our own WebView2 tree while `action` runs: PresentMon's own timed capture, never stopped by force. */
async function presentsDuring(pm, pids, file, action) {
  const d = pm.v2 ? '--' : '-'
  const seconds = 8
  const args = [`${d}process_name`, 'msedgewebview2.exe', `${d}output_file`, file, `${d}timed`, String(seconds), `${d}terminate_after_timed`, `${d}stop_existing_session`, ...(pm.v2 ? ['--no_console_stats'] : ['-no_top'])]
  const child = spawn(pm.exe, args, { windowsHide: true, stdio: 'ignore' })
  const exited = new Promise(ok => child.on('close', (code) => ok(code)))
  await sleep(1500)
  await action()
  const code = await exited
  if (!existsSync(file)) return { error: `PresentMon exited ${code} without a capture (it needs an elevated prompt)` }
  const [head, ...rows] = (await readFile(file, 'utf8')).split(/\r?\n/).filter(Boolean)
  const cols = head.split(','), pidAt = cols.indexOf('ProcessID'), msAt = cols.findIndex(c => /^(MsBetweenPresents|FrameTime|msBetweenPresents)$/.test(c))
  if (pidAt < 0 || msAt < 0) return { error: 'PresentMon CSV has no ProcessID / MsBetweenPresents column' }
  const ms = rows.map(r => r.split(',')).filter(c => pids.has(Number(c[pidAt]))).map(c => Number(c[msAt])).filter(Number.isFinite)
  return { presents: ms.length, p50: round(pct(ms, 0.5)), p95: round(pct(ms, 0.95)), p99: round(pct(ms, 0.99)), max: round(Math.max(0, ...ms)) }
}

// -------------------------------------------------------------------------------------------------------------- run

/** Starts Exo at normal priority: a child takes this process's Idle class otherwise, and so would its WebView2. */
function launchNormal(options) {
  os.setPriority(0, PRIORITY_NORMAL)
  try { return exo.launch(options) } finally { os.setPriority(0, PRIORITY_LOW) }
}

const notes = []
const report = {
  label, startedAt: new Date().toISOString(), app: { name: app.name, exe: mask(app.exe) },
  options: { lab: LAB_VERSION, rounds, presses, rooms: app.rooms, warmupSec: warmup, settleSec, idleSec, attach, etw: flag('etw') }, notes,
}
const proc = sampler(app.exe)
const t = { shell: null }
const k = kit(t)
let guard = null, halted = null, exitCode = 0, startRoom = null
const began = Date.now()

const halt = async (where) => {
  if (!isExo) return
  if ((await proc.ask('game')).running) { halted = `a game started during ${where}; the run stops, Exo left as it is`; throw Object.assign(new Error(halted), { code: 2 }) }
}

try {
  await mkdir(out, { recursive: true })
  const version = await new Promise(ok => execFile('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `(Get-Item -LiteralPath '${app.exe.replace(/'/g, "''")}').VersionInfo.ProductVersion`], { windowsHide: true }, (e, so) => ok(e ? null : so.trim())))
  report.app.version = version
  report.app.commit = /\+([0-9a-f]{7,40})/.exec(version ?? '')?.[1] ?? null
  const machine = await proc.ask('machine')
  delete machine.ok
  report.machine = machine
  report.priority = { lab: 'Idle', sampler: machine.samplerPriority }
  console.log(`Exo Lab: ${app.name} ${version ?? '?'}; label ${label}; ${rounds} round(s) of ${app.rooms.join(', ')}; report ${out}`)

  if (isExo) {
    if ((await proc.ask('game')).running) throw Object.assign(new Error('a game is running; try again after it'), { code: 2 })
    if ((await exo.pids()).length) {
      const udp = await win.udp().catch(() => ({ count: 0 }))
      if (udp.count > 0) throw Object.assign(new Error('Exo is in a voice call; try again after it'), { code: 2 })
    }
    guard = new exo.Guard(join(out, 'guard'))
    await guard.save()
  }

  if (attach) {
    t.shell = isExo ? await exo.attach(app.port) : await openPage(app.port, app.page, 5000)
    if (!t.shell) throw Object.assign(new Error(`no ${app.page} page on 127.0.0.1:${app.port}`), { code: 2 })
    if (isExo) startRoom = (await k.state()).room
  } else {
    if ((await exo.pids()).length) await exo.close()
    const started = await launchNormal({ port: app.port })
    t.shell = started.shell
    await t.shell.waitFor(() => document.querySelector('.exo-titlebar') && !document.querySelector('.exo-boot'), { timeoutMs: 60000 })
    await k.warmedUp()
  }
  if (isExo && await t.shell.eval(`!!document.querySelector('.exo-continue-card.is-playing')`)) throw Object.assign(new Error('a game is running; try again after it'), { code: 2 })
  if (warmup) { console.log(`warming up ${warmup} s`); await sleep(warmup * 1000) }

  const first = await proc.ask('sample')
  report.priority.app = first.rootPriority
  if (first.rootPriority !== 'Normal') notes.push(`the app runs at ${first.rootPriority} priority, not Normal`)
  if (isExo) {
    const w = await win.window().catch(() => null)
    report.window = w && { width: w.client.w, height: w.client.h, maximized: w.maximized, minimized: w.minimized, foreground: w.foreground, scale: w.monitor?.scale }
    if (w?.minimized) notes.push('Exo was minimized: frames do not run; the numbers are not valid')
    else if (w && !w.foreground) notes.push('Exo was not the foreground window (the meter never focuses it): Chromium may throttle frames if it is covered')
  }

  // The page's own frame interval at rest is the refresh the dropped-frame count is measured against.
  await t.shell.call(frameStart); await sleep(1500)
  const idleGaps = await t.shell.call(frameStop)
  const refreshMs = median(idleGaps) || 1000 / (machine.gpus?.[0]?.refreshHz || 60)
  report.refresh = { measuredHz: round(1000 / refreshMs, 1), reportedHz: machine.gpus?.map(g => g.refreshHz) ?? [] }

  const pm = flag('etw') ? await presentMon() : null
  report.etw = !flag('etw') ? 'off' : pm ? { exe: mask(pm.exe), cli: pm.v2 ? 2 : 1 } : 'skipped: PresentMon not found (set EXO_PRESENTMON or put PresentMon*.exe in tools/lab/bin)'

  // Each round enters the first room from the last one, so every room is measured coming from the same neighbour.
  const rooms = {}
  const available = []
  if (isExo) {
    for (const room of app.rooms) {
      if (await t.shell.eval(`!!(${roomTab})(${JSON.stringify(room)})`).catch(() => false)) available.push(room)
      else rooms[room] = { skipped: 'no Bar tab for this room' }
    }
    if (available.length) await k.nav(available.at(-1)).catch(() => {})
  } else available.push('page')

  for (let r = 1; r <= rounds; r++) {
    for (const room of available) {
      const entry = rooms[room] ??= { rounds: [] }
      if (entry.skipped) continue
      process.stdout.write(`round ${r} ${room}: `)
      const m = {}
      try {
        // Motion: the room change into it, then the wheel down and back over its main scroller. Finding the scroller
        // walks every element, so it runs between two recordings rather than inside one.
        let navMs = null, scroller = null, gaps = []
        const phase = { start: Date.now() }
        const motion = async () => {
          await t.shell.call(frameStart)
          if (isExo) navMs = await k.nav(room)
          gaps = await t.shell.call(frameStop)
          phase.nav = Date.now() - phase.start
          scroller = await t.shell.call(mainScroller)
          phase.find = Date.now() - phase.start
          await t.shell.call(frameStart)
          if (scroller) {
            const dir = scroller.down ? 1 : -1
            for (let i = 0; i < 16; i++) { await t.shell.wheel(scroller.x, scroller.y, 120 * dir); await sleep(16) }
            await sleep(300)
            for (let i = 0; i < 16; i++) { await t.shell.wheel(scroller.x, scroller.y, -120 * dir); await sleep(16) }
          }
          await sleep(500)
          gaps.push(...(await t.shell.call(frameStop)).slice(1))
          phase.motion = Date.now() - phase.start
        }
        if (pm) {
          const pids = new Set((await proc.ask('sample')).procs.map(p => p.pid))
          entry.etw = [...(entry.etw ?? []), await presentsDuring(pm, pids, join(out, `presentmon-${room}-${r}.csv`), motion).catch(e => ({ error: e.message }))]
        } else await motion()
        Object.assign(m, frameStats(gaps, refreshMs))
        if (navMs != null) m['nav.ms'] = navMs
        if (scroller?.what !== 'iframe') await t.shell.call((top) => { if (window.__labScroller) window.__labScroller.scrollTop = top }, scroller?.top ?? 0)
        entry.scroller = scroller?.what ?? null

        // Press latency on something harmless: the current room's Bar tab (Exo), or --press for another app.
        const at = await t.shell.call((room, sel) => {
          const el = sel ? document.querySelector(sel) : [...document.querySelectorAll('.exo-titlebar button, nav button')].find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === room)
          const r = el?.getBoundingClientRect()
          return r?.width ? { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) } : null
        }, room, app.press ?? '')
        if (at) {
          const frames = [], changes = []
          await t.shell.refuseLaunch(`document.elementFromPoint(${at.x}, ${at.y})`, 'a press')
          await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: at.x, y: at.y })
          await sleep(200)
          for (let i = 0; i < presses; i++) {
            await t.shell.call(pressProbe)
            await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: at.x, y: at.y, button: 'left', clickCount: 1 })
            await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: at.x, y: at.y, button: 'left', clickCount: 1 })
            const p = await t.shell.eval('window.__labPress')
            if (p.frame != null) frames.push(p.frame)
            if (p.change != null) changes.push(p.change)
            await sleep(100)
          }
          m['press.frameMs'] = round(median(frames))
          // Only when the press itself changes the page: an unrelated change (a timer, a list loading) would be timed otherwise.
          if (changes.length * 2 > presses) m['press.changeMs'] = round(median(changes))
          entry.pressChanged = `${changes.length}/${presses}`
          // Off the tab, so a hover doesn't count as idle work.
          await t.shell.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: 2, y: Math.round(await t.shell.eval('innerHeight') - 2) })
        }
        phase.press = Date.now() - phase.start

        // At rest: memory after settling, then CPU over the idle window.
        await sleep(settleSec * 1000)
        await t.shell.s.send('Performance.enable').catch(() => {})
        const { metrics } = await t.shell.s.send('Performance.getMetrics')
        const heap = Object.fromEntries(metrics.map(x => [x.name, x.value]))
        m['heap.usedMB'] = round(heap.JSHeapUsedSize / MB, 1)
        m['heap.totalMB'] = round(heap.JSHeapTotalSize / MB, 1)
        const a = await proc.ask('sample')
        for (const [g, v] of Object.entries(memory(a))) {
          if (!v.procs) continue
          m[`mem.${g}.privateMB`] = v.privateMB; m[`mem.${g}.wsMB`] = v.wsMB; m[`mem.${g}.procs`] = v.procs
        }
        await sleep(idleSec * 1000)
        const b = await proc.ask('sample')
        const act = activity(a, b)
        m['cpu.host'] = act.host.cpu; m['cpu.webview'] = act.webview.cpu; m['cpu.other'] = act.other.cpu
        m['cs.host'] = act.host.cs; m['cs.webview'] = act.webview.cs
        phase.total = Date.now() - phase.start
        delete phase.start
        entry.phasesMs = [...(entry.phasesMs ?? []), phase]
        entry.rounds.push(m)
        console.log(`p95 ${m['frame.p95']} ms, press ${m['press.frameMs'] ?? '-'} ms, gpu ${m['mem.gpu.privateMB'] ?? '-'} MB, renderer ${m['mem.renderer.privateMB'] ?? '-'} MB, host ${m['mem.host.privateMB']} MB, cpu ${m['cpu.host']}/${m['cpu.webview']}%`)
      } catch (error) {
        console.log(`failed: ${error.message}`)
        entry.errors = [...(entry.errors ?? []), `round ${r}: ${error.message.slice(0, 200)}`]
        if (!entry.rounds.length && r === 1 && isExo && /did not become the current room|no Bar tab/.test(error.message)) entry.skipped = error.message
      }
      await halt(`${room}, round ${r}`)
    }
  }

  for (const entry of Object.values(rooms)) {
    if (!entry.rounds?.length) continue
    const keys = [...new Set(entry.rounds.flatMap(Object.keys))]
    entry.median = Object.fromEntries(keys.map(key => [key, round(median(entry.rounds.map(x => x[key])))]))
  }
  report.rooms = rooms
  const measured = Object.values(rooms).filter(r => r.median)
  const keys = [...new Set(measured.flatMap(r => Object.keys(r.median)))]
  report.overall = Object.fromEntries(keys.map(key => [key, round(median(measured.map(r => r.median[key])))]))
  if (isExo && startRoom && attach) await k.nav(startRoom).catch(() => {})
} catch (error) {
  console.error('run stopped:', error.message)
  report.stopped = error.message
  exitCode = error.code ?? 2
} finally {
  try { t.shell?.close() } catch { /* gone */ }
  try {
    if (isExo && guard && !halted) {
      if (!attach) {
        if ((await exo.pids()).length) await exo.close()
        report.restored = (await guard.restore()).map(f => f.split(/[\\/]/).pop())
        await launchNormal({})
      } else {
        const changed = await guard.changed()
        if (changed.length) notes.push(`attached: ${changed.map(f => f.split(/[\\/]/).pop()).join(', ')} changed while measuring and were left as Exo wrote them (copies in guard/)`)
      }
    } else if (halted) notes.push(`${halted}; guarded files not put back: copy them from guard/ with Exo closed, after the game`)
  } catch (error) { console.error('putting Exo back:', error.message); report.restoreError = error.message }
  proc.close()
  report.durationSec = Math.round((Date.now() - began) / 1000)
  await mkdir(out, { recursive: true })
  await writeFile(join(out, 'report.json'), JSON.stringify(report, null, 2))
  await writeFile(join(out, 'summary.md'), summary(report))
  console.log(`\n${report.stopped ? 'stopped' : 'done'} in ${report.durationSec} s - ${join(out, 'summary.md')}`)
  process.exit(exitCode)
}

function summary(r) {
  const v = (x) => x == null ? '-' : String(x)
  const lines = [`# Exo Lab: ${r.label}`, '',
    `- App: ${r.app.name} ${v(r.app.version)} (commit ${v(r.app.commit)})`,
    `- Machine: ${r.machine?.gpus?.map(g => g.name).join(', ') ?? '-'}; ${v(r.machine?.cpu)}; ${v(r.machine?.memoryGB)} GB; ${v(r.machine?.windows)}`,
    `- Screen: ${r.refresh ? `${r.refresh.measuredHz} Hz measured (${r.refresh.reportedHz.join('/')} reported)` : '-'}; window ${r.window ? `${r.window.width}x${r.window.height}${r.window.maximized ? ' maximized' : ''}, foreground ${r.window.foreground}` : '-'}`,
    `- Script: ${r.options.rounds} round(s), ${r.options.presses} presses, settle ${r.options.settleSec} s, idle ${r.options.idleSec} s, warm-up ${r.options.warmupSec} s; priority lab ${r.priority?.lab}, sampler ${v(r.priority?.sampler)}, app ${v(r.priority?.app)}`,
    `- Started ${r.startedAt}, took ${r.durationSec} s${r.stopped ? `; **stopped: ${r.stopped}**` : ''}`,
    `- ETW: ${typeof r.etw === 'string' ? r.etw : JSON.stringify(r.etw)}`, '',
    'Medians over rounds. Frames while entering the room and wheel-scrolling it; press = pointerdown to the next frame; memory = private MB after 5 s at rest; CPU = % of one core over 10 s at rest.', '',
    '| Room | frame p50 | p95 | p99 | max | dropped | press ms | nav ms | host MB | browser MB | gpu MB | renderer MB | utility MB | total MB | heap MB | CPU host % | CPU webview % |',
    '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
  const row = (name, m) => `| ${name} | ${['frame.p50', 'frame.p95', 'frame.p99', 'frame.max', 'frame.dropped', 'press.frameMs', 'nav.ms', 'mem.host.privateMB', 'mem.browser.privateMB', 'mem.gpu.privateMB', 'mem.renderer.privateMB', 'mem.utility.privateMB', 'mem.total.privateMB', 'heap.usedMB', 'cpu.host', 'cpu.webview'].map(k => v(m[k])).join(' | ')} |`
  for (const [name, room] of Object.entries(r.rooms ?? {})) lines.push(room.median ? row(name, room.median) : `| ${name} | skipped: ${room.skipped ?? (room.errors ?? []).join('; ')} |`)
  if (r.overall) lines.push(row('**all (median of rooms)**', r.overall))
  lines.push('', 'Working sets (MB) per group, medians:', '', '| Room | host | browser | gpu | renderer | utility | other | total |', '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
  for (const [name, room] of Object.entries(r.rooms ?? {})) if (room.median) lines.push(`| ${name} | ${GROUPS.concat('total').map(g => v(room.median[`mem.${g}.wsMB`])).join(' | ')} |`)
  const errors = Object.entries(r.rooms ?? {}).flatMap(([n, x]) => (x.errors ?? []).map(e => `${n}: ${e}`))
  if (errors.length || r.notes.length) lines.push('', '## Notes', '', ...r.notes.map(n => `- ${n}`), ...errors.map(e => `- ${e}`))
  return mask(lines.join('\n')) + '\n'
}

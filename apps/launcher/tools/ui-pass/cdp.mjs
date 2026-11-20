// Minimal CDP driver for the UI pass: launches Playwright's headless Chromium (or EXO_UIPASS_BROWSER), no packages.
import { spawn } from 'node:child_process'
import { mkdtempSync, existsSync, readdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
export const PORT = Number(process.env.EXO_UIPASS_CDP ?? 9391)
export const ORIGIN = process.env.EXO_UIPASS_ORIGIN ?? 'http://127.0.0.1:5291/'

function findBrowser() {
  if (process.env.EXO_UIPASS_BROWSER) return process.env.EXO_UIPASS_BROWSER
  const pw = join(process.env.LOCALAPPDATA ?? '', 'ms-playwright')
  if (existsSync(pw)) {
    for (const d of readdirSync(pw).filter((n) => n.startsWith('chromium-')).sort().reverse()) {
      const p = join(pw, d, 'chrome-win64', 'chrome.exe')
      if (existsSync(p)) return p
    }
  }
  for (const p of ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe']) if (existsSync(p)) return p
  throw new Error('no Chromium found; set EXO_UIPASS_BROWSER')
}

export async function launch() {
  const dir = mkdtempSync(join(tmpdir(), 'exo-uipass-'))
  const proc = spawn(findBrowser(), [
    '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${dir}`, '--no-first-run', '--no-default-browser-check',
    '--disable-gpu-vsync', '--hide-scrollbars', '--mute-audio', '--force-color-profile=srgb', '--disable-background-timer-throttling',
    '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows', 'about:blank',
  ], { stdio: 'ignore', windowsHide: true })
  for (let i = 0; i < 60; i++) {
    try { const r = await fetch(`http://127.0.0.1:${PORT}/json/version`); if (r.ok) return { proc, dir } } catch { /* wait */ }
    await sleep(250)
  }
  throw new Error('browser did not start')
}

export class Page {
  constructor(ws, id) { this.ws = ws; this.id = id; this.n = 0; this.pending = new Map(); this.handlers = []; this.console = []; this.errors = [] }
  static async create(url = 'about:blank') {
    const t = await (await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' })).json()
    const ws = new WebSocket(t.webSocketDebuggerUrl)
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
    const p = new Page(ws, t.id)
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data)
      if (m.id && p.pending.has(m.id)) { const { res, rej } = p.pending.get(m.id); p.pending.delete(m.id); m.error ? rej(new Error(m.error.message)) : res(m.result) }
      else if (m.method) {
        for (const h of p.handlers) h(m)
        if (m.method === 'Runtime.consoleAPICalled' && ['error', 'warning'].includes(m.params.type)) p.console.push({ type: m.params.type, text: m.params.args.map((a) => a.value ?? a.description ?? '').join(' ').slice(0, 300) })
        if (m.method === 'Runtime.exceptionThrown') p.errors.push((m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text).slice(0, 300))
        if (m.method === 'Log.entryAdded' && ['error', 'warning'].includes(m.params.entry.level)) p.console.push({ type: m.params.entry.level, text: (m.params.entry.text + ' ' + (m.params.entry.url ?? '')).slice(0, 300) })
      }
    }
    await p.send('Page.enable'); await p.send('Runtime.enable'); await p.send('Log.enable'); await p.send('DOM.enable'); await p.send('Network.enable')
    return p
  }
  send(method, params = {}) { const id = ++this.n; this.ws.send(JSON.stringify({ id, method, params })); return new Promise((res, rej) => this.pending.set(id, { res, rej })) }
  async eval(expr) {
    const r = await this.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true })
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
    return r.result.value
  }
  /** call a function (serialised) in page with JSON args */
  async fn(f, ...args) {
    const ctx = await this.send('Runtime.evaluate', { expression: 'globalThis' })
    if (ctx.exceptionDetails || !ctx.result?.objectId) throw new Error('no globalThis')
    try {
      const r = await this.send('Runtime.callFunctionOn', {
        functionDeclaration: f.toString(),
        objectId: ctx.result.objectId,
        arguments: args.map((v) => (v === undefined ? {} : { value: v })),
        awaitPromise: true,
        returnByValue: true,
      })
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
      return r.result.value
    } finally {
      await this.send('Runtime.releaseObject', { objectId: ctx.result.objectId }).catch(() => {})
    }
  }
  async waitFor(f, ...args) { const t0 = Date.now(); const timeout = 20000; while (Date.now() - t0 < timeout) { if (await this.fn(f, ...args)) return true; await sleep(150) } return false }
  async view(w, h, opts = {}) {
    await this.send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: opts.dpr ?? 1, mobile: false })
    const features = []
    if (opts.contrast) features.push({ name: 'prefers-contrast', value: 'more' })
    if (opts.forced) features.push({ name: 'forced-colors', value: 'active' })
    features.push({ name: 'prefers-reduced-motion', value: opts.reduce ? 'reduce' : 'no-preference' })
    await this.send('Emulation.setEmulatedMedia', { features })
  }
  async goto(url, marker) {
    await this.send('Page.navigate', { url })
    await sleep(500)
    if (marker) await this.waitFor((m) => document.body && document.body.innerText.includes(m), marker)
    await this.waitFor(() => !document.querySelector('.exo-boot.is-playing') && document.readyState === 'complete')
  }
  async shot(file, clip, scale = 1) {
    const r = await this.send('Page.captureScreenshot', { format: 'png', ...(clip ? { clip: { ...clip, scale } } : {}) })
    const { writeFileSync } = await import('node:fs')
    writeFileSync(file, Buffer.from(r.data, 'base64'))
    return file
  }
  async pixels() { // PNG of the viewport as base64 for in-page decode
    const r = await this.send('Page.captureScreenshot', { format: 'png' }); return r.data
  }
  async key(key, opts = {}) {
    const map = { Tab: 9, Enter: 13, Escape: 27, ArrowDown: 40, ArrowUp: 38, ArrowLeft: 37, ArrowRight: 39, ' ': 32 }
    const mods = (opts.ctrl ? 2 : 0) | (opts.shift ? 8 : 0) | (opts.alt ? 1 : 0)
    const code = map[key] ?? key.toUpperCase().charCodeAt(0)
    const base = { key, code: key.length === 1 ? 'Key' + key.toUpperCase() : key, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code, modifiers: mods }
    await this.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...base })
    if (key.length === 1 && !opts.ctrl) await this.send('Input.dispatchKeyEvent', { type: 'char', text: key, ...base })
    await this.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base })
  }
  async mouse(x, y, type = 'move', button = 'none') {
    await this.send('Input.dispatchMouseEvent', { type: type === 'move' ? 'mouseMoved' : type === 'down' ? 'mousePressed' : 'mouseReleased', x, y, button, clickCount: type === 'move' ? 0 : 1 })
  }
  async rclick(x, y) { await this.mouse(x, y, 'move'); await this.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'right', clickCount: 1 }); await this.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'right', clickCount: 1 }) }
  async click(x, y) { await this.mouse(x, y, 'move'); await this.mouse(x, y, 'down', 'left'); await this.mouse(x, y, 'up', 'left') }
  async clickSel(sel, nth = 0) {
    const r = await this.fn((s, n) => { const e = [...document.querySelectorAll(s)].filter((x) => { const b = x.getBoundingClientRect(); if (!(b.width > 0 && b.height > 0 && x.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }))) return false; const t = document.elementFromPoint(Math.min(innerWidth - 1, Math.max(0, b.left + b.width / 2)), Math.min(innerHeight - 1, Math.max(0, b.top + b.height / 2))); return !!t && (x === t || x.contains(t) || t.contains(x)) })[n]; if (!e) return null; const b = e.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 } }, sel, nth)
    if (!r) throw new Error('no element ' + sel)
    await this.click(r.x, r.y); return r
  }
  async clickText(text, scope = 'button,[role=button],[role=tab],[role=menuitem],[role=option],a,.exo-set-btn') {
    const r = await this.fn((t, s) => { const e = [...document.querySelectorAll(s)].filter((x) => { { const b = x.getBoundingClientRect(); if (!(b.width > 0 && b.height > 0 && x.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }))) return false; const t = document.elementFromPoint(Math.min(innerWidth - 1, Math.max(0, b.left + b.width / 2)), Math.min(innerHeight - 1, Math.max(0, b.top + b.height / 2))); if (!(!!t && (x === t || x.contains(t) || t.contains(x)))) return false } return ((x.getAttribute('aria-label') || '') + ' ' + x.textContent).replace(/\s+/g, ' ').trim().toLowerCase().includes(t.toLowerCase()) })[0]; if (!e) return null; const b = e.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 } }, text, scope)
    if (!r) throw new Error('no clickable text ' + text)
    await this.click(r.x, r.y); return r
  }
  async clickExact(text, scope = 'button,[role=tab],a') {
    const r = await this.fn((t, s) => { const e = [...document.querySelectorAll(s)].filter((x) => { const b = x.getBoundingClientRect(); return b.width > 0 && b.height > 0 && x.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) && (x.textContent || '').replace(/\s+/g, ' ').trim() === t })[0]; if (!e) return null; const b = e.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 } }, text, scope)
    if (!r) throw new Error('no exact text ' + text)
    await this.click(r.x, r.y); return r
  }
  close() { try { this.ws.close() } catch { /* ignore */ } return fetch(`http://127.0.0.1:${PORT}/json/close/${this.id}`).catch(() => {}) }
}

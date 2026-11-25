import { spawn } from 'node:child_process'
import { existsSync, mkdtempSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const MOUSE_DIR = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const VITE_PORT = Number(process.env.EXO_MOUSE_TEST_PORT ?? 5297)
const CDP_PORT = Number(process.env.EXO_MOUSE_CDP_PORT ?? 9397)
const IDLE_MS = Number(process.env.EXO_MOUSE_IDLE_MS ?? 62_000)

const sleep = ms => new Promise(r => setTimeout(r, ms))

function findBrowser() {
  if (process.env.EXO_UIPASS_BROWSER) return process.env.EXO_UIPASS_BROWSER
  const pw = join(process.env.LOCALAPPDATA ?? '', 'ms-playwright')
  if (existsSync(pw)) {
    for (const d of readdirSync(pw).filter(n => n.startsWith('chromium-')).sort().reverse()) {
      const p = join(pw, d, 'chrome-win64', 'chrome.exe')
      if (existsSync(p)) return p
    }
  }
  for (const p of [
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
  ]) {
    if (existsSync(p)) return p
  }
  throw new Error('No Chromium browser found.')
}

async function startVite() {
  const viteBin = join(MOUSE_DIR, 'node_modules', 'vite', 'bin', 'vite.js')
  const proc = spawn(process.execPath, [viteBin, '--host', '127.0.0.1', '--port', String(VITE_PORT), '--strictPort'], {
    cwd: MOUSE_DIR,
    stdio: 'ignore',
    windowsHide: true,
  })
  const url = `http://127.0.0.1:${VITE_PORT}/?mock&legacy-slx`
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(url)
      if (res.ok) return { proc, url }
    } catch {}
    await sleep(250)
  }
  proc.kill()
  throw new Error('Vite dev server did not start in time.')
}

async function startBrowser() {
  const dir = mkdtempSync(join(tmpdir(), 'exo-mouse-idle-'))
  const proc = spawn(findBrowser(), [
    '--headless=new',
    `--remote-debugging-port=${CDP_PORT}`,
    `--user-data-dir=${dir}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-background-timer-throttling',
    '--disable-renderer-backgrounding',
    '--disable-backgrounding-occluded-windows',
    'about:blank',
  ], { stdio: 'ignore', windowsHide: true })
  for (let i = 0; i < 60; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)
      if (res.ok) return { proc, dir }
    } catch {}
    await sleep(250)
  }
  proc.kill()
  throw new Error('Headless Chromium did not start in time.')
}

async function openCdpPage(url) {
  const target = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' })).json()
  const ws = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
  let seq = 0
  const pending = new Map()
  const exceptions = []
  ws.onmessage = e => {
    const m = JSON.parse(e.data)
    if (m.id && pending.has(m.id)) {
      const { res, rej } = pending.get(m.id)
      pending.delete(m.id)
      m.error ? rej(new Error(m.error.message)) : res(m.result)
    } else if (m.method === 'Runtime.exceptionThrown') {
      exceptions.push(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text)
    }
  }
  const send = (method, params = {}) => {
    const id = ++seq
    ws.send(JSON.stringify({ id, method, params }))
    return new Promise((res, rej) => pending.set(id, { res, rej }))
  }
  const evalJs = async expression => {
    const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
    return r.result?.value
  }
  await send('Page.enable')
  await send('Runtime.enable')
  return { ws, send, evalJs, exceptions }
}

const vite = await startVite()
const browser = await startBrowser()
let page = null
try {
  page = await openCdpPage(vite.url)
  for (let i = 0; i < 40; i++) {
    const ready = await page.evalJs(`document.body.innerText.includes('Finalmouse Starlight X') && document.body.innerText.includes('Profiles') && document.body.innerText.includes('Desktop')`)
    if (ready) break
    await sleep(250)
  }
  const loadedText = await page.evalJs('document.body.innerText')
  assert.ok(loadedText.includes('Finalmouse Starlight X'), 'Expected Starlight X panel to render')
  assert.ok(loadedText.includes('Desktop') && loadedText.includes('FPS'), 'Expected Desktop and FPS profiles to render')

  await sleep(IDLE_MS)

  const idleStats = await page.evalJs(`({
    settingWrites: window.__exoMouseMock.settingWrites.length,
    profileWrites: window.__exoMouseMock.profileWrites.length,
    polls: window.__exoMouseMock.polls,
    activeDesktop: document.body.innerText.includes('Desktop') && document.body.innerText.includes('Active'),
  })`)
  assert.equal(idleStats.settingWrites, 0, `Expected 0 setting writes over ${IDLE_MS} ms idle, got ${idleStats.settingWrites}`)
  assert.equal(idleStats.profileWrites, 0, `Expected 0 profile writes over ${IDLE_MS} ms idle, got ${idleStats.profileWrites}`)
  assert.ok(idleStats.polls >= 10, `Expected periodic polls to run during idle, got ${idleStats.polls}`)
  assert.equal(idleStats.activeDesktop, true, 'Expected Desktop profile to stay Active')

  await page.evalJs(`window.__exoMouseMock.emitGameRunState('riot:valorant', true)`)
  await sleep(600)
  await page.evalJs(`window.__exoMouseMock.emitGameRunState('riot:valorant', false)`)
  await sleep(2_600)
  const afterFlap = await page.evalJs(`window.__exoMouseMock.settingWrites.length`)
  assert.equal(afterFlap, 0, `Expected 0 writes on transient 600 ms game flap, got ${afterFlap}`)

  await page.evalJs(`window.__exoMouseMock.emitGameRunState('riot:valorant', true)`)
  await sleep(2_400)
  const afterGameStart = await page.evalJs(`({
    writes: window.__exoMouseMock.settingWrites.length,
    last: window.__exoMouseMock.settingWrites.at(-1),
  })`)
  assert.equal(afterGameStart.writes, 1, `Expected 1 write after stable game start, got ${afterGameStart.writes}`)
  assert.deepEqual(afterGameStart.last, [
    ['setClickMode', [1, 1, 0, 0, 2, 2]],
    ['setTmrActuation', [0.1, 0.1, 220, 220]],
  ])

  await page.evalJs(`window.__exoMouseMock.emitGameRunState('riot:valorant', false)`)
  await sleep(2_400)
  const afterGameStop = await page.evalJs(`window.__exoMouseMock.settingWrites.length`)
  assert.equal(afterGameStop, 2, `Expected 2 total writes after stable game stop back to Desktop, got ${afterGameStop}`)
  assert.equal(page.exceptions.length, 0, `Unexpected page exceptions: ${page.exceptions.join('; ')}`)

  console.log(JSON.stringify({
    ok: true,
    idleMs: IDLE_MS,
    pollsDuringIdle: idleStats.polls,
    settingWritesDuringIdle: idleStats.settingWrites,
    profileWritesDuringIdle: idleStats.profileWrites,
    writesAfterFlap: afterFlap,
    writesAfterGameStart: afterGameStart.writes,
    writesAfterGameStop: afterGameStop,
  }))
} finally {
  try { page?.ws.close() } catch {}
  try { browser.proc.kill() } catch {}
  try { vite.proc.kill() } catch {}
  try { rmSync(browser.dir, { recursive: true, force: true }) } catch {}
}

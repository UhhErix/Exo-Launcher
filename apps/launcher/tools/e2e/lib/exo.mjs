// The installed Exo: start it with a loopback-only debug port (in its own environment only), close it politely,
// read its log, call its host, and keep the files a run may touch so they go back byte for byte.
import { spawn, execFile } from 'node:child_process'
import { copyFile, mkdir, readFile, stat, writeFile, open } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { join, basename } from 'node:path'
import { fileURLToPath } from 'node:url'
import { openShell, openPage, sleep } from '../cdp.mjs'

export const DATA = join(process.env.LOCALAPPDATA, 'ExoLauncher')
export const EXE = join(DATA, 'app', 'ExoLauncher.exe')
export const LOG = join(DATA, 'logs', 'app.log')

/** What a run may change: put back as it was at the end. */
export const GUARDED = ['settings.json', 'web-client-preferences.json', 'window-placement.json'].map(f => join(DATA, f))

/**
 * EXO_E2E_TEST_INSTANCE=1: the run starts a disposable test instance (tools/test-instance.ps1) instead of the installed
 * Exo, and touches none of the installed one: it is not closed, and its settings files are neither saved nor restored.
 */
export const TEST_INSTANCE = process.env.EXO_E2E_TEST_INSTANCE === '1'
const TEST_SCRIPT = fileURLToPath(new URL('../../test-instance.ps1', import.meta.url))
const WIN_SCRIPT = fileURLToPath(new URL('./win.ps1', import.meta.url))
const TEST_EXE = fileURLToPath(new URL('../../ExoLauncher/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/ExoLauncher.exe', import.meta.url))
const TEST_FOLDER = process.env.EXO_E2E_TEST_FOLDER || join(process.env.TEMP, 'exo-e2e-test-instance')
let test = null // { pid, port, dataDir } of the test instance this run started
const q = (s) => String(s).replace(/'/g, "''")

const pwsh = (script) => new Promise((resolve, reject) =>
  execFile('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, timeout: 90000 }, (e, out, err) => e ? reject(new Error((err || e.message).trim())) : resolve(out.trim())))

export async function version() {
  return pwsh(`(Get-Item '${q(TEST_INSTANCE ? TEST_EXE : EXE)}').VersionInfo.ProductVersion`)
}

export async function pids() {
  if (TEST_INSTANCE) return test ? [test.pid] : []
  const out = await pwsh(`@(Get-Process ExoLauncher -ErrorAction SilentlyContinue | Where-Object Path -eq '${EXE}' | ForEach-Object Id) -join ','`)
  return out ? out.split(',').map(Number) : []
}

/** Closes Exo the way its window's close does (never a kill), waiting for it to finish. */
export async function close(timeoutSec = 40) {
  if (TEST_INSTANCE) {
    // The script closes that PID only (CloseMainWindow, then waits up to 40 s); the installed Exo is never matched.
    if (!test) return
    await pwsh(`& '${q(TEST_SCRIPT)}' -Stop ${test.pid}`)
    test = null
    await sleep(800)
    return
  }
  await new Promise((resolve, reject) =>
    execFile('pwsh', ['-NoProfile', '-NonInteractive', '-File', WIN_SCRIPT, '-Command', 'close', '-W', String(timeoutSec)], { windowsHide: true, timeout: Math.max(90000, timeoutSec * 1000 + 15000) }, (e, out, err) => e ? reject(new Error((err || e.message).trim())) : resolve(out.trim())))
  await sleep(800)
}

/**
 * Starts Exo, with the shell's debug port when `port` is given and any extra environment (EXO_PAGE_SCREEN), and
 * waits for its page. Returns the page session and when it started.
 */
export async function launch({ port = null, env = {}, waitPage = true } = {}) {
  if (TEST_INSTANCE) {
    // The script picks its own debug port (returned), and the extra environment goes to the test instance.
    const startedAt = Date.now()
    const extra = Object.entries(env).map(([k, v]) => `'${q(k)}' = '${q(v)}'`).join('; ')
    const out = await pwsh(`& '${q(TEST_SCRIPT)}' -Folder '${q(TEST_FOLDER)}' -Fresh -Env @{ ${extra} }`)
    test = JSON.parse(out.split(/\r?\n/).pop())
    if (!waitPage) return { startedAt, shell: null }
    return { startedAt, shell: await openShell(test.port, 90000) }
  }
  const childEnv = { ...process.env, ...env }
  if (port) childEnv.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = `--remote-debugging-port=${port}`
  else delete childEnv.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
  const startedAt = Date.now()
  const child = spawn(EXE, [], { cwd: join(DATA, 'app'), env: childEnv, detached: true, stdio: 'ignore', windowsHide: false })
  child.unref()
  if (!port || !waitPage) return { startedAt, shell: null }
  const shell = await openShell(port, 90000)
  return { startedAt, shell }
}

/**
 * A read-only look at an Exo someone is using: the page with no launch guard. attach and openShell put that guard in the
 * page for good, so every Play stops working in the owner's window until Exo restarts: never use them on a live Exo.
 */
export async function look(port) {
  try { return await openPage(port, 'app.exo-launcher.local', 5000) } catch { return null }
}

/** For a run only (see look): the page if Exo already runs with this debug port, else null. */
export async function attach(port) {
  try {
    const list = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(1500) })).json()
    return list.some(t => t.type === 'page' && t.url.includes('app.exo-launcher.local')) ? await openShell(port, 5000) : null
  } catch { return null }
}

/** The log's length now: pass it to logSince to read only what a step wrote. */
export async function logMark() {
  try { return (await stat(LOG)).size } catch { return 0 }
}

/** app.log lines written since `mark`, as { at, level, text }. */
export async function logSince(mark) {
  try {
    const handle = await open(LOG, 'r')
    const size = (await handle.stat()).size
    const buffer = Buffer.alloc(Math.max(0, size - mark))
    await handle.read(buffer, 0, buffer.length, mark)
    await handle.close()
    return buffer.toString('utf8').split(/\r?\n/).filter(Boolean).map(line => {
      const m = /^\[([^\]]+)\]\s+(\w+)\s+(.*)$/.exec(line)
      return m ? { at: m[1], level: m[2], text: m[3] } : { at: '', level: 'INFO', text: line }
    })
  } catch { return [] }
}

/** Calls the host as the page does (chrome.webview message, the page's own protocol). */
export function host(shell, method, params = {}, timeoutMs = 30000) {
  return shell.eval(`new Promise((resolve, reject) => {
    const wv = window.chrome.webview, id = crypto.randomUUID()
    const timer = setTimeout(() => { wv.removeEventListener('message', on); reject(new Error('host did not answer ${method}')) }, ${timeoutMs})
    function on(e) { let d = e.data; try { if (typeof d === 'string') d = JSON.parse(d) } catch { return } if (!d || d.id !== id) return
      clearTimeout(timer); wv.removeEventListener('message', on); d.ok ? resolve(d.result) : reject(new Error(d.error || 'host error')) }
    wv.addEventListener('message', on)
    wv.postMessage(JSON.stringify({ version: 1, id, method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} }))
  })`)
}

export class Guard {
  constructor(dir) { this.dir = dir; this.saved = [] }
  async save() {
    if (TEST_INSTANCE) return // the installed Exo's files are not the run's to keep or put back
    await mkdir(this.dir, { recursive: true })
    for (const file of GUARDED) {
      if (!existsSync(file)) continue
      const copy = join(this.dir, basename(file))
      await copyFile(file, copy)
      this.saved.push({ file, copy })
    }
  }
  /** Files that differ from what was saved (call with Exo closed, or it may write again). */
  async changed() {
    const out = []
    for (const { file, copy } of this.saved) {
      const [a, b] = await Promise.all([readFile(file).catch(() => null), readFile(copy)])
      if (!a || !a.equals(b)) out.push(file)
    }
    return out
  }
  async restore() {
    const changed = await this.changed()
    for (const { file, copy } of this.saved) if (changed.includes(file)) await writeFile(file, await readFile(copy))
    return changed
  }
}

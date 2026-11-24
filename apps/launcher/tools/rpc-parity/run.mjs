import { execFile, spawn } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { openShell, sleep } from '../e2e/cdp.mjs'
import { host as callHost } from '../e2e/lib/exo.mjs'
import { kit } from '../e2e/lib/kit.mjs'
import { parseTrace, sliceTraceByWindow } from './lib.mjs'
import { generateDiffReport } from './diff.mjs'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const APP_ROOT = path.resolve(HERE, '../..')
const TEST_SCRIPT = path.join(APP_ROOT, 'tools', 'test-instance.ps1')
const UIA_SCRIPT = path.join(HERE, 'uia.ps1')
const INSTALLED_EXE = path.resolve(process.env.LOCALAPPDATA || '', 'ExoLauncher', 'app', 'ExoLauncher.exe')
const DEFAULT_HOST_EXE = path.join(APP_ROOT, 'ExoLauncher', 'bin', 'x64', 'Debug', 'net10.0-windows10.0.26100.0', 'win-x64', 'ExoLauncher.exe')
const DEFAULT_UI_EXE_RELEASE = path.join(APP_ROOT, 'native-ui', 'target', 'release', 'exo-ui.exe')
const DEFAULT_UI_EXE_DEBUG = path.join(APP_ROOT, 'native-ui', 'target', 'debug', 'exo-ui.exe')
const GAMES = ['VALORANT-Win64-Shipping', 'LeagueOfLegends', 'cs2', 'dota2', 'FortniteClient-Win64-Shipping']

const q = (s) => String(s).replace(/'/g, "''")

function pwsh(script, timeout = 90000) {
  return new Promise((resolve, reject) => {
    execFile('pwsh', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, timeout }, (err, out, stderr) => {
      if (err) reject(new Error((stderr || out || err.message).trim()))
      else resolve(out.trim())
    })
  })
}

async function uia(args, timeout = 30000) {
  let lastErr = null
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      return await new Promise((resolve, reject) => {
        execFile('pwsh', ['-NoProfile', '-NonInteractive', '-File', UIA_SCRIPT, ...args], { windowsHide: true, timeout }, (err, out, stderr) => {
          if (err) reject(new Error((stderr || out || err.message).trim()))
          else {
            const line = out.trim().split(/\r?\n/).pop() || '{}'
            try { resolve(JSON.parse(line)) } catch { reject(new Error(`Invalid UIA output: ${out}`)) }
          }
        })
      })
    } catch (err) {
      lastErr = err
      await sleep(400)
    }
  }
  throw lastErr
}

async function assertNoGameRunning() {
  const list = GAMES.map((g) => `'${g}'`).join(',')
  const found = await pwsh(`@(Get-Process -Name ${list} -ErrorAction SilentlyContinue | ForEach-Object Name) -join ','`)
  if (found) throw new Error(`Refusing to run while a game is running: ${found}`)
}

function seedCleanDataDir(dir) {
  fs.rmSync(dir, { recursive: true, force: true })
  fs.mkdirSync(dir, { recursive: true })
  fs.writeFileSync(path.join(dir, '.onboarded'), '1\n', 'utf8')
  fs.writeFileSync(path.join(dir, 'settings.json'), JSON.stringify({
    onboardingComplete: true,
    accountAsked: true,
    uiPrefs: { tourSeen: true },
  }, null, 2), 'utf8')
}

function resolveUiExe(explicit) {
  if (explicit) return path.resolve(explicit)
  if (fs.existsSync(DEFAULT_UI_EXE_RELEASE)) return DEFAULT_UI_EXE_RELEASE
  if (fs.existsSync(DEFAULT_UI_EXE_DEBUG)) return DEFAULT_UI_EXE_DEBUG
  throw new Error('exo-ui.exe not found. Build native-ui first (cargo build --release --bin exo-ui).')
}

async function navWebBar(shell, k, room) {
  await k.closeAll()
  const clicked = await shell.click(
    (name) => [...document.querySelectorAll('.exo-top-rooms button, nav[aria-label="Rooms"] button')]
      .find((b) => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === name),
    room,
  )
  if (!clicked) throw new Error(`no Bar tab for ${room}`)
  const ok = await shell.waitFor(
    (name) => {
      const cur = document.querySelector('.exo-top-rooms [aria-current="page"], nav[aria-label="Rooms"] [aria-current="page"]')
      return !!cur && (cur.getAttribute('aria-label') || cur.textContent || '').trim().replace(/\s*\d+$/, '') === name
    },
    { timeoutMs: 15000, args: [room] },
  )
  if (!ok) throw new Error(`${room} did not become the current room`)
  await k.settle()
}

async function runWebAction(shell, k, action) {
  const spec = action.web
  if (!spec) return { status: 'not_drivable', reason: 'no web action defined' }

  if (spec.kind === 'startup') return { status: 'ok' }

  if (spec.kind === 'nav') {
    await navWebBar(shell, k, spec.room)
    return { status: 'ok' }
  }

  if (spec.kind === 'game-page') {
    await navWebBar(shell, k, 'Library')
    const hasGridBtn = await shell.eval(`!!document.querySelector('button[aria-label="All games grid"]')`)
    if (hasGridBtn) {
      await shell.click(() => document.querySelector('button[aria-label="All games grid"]'))
      await shell.waitFor(() => !!document.querySelector('.exo-game-grid button[data-game-id]'), { timeoutMs: 5000 })
      await sleep(250)
    }
    const firstId = await shell.eval(`(() => {
      const btn = document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')
        || document.querySelector('button[data-game-id]')
      return btn ? btn.dataset.gameId : null
    })()`)
    if (!firstId) return { status: 'not_drivable', reason: 'no game tiles in Library' }
    await shell.click((id) => document.querySelector(`button[data-game-id="${CSS.escape(id)}"]`), firstId)
    const opened = await shell.waitFor(
      () => [...document.querySelectorAll('.exo-game-overlay-stage, .exo-game-title')].some((s) => s.checkVisibility()),
      { timeoutMs: 6000 },
    )
    if (!opened) return { status: 'not_drivable', reason: 'game sheet did not open' }
    await k.settle()
    return { status: 'ok' }
  }

  if (spec.kind === 'downloads') {
    await navWebBar(shell, k, 'Library')
    const hasGridBtn = await shell.eval(`!!document.querySelector('button[aria-label="All games grid"]')`)
    if (hasGridBtn) {
      await shell.click(() => document.querySelector('button[aria-label="All games grid"]'))
      await shell.waitFor(() => !!document.querySelector('button.exo-library-more'), { timeoutMs: 5000 })
      await sleep(250)
    }
    const moreOpened = await shell.click(
      () => document.querySelector('button.exo-library-more')
        || [...document.querySelectorAll('button.exo-home-icon-btn')].find((b) => b.getAttribute('aria-label') === 'More'),
    )
    if (!moreOpened) return { status: 'not_drivable', reason: 'Library More button not found' }
    await sleep(350)
    const itemClicked = await shell.click(
      () => [...document.querySelectorAll('[role^="menuitem"]')].find((b) => b.getBoundingClientRect().width > 0 && b.textContent.trim() === 'Downloads'),
    )
    if (!itemClicked) return { status: 'not_drivable', reason: 'Downloads menu item not found' }
    const opened = await shell.waitFor(() => !!document.querySelector('.exo-downloads-sheet')?.checkVisibility(), { timeoutMs: 4000 })
    if (!opened) return { status: 'not_drivable', reason: 'Downloads sheet did not open' }
    await k.settle()
    return { status: 'ok' }
  }

  if (spec.kind === 'palette') {
    await k.closeAll()
    await shell.key('k', { ctrl: true })
    const opened = await shell.waitFor(
      () => !!document.querySelector('[role="dialog"] input, .exo-palette, .exo-cmd-dialog')?.checkVisibility(),
      { timeoutMs: 4000 },
    )
    if (!opened) return { status: 'not_drivable', reason: 'Command palette did not open on Ctrl+K' }
    await k.settle()
    return { status: 'ok' }
  }

  if (spec.kind === 'settings') {
    const isOpen = await shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`)
    if (!isOpen) {
      await k.closeAll()
      await shell.click(() => document.querySelector('.exo-settings-btn'))
      const ok = await shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 5000 })
      if (!ok) return { status: 'not_drivable', reason: 'Settings panel did not open' }
      await sleep(300)
    }
    const clicked = await shell.click(
      (name) => [...document.querySelectorAll('.exo-settings-nav button')].find((b) => b.textContent.trim() === name),
      spec.section,
    )
    if (!clicked) return { status: 'not_drivable', reason: `Settings page '${spec.section}' not found` }
    await k.settle()
    return { status: 'ok' }
  }

  if (spec.kind === 'discord-dm') {
    await navWebBar(shell, k, 'Discord')
    const hasClient = await shell.waitFor(() => !!document.querySelector('main.exo-dc'), { timeoutMs: 5000 })
    if (!hasClient) return { status: 'not_drivable', reason: 'Discord not signed in in disposable test instance' }
    const clicked = await shell.click(
      () => document.querySelector('.exo-dc-dms .exo-dc-item.is-dm, .exo-dc-dms .exo-dc-dm-row button'),
    )
    if (!clicked) return { status: 'not_drivable', reason: 'No Discord DM rows visible (signed out or empty DM list)' }
    await k.settle()
    return { status: 'ok' }
  }

  return { status: 'not_drivable', reason: `unsupported web action kind '${spec.kind}'` }
}

async function runWebCapture({ actions, hostExe, tempRoot, outDir }) {
  const dataDir = path.join(tempRoot, 'web-data')
  const rawTrace = path.join(tempRoot, 'web-raw.jsonl')
  seedCleanDataDir(dataDir)
  fs.rmSync(rawTrace, { force: true })

  const launchMs = Date.now()
  const out = await pwsh(`& '${q(TEST_SCRIPT)}' -Folder '${q(tempRoot)}' -DataDir '${q(dataDir)}' -Exe '${q(hostExe)}' -Env @{ 'EXO_UI' = 'web'; 'EXO_RPC_TRACE' = '${q(rawTrace)}'; 'EXO_E2E_TEST_INSTANCE' = '1' }`)
  const info = JSON.parse(out.split(/\r?\n/).pop())
  const windows = {}
  const statuses = {}
  let shell = null

  try {
    shell = await openShell(info.port, 90000)
    const k = kit({ shell, console: [] })
    await shell.waitFor(() => !!document.querySelector('.exo-top-rooms button, nav[aria-label="Rooms"] button'), { timeoutMs: 30000 })
    await shell.waitFor(() => !!document.querySelector('.exo-deck-card-btn, .exo-game-grid button[data-game-id], .exo-library-empty'), { timeoutMs: 20000 })
    await k.warmedUp()
    await sleep(1500)
    const startupEnd = Date.now()

    for (const action of actions) {
      if (action.id === 'startup') {
        windows[action.id] = { startMs: launchMs, endMs: startupEnd }
        statuses[action.id] = { status: 'ok' }
        continue
      }
      await callHost(shell, 'debug.mark', { phase: 'start' }).catch(() => {})
      const startMs = Date.now()
      let result
      try {
        result = await runWebAction(shell, k, action)
      } catch (err) {
        result = { status: 'not_drivable', reason: err.message }
      }
      await sleep(action.settleMs ?? 1000)
      await callHost(shell, 'debug.mark', { phase: 'end' }).catch(() => {})
      const endMs = Date.now()
      windows[action.id] = { startMs, endMs }
      statuses[action.id] = result
      if (action.id === 'game-page' || action.id === 'downloads' || action.id === 'palette') {
        await k.closeAll()
        await sleep(300)
      }
    }
  } finally {
    if (shell) shell.close()
    await pwsh(`& '${q(TEST_SCRIPT)}' -Stop ${info.pid} -Exe '${q(hostExe)}'`).catch(() => {})
    await sleep(800)
  }

  const allEntries = parseTrace(fs.existsSync(rawTrace) ? fs.readFileSync(rawTrace, 'utf8') : '')
  const webOut = path.join(outDir, 'web')
  fs.mkdirSync(webOut, { recursive: true })
  for (const action of actions) {
    const w = windows[action.id]
    const sliced = w ? sliceTraceByWindow(allEntries, w.startMs, w.endMs, 'web') : []
    fs.writeFileSync(path.join(webOut, `${action.id}.jsonl`), sliced.map((e) => JSON.stringify(e)).join('\n') + (sliced.length ? '\n' : ''), 'utf8')
  }
  return statuses
}

async function waitForChildUiPid(hostPid, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const out = await pwsh(`@(Get-CimInstance Win32_Process -Filter "ParentProcessId = ${hostPid} AND Name = 'exo-ui.exe'" | ForEach-Object ProcessId) -join ','`).catch(() => '')
    const pid = Number(out.split(',')[0])
    if (pid > 0) return pid
    await sleep(250)
  }
  throw new Error(`exo-ui.exe child of host PID ${hostPid} did not start within ${timeoutMs} ms`)
}

async function runNativeAction(uiPid, action, state) {
  const spec = action.native
  if (!spec) return { status: 'not_drivable', reason: 'no native action defined' }

  if (spec.kind === 'startup') return { status: 'ok' }

  if (state.settingsOpen && spec.kind !== 'settings') {
    await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Back', '-ControlType', 'Button']).catch(() => {})
    state.settingsOpen = false
    await sleep(350)
  }

  if (spec.kind === 'uia-invoke') {
    const args = ['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', spec.name]
    if (spec.controlType) args.push('-ControlType', spec.controlType)
    const res = await uia(args)
    if (!res.ok) return { status: 'not_drivable', reason: res.why || `UIA invoke failed for ${spec.name}` }
    return { status: 'ok' }
  }

  if (spec.kind === 'game-page') {
    await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Library', '-ControlType', 'TabItem'])
    await sleep(500)
    const exclude = [
      'Library', 'Store', 'Discord', 'Music', 'Devices', 'Exo AI', 'Apps', 'Settings', 'Search',
      'Capture', 'Stop recording', 'Minimize', 'Maximize', 'Close', 'Restore', 'Play', 'Install',
      'More', 'Add', 'Refresh', 'Filter', 'Sort', 'All', 'Installed', 'Recent', 'Played', 'Size',
      'Name', 'View', 'Grid', 'List', 'Activate a key', 'Guided tour',
    ].join(',')
    const res = await uia(['-Command', 'invoke-first', '-TargetPid', String(uiPid), '-ControlType', 'Button', '-MinY', '140', '-ExcludeNames', exclude])
    if (!res.ok) return { status: 'not_drivable', reason: res.why || 'No game card button found in Library UIA tree' }
    state.overlayOpen = true
    return { status: 'ok' }
  }

  if (spec.kind === 'downloads') {
    await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Library', '-ControlType', 'TabItem'])
    await sleep(400)
    const moreRes = await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'More', '-ControlType', 'Button'])
    if (!moreRes.ok) return { status: 'not_drivable', reason: moreRes.why || 'Library More button not found in UIA tree' }
    await sleep(350)
    let res = await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Downloads', '-ControlType', 'MenuItem'])
    if (!res.ok) {
      res = await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Downloads', '-ControlType', 'Button'])
    }
    if (!res.ok) return { status: 'not_drivable', reason: res.why || 'Downloads menu item not found in UIA tree' }
    state.overlayOpen = true
    return { status: 'ok' }
  }

  if (spec.kind === 'palette') {
    const res = await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Search', '-ControlType', 'Button'])
    if (!res.ok) {
      const keyRes = await uia(['-Command', 'key', '-TargetPid', String(uiPid), '-Key', 'CtrlK'])
      if (!keyRes.ok) return { status: 'not_drivable', reason: res.why || keyRes.why || 'Could not open palette' }
    }
    state.overlayOpen = true
    return { status: 'ok' }
  }

  if (spec.kind === 'settings') {
    if (!state.settingsOpen) {
      const openRes = await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Settings', '-ControlType', 'Button'])
      if (!openRes.ok) return { status: 'not_drivable', reason: openRes.why || 'Settings button not found in UIA tree' }
      state.settingsOpen = true
      await sleep(500)
    }
    const secRes = await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', spec.section, '-ControlType', 'TabItem'])
    if (!secRes.ok) return { status: 'not_drivable', reason: secRes.why || `Settings tab '${spec.section}' not found` }
    return { status: 'ok' }
  }

  if (spec.kind === 'discord-dm') {
    await uia(['-Command', 'invoke', '-TargetPid', String(uiPid), '-Name', 'Discord', '-ControlType', 'TabItem'])
    await sleep(600)
    const tree = await uia(['-Command', 'tree', '-TargetPid', String(uiPid)])
    const names = (tree.items || []).map((i) => i.name || '')
    if (names.some((n) => /sign in|log in/i.test(n))) {
      return { status: 'not_drivable', reason: 'Discord not signed in in disposable test instance' }
    }
    const exclude = [
      'Library', 'Store', 'Discord', 'Music', 'Devices', 'Exo AI', 'Apps', 'Settings', 'Search',
      'Capture', 'Home', 'Shop', 'Online', 'All', 'Pending', 'Blocked', 'Add Friend', 'Friends',
      'Mute', 'Unmute', 'Deafen', 'Undeafen', 'Discord settings', 'Minimize', 'Maximize', 'Close',
    ].join(',')
    const res = await uia(['-Command', 'invoke-first', '-TargetPid', String(uiPid), '-ControlType', 'Button', '-MinY', '140', '-ExcludeNames', exclude])
    if (!res.ok) return { status: 'not_drivable', reason: 'No Discord DM button found in UIA tree (signed out or empty DM list)' }
    return { status: 'ok' }
  }

  return { status: 'not_drivable', reason: `unsupported native action kind '${spec.kind}'` }
}

async function runNativeCapture({ actions, hostExe, uiExe, tempRoot, outDir }) {
  const dataDir = path.join(tempRoot, 'native-data')
  const rawTrace = path.join(tempRoot, 'native-raw.jsonl')
  seedCleanDataDir(dataDir)
  fs.rmSync(rawTrace, { force: true })

  const childEnv = {
    ...process.env,
    EXO_LAUNCHER_DATA_DIR: dataDir,
    EXO_RPC_TRACE: rawTrace,
    EXO_UI: 'native',
    EXO_RUNE_UI_PATH: uiExe,
    EXO_NO_ACTIVATE: '1',
  }

  const launchMs = Date.now()
  const hostProc = spawn(hostExe, ['--ui=native', '--rune-ui-path', uiExe], {
    cwd: path.dirname(hostExe),
    env: childEnv,
    stdio: 'ignore',
    windowsHide: true,
  })

  const windows = {}
  const statuses = {}
  let uiPid = null

  try {
    uiPid = await waitForChildUiPid(hostProc.pid, 30000)
    const winDeadline = Date.now() + 20000
    let winReady = false
    while (Date.now() < winDeadline) {
      const w = await uia(['-Command', 'window', '-TargetPid', String(uiPid)]).catch(() => ({ ok: false }))
      if (w.ok) { winReady = true; break }
      await sleep(250)
    }
    if (!winReady) throw new Error(`exo-ui window for PID ${uiPid} did not appear`)

    await uia(['-Command', 'place-second', '-TargetPid', String(uiPid)]).catch(() => {})
    await sleep(3000)
    const startupEnd = Date.now()

    const state = { settingsOpen: false, overlayOpen: false }
    for (const action of actions) {
      if (action.id === 'startup') {
        windows[action.id] = { startMs: launchMs, endMs: startupEnd }
        statuses[action.id] = { status: 'ok' }
        continue
      }
      if (state.overlayOpen) {
        await uia(['-Command', 'key', '-TargetPid', String(uiPid), '-Key', 'Escape']).catch(() => {})
        state.overlayOpen = false
        await sleep(350)
      }
      const startMs = Date.now()
      let result
      try {
        result = await runNativeAction(uiPid, action, state)
      } catch (err) {
        result = { status: 'not_drivable', reason: err.message }
      }
      await sleep(action.settleMs ?? 1000)
      const endMs = Date.now()
      windows[action.id] = { startMs, endMs }
      statuses[action.id] = result
    }
  } finally {
    if (uiPid) {
      await uia(['-Command', 'close', '-TargetPid', String(uiPid), '-WaitSec', '20']).catch(() => {})
    }
    await sleep(1000)
    if (hostProc.exitCode === null) {
      await pwsh(`$p = Get-Process -Id ${hostProc.pid} -ErrorAction SilentlyContinue; if ($p) { $p.CloseMainWindow() | Out-Null; $p.WaitForExit(10000) | Out-Null }`).catch(() => {})
    }
  }

  const allEntries = parseTrace(fs.existsSync(rawTrace) ? fs.readFileSync(rawTrace, 'utf8') : '')
  const nativeOut = path.join(outDir, 'native')
  fs.mkdirSync(nativeOut, { recursive: true })
  for (const action of actions) {
    const w = windows[action.id]
    const sliced = w ? sliceTraceByWindow(allEntries, w.startMs, w.endMs, 'native') : []
    fs.writeFileSync(path.join(nativeOut, `${action.id}.jsonl`), sliced.map((e) => JSON.stringify(e)).join('\n') + (sliced.length ? '\n' : ''), 'utf8')
  }
  return statuses
}

async function main() {
  const args = process.argv.slice(2)
  const onlyArg = args.find((a) => a.startsWith('--only='))?.slice(7)
  const outArg = args.find((a) => a.startsWith('--out='))?.slice(6)
  const exeArg = args.find((a) => a.startsWith('--exe='))?.slice(6)
  const uiExeArg = args.find((a) => a.startsWith('--ui-exe='))?.slice(9)
  const webOnly = args.includes('--web-only')
  const nativeOnly = args.includes('--native-only')

  await assertNoGameRunning()

  const hostExe = path.resolve(exeArg || DEFAULT_HOST_EXE)
  if (hostExe.toLowerCase() === INSTALLED_EXE.toLowerCase()) {
    throw new Error('Refusing to run against the installed ExoLauncher.exe')
  }
  if (!fs.existsSync(hostExe)) {
    throw new Error(`Host exe not found at ${hostExe}. Build Debug ExoLauncher first.`)
  }
  const uiExe = webOnly ? null : resolveUiExe(uiExeArg)

  const allActions = JSON.parse(fs.readFileSync(path.join(HERE, 'actions.json'), 'utf8'))
  const selectedIds = onlyArg ? new Set(onlyArg.split(',').map((s) => s.trim()).filter(Boolean)) : null
  const actions = selectedIds ? allActions.filter((a) => selectedIds.has(a.id)) : allActions

  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19)
  const outDir = path.resolve(outArg || path.join(HERE, 'out', stamp))
  const tempRoot = path.join(process.env.TEMP || os.tmpdir(), `exo-parity-${stamp}`)
  fs.mkdirSync(outDir, { recursive: true })
  fs.mkdirSync(tempRoot, { recursive: true })

  const manifest = { generatedAt: new Date().toISOString(), hostExe, uiExe, web: {}, native: {} }

  if (!nativeOnly) {
    console.log(`Recording web trace (${actions.length} actions)...`)
    manifest.web = await runWebCapture({ actions, hostExe, tempRoot, outDir })
  }
  if (!webOnly) {
    console.log(`Recording native trace (${actions.length} actions)...`)
    manifest.native = await runNativeCapture({ actions, hostExe, uiExe, tempRoot, outDir })
  }

  fs.writeFileSync(path.join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8')
  const { diff, outMd, outJson } = generateDiffReport({ captureDir: outDir })
  console.log(`Done. Report: ${outMd} (${outJson})`)
  console.log(`Unique methods: web=${diff.totals.uniqueWebMethods}, native=${diff.totals.uniqueNativeMethods}, reactOnly=${diff.totals.sessionReactOnlyMethods}, nativeOnly=${diff.totals.sessionNativeOnlyMethods}, chatty=${diff.totals.sessionChattyMethods}`)
}

main().catch((err) => {
  console.error(err.stack || err.message)
  process.exit(1)
})

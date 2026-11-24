import { execFile, spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { randomBytes } from 'node:crypto'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { sleep } from '../e2e/cdp.mjs'
import { assertSafeDataDir, seedInstanceData } from './react-driver.mjs'

const LAUNCHER_ROOT = fileURLToPath(new URL('../..', import.meta.url))
const INSTALLED_EXE = resolve(process.env.LOCALAPPDATA || '', 'ExoLauncher', 'app', 'ExoLauncher.exe')

export function resolveRepoHostExe(override = null) {
  const candidates = [
    override,
    join(LAUNCHER_ROOT, 'ExoLauncher', 'bin', 'x64', 'Debug', 'net10.0-windows10.0.26100.0', 'win-x64', 'ExoLauncher.exe'),
    join(LAUNCHER_ROOT, 'ExoLauncher', 'bin', 'x64', 'Release', 'net10.0-windows10.0.26100.0', 'win-x64', 'ExoLauncher.exe'),
  ].filter(Boolean)
  for (const p of candidates) {
    const full = resolve(p)
    if (full.toLowerCase() === INSTALLED_EXE.toLowerCase()) {
      throw new Error('Refused: cannot run installed ExoLauncher.exe.')
    }
    if (existsSync(full)) return full
  }
  throw new Error('No built repo ExoLauncher.exe found under ExoLauncher/bin/x64/{Debug,Release}.')
}

export function resolveExoUiExe(override = null) {
  const candidates = [
    override,
    join(LAUNCHER_ROOT, 'native-ui', 'target', 'debug', 'exo-ui.exe'),
    join(LAUNCHER_ROOT, 'native-ui', 'target', 'release', 'exo-ui.exe'),
  ].filter(Boolean)
  for (const p of candidates) {
    const full = resolve(p)
    if (existsSync(full)) return full
  }
  throw new Error('No built exo-ui.exe found under native-ui/target/{debug,release}.')
}

export function resolveProbeExe(override = null) {
  const candidates = [
    override,
    join(LAUNCHER_ROOT, 'native-ui', 'target', 'release', 'exo-ui.exe'),
    join(LAUNCHER_ROOT, 'native-ui', 'target', 'debug', 'exo-ui.exe'),
  ].filter(Boolean)
  for (const p of candidates) {
    const full = resolve(p)
    if (existsSync(full)) return full
  }
  throw new Error('No built exo-ui.exe found under native-ui/target/{release,debug}.')
}

function runExe(exe, args, timeoutMs = 45000) {
  return new Promise((resolvePromise) => {
    execFile(exe, args, { windowsHide: true, timeout: timeoutMs }, (err, stdout, stderr) => {
      resolvePromise({
        code: err ? (typeof err.code === 'number' ? err.code : 1) : 0,
        stdout: String(stdout || ''),
        stderr: String(stderr || ''),
        error: err ? String(err.message || err) : null,
      })
    })
  })
}

function extractPopupMenuItems(popupMenu) {
  if (!popupMenu || !Array.isArray(popupMenu.items)) return []
  const items = []
  const walk = (list) => {
    for (const entry of list) {
      if (!entry) continue
      if (typeof entry.title === 'string' && entry.title.trim()) {
        items.push(entry.title.trim())
      }
      if (Array.isArray(entry.items)) walk(entry.items)
    }
  }
  walk(popupMenu.items)
  return items
}

export function evaluateNativeOpened(expect, probe, tree, menuItems) {
  if (!expect) return Boolean(probe && probe.elementsCount > 0)
  const s = probe?.stateSummary || {}
  if (expect.room && s.room !== expect.room) return false
  if (typeof expect.libraryAllGames === 'boolean' && s.libraryAllGames !== expect.libraryAllGames) return false
  if (expect.libraryMoreOpen && !s.libraryMoreOpen) return false
  if (expect.librarySearchOpen && !s.librarySearchOpen) return false
  if (expect.openGame && !s.openGameId) return false
  if (typeof expect.gamePropsTab === 'number' && s.gamePropsTab !== expect.gamePropsTab) return false
  if (typeof expect.storeTab === 'number' && s.storeTab !== expect.storeTab) return false
  if (expect.storeGameOpen && !s.storeGameOpen) return false
  if (expect.paletteOpen && !s.paletteOpen) return false
  if (expect.captureMenuOpen && !s.captureMenuOpen) return false
  if (expect.capturesOpen && !s.capturesOpen) return false
  if (expect.capturesSettingsOpen && !s.capturesSettingsOpen) return false
  if (expect.keyModalOpen && !s.keyModalOpen) return false
  if (expect.downloadsOpen && !s.downloadsOpen) return false
  if (expect.whatsNewOpen && !s.whatsNewOpen) return false
  if (expect.updateSheetOpen && !s.updateSheetOpen) return false
  if (expect.islandVisible && !s.islandVisible) return false
  if (expect.driverInstallOpen && !s.driverInstallOpen) return false
  if (expect.tourOpen && (s.tourStep === null || s.tourStep === undefined)) return false
  if (expect.settingsSection && s.settingsSection !== expect.settingsSection) return false
  if (expect.pcTab && s.pcTab !== expect.pcTab) return false
  if (expect.appsSubTab && s.appsSubTab !== expect.appsSubTab) return false
  if (expect.selectedDevice && !s.selectedDeviceId) return false
  if (expect.musicNav && !String(s.musicNav || '').startsWith(expect.musicNav)) return false
  if (expect.musicPanel && s.musicPanel !== expect.musicPanel) return false
  if (expect.musicImmersion && !s.musicImmersion) return false
  if (expect.discordPlace && s.discordPlace !== expect.discordPlace) return false
  if (expect.discordStatusMenuOpen && !s.discordStatusMenuOpen) return false
  if (expect.discordVoiceActive && !s.discordVoiceChannel) return false
  if (expect.menuOpen) {
    const hasPopup = Boolean(probe?.popupMenuOpen || tree?.popupMenu)
    const hasMenuItems = Array.isArray(menuItems) && menuItems.length > 0
    if (!hasPopup && !hasMenuItems) return false
  }
  if (expect.hoveredAny && !(probe?.hoveredKeys?.length > 0)) return false
  if (expect.pressedAny && !(probe?.pressedKeys?.length > 0)) return false
  return true
}

function buildScriptEvents(state, reactRecord) {
  const raw = state.native?.script
  if (!raw?.length) return null
  const out = []
  for (const ev of raw) {
    const copy = { ...ev }
    if (copy.fromReactTarget) {
      delete copy.fromReactTarget
      const target = reactRecord?.lastTarget
      if (target && Number.isFinite(target.x) && Number.isFinite(target.y)) {
        copy.x = target.x
        copy.y = target.y
      } else if (copy.fallbackKey) {
        copy.key = copy.fallbackKey
      }
      delete copy.fallbackKey
    }
    if (copy.key || (Number.isFinite(copy.x) && Number.isFinite(copy.y)) || copy.type === 'key' || copy.type === 'type') {
      out.push(copy)
    }
  }
  return out.length ? out : null
}

function defaultProbePoints() {
  return [
    { name: 'bar:search', x: 90, y: 24 },
    { name: 'bar:nav:Library', x: 640, y: 24 },
    { name: 'bar:nav:Store', x: 720, y: 24 },
    { name: 'bar:nav:Discord', x: 800, y: 24 },
    { name: 'bar:nav:Music', x: 880, y: 24 },
    { name: 'bar:capture', x: 1740, y: 24 },
    { name: 'bar:settings', x: 1785, y: 24 },
  ]
}

export async function runNativePass({
  states,
  outDir,
  dataDir,
  reactResults = {},
  hostExe = null,
  exoUiExe = null,
  resume = false,
  seedDir = null,
}) {
  const safeDataDir = assertSafeDataDir(dataDir)
  await seedInstanceData(seedDir, safeDataDir)
  const resolvedHost = resolveRepoHostExe(hostExe)
  const resolvedUi = resolveExoUiExe(exoUiExe)
  const resolvedProbe = resolveProbeExe(exoUiExe)
  const nativeDir = join(outDir, 'native')
  await mkdir(nativeDir, { recursive: true })

  const pipe = `exo-parity-${randomBytes(4).toString('hex')}`
  const hostProc = spawn(resolvedHost, [`--host-pipe=${pipe}`], {
    cwd: dirname(resolvedHost),
    env: { ...process.env, EXO_LAUNCHER_DATA_DIR: safeDataDir, EXO_NO_ACTIVATE: '1' },
    stdio: 'ignore',
    windowsHide: true,
  })
  await sleep(3500)

  const results = {}
  try {
    for (const state of states) {
      const recordPath = join(nativeDir, `${state.id}.json`)
      if (resume && existsSync(recordPath)) {
        const cached = JSON.parse(await readFile(recordPath, 'utf8'))
        if (cached?.snapOk) {
          results[state.id] = cached
          continue
        }
      }
      const reactRec = reactResults[state.id] || null
      const scene = state.native?.scene || 'library'
      const pngPath = join(nativeDir, `${state.id}.png`)
      const treePath = join(nativeDir, `${state.id}.tree.json`)
      const probePath = join(nativeDir, `${state.id}.probe.json`)
      const pointsPath = join(nativeDir, `${state.id}.points.json`)
      const scriptPath = join(nativeDir, `${state.id}.script.json`)

      const scriptEvents = buildScriptEvents(state, reactRec)
      if (scriptEvents) {
        await writeFile(scriptPath, JSON.stringify(scriptEvents, null, 2))
      }

      const probePoints =
        reactRec?.cursors?.length > 0
          ? reactRec.cursors.map((c) => ({ name: c.name, x: c.x, y: c.y }))
          : defaultProbePoints()
      await writeFile(pointsPath, JSON.stringify(probePoints, null, 2))

      const baseArgs = ['--pipe', pipe, '--size', '1920x1080', '--scale', '1']
      if (scene && scene !== 'library') baseArgs.push('--scene', scene)
      if (scriptEvents) baseArgs.push('--script', scriptPath)

      const snapRun = await runExe(resolvedUi, [...baseArgs, '--snap', pngPath, '--tree', treePath], 45000)
      const probeRun = await runExe(
        resolvedProbe,
        [...baseArgs, '--parity-probe', probePath, '--probe-points', pointsPath],
        45000,
      )

      const tree = existsSync(treePath) ? JSON.parse(await readFile(treePath, 'utf8').catch(() => 'null')) : null
      const probe = existsSync(probePath) ? JSON.parse(await readFile(probePath, 'utf8').catch(() => 'null')) : null

      const popupItems = extractPopupMenuItems(tree?.popupMenu)
      const inTreeItems = Array.isArray(probe?.menuItems) ? probe.menuItems : []
      const menuItems = Array.from(new Set([...popupItems, ...inTreeItems]))

      const opened =
        existsSync(pngPath) &&
        Boolean(probe) &&
        evaluateNativeOpened(state.native?.expect, probe, tree, menuItems)

      const record = {
        id: state.id,
        room: state.room,
        scene,
        opened,
        snapOk: existsSync(pngPath),
        snapExit: snapRun.code,
        probeExit: probeRun.code,
        error: snapRun.code !== 0 ? (snapRun.stderr || snapRun.error || '').slice(0, 300) : null,
        screenshot: existsSync(pngPath) ? pngPath : null,
        glassAtFrame0: probe?.glassAtFrame0 || [],
        glassAtFrame10: probe?.glassAtFrame10 || [],
        glass: probe?.glassFinal || [],
        glassAppearedLate: Boolean(probe?.glassAppearedLate),
        popupMenuOpen: Boolean(probe?.popupMenuOpen || tree?.popupMenu),
        menuItems,
        hoveredKeys: probe?.hoveredKeys || [],
        pressedKeys: probe?.pressedKeys || [],
        cursors: probe?.cursors || [],
        stateSummary: probe?.stateSummary || null,
        elementsCount: probe?.elementsCount || 0,
      }
      results[state.id] = record
      await writeFile(join(nativeDir, `${state.id}.json`), JSON.stringify(record, null, 2))
      process.stdout.write(
        `[native] ${record.opened ? 'OK  ' : 'FAIL'} ${state.id} (glass=${record.glass.length}${record.glassAppearedLate ? ' LATE' : ''}, menu=${record.menuItems.length})\n`,
      )
    }
  } finally {
    if (hostProc && !hostProc.killed) {
      hostProc.kill()
    }
    await sleep(400)
  }
  return { pipe, results }
}

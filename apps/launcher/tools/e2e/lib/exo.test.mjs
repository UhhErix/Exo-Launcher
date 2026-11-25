import { before, test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { logMark, logPath, logSince, pickVersion, resolvePaths, version } from './exo.mjs'

const EXO_MODULE_URL = pathToFileURL(fileURLToPath(new URL('./exo.mjs', import.meta.url))).href
const LAUNCHER_ROOT = fileURLToPath(new URL('../../..', import.meta.url))
const TEST_INSTANCE_SCRIPT = fileURLToPath(new URL('../../test-instance.ps1', import.meta.url))

function loadModuleExports(extraEnv) {
  return JSON.parse(execFileSync(
    process.execPath,
    ['--input-type=module', '-e', `import * as exo from ${JSON.stringify(EXO_MODULE_URL)}; console.log(JSON.stringify({ TEST_INSTANCE: exo.TEST_INSTANCE, DATA: exo.DATA, LOG: exo.LOG, EXE: exo.EXE }))`],
    {
      env: {
        ...process.env,
        LOCALAPPDATA: 'C:\\Users\\Test\\AppData\\Local',
        TEMP: 'C:\\Temp',
        ...extraEnv,
      },
      encoding: 'utf8',
    },
  ).trim())
}

let testModeExports
let installedModeExports

before(() => {
  testModeExports = loadModuleExports({ EXO_E2E_TEST_INSTANCE: '1', EXO_E2E_TEST_FOLDER: 'C:\\Temp\\b7-run' })
  installedModeExports = loadModuleExports({ EXO_E2E_TEST_INSTANCE: '0', EXO_E2E_TEST_FOLDER: '' })
})

test('resolvePaths uses installed Exo paths when EXO_E2E_TEST_INSTANCE is not 1', () => {
  const env = { LOCALAPPDATA: 'C:\\Users\\Test\\AppData\\Local', TEMP: 'C:\\Temp' }
  const p = resolvePaths(env)
  assert.equal(p.testInstance, false)
  assert.equal(p.data, join(env.LOCALAPPDATA, 'ExoLauncher'))
  assert.equal(p.exe, join(env.LOCALAPPDATA, 'ExoLauncher', 'app', 'ExoLauncher.exe'))
  assert.equal(p.log, join(env.LOCALAPPDATA, 'ExoLauncher', 'logs', 'app.log'))
  assert.equal(p.guarded[0], join(env.LOCALAPPDATA, 'ExoLauncher', 'settings.json'))
})

test('resolvePaths uses test-instance folder and log when EXO_E2E_TEST_INSTANCE=1', () => {
  const env = {
    LOCALAPPDATA: 'C:\\Users\\Test\\AppData\\Local',
    TEMP: 'C:\\Temp',
    EXO_E2E_TEST_INSTANCE: '1',
    EXO_E2E_TEST_FOLDER: 'C:\\Custom\\exo-test',
  }
  const p = resolvePaths(env)
  assert.equal(p.testInstance, true)
  assert.equal(p.testFolderCustom, true)
  assert.equal(p.data, join(resolve('C:\\Custom\\exo-test'), 'instance'))
  assert.equal(p.log, join(resolve('C:\\Custom\\exo-test'), 'instance', 'logs', 'app.log'))
  assert.notEqual(p.log, join(env.LOCALAPPDATA, 'ExoLauncher', 'logs', 'app.log'))
  assert.ok(p.exe.startsWith(join(LAUNCHER_ROOT, 'ExoLauncher')), p.exe)
  assert.ok(!p.exe.includes(join('tools', 'ExoLauncher')), p.exe)
})

test('resolvePaths defaults test-instance folder under TEMP when EXO_E2E_TEST_FOLDER is unset', () => {
  const env = {
    LOCALAPPDATA: 'C:\\Users\\Test\\AppData\\Local',
    TEMP: 'C:\\Temp',
    EXO_E2E_TEST_INSTANCE: '1',
  }
  const p = resolvePaths(env)
  assert.equal(p.testInstance, true)
  assert.equal(p.testFolderCustom, false)
  assert.equal(p.data, join('C:\\Temp', 'exo-e2e-test-instance', 'instance'))
  assert.equal(p.log, join('C:\\Temp', 'exo-e2e-test-instance', 'instance', 'logs', 'app.log'))
})

test('module exports switch LOG and DATA between test-instance mode and installed mode', () => {
  assert.equal(testModeExports.TEST_INSTANCE, true)
  assert.equal(testModeExports.DATA, join(resolve('C:\\Temp\\b7-run'), 'instance'))
  assert.equal(testModeExports.LOG, join(resolve('C:\\Temp\\b7-run'), 'instance', 'logs', 'app.log'))
  assert.ok(testModeExports.EXE.startsWith(join(LAUNCHER_ROOT, 'ExoLauncher')), testModeExports.EXE)

  assert.equal(installedModeExports.TEST_INSTANCE, false)
  assert.equal(installedModeExports.DATA, join('C:\\Users\\Test\\AppData\\Local', 'ExoLauncher'))
  assert.equal(installedModeExports.LOG, join('C:\\Users\\Test\\AppData\\Local', 'ExoLauncher', 'logs', 'app.log'))
  assert.equal(installedModeExports.EXE, join('C:\\Users\\Test\\AppData\\Local', 'ExoLauncher', 'app', 'ExoLauncher.exe'))
})

test('logPath prefers active test dataDir over fallback log', () => {
  assert.equal(
    logPath('C:\\Temp\\run\\instance', 'C:\\Fallback\\app.log'),
    join('C:\\Temp\\run\\instance', 'logs', 'app.log'),
  )
  assert.equal(logPath(null, 'C:\\Fallback\\app.log'), 'C:\\Fallback\\app.log')
})

test('logMark and logSince read only the targeted log and recover after fresh truncation', async () => {
  const root = await mkdtemp(join(tmpdir(), 'exo-e2e-log-'))
  try {
    const installedLog = join(root, 'installed', 'logs', 'app.log')
    const testLog = join(root, 'test', 'instance', 'logs', 'app.log')
    await mkdir(dirname(installedLog), { recursive: true })
    await mkdir(dirname(testLog), { recursive: true })

    await writeFile(installedLog, '[2026-11-25T07:25:02Z] INFO Launch: riot:valorant started outside Exo\n', 'utf8')
    await writeFile(testLog, '[2026-11-25T07:24:17Z] INFO PERF startup milestone=window-constructed elapsedMs=423\n', 'utf8')

    const markBefore = await logMark(testLog)
    assert.ok(markBefore > 0)

    const initialLines = await logSince(0, testLog)
    assert.equal(initialLines.length, 1)
    assert.match(initialLines[0].text, /window-constructed/)
    assert.ok(!initialLines.some(l => l.text.includes('valorant')))

    await writeFile(
      testLog,
      '[2026-11-25T07:24:17Z] INFO PERF startup milestone=window-constructed elapsedMs=423\n[2026-11-25T07:24:22Z] INFO PERF startup milestone=webview-visible elapsedMs=5074\n',
      'utf8',
    )
    const appended = await logSince(markBefore, testLog)
    assert.equal(appended.length, 1)
    assert.match(appended[0].text, /webview-visible/)

    const largerMark = await logMark(testLog)
    await writeFile(testLog, '[2026-11-25T07:30:00Z] INFO PERF startup milestone=webview-visible elapsedMs=900\n', 'utf8')
    const afterFresh = await logSince(largerMark, testLog)
    assert.equal(afterFresh.length, 1)
    assert.match(afterFresh[0].text, /elapsedMs=900/)
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('pickVersion prefers ProductVersion and falls back to FileVersion or VERSION file', () => {
  assert.equal(pickVersion({ productVersion: '4.0.0', fileVersion: '1.0.0.0', versionFileText: '3.9.0' }), '4.0.0')
  assert.equal(pickVersion({ productVersion: '', fileVersion: '4.0.0.0', versionFileText: '3.9.0' }), '4.0.0.0')
  assert.equal(pickVersion({ productVersion: '', fileVersion: '1.0.0.0', versionFileText: '4.0.0\r\n' }), '4.0.0')
  assert.equal(pickVersion({ productVersion: '', fileVersion: '', versionFileText: '4.0.0' }), '4.0.0')
  assert.equal(pickVersion({ productVersion: '', fileVersion: '1.0.0.0', versionFileText: '' }), '1.0.0.0')
  assert.equal(pickVersion({ productVersion: '', fileVersion: '', versionFileText: '' }), 'unknown')
})

test('version falls back to repo VERSION file when exe ProductVersion is blank', async () => {
  const repoVersion = (await readFile(join(LAUNCHER_ROOT, 'VERSION'), 'utf8')).trim()
  assert.ok(repoVersion.length > 0)
  const resolvedVersion = await version({
    exe: join(LAUNCHER_ROOT, 'ExoLauncher', 'bin', 'missing', 'ExoLauncher.exe'),
    readExeVersion: async () => ({ productVersion: '', fileVersion: '' }),
  })
  assert.equal(resolvedVersion, repoVersion)
})

test('test-instance.ps1 forces EXO_UI=web alongside EXO_CDP=1', async () => {
  assert.ok(existsSync(TEST_INSTANCE_SCRIPT))
  const script = await readFile(TEST_INSTANCE_SCRIPT, 'utf8')
  assert.match(script, /\$env:EXO_UI\s*=\s*'web'/)
  assert.match(script, /\$env:EXO_CDP\s*=\s*'1'/)
})

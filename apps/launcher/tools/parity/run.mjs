import { existsSync } from 'node:fs'
import { mkdir, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { filterStates } from './states.mjs'
import { assertSafeDataDir, runReactPass } from './react-driver.mjs'
import { runNativePass } from './native-driver.mjs'
import { compareAll } from './compare.mjs'

const LAUNCHER_ROOT = fileURLToPath(new URL('../..', import.meta.url))

function timestamp() {
  const d = new Date()
  const p = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`
}

function parseArgs(argv) {
  const opts = {
    rooms: null,
    ids: null,
    skipReact: false,
    skipNative: false,
    resumeNative: false,
    reportDir: null,
    dataDir: null,
    keepData: false,
    hostExe: null,
    exoUiExe: null,
    seedData: null,
  }
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (a === '--rooms') opts.rooms = argv[++i]?.split(',').filter(Boolean) || null
    else if (a === '--states') opts.ids = argv[++i]?.split(',').filter(Boolean) || null
    else if (a === '--skip-react') opts.skipReact = true
    else if (a === '--skip-native') opts.skipNative = true
    else if (a === '--resume-native') opts.resumeNative = true
    else if (a === '--report-dir') opts.reportDir = argv[++i] || null
    else if (a === '--data-dir') opts.dataDir = argv[++i] || null
    else if (a === '--keep-data') opts.keepData = true
    else if (a === '--host-exe') opts.hostExe = argv[++i] || null
    else if (a === '--exo-ui') opts.exoUiExe = argv[++i] || null
    else if (a === '--seed-data') opts.seedData = argv[++i] || null
  }
  return opts
}

async function loadExistingRecords(dir, states) {
  const out = {}
  for (const state of states) {
    const p = join(dir, `${state.id}.json`)
    if (existsSync(p)) {
      out[state.id] = JSON.parse(await readFile(p, 'utf8'))
    }
  }
  return out
}

async function main() {
  process.env.EXO_E2E_TEST_INSTANCE = '1'
  const opts = parseArgs(process.argv.slice(2))
  const states = filterStates({ rooms: opts.rooms, ids: opts.ids })
  if (states.length === 0) {
    throw new Error('No parity states matched the given filters.')
  }

  const ts = timestamp()
  const outDir = resolve(opts.reportDir || join(LAUNCHER_ROOT, 'parity-report', ts))
  await mkdir(outDir, { recursive: true })

  const tempRoot = join(tmpdir(), `exo-parity-${ts}`)
  const dataDir = assertSafeDataDir(opts.dataDir || join(tempRoot, 'instance'))
  const folder = opts.dataDir ? resolve(dataDir, '..') : tempRoot
  await mkdir(dataDir, { recursive: true })

  let reactResults = {}
  let nativeResults = {}
  try {
    if (opts.skipReact) {
      reactResults = await loadExistingRecords(join(outDir, 'react'), states)
    } else {
      const r = await runReactPass({ states, outDir, folder, dataDir, hostExe: opts.hostExe, seedDir: opts.seedData })
      reactResults = r.results
    }

    if (opts.skipNative) {
      nativeResults = await loadExistingRecords(join(outDir, 'native'), states)
    } else {
      const n = await runNativePass({
        states,
        outDir,
        dataDir,
        reactResults,
        hostExe: opts.hostExe,
        exoUiExe: opts.exoUiExe,
        resume: opts.resumeNative,
        seedDir: opts.seedData,
      })
      nativeResults = n.results
    }

    const report = await compareAll({ states, reactResults, nativeResults, outDir })
    const c = report.summary.counts
    console.log('')
    console.log(`Parity run complete: ${c.total} states (CRITICAL=${c.CRITICAL}, HIGH=${c.HIGH}, MEDIUM=${c.MEDIUM}, LOW=${c.LOW}, PASS=${c.PASS}, SKIP=${c.SKIP})`)
    console.log(`Report:  ${join(outDir, 'report.md')}`)
    console.log(`Gallery: ${join(outDir, 'index.html')}`)
  } finally {
    if (!opts.keepData && !opts.dataDir && existsSync(tempRoot)) {
      await rm(tempRoot, { recursive: true, force: true }).catch(() => {})
    }
  }
}

await main()

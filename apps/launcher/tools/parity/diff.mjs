import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  diffParity,
  formatMarkdownReport,
  indexNativeHandlers,
  indexReactCallers,
  isTraceSafe,
  parseTrace,
} from './lib.mjs'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const APP_ROOT = path.resolve(HERE, '../..')

export function generateDiffReport({
  captureDir,
  actionsPath = path.join(HERE, 'actions.json'),
  outMd = path.join(APP_ROOT, 'improve', 'rpc-parity.md'),
  outJson = path.join(APP_ROOT, 'improve', 'rpc-parity.json'),
}) {
  const actions = JSON.parse(fs.readFileSync(actionsPath, 'utf8'))
  const manifestPath = path.join(captureDir, 'manifest.json')
  const manifest = fs.existsSync(manifestPath)
    ? JSON.parse(fs.readFileSync(manifestPath, 'utf8'))
    : { web: {}, native: {} }

  const webTraces = {}
  const nativeTraces = {}
  const safetyIssues = []

  for (const action of actions) {
    const webFile = path.join(captureDir, 'web', `${action.id}.jsonl`)
    const nativeFile = path.join(captureDir, 'native', `${action.id}.jsonl`)
    const webText = fs.existsSync(webFile) ? fs.readFileSync(webFile, 'utf8') : ''
    const nativeText = fs.existsSync(nativeFile) ? fs.readFileSync(nativeFile, 'utf8') : ''
    const ws = isTraceSafe(webText)
    if (!ws.safe) safetyIssues.push({ file: webFile, findings: ws.findings })
    const ns = isTraceSafe(nativeText)
    if (!ns.safe) safetyIssues.push({ file: nativeFile, findings: ns.findings })
    webTraces[action.id] = parseTrace(webText)
    nativeTraces[action.id] = parseTrace(nativeText)
  }

  if (safetyIssues.length) {
    throw new Error(`Unsafe value-like content detected in trace files: ${JSON.stringify(safetyIssues)}`)
  }

  const { callers, eventSubscribers } = indexReactCallers(path.join(APP_ROOT, 'ui', 'src'))
  const { handledEvents } = indexNativeHandlers(path.join(APP_ROOT, 'native-ui'))

  const diff = diffParity({
    actions,
    webTraces,
    nativeTraces,
    webStatus: manifest.web ?? {},
    nativeStatus: manifest.native ?? {},
    reactCallers: callers,
    reactEventSubscribers: eventSubscribers,
    nativeHandledEvents: handledEvents,
  })

  fs.mkdirSync(path.dirname(outMd), { recursive: true })
  fs.mkdirSync(path.dirname(outJson), { recursive: true })
  const md = formatMarkdownReport(diff, { captureDir: path.relative(APP_ROOT, captureDir).replaceAll('\\', '/') })
  fs.writeFileSync(outMd, md, 'utf8')
  fs.writeFileSync(outJson, JSON.stringify(diff, null, 2) + '\n', 'utf8')
  return { diff, outMd, outJson }
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  const args = process.argv.slice(2)
  const dirArg = args.find((a) => a.startsWith('--dir='))?.slice(6)
    ?? (args.includes('--dir') ? args[args.indexOf('--dir') + 1] : null)
  const outMd = args.find((a) => a.startsWith('--out-md='))?.slice(9)
    ?? path.join(APP_ROOT, 'improve', 'rpc-parity.md')
  const outJson = args.find((a) => a.startsWith('--out-json='))?.slice(11)
    ?? path.join(APP_ROOT, 'improve', 'rpc-parity.json')

  let captureDir = dirArg ? path.resolve(dirArg) : null
  if (!captureDir) {
    const outRoot = path.join(HERE, 'out')
    if (fs.existsSync(outRoot)) {
      const dirs = fs.readdirSync(outRoot).filter((d) => fs.statSync(path.join(outRoot, d)).isDirectory()).sort()
      if (dirs.length) captureDir = path.join(outRoot, dirs[dirs.length - 1])
    }
  }
  if (!captureDir || !fs.existsSync(captureDir)) {
    console.error('No capture directory found. Pass --dir <path> or run tools/parity/run.mjs first.')
    process.exit(1)
  }

  const { diff } = generateDiffReport({ captureDir, outMd, outJson })
  console.log(`Wrote ${outMd} and ${outJson}`)
  console.log(`Summary: web=${diff.totals.uniqueWebMethods} methods, native=${diff.totals.uniqueNativeMethods} methods, reactOnly=${diff.totals.sessionReactOnlyMethods}, nativeOnly=${diff.totals.sessionNativeOnlyMethods}, chatty=${diff.totals.sessionChattyMethods}, notDrivable=${diff.notDrivable.length}`)
}

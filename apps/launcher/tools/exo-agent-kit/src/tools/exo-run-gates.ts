import fs from 'node:fs'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  fitSectionsToByteBudget,
  foldHeadTail,
  stripAnsi,
  type BudgetSection,
} from '../utils/budget.js'
import { MCP_CALL_BUDGET_MS, resolveNpmInvocation, runCommand, type CommandResult } from '../utils/exec.js'
import { runReadOnlyGit } from '../utils/git.js'
import { parseMaybeJsonArray, resolveWorkspacePaths, toPosixPath } from '../utils/paths.js'

interface GateSpec {
  id: string
  group: string
  command: string
  args: string[]
  cwd: string
}

interface GateOutcome {
  spec: GateSpec
  res: CommandResult
  ok: boolean
  envTags: string[]
}

function detectEnvTags(output: string): string[] {
  const tags: string[] = []
  if (/build\s+22000|WindowsRequirement|Windows\s+Server\s+2022|20348/i.test(output)) {
    tags.push('env:windows-build-lt-22000')
  }
  if (/playwright.*chromium|Executable doesn't exist|browserType\.launch/i.test(output)) {
    tags.push('env:missing-playwright-chromium')
  }
  if (/ExoLauncher\.exe.*(being used by another process|cannot access)|MSB3021|MSB3027/i.test(output)) {
    tags.push('env:locked-exolauncher-exe')
  }
  if (/ReadyToRun|CrossGen2|win-x64[\\/]R2R|shell\.precompiled/i.test(output)) {
    tags.push('env:readytorun-incremental-pitfall')
  }
  return tags
}

function normalizeLauncherRel(p: string, repoRoot: string, launcherRoot: string): string {
  const abs = path.isAbsolute(p) ? path.resolve(p) : path.resolve(repoRoot, p)
  const relToLauncher = toPosixPath(path.relative(launcherRoot, abs))
  if (!relToLauncher.startsWith('..')) return relToLauncher
  const clean = toPosixPath(p).replace(/^\.\//, '')
  if (clean.startsWith('apps/launcher/')) return clean.slice('apps/launcher/'.length)
  return clean
}

function collectE2eTestFiles(launcherRoot: string): string[] {
  const dir = path.join(launcherRoot, 'tools', 'e2e', 'lib')
  if (!fs.existsSync(dir)) return []
  return fs
    .readdirSync(dir)
    .filter((f) => f.endsWith('.test.mjs'))
    .sort()
    .map((f) => path.join('tools', 'e2e', 'lib', f))
}

function buildGateRegistry(launcherRoot: string): Map<string, GateSpec> {
  const reg = new Map<string, GateSpec>()
  const nativeDir = path.join(launcherRoot, 'native-ui')
  const uiDir = path.join(launcherRoot, 'ui')
  const mouseDir = path.join(launcherRoot, 'mouse')
  const kitDir = path.join(launcherRoot, 'tools', 'exo-agent-kit')

  const npmCheck = resolveNpmInvocation(['run', 'check'])
  const npmDead = resolveNpmInvocation(['run', 'dead'])
  const npmBuild = resolveNpmInvocation(['run', 'build'])
  const npmTest = resolveNpmInvocation(['test'])

  reg.set('cargo-fmt', {
    id: 'cargo-fmt',
    group: 'native-ui',
    command: 'cargo',
    args: ['fmt', '--check'],
    cwd: nativeDir,
  })
  reg.set('cargo-clippy', {
    id: 'cargo-clippy',
    group: 'native-ui',
    command: 'cargo',
    args: ['clippy', '--workspace', '--locked'],
    cwd: nativeDir,
  })
  reg.set('cargo-test', {
    id: 'cargo-test',
    group: 'native-ui',
    command: 'cargo',
    args: ['test', '--workspace', '--locked'],
    cwd: nativeDir,
  })
  reg.set('ui-check', {
    id: 'ui-check',
    group: 'ui',
    command: npmCheck.command,
    args: npmCheck.args,
    cwd: uiDir,
  })
  reg.set('ui-dead', {
    id: 'ui-dead',
    group: 'ui',
    command: npmDead.command,
    args: npmDead.args,
    cwd: uiDir,
  })
  reg.set('mouse-check', {
    id: 'mouse-check',
    group: 'mouse',
    command: npmCheck.command,
    args: npmCheck.args,
    cwd: mouseDir,
  })
  reg.set('host-build', {
    id: 'host-build',
    group: 'host',
    command: 'dotnet',
    args: ['build', 'ExoLauncher/ExoLauncher.csproj', '-c', 'Release', '-p:Platform=x64'],
    cwd: launcherRoot,
  })
  reg.set('host-test', {
    id: 'host-test',
    group: 'host',
    command: 'dotnet',
    args: ['test', '--project', 'ExoLauncher.Tests', '-c', 'Release', '-p:Platform=x64'],
    cwd: launcherRoot,
  })

  const e2eFiles = collectE2eTestFiles(launcherRoot)
  if (e2eFiles.length > 0) {
    reg.set('e2e-lib-test', {
      id: 'e2e-lib-test',
      group: 'e2e',
      command: process.execPath,
      args: ['--test', ...e2eFiles],
      cwd: launcherRoot,
    })
  }

  reg.set('shaders-test', {
    id: 'shaders-test',
    group: 'shaders',
    command: 'dotnet',
    args: ['test', 'shaders/engine/tests/ExoShaders.Service.Tests'],
    cwd: launcherRoot,
  })

  if (fs.existsSync(path.join(launcherRoot, 'tools', 'parity', 'parity.test.mjs'))) {
    reg.set('parity-test', {
      id: 'parity-test',
      group: 'parity',
      command: process.execPath,
      args: ['--test', 'tools/parity/parity.test.mjs'],
      cwd: launcherRoot,
    })
  }

  reg.set('comments', {
    id: 'comments',
    group: 'comments',
    command: process.execPath,
    args: ['tools/check-comments.mjs'],
    cwd: launcherRoot,
  })

  if (fs.existsSync(path.join(kitDir, 'package.json'))) {
    reg.set('kit-build', {
      id: 'kit-build',
      group: 'kit',
      command: npmBuild.command,
      args: npmBuild.args,
      cwd: kitDir,
    })
    reg.set('kit-test', {
      id: 'kit-test',
      group: 'kit',
      command: npmTest.command,
      args: npmTest.args,
      cwd: kitDir,
    })
  }

  return reg
}

function mapPathsToGateIds(relPaths: string[], registry: Map<string, GateSpec>): string[] {
  const selected = new Set<string>()
  for (const rel of relPaths) {
    if (rel.startsWith('native-ui/')) {
      selected.add('cargo-fmt')
      selected.add('cargo-clippy')
      selected.add('cargo-test')
    } else if (rel.startsWith('ui/')) {
      selected.add('ui-check')
      selected.add('ui-dead')
    } else if (rel.startsWith('mouse/')) {
      selected.add('mouse-check')
    } else if (rel.startsWith('ExoLauncher/') || rel.startsWith('ExoLauncher.Tests/')) {
      selected.add('host-build')
      selected.add('host-test')
    } else if (rel.startsWith('tools/e2e/')) {
      selected.add('e2e-lib-test')
    } else if (rel.startsWith('tools/parity/')) {
      selected.add('parity-test')
    } else if (rel.startsWith('shaders/')) {
      selected.add('shaders-test')
    } else if (rel.startsWith('tools/exo-agent-kit/')) {
      selected.add('kit-build')
      selected.add('kit-test')
    }
  }
  return Array.from(selected).filter((id) => registry.has(id))
}

async function detectChangedPaths(repoRoot: string): Promise<string[]> {
  const res = await runReadOnlyGit(repoRoot, 'status', ['--porcelain=v1', '-uall'], 5000)
  if (res.exitCode !== 0) return []
  const files: string[] = []
  for (const line of res.stdout.split(/\r?\n/)) {
    if (line.length < 4) continue
    let entry = line.slice(3).trim()
    const arrowIdx = entry.indexOf(' -> ')
    if (arrowIdx >= 0) entry = entry.slice(arrowIdx + 4).trim()
    if (entry.startsWith('"') && entry.endsWith('"')) entry = entry.slice(1, -1)
    if (entry) files.push(entry)
  }
  return files
}

async function runGroupedPool(
  specs: GateSpec[],
  concurrency: number,
  deadlineMs: number,
  perGateTimeoutMs: number,
): Promise<GateOutcome[]> {
  const groups = new Map<string, GateSpec[]>()
  for (const spec of specs) {
    const list = groups.get(spec.group) ?? []
    list.push(spec)
    groups.set(spec.group, list)
  }

  const groupEntries = Array.from(groups.values())
  const outcomes: GateOutcome[] = []
  let cursor = 0

  const worker = async (): Promise<void> => {
    while (cursor < groupEntries.length) {
      const idx = cursor++
      const queue = groupEntries[idx]!
      for (const spec of queue) {
        const remaining = deadlineMs - Date.now()
        if (remaining <= 500) {
          outcomes.push({
            spec,
            ok: false,
            envTags: ['budget:timeout'],
            res: {
              command: spec.command,
              args: spec.args,
              cwd: spec.cwd,
              exitCode: null,
              signal: null,
              stdout: '',
              stderr: 'Skipped: shared call time budget exhausted.',
              timedOut: true,
              durationMs: 0,
            },
          })
          continue
        }
        const timeoutMs = Math.min(perGateTimeoutMs, remaining)
        const res = await runCommand(spec.command, spec.args, { cwd: spec.cwd, timeoutMs })
        const ok = res.exitCode === 0 && !res.timedOut
        const combined = stripAnsi(`${res.stdout}\n${res.stderr}`)
        const envTags = ok ? [] : detectEnvTags(combined)
        outcomes.push({ spec, res, ok, envTags })
        if (!ok && spec.id === 'host-build') break
      }
    }
  }

  const poolSize = Math.max(1, Math.min(concurrency, groupEntries.length))
  await Promise.all(Array.from({ length: poolSize }, () => worker()))

  const order = new Map(specs.map((s, i) => [s.id, i]))
  outcomes.sort((a, b) => (order.get(a.spec.id) ?? 0) - (order.get(b.spec.id) ?? 0))
  return outcomes
}

export async function runExoGates(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const ws = resolveWorkspacePaths(typeof args.cwd === 'string' ? args.cwd : undefined)
  if (!ws.launcherRoot) {
    return {
      text: `Not an Exo Launcher workspace (no apps/launcher/ui/contracts/host-v1.json found from ${ws.cwd}).`,
      isError: true,
    }
  }

  const registry = buildGateRegistry(ws.launcherRoot)
  const explicitGates = parseMaybeJsonArray<string>(args.gates)
  const explicitPaths = parseMaybeJsonArray<string>(args.paths)
  const dryRun = args.dryRun === true

  let selectedIds: string[] = []
  let unknownGates: string[] = []
  let sourceDesc = ''

  if (explicitGates && explicitGates.length > 0) {
    for (const id of explicitGates) {
      if (registry.has(id)) selectedIds.push(id)
      else unknownGates.push(id)
    }
    sourceDesc = `explicit gates (${explicitGates.join(', ')})`
  } else {
    const rawPaths =
      explicitPaths && explicitPaths.length > 0
        ? explicitPaths
        : await detectChangedPaths(ws.repoRoot)
    const relPaths = rawPaths.map((p) => normalizeLauncherRel(p, ws.repoRoot, ws.launcherRoot!))
    selectedIds = mapPathsToGateIds(relPaths, registry)
    sourceDesc =
      explicitPaths && explicitPaths.length > 0
        ? `${relPaths.length} explicit path(s)`
        : `${relPaths.length} git-changed path(s)`
  }

  if (unknownGates.length > 0) {
    return {
      text: `Unknown gate(s): ${unknownGates.join(', ')}. Available: ${Array.from(registry.keys()).join(', ')}`,
      isError: true,
    }
  }

  if (selectedIds.length === 0) {
    return {
      text: `exo_run_gates: 0 gates matched for ${sourceDesc}.\nAvailable gates: ${Array.from(registry.keys()).join(', ')}`,
    }
  }

  const specs = selectedIds.map((id) => registry.get(id)!)
  if (dryRun) {
    const lines = specs.map(
      (s) => `- ${s.id} [${s.group}]: ${s.command} ${s.args.join(' ')} (cwd: ${toPosixPath(path.relative(ws.repoRoot, s.cwd) || '.')})`,
    )
    return {
      text: `exo_run_gates dry-run (${specs.length} gate(s) from ${sourceDesc}):\n${lines.join('\n')}`,
    }
  }

  const concurrency =
    typeof args.concurrency === 'number' && Number.isFinite(args.concurrency)
      ? Math.max(1, Math.min(8, Math.floor(args.concurrency)))
      : 4
  const timeoutSec =
    typeof args.timeoutSec === 'number' && Number.isFinite(args.timeoutSec)
      ? Math.max(5, Math.min(165, Math.floor(args.timeoutSec)))
      : 150
  const deadlineMs = Date.now() + Math.min(timeoutSec * 1000, MCP_CALL_BUDGET_MS - 3000)

  const startedAt = Date.now()
  const outcomes = await runGroupedPool(specs, concurrency, deadlineMs, timeoutSec * 1000)
  const totalMs = Date.now() - startedAt
  const passed = outcomes.filter((o) => o.ok).length
  const failed = outcomes.length - passed

  const preamble = `exo_run_gates: ${passed}/${outcomes.length} PASS (${failed} FAIL) in ${totalMs}ms [${sourceDesc}]`
  const sections: BudgetSection[] = outcomes.map((o) => {
    const status = o.ok ? 'PASS' : o.res.timedOut ? 'TIMEOUT' : `FAIL(exit=${o.res.exitCode ?? 'err'})`
    const tagStr = o.envTags.length > 0 ? ` [${o.envTags.join(', ')}]` : ''
    const header = `[${o.spec.id}] ${status} ${o.res.durationMs}ms${tagStr}`
    if (o.ok) {
      return { header, body: '' }
    }
    const raw = stripAnsi([o.res.stdout.trim(), o.res.stderr.trim()].filter(Boolean).join('\n'))
    return {
      header,
      body: foldHeadTail(raw || '(no output)', Math.max(512, Math.floor(maxBytes / Math.max(1, failed)))),
    }
  })

  return {
    text: fitSectionsToByteBudget(sections, maxBytes, preamble),
    isError: failed > 0,
  }
}

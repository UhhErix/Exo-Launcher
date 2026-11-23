import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  foldHeadTail,
  stripAnsi,
  truncateToJsonBytes,
} from '../utils/budget.js'
import { MCP_CALL_BUDGET_MS, runCommand } from '../utils/exec.js'
import {
  parseMaybeJsonArray,
  resolveLocalAppData,
  resolveSafePath,
  resolveWorkspacePaths,
  toPosixPath,
} from '../utils/paths.js'

export const VALID_PARITY_SCENES = [
  'library',
  'library-all',
  'game',
  'store',
  'discord',
  'music',
  'pc-devices',
  'pc',
  'pc-apps',
  'settings',
  'captures',
  'palette',
] as const

const VALID_SCENE_SET = new Set<string>(VALID_PARITY_SCENES)

interface CompareResultJson {
  ref: string
  snap: string
  out: string
  sizeRef: [number, number]
  sizeSnap: [number, number]
  sizeMatch: boolean
  meanAbsDiff: number
  maxCellDiff: number
  worstCells: {
    row: number
    col: number
    x0: number
    x1: number
    y0: number
    y1: number
    score: number
  }[]
}

function isInsideDir(candidate: string, forbiddenDir: string): boolean {
  const normCand = path.resolve(candidate).toLowerCase().replace(/[\\\/]+$/, '')
  const normForb = path.resolve(forbiddenDir).toLowerCase().replace(/[\\\/]+$/, '')
  return normCand === normForb || normCand.startsWith(normForb + path.sep)
}

function resolveWorktreeHostExe(launcherRoot: string, explicitHostExe?: string): string | null {
  if (explicitHostExe && explicitHostExe.trim()) {
    return path.resolve(explicitHostExe.trim())
  }
  if (process.env.EXO_HOST_EXE && process.env.EXO_HOST_EXE.trim()) {
    return path.resolve(process.env.EXO_HOST_EXE.trim())
  }
  const candidates = [
    path.join(
      launcherRoot,
      'ExoLauncher',
      'bin',
      'x64',
      'Debug',
      'net10.0-windows10.0.26100.0',
      'win-x64',
      'ExoLauncher.exe',
    ),
    path.join(
      launcherRoot,
      'ExoLauncher',
      'bin',
      'x64',
      'Release',
      'net10.0-windows10.0.26100.0',
      'win-x64',
      'ExoLauncher.exe',
    ),
  ]
  for (const c of candidates) {
    if (fs.existsSync(c)) return c
  }
  return null
}

function formatCompareJson(res: CompareResultJson): string {
  const lines = [
    `compare -> ${toPosixPath(res.out)}`,
    `size: ref=${res.sizeRef[0]}x${res.sizeRef[1]} snap=${res.sizeSnap[0]}x${res.sizeSnap[1]} match=${res.sizeMatch}`,
    `meanAbsDiff=${res.meanAbsDiff} maxCellDiff=${res.maxCellDiff} (0 = identical)`,
    'worst 8x6 grid cells:',
  ]
  for (const c of res.worstCells) {
    lines.push(`  (${c.row},${c.col}) x=${c.x0}-${c.x1} y=${c.y0}-${c.y1}: ${c.score}`)
  }
  return lines.join('\n')
}

export async function runExoParitySnap(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const ws = resolveWorkspacePaths(
    typeof args.worktreeRoot === 'string'
      ? args.worktreeRoot
      : typeof args.cwd === 'string'
        ? args.cwd
        : undefined,
  )
  if (!ws.launcherRoot) {
    return {
      text: `Not an Exo Launcher workspace (apps/launcher not found from ${ws.cwd}).`,
      isError: true,
    }
  }

  const parityDir = path.join(ws.launcherRoot, 'tools', 'parity')
  const snapScript = path.join(parityDir, 'parity-snap.ps1')
  const compareScript = path.join(parityDir, 'parity-compare.py')

  if (!fs.existsSync(snapScript) || !fs.existsSync(compareScript)) {
    return {
      text: `In-repo parity harness not found at ${toPosixPath(parityDir)} (expected parity-snap.ps1 and parity-compare.py).`,
      isError: true,
    }
  }

  const compareArg =
    args.compare && typeof args.compare === 'object' && !Array.isArray(args.compare)
      ? (args.compare as Record<string, unknown>)
      : null

  if (compareArg) {
    const refPath = typeof compareArg.refPath === 'string' ? resolveSafePath(ws.cwd, compareArg.refPath) : ''
    const snapPath = typeof compareArg.snapPath === 'string' ? resolveSafePath(ws.cwd, compareArg.snapPath) : ''
    if (!refPath || !snapPath) {
      return { text: 'compare requires both refPath and snapPath.', isError: true }
    }
    if (!fs.existsSync(refPath)) return { text: `refPath not found: ${refPath}`, isError: true }
    if (!fs.existsSync(snapPath)) return { text: `snapPath not found: ${snapPath}`, isError: true }
    const outPath =
      typeof compareArg.outPath === 'string' && compareArg.outPath.trim()
        ? resolveSafePath(ws.cwd, compareArg.outPath)
        : path.join(os.tmpdir(), `exo-parity-compare-${crypto.randomBytes(4).toString('hex')}.png`)
    fs.mkdirSync(path.dirname(outPath), { recursive: true })

    const pyRes = await runCommand(
      'python',
      ['-I', compareScript, '--json', '-', refPath, snapPath, outPath],
      { cwd: parityDir, timeoutMs: 30_000 },
    )
    if (pyRes.exitCode !== 0) {
      return {
        text: truncateToJsonBytes(
          `parity-compare.py failed (exit ${pyRes.exitCode}):\n${stripAnsi(pyRes.stderr || pyRes.stdout).trim()}`,
          maxBytes,
        ),
        isError: true,
      }
    }
    const jsonLine = pyRes.stdout
      .trim()
      .split(/\r?\n/)
      .reverse()
      .find((l) => l.trim().startsWith('{'))
    if (jsonLine) {
      try {
        const parsed = JSON.parse(jsonLine) as CompareResultJson
        return { text: truncateToJsonBytes(formatCompareJson(parsed), maxBytes) }
      } catch {}
    }
    return { text: truncateToJsonBytes(stripAnsi(pyRes.stdout).trim(), maxBytes) }
  }

  const rawScenes = parseMaybeJsonArray<string>(args.scenes)
  const scenes = rawScenes && rawScenes.length > 0 ? rawScenes : ['library']
  const invalidScenes = scenes.filter((s) => !VALID_SCENE_SET.has(s))
  if (invalidScenes.length > 0) {
    return {
      text: `Invalid scene(s): ${invalidScenes.join(', ')}. Valid scenes: ${VALID_PARITY_SCENES.join(', ')}`,
      isError: true,
    }
  }

  const installedDataDir = path.join(resolveLocalAppData(), 'ExoLauncher')
  const installedExe = path.join(installedDataDir, 'app', 'ExoLauncher.exe')

  const hostExe = resolveWorktreeHostExe(
    ws.launcherRoot,
    typeof args.hostExe === 'string' ? args.hostExe : undefined,
  )
  if (hostExe && isInsideDir(hostExe, installedDataDir)) {
    return {
      text: `Refused: will not execute installed ExoLauncher.exe (${toPosixPath(installedExe)}). Build a worktree ExoLauncher.exe instead.`,
      isError: true,
    }
  }

  const dataDir =
    typeof args.dataDir === 'string' && args.dataDir.trim()
      ? resolveSafePath(ws.cwd, args.dataDir)
      : undefined
  if (dataDir && isInsideDir(dataDir, installedDataDir)) {
    return {
      text: 'Refused: dataDir cannot be inside %LOCALAPPDATA%\\ExoLauncher.',
      isError: true,
    }
  }

  const tag =
    typeof args.tag === 'string' && /^[A-Za-z0-9._-]+$/.test(args.tag.trim())
      ? args.tag.trim()
      : 'snap'
  const outDir =
    typeof args.outDir === 'string' && args.outDir.trim()
      ? resolveSafePath(ws.cwd, args.outDir)
      : path.join(os.tmpdir(), `exo-parity-out-${crypto.randomBytes(4).toString('hex')}`)
  const refsDir =
    typeof args.refsDir === 'string' && args.refsDir.trim()
      ? resolveSafePath(ws.cwd, args.refsDir)
      : process.env.EXO_PARITY_REFS && process.env.EXO_PARITY_REFS.trim()
        ? path.resolve(process.env.EXO_PARITY_REFS.trim())
        : undefined
  const noBuild = args.noBuild === true
  const dryRun = args.dryRun === true

  if (!hostExe || !fs.existsSync(hostExe)) {
    const msg =
      `No worktree ExoLauncher.exe found under apps/launcher/ExoLauncher/bin/x64/{Debug,Release} (and EXO_HOST_EXE unset). Build the worktree host first; installed %LOCALAPPDATA%\\ExoLauncher\\app\\ExoLauncher.exe is never used.`
    if (dryRun) {
      return {
        text: truncateToJsonBytes(
          `exo_parity_snap dry-run:\n  harness: ${toPosixPath(snapScript)}\n  scenes: ${scenes.join(', ')}\n  outDir: ${toPosixPath(outDir)}\n  refsDir: ${refsDir ? toPosixPath(refsDir) : '(none)'}\n  hostExe: (not built yet)\n  note: ${msg}`,
          maxBytes,
        ),
      }
    }
    return { text: msg, isError: true }
  }

  const buildPsArgs = (scene: string, skipBuild: boolean): string[] => {
    const psArgs = [
      '-NoProfile',
      '-NonInteractive',
      '-File',
      snapScript,
      '-Scenes',
      scene,
      '-Out',
      outDir,
      '-Tag',
      tag,
      '-HostExe',
      hostExe,
    ]
    if (skipBuild) psArgs.push('-NoBuild')
    if (refsDir) psArgs.push('-Refs', refsDir)
    if (dataDir) psArgs.push('-DataDir', dataDir)
    return psArgs
  }

  if (dryRun) {
    const sample = buildPsArgs(scenes[0]!, noBuild)
    return {
      text: truncateToJsonBytes(
        `exo_parity_snap dry-run:\n  scenes: ${scenes.join(', ')}\n  command: pwsh ${sample.join(' ')}\n  outDir: ${toPosixPath(outDir)}`,
        maxBytes,
      ),
    }
  }

  const timeoutSec =
    typeof args.timeoutSec === 'number' && Number.isFinite(args.timeoutSec)
      ? Math.max(15, Math.min(165, Math.floor(args.timeoutSec)))
      : 150
  const deadlineMs = Date.now() + Math.min(timeoutSec * 1000, MCP_CALL_BUDGET_MS - 3000)

  const logs: string[] = [`exo_parity_snap: ${scenes.length} scene(s) -> ${toPosixPath(outDir)}`]
  let anyFailed = false

  for (let i = 0; i < scenes.length; i++) {
    const scene = scenes[i]!
    const remaining = deadlineMs - Date.now()
    if (remaining <= 1000) {
      logs.push(`[${scene}] SKIPPED (call budget exhausted)`)
      anyFailed = true
      break
    }
    const psArgs = buildPsArgs(scene, noBuild || i > 0)
    const res = await runCommand('pwsh', psArgs, {
      cwd: parityDir,
      timeoutMs: remaining,
    })
    const out = stripAnsi([res.stdout.trim(), res.stderr.trim()].filter(Boolean).join('\n'))
    const ok = res.exitCode === 0 && !res.timedOut
    if (!ok) anyFailed = true
    logs.push(`[${scene}] ${ok ? 'OK' : `FAIL(exit=${res.exitCode})`} ${res.durationMs}ms`)
    if (out) logs.push(out)
  }

  return {
    text: foldHeadTail(logs.join('\n'), maxBytes),
    isError: anyFailed,
  }
}

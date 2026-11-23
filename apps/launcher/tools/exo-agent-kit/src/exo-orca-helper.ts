import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { runCommand } from './utils/exec.js'
import { runReadOnlyGit } from './utils/git.js'
import { toPosixPath } from './utils/paths.js'

interface WaveWorkerSpec {
  name?: string
  task?: string
  specFile?: string
  agent?: string
  worktree?: string
  baseBranch?: string
}

function getFlag(argv: string[], flag: string): string | undefined {
  const idx = argv.indexOf(flag)
  if (idx >= 0 && idx + 1 < argv.length) return argv[idx + 1]
  return undefined
}

function hasFlag(argv: string[], flag: string): boolean {
  return argv.includes(flag)
}

function printUsage(): void {
  process.stdout.write(
    [
      'exo-orca-helper — parallel Orca worker wave launcher & safe recovery CLI',
      '',
      'Usage:',
      '  exo-orca-helper wave --run <run_id> --specs <a.md,b.md,...> [--agent <agent>] [--worktree new-child] [--base-branch <branch>] [--receipt-dir <dir>] [--cwd <repo>] [--dry-run]',
      '  exo-orca-helper wave --run <run_id> --manifest <workers.json> [--receipt-dir <dir>] [--cwd <repo>] [--dry-run]',
      '  exo-orca-helper recover --dispatch <dispatch_id> --task <task_id> --run <run_id> --worktree-path <path> [--agent <agent>] [--live] [--dry-run]',
    ].join('\n') + '\n',
  )
}

async function detectBranch(cwd: string): Promise<string | null> {
  const res = await runReadOnlyGit(cwd, 'rev-parse', ['--abbrev-ref', 'HEAD'], 3000)
  if (res.exitCode !== 0) return null
  const branch = res.stdout.trim()
  if (!branch || branch === 'HEAD') return null
  return branch
}

async function runWave(argv: string[]): Promise<number> {
  const runId = getFlag(argv, '--run')
  const cwd = path.resolve(getFlag(argv, '--cwd') ?? process.cwd())
  const dryRun = hasFlag(argv, '--dry-run')
  const explicitBase = getFlag(argv, '--base-branch')
  const defaultAgent = getFlag(argv, '--agent')
  const defaultWorktree = getFlag(argv, '--worktree') ?? 'new-child'
  const receiptDir = path.resolve(
    getFlag(argv, '--receipt-dir') ?? path.join(os.tmpdir(), `exo-orca-wave-${Date.now()}`),
  )

  if (!runId) {
    process.stderr.write('Error: wave requires --run <run_id>\n')
    return 1
  }

  let workers: WaveWorkerSpec[] = []
  const manifestPath = getFlag(argv, '--manifest')
  const specsArg = getFlag(argv, '--specs')

  if (manifestPath) {
    const parsed = JSON.parse(fs.readFileSync(path.resolve(cwd, manifestPath), 'utf8')) as unknown
    if (!Array.isArray(parsed)) {
      process.stderr.write('Error: --manifest must be a JSON array of worker specs\n')
      return 1
    }
    workers = parsed as WaveWorkerSpec[]
  } else if (specsArg) {
    const files = specsArg
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean)
    workers = files.map((specFile, i) => ({
      name: path.basename(specFile, path.extname(specFile)) || `worker-${i + 1}`,
      specFile: path.resolve(cwd, specFile),
      agent: defaultAgent,
      worktree: defaultWorktree,
      baseBranch: explicitBase,
    }))
  } else {
    process.stderr.write('Error: wave requires --specs <file1,file2,...> or --manifest <json>\n')
    return 1
  }

  if (workers.length === 0 || workers.length > 4) {
    process.stderr.write(`Error: wave supports 1..4 workers per batch (got ${workers.length})\n`)
    return 1
  }

  const detectedBranch = explicitBase ?? (await detectBranch(cwd))
  const commands = workers.map((w, idx) => {
    const label = w.name || `worker-${idx + 1}`
    const worktree = w.worktree ?? defaultWorktree
    const baseBranch = w.baseBranch ?? detectedBranch
    const args: string[] = ['orchestration', 'worker-start', '--run', runId]
    if (w.task) args.push('--task', w.task)
    if (w.specFile) args.push('--spec-file', path.resolve(cwd, w.specFile))
    const agent = w.agent ?? defaultAgent
    if (agent) args.push('--agent', agent)
    if (worktree) args.push('--worktree', worktree)
    if (baseBranch && worktree === 'new-child') {
      args.push('--base-branch', baseBranch)
    }
    return { label, args }
  })

  if (dryRun) {
    process.stdout.write(
      JSON.stringify(
        {
          mode: 'wave',
          dryRun: true,
          detectedBranch,
          receiptDir: toPosixPath(receiptDir),
          commands: commands.map((c) => ({ label: c.label, command: 'orca', args: c.args })),
        },
        null,
        2,
      ) + '\n',
    )
    return 0
  }

  fs.mkdirSync(receiptDir, { recursive: true })
  const results = await Promise.all(
    commands.map(async (c) => {
      const res = await runCommand('orca', c.args, { cwd, timeoutMs: 60_000 })
      const safeLabel = c.label.replace(/[^A-Za-z0-9._-]/g, '_')
      const outPath = path.join(receiptDir, `${safeLabel}.out.txt`)
      const errPath = path.join(receiptDir, `${safeLabel}.err.txt`)
      fs.writeFileSync(outPath, res.stdout, 'utf8')
      fs.writeFileSync(errPath, res.stderr, 'utf8')
      return {
        label: c.label,
        ok: res.exitCode === 0 && !res.timedOut,
        exitCode: res.exitCode,
        durationMs: res.durationMs,
        stdoutFile: toPosixPath(outPath),
        stderrFile: toPosixPath(errPath),
      }
    }),
  )

  process.stdout.write(JSON.stringify({ mode: 'wave', receiptDir: toPosixPath(receiptDir), results }, null, 2) + '\n')
  return results.every((r) => r.ok) ? 0 : 1
}

async function runRecover(argv: string[]): Promise<number> {
  const dispatchId = getFlag(argv, '--dispatch')
  const taskId = getFlag(argv, '--task')
  const runId = getFlag(argv, '--run')
  const worktreePath = getFlag(argv, '--worktree-path')
  const agent = getFlag(argv, '--agent')
  const live = hasFlag(argv, '--live')
  const dryRun = hasFlag(argv, '--dry-run')
  const cwd = path.resolve(getFlag(argv, '--cwd') ?? process.cwd())

  if (!dispatchId || !taskId || !runId || !worktreePath) {
    process.stderr.write(
      'Error: recover requires --dispatch <id> --task <id> --run <id> --worktree-path <path>\n',
    )
    return 1
  }

  const steps: { step: string; args: string[] }[] = [
    {
      step: live ? 'worker-stop' : 'worker-abandon',
      args: ['orchestration', live ? 'worker-stop' : 'worker-abandon', '--dispatch', dispatchId],
    },
    {
      step: 'task-reopen',
      args: ['orchestration', 'task-update', '--id', taskId, '--status', 'ready', '--run', runId],
    },
    {
      step: 'worker-restart',
      args: [
        'orchestration',
        'worker-start',
        '--task',
        taskId,
        '--run',
        runId,
        '--worktree',
        `path:${path.resolve(worktreePath)}`,
        ...(agent ? ['--agent', agent] : []),
      ],
    },
  ]

  if (dryRun) {
    process.stdout.write(
      JSON.stringify(
        {
          mode: 'recover',
          dryRun: true,
          steps: steps.map((s) => ({ step: s.step, command: 'orca', args: s.args })),
        },
        null,
        2,
      ) + '\n',
    )
    return 0
  }

  const outcomes: { step: string; ok: boolean; exitCode: number | null; stdout: string; stderr: string }[] = []
  for (const s of steps) {
    const res = await runCommand('orca', s.args, { cwd, timeoutMs: 30_000 })
    const ok = res.exitCode === 0 && !res.timedOut
    outcomes.push({
      step: s.step,
      ok,
      exitCode: res.exitCode,
      stdout: res.stdout.trim(),
      stderr: res.stderr.trim(),
    })
    if (!ok) {
      process.stdout.write(JSON.stringify({ mode: 'recover', ok: false, outcomes }, null, 2) + '\n')
      return 1
    }
  }

  process.stdout.write(JSON.stringify({ mode: 'recover', ok: true, outcomes }, null, 2) + '\n')
  return 0
}

async function main(): Promise<void> {
  const argv = process.argv.slice(2)
  const cmd = argv[0]
  if (!cmd || cmd === '--help' || cmd === '-h') {
    printUsage()
    process.exit(0)
  }
  if (cmd === 'wave') {
    process.exit(await runWave(argv.slice(1)))
  }
  if (cmd === 'recover') {
    process.exit(await runRecover(argv.slice(1)))
  }
  process.stderr.write(`Unknown subcommand: ${cmd}\n`)
  printUsage()
  process.exit(1)
}

void main()

import { runCommand, type CommandResult } from './exec.js'

export const READONLY_GIT_FLAGS: readonly string[] = [
  '--no-optional-locks',
  '-c',
  'core.fsmonitor=false',
  '-c',
  'diff.external=',
  '-c',
  'gpg.program=false',
]

export const READONLY_GIT_SUBCOMMANDS = new Set<string>([
  'status',
  'diff',
  'log',
  'show',
  'rev-parse',
  'branch',
  'ls-files',
  'merge-base',
  'grep',
  'rev-list',
  'describe',
  'shortlog',
  'blame',
])

const FORBIDDEN_GIT_GLOBAL_FLAGS = new Set<string>([
  '-c',
  '--config-env',
  '--exec-path',
  '--git-dir',
  '--work-tree',
  '--namespace',
  '--super-prefix',
  '--upload-pack',
])

const FORBIDDEN_BRANCH_FLAGS = new Set<string>([
  '-d',
  '-D',
  '--delete',
  '-m',
  '-M',
  '--move',
  '-c',
  '-C',
  '--copy',
  '-u',
  '--set-upstream-to',
  '--unset-upstream',
  '--edit-description',
  '-f',
  '--force',
])

export function validateReadOnlyGitArgs(subcommand: string, args: string[]): string | null {
  const sub = subcommand.trim()
  if (!READONLY_GIT_SUBCOMMANDS.has(sub)) {
    return `git subcommand "${sub}" is not in the read-only allowlist`
  }

  for (const arg of args) {
    const eqIdx = arg.indexOf('=')
    const flag = eqIdx > 0 ? arg.slice(0, eqIdx) : arg
    if (FORBIDDEN_GIT_GLOBAL_FLAGS.has(flag)) {
      return `git flag "${flag}" is forbidden`
    }
    if (arg === '--ext-diff' || flag === '--output') {
      return `git flag "${flag}" is forbidden`
    }
  }

  if (sub === 'status') {
    for (const arg of args) {
      if (arg === '-v' || arg === '-vv' || arg === '--verbose') {
        return 'git status -v/--verbose is forbidden'
      }
    }
  }

  if (sub === 'branch') {
    for (const arg of args) {
      const eqIdx = arg.indexOf('=')
      const flag = eqIdx > 0 ? arg.slice(0, eqIdx) : arg
      if (FORBIDDEN_BRANCH_FLAGS.has(flag)) {
        return `git branch flag "${flag}" is forbidden in read-only mode`
      }
      if (!arg.startsWith('-') && !args.includes('--list') && !args.includes('-l') && !args.includes('--contains') && !args.includes('--merged') && !args.includes('--no-merged')) {
        return `git branch positional argument "${arg}" requires --list in read-only mode`
      }
    }
  }

  return null
}

export async function runReadOnlyGit(
  cwd: string,
  subcommand: string,
  args: string[] = [],
  timeoutMs = 10_000,
): Promise<CommandResult> {
  const err = validateReadOnlyGitArgs(subcommand, args)
  if (err) {
    return {
      command: 'git',
      args: [subcommand, ...args],
      cwd,
      exitCode: 1,
      signal: null,
      stdout: '',
      stderr: err,
      timedOut: false,
      durationMs: 0,
      errorCode: 'GIT_POLICY_DENIED',
    }
  }
  return runCommand('git', [...READONLY_GIT_FLAGS, subcommand, ...args], {
    cwd,
    timeoutMs,
  })
}

export function splitCommandArgs(input: string): string[] {
  const out: string[] = []
  let cur = ''
  let quote: '"' | "'" | null = null
  for (let i = 0; i < input.length; i++) {
    const ch = input[i]!
    if (quote) {
      if (ch === '\\' && quote === '"' && i + 1 < input.length) {
        cur += input[i + 1]!
        i++
      } else if (ch === quote) {
        quote = null
      } else {
        cur += ch
      }
    } else if (ch === '"' || ch === "'") {
      quote = ch
    } else if (ch === ' ' || ch === '\t' || ch === '\r' || ch === '\n') {
      if (cur.length > 0) {
        out.push(cur)
        cur = ''
      }
    } else {
      cur += ch
    }
  }
  if (cur.length > 0) out.push(cur)
  return out
}

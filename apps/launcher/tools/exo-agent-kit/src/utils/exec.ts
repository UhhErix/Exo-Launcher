import { spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

export const MCP_CALL_BUDGET_MS = 170_000

export interface CommandResult {
  command: string
  args: string[]
  cwd: string
  exitCode: number | null
  signal: NodeJS.Signals | null
  stdout: string
  stderr: string
  timedOut: boolean
  durationMs: number
  errorCode?: string
}

export interface RunCommandOptions {
  cwd?: string
  timeoutMs?: number
  env?: NodeJS.ProcessEnv
  maxCaptureBytes?: number
}

const DEFAULT_MAX_CAPTURE_BYTES = 512 * 1024

export function resolveNpmInvocation(args: string[]): { command: string; args: string[] } {
  const npmCli = path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js')
  if (fs.existsSync(npmCli)) {
    return { command: process.execPath, args: [npmCli, ...args] }
  }
  return { command: 'npm', args }
}

export function runCommand(
  command: string,
  args: string[],
  options: RunCommandOptions = {},
): Promise<CommandResult> {
  const cwd = options.cwd ?? process.cwd()
  const timeoutMs = Math.max(50, Math.min(options.timeoutMs ?? 60_000, MCP_CALL_BUDGET_MS))
  const maxCapture = options.maxCaptureBytes ?? DEFAULT_MAX_CAPTURE_BYTES
  const startedAt = Date.now()

  return new Promise((resolve) => {
    let stdoutBytes = 0
    let stderrBytes = 0
    const stdoutChunks: Buffer[] = []
    const stderrChunks: Buffer[] = []
    let timedOut = false
    let settled = false

    const child = spawn(command, args, {
      cwd,
      env: options.env ?? process.env,
      shell: false,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    })

    const finish = (
      exitCode: number | null,
      signal: NodeJS.Signals | null,
      errorCode?: string,
    ): void => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      resolve({
        command,
        args,
        cwd,
        exitCode,
        signal,
        stdout: Buffer.concat(stdoutChunks).toString('utf8'),
        stderr: Buffer.concat(stderrChunks).toString('utf8'),
        timedOut,
        durationMs: Date.now() - startedAt,
        errorCode,
      })
    }

    const timer = setTimeout(() => {
      timedOut = true
      try {
        child.kill('SIGTERM')
      } catch {}
      setTimeout(() => {
        try {
          child.kill('SIGKILL')
        } catch {}
      }, 1500).unref()
    }, timeoutMs)
    timer.unref()

    child.stdout?.on('data', (chunk: Buffer) => {
      if (stdoutBytes >= maxCapture) return
      const keep = Math.min(chunk.length, maxCapture - stdoutBytes)
      stdoutChunks.push(keep === chunk.length ? chunk : chunk.subarray(0, keep))
      stdoutBytes += keep
    })

    child.stderr?.on('data', (chunk: Buffer) => {
      if (stderrBytes >= maxCapture) return
      const keep = Math.min(chunk.length, maxCapture - stderrBytes)
      stderrChunks.push(keep === chunk.length ? chunk : chunk.subarray(0, keep))
      stderrBytes += keep
    })

    child.on('error', (err: NodeJS.ErrnoException) => {
      stderrChunks.push(Buffer.from(err.message, 'utf8'))
      finish(null, null, err.code)
    })

    child.on('close', (code, signal) => {
      finish(code, signal)
    })
  })
}

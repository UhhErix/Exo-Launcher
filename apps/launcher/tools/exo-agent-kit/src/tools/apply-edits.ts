import crypto from 'node:crypto'
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
import { resolveNpmInvocation, runCommand, type CommandResult } from '../utils/exec.js'
import { runReadOnlyGit, validateReadOnlyGitArgs } from '../utils/git.js'
import {
  parseMaybeJsonArray,
  resolveConfinedDir,
  resolveConfinedWritePath,
  resolveWorkspacePaths,
  toPosixPath,
} from '../utils/paths.js'

interface EditHunk {
  oldText: string
  newText: string
  replaceAll?: boolean
}

interface FileEditSpec {
  path: string
  hunks?: EditHunk[]
  oldText?: string
  newText?: string
  replaceAll?: boolean
  createIfMissing?: boolean
  content?: string
}

interface VerifySpec {
  command: string
  args?: string[]
  cwd?: string
  timeoutSec?: number
}

export const ALLOWED_VERIFY_COMMANDS = new Set([
  'cargo',
  'dotnet',
  'npm',
  'node',
  'python',
  'python3',
  'rg',
  'git',
])

const ALLOWED_CARGO_SUBCOMMANDS = new Set(['fmt', 'clippy', 'test', 'build', 'check'])
const ALLOWED_DOTNET_SUBCOMMANDS = new Set(['build', 'test'])
const FORBIDDEN_NODE_LONG_FLAGS = new Set([
  '--eval',
  '--print',
  '--import',
  '--require',
  '--loader',
  '--experimental-loader',
  '--env-file',
])
const FORBIDDEN_NPM_FLAGS = new Set([
  '--script-shell',
  '--prefix',
  '--userconfig',
  '--globalconfig',
])

type ValidatedVerifyPlan =
  | {
      ok: true
      kind: 'exec'
      display: string
      command: string
      args: string[]
      cwd: string
      timeoutMs: number
    }
  | {
      ok: true
      kind: 'git'
      display: string
      subcommand: string
      args: string[]
      cwd: string
      timeoutMs: number
    }
  | { ok: false; error: string }

function hasForbiddenNodeFlag(args: string[]): string | null {
  for (const arg of args) {
    if (arg === '--') break
    if (arg === '-e' || arg === '-p' || arg === '-r') return arg
    const eqIdx = arg.indexOf('=')
    const base = eqIdx > 0 ? arg.slice(0, eqIdx) : arg
    if (FORBIDDEN_NODE_LONG_FLAGS.has(base)) return base
    if (/^-[a-zA-Z]+$/.test(arg)) {
      if (arg.includes('e') || arg.includes('p') || arg.includes('r')) {
        return arg
      }
    }
  }
  return null
}

export function validateVerifySpec(
  repoRoot: string,
  baseCwd: string,
  spec: VerifySpec,
): ValidatedVerifyPlan {
  const rawCmd = typeof spec.command === 'string' ? spec.command.trim() : ''
  if (!rawCmd) {
    return { ok: false, error: 'Refused verify: command must be a non-empty string.' }
  }
  if (rawCmd.includes('/') || rawCmd.includes('\\')) {
    return {
      ok: false,
      error: `Refused verify command "${rawCmd}": path separators are not allowed; use a bare allowlisted command (${Array.from(ALLOWED_VERIFY_COMMANDS).join(', ')}).`,
    }
  }

  const cwdTarget = typeof spec.cwd === 'string' && spec.cwd.trim() ? spec.cwd.trim() : '.'
  const cwdCheck = resolveConfinedDir(repoRoot, baseCwd, cwdTarget)
  if (!cwdCheck.ok) {
    return { ok: false, error: `Refused verify cwd: ${cwdCheck.error}` }
  }
  const verifyCwd = cwdCheck.realPath
  if (!fs.existsSync(verifyCwd) || !fs.statSync(verifyCwd).isDirectory()) {
    return { ok: false, error: `Refused verify cwd: directory does not exist (${cwdTarget}).` }
  }

  const args = Array.isArray(spec.args) ? spec.args.map((a) => String(a)) : []
  const timeoutMs =
    typeof spec.timeoutSec === 'number' && Number.isFinite(spec.timeoutSec)
      ? Math.max(2, Math.min(120, Math.floor(spec.timeoutSec))) * 1000
      : 60_000

  const cmd = rawCmd.toLowerCase().replace(/\.(exe|cmd)$/i, '')
  if (!ALLOWED_VERIFY_COMMANDS.has(cmd)) {
    return {
      ok: false,
      error: `Refused verify command "${rawCmd}": not in allowlist (${Array.from(ALLOWED_VERIFY_COMMANDS).join(', ')}).`,
    }
  }

  if (cmd === 'cargo') {
    const sub = args[0] ?? ''
    if (!ALLOWED_CARGO_SUBCOMMANDS.has(sub)) {
      return {
        ok: false,
        error: `Refused cargo verify subcommand "${sub}": allowed subcommands are ${Array.from(ALLOWED_CARGO_SUBCOMMANDS).join(', ')}.`,
      }
    }
    for (const arg of args.slice(1)) {
      const base = arg.includes('=') ? arg.slice(0, arg.indexOf('=')) : arg
      if (base === '--config' || base === '-Z') {
        return { ok: false, error: `Refused cargo verify flag "${base}".` }
      }
    }
    return {
      ok: true,
      kind: 'exec',
      display: `cargo ${args.join(' ')}`,
      command: 'cargo',
      args,
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  if (cmd === 'dotnet') {
    const sub = args[0] ?? ''
    if (!ALLOWED_DOTNET_SUBCOMMANDS.has(sub)) {
      return {
        ok: false,
        error: `Refused dotnet verify subcommand "${sub}": allowed subcommands are build, test.`,
      }
    }
    return {
      ok: true,
      kind: 'exec',
      display: `dotnet ${args.join(' ')}`,
      command: 'dotnet',
      args,
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  if (cmd === 'npm') {
    if (args[0] !== 'run' || !args[1] || !/^[A-Za-z0-9:._-]+$/.test(args[1])) {
      return {
        ok: false,
        error: 'Refused npm verify: only "npm run <script>" with a valid script name is allowed.',
      }
    }
    for (const arg of args.slice(2)) {
      const base = arg.includes('=') ? arg.slice(0, arg.indexOf('=')) : arg
      if (FORBIDDEN_NPM_FLAGS.has(base)) {
        return { ok: false, error: `Refused npm verify flag "${base}".` }
      }
    }
    const invoked = resolveNpmInvocation(args)
    return {
      ok: true,
      kind: 'exec',
      display: `npm ${args.join(' ')}`,
      command: invoked.command,
      args: invoked.args,
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  if (cmd === 'node') {
    if (args.length === 0) {
      return {
        ok: false,
        error: 'Refused node verify: must be "node --test <path inside repo>" or "node <script inside repo>".',
      }
    }
    const badFlag = hasForbiddenNodeFlag(args)
    if (badFlag) {
      return {
        ok: false,
        error: `Refused node verify flag "${badFlag}": inline evaluation and module preload flags are disallowed.`,
      }
    }
    if (args[0] === '--test') {
      const rest = args.slice(1)
      let testPathCount = 0
      const resolvedArgs: string[] = ['--test']
      for (const arg of rest) {
        if (arg.startsWith('-')) {
          if (!arg.startsWith('--test-')) {
            return {
              ok: false,
              error: `Refused node --test flag "${arg}": only --test-* flags and repo test paths are allowed.`,
            }
          }
          resolvedArgs.push(arg)
        } else {
          const checked = resolveConfinedWritePath(repoRoot, verifyCwd, arg)
          if (!checked.ok) {
            return { ok: false, error: `Refused node --test path "${arg}": ${checked.error}` }
          }
          resolvedArgs.push(checked.realPath)
          testPathCount++
        }
      }
      if (testPathCount === 0) {
        return {
          ok: false,
          error: 'Refused node --test verify: at least one test file path inside the repo is required.',
        }
      }
      return {
        ok: true,
        kind: 'exec',
        display: `node ${args.join(' ')}`,
        command: process.execPath,
        args: resolvedArgs,
        cwd: verifyCwd,
        timeoutMs,
      }
    }

    const scriptArg = args[0]!
    if (scriptArg.startsWith('-')) {
      return {
        ok: false,
        error: `Refused node verify flag "${scriptArg}": only "node --test <path>" or "node <script inside repo>" is allowed.`,
      }
    }
    const scriptCheck = resolveConfinedWritePath(repoRoot, verifyCwd, scriptArg)
    if (!scriptCheck.ok) {
      return { ok: false, error: `Refused node script "${scriptArg}": ${scriptCheck.error}` }
    }
    if (!fs.existsSync(scriptCheck.realPath) || !fs.statSync(scriptCheck.realPath).isFile()) {
      return { ok: false, error: `Refused node script "${scriptArg}": file does not exist inside repo.` }
    }
    return {
      ok: true,
      kind: 'exec',
      display: `node ${args.join(' ')}`,
      command: process.execPath,
      args: [scriptCheck.realPath, ...args.slice(1)],
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  if (cmd === 'python' || cmd === 'python3') {
    if (args.length < 2 || args[0] !== '-I' || args[1]!.startsWith('-')) {
      return {
        ok: false,
        error: 'Refused python verify: only "python -I <script inside repo> [...]" is allowed (no -c/-m).',
      }
    }
    const scriptCheck = resolveConfinedWritePath(repoRoot, verifyCwd, args[1]!)
    if (!scriptCheck.ok) {
      return { ok: false, error: `Refused python script "${args[1]!}": ${scriptCheck.error}` }
    }
    if (!fs.existsSync(scriptCheck.realPath) || !fs.statSync(scriptCheck.realPath).isFile()) {
      return { ok: false, error: `Refused python script "${args[1]!}": file does not exist inside repo.` }
    }
    return {
      ok: true,
      kind: 'exec',
      display: `${cmd} ${args.join(' ')}`,
      command: cmd,
      args: ['-I', scriptCheck.realPath, ...args.slice(2)],
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  if (cmd === 'rg') {
    for (const arg of args) {
      const base = arg.includes('=') ? arg.slice(0, arg.indexOf('=')) : arg
      if (base === '--pre' || base === '--pre-glob') {
        return { ok: false, error: `Refused rg verify flag "${base}".` }
      }
    }
    return {
      ok: true,
      kind: 'exec',
      display: `rg ${args.join(' ')}`,
      command: 'rg',
      args,
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  if (cmd === 'git') {
    const sub = args[0] ?? ''
    const rest = args.slice(1)
    const gitErr = validateReadOnlyGitArgs(sub, rest)
    if (gitErr) {
      return { ok: false, error: `Refused git verify: ${gitErr}` }
    }
    return {
      ok: true,
      kind: 'git',
      display: `git ${args.join(' ')}`,
      subcommand: sub,
      args: rest,
      cwd: verifyCwd,
      timeoutMs,
    }
  }

  return { ok: false, error: `Refused verify command "${rawCmd}".` }
}

function detectEol(text: string): '\r\n' | '\n' {
  const crlf = text.indexOf('\r\n')
  if (crlf >= 0) return '\r\n'
  return '\n'
}

function normalizeLf(text: string): string {
  return text.replace(/\r\n/g, '\n')
}

function restoreEol(text: string, eol: '\r\n' | '\n'): string {
  return eol === '\r\n' ? text.replace(/\n/g, '\r\n') : text
}

function findClosestLineHint(contentLf: string, needleLf: string): string {
  const contentLines = contentLf.split('\n')
  const needleLines = needleLf
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
  const anchor = needleLines[0] ?? needleLf.trim()
  if (!anchor) return ''

  const anchorTokens = anchor
    .toLowerCase()
    .split(/[^a-z0-9_]+/)
    .filter((t) => t.length >= 2)

  let bestIdx = -1
  let bestScore = 0
  const anchorLow = anchor.toLowerCase()

  for (let i = 0; i < contentLines.length; i++) {
    const lineLow = contentLines[i]!.trim().toLowerCase()
    if (!lineLow) continue
    let score = 0
    if (lineLow === anchorLow) score = 100
    else if (lineLow.includes(anchorLow) || anchorLow.includes(lineLow)) {
      score = 70
    } else if (anchorTokens.length > 0) {
      let hits = 0
      for (const tok of anchorTokens) {
        if (lineLow.includes(tok)) hits++
      }
      score = (hits / anchorTokens.length) * 60
    }
    if (score > bestScore) {
      bestScore = score
      bestIdx = i
    }
  }

  if (bestIdx < 0 || bestScore < 20) return ''
  return ` (closest L${bestIdx + 1}: ${contentLines[bestIdx]!.trim().slice(0, 90)})`
}

function atomicWritePreservingMode(targetPath: string, content: string, mode: number | null): void {
  const dir = path.dirname(targetPath)
  fs.mkdirSync(dir, { recursive: true })
  const tmpPath = path.join(
    dir,
    `.${path.basename(targetPath)}.exo-tmp-${crypto.randomBytes(4).toString('hex')}`,
  )
  fs.writeFileSync(tmpPath, content, 'utf8')
  if (mode !== null) {
    try {
      fs.chmodSync(tmpPath, mode)
    } catch {}
  }
  fs.renameSync(tmpPath, targetPath)
}

function normalizeHunks(spec: FileEditSpec): EditHunk[] {
  const out: EditHunk[] = []
  if (Array.isArray(spec.hunks)) {
    for (const h of spec.hunks) {
      if (typeof h.oldText === 'string' && typeof h.newText === 'string') {
        out.push({ oldText: h.oldText, newText: h.newText, replaceAll: h.replaceAll === true })
      }
    }
  }
  if (typeof spec.oldText === 'string' && typeof spec.newText === 'string') {
    out.push({
      oldText: spec.oldText,
      newText: spec.newText,
      replaceAll: spec.replaceAll === true,
    })
  }
  return out
}

export async function runApplyEdits(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const rawEdits = parseMaybeJsonArray<FileEditSpec>(args.edits)
  if (!rawEdits || rawEdits.length === 0) {
    return { text: 'apply_edits requires a non-empty edits array (1..50 items).', isError: true }
  }
  if (rawEdits.length > 50) {
    return { text: `apply_edits accepts at most 50 edit specs per call (got ${rawEdits.length}).`, isError: true }
  }

  const ws = resolveWorkspacePaths(typeof args.cwd === 'string' ? args.cwd : undefined)
  const bestEffort = args.bestEffort === true
  const dryRun = args.dryRun === true

  const verifyArg =
    args.verify && typeof args.verify === 'object' && !Array.isArray(args.verify)
      ? (args.verify as unknown as VerifySpec)
      : null

  let verifyPlan: ValidatedVerifyPlan | null = null
  if (verifyArg && !dryRun) {
    verifyPlan = validateVerifySpec(ws.repoRoot, ws.cwd, verifyArg)
    if (!verifyPlan.ok) {
      return { text: verifyPlan.error, isError: true }
    }
  }

  interface MergedFilePlan {
    displayPath: string
    targetPath: string
    exists: boolean
    mode: number | null
    createContent: string | null
    hunks: EditHunk[]
  }

  const merged = new Map<string, MergedFilePlan>()
  for (const spec of rawEdits) {
    if (!spec || typeof spec.path !== 'string' || !spec.path.trim()) {
      return { text: 'Every edit spec must include a non-empty "path".', isError: true }
    }
    const confined = resolveConfinedWritePath(ws.repoRoot, ws.cwd, spec.path)
    if (!confined.ok) {
      return { text: confined.error, isError: true }
    }

    const exists = fs.existsSync(confined.realPath)
    let mode: number | null = null
    if (exists) {
      try {
        mode = fs.statSync(confined.realPath).mode
      } catch {}
    }

    const key = confined.realPath.toLowerCase()
    const existing = merged.get(key)
    const hunks = normalizeHunks(spec)
    if (existing) {
      if (typeof spec.content === 'string') existing.createContent = spec.content
      existing.hunks.push(...hunks)
    } else {
      merged.set(key, {
        displayPath: toPosixPath(confined.relPath),
        targetPath: confined.realPath,
        exists,
        mode,
        createContent: spec.createIfMissing && typeof spec.content === 'string' ? spec.content : null,
        hunks,
      })
    }
  }

  interface ValidatedFile {
    plan: MergedFilePlan
    ok: boolean
    appliedHunks: number
    finalText: string | null
    errors: string[]
  }

  const validated: ValidatedFile[] = []

  for (const plan of merged.values()) {
    const errors: string[] = []
    if (!plan.exists) {
      if (plan.createContent !== null && plan.hunks.length === 0) {
        validated.push({
          plan,
          ok: true,
          appliedHunks: 1,
          finalText: plan.createContent,
          errors,
        })
      } else {
        validated.push({
          plan,
          ok: false,
          appliedHunks: 0,
          finalText: null,
          errors: [`File not found: ${plan.displayPath}`],
        })
      }
      continue
    }

    if (plan.hunks.length === 0 && plan.createContent === null) {
      validated.push({
        plan,
        ok: false,
        appliedHunks: 0,
        finalText: null,
        errors: ['No hunks provided for existing file'],
      })
      continue
    }

    const original = fs.readFileSync(plan.targetPath, 'utf8')
    const eol = detectEol(original)
    let working = normalizeLf(original)
    let applied = 0

    for (let i = 0; i < plan.hunks.length; i++) {
      const hunk = plan.hunks[i]!
      const oldLf = normalizeLf(hunk.oldText)
      const newLf = normalizeLf(hunk.newText)
      if (oldLf.length === 0) {
        errors.push(`Hunk #${i + 1}: oldText cannot be empty`)
        continue
      }

      const firstIdx = working.indexOf(oldLf)
      if (firstIdx < 0) {
        const hint = findClosestLineHint(working, oldLf)
        errors.push(`Hunk #${i + 1}: oldText not found${hint}`)
        continue
      }

      if (hunk.replaceAll) {
        working = working.split(oldLf).join(newLf)
        applied++
      } else {
        const secondIdx = working.indexOf(oldLf, firstIdx + 1)
        if (secondIdx >= 0) {
          const line1 = working.slice(0, firstIdx).split('\n').length
          const line2 = working.slice(0, secondIdx).split('\n').length
          errors.push(
            `Hunk #${i + 1}: ambiguous match (matches at L${line1} and L${line2}; set replaceAll or add context)`,
          )
          continue
        }
        working = working.slice(0, firstIdx) + newLf + working.slice(firstIdx + oldLf.length)
        applied++
      }
    }

    const finalText = errors.length === 0 ? restoreEol(working, eol) : null
    validated.push({
      plan,
      ok: errors.length === 0,
      appliedHunks: applied,
      finalText,
      errors,
    })
  }

  const failedFiles = validated.filter((v) => !v.ok)
  const validFiles = validated.filter((v) => v.ok)

  if (failedFiles.length > 0 && !bestEffort) {
    const sections: BudgetSection[] = failedFiles.map((f) => ({
      header: `FAILED ${f.plan.displayPath}`,
      body: f.errors.join('\n'),
    }))
    return {
      text: fitSectionsToByteBudget(
        sections,
        maxBytes,
        `apply_edits ABORTED (atomic mode): ${failedFiles.length}/${validated.length} file(s) failed validation; 0 files written.`,
      ),
      isError: true,
    }
  }

  let writtenCount = 0
  if (!dryRun) {
    for (const v of validFiles) {
      if (v.finalText !== null) {
        atomicWritePreservingMode(v.plan.targetPath, v.finalText, v.plan.mode)
        writtenCount++
      }
    }
  }

  const sections: BudgetSection[] = []
  if (validFiles.length > 0) {
    sections.push({
      header: dryRun
        ? `Validated (${validFiles.length} file(s), dry-run)`
        : `Written (${writtenCount} file(s))`,
      body: validFiles
        .map((v) => `- ${v.plan.displayPath} (${v.appliedHunks} hunk${v.appliedHunks === 1 ? '' : 's'})`)
        .join('\n'),
    })
  }
  if (failedFiles.length > 0) {
    sections.push({
      header: `Failed (${failedFiles.length} file(s), skipped in bestEffort mode)`,
      body: failedFiles.map((f) => `- ${f.plan.displayPath}: ${f.errors.join('; ')}`).join('\n'),
    })
  }

  let verifyFailed = false
  if (verifyPlan && verifyPlan.ok && !dryRun) {
    let res: CommandResult
    if (verifyPlan.kind === 'git') {
      res = await runReadOnlyGit(
        verifyPlan.cwd,
        verifyPlan.subcommand,
        verifyPlan.args,
        verifyPlan.timeoutMs,
      )
    } else {
      res = await runCommand(verifyPlan.command, verifyPlan.args, {
        cwd: verifyPlan.cwd,
        timeoutMs: verifyPlan.timeoutMs,
      })
    }
    const ok = res.exitCode === 0 && !res.timedOut
    if (!ok) verifyFailed = true
    const combined = stripAnsi([res.stdout.trim(), res.stderr.trim()].filter(Boolean).join('\n'))
    sections.push({
      header: `Verify [${verifyPlan.display}] ${ok ? 'PASS' : `FAIL(exit=${res.exitCode})`} ${res.durationMs}ms`,
      body: ok ? '(passed)' : foldHeadTail(combined || '(no output)', 2400),
    })
  }

  const preamble = `apply_edits: ${validFiles.length}/${validated.length} ok${
    failedFiles.length > 0 ? `, ${failedFiles.length} failed` : ''
  }${dryRun ? ' (dry-run)' : ''}`

  return {
    text: fitSectionsToByteBudget(sections, maxBytes, preamble),
    isError: failedFiles.length > 0 || verifyFailed,
  }
}

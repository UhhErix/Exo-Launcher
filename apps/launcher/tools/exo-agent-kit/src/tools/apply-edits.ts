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
import { resolveNpmInvocation, runCommand } from '../utils/exec.js'
import {
  parseMaybeJsonArray,
  resolveSafePath,
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

const DISALLOWED_VERIFY_SHELLS = new Set([
  'cmd',
  'cmd.exe',
  'powershell',
  'powershell.exe',
  'pwsh',
  'pwsh.exe',
  'sh',
  'bash',
  'zsh',
  'wsl',
  'wsl.exe',
])

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

function resolveRealTargetPath(absPath: string): {
  targetPath: string
  exists: boolean
  mode: number | null
} {
  if (!fs.existsSync(absPath)) {
    return { targetPath: absPath, exists: false, mode: null }
  }
  let targetPath = absPath
  try {
    targetPath = fs.realpathSync(absPath)
  } catch {}
  let mode: number | null = null
  try {
    mode = fs.statSync(targetPath).mode
  } catch {}
  return { targetPath, exists: true, mode }
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
    const abs = resolveSafePath(ws.cwd, spec.path)
    const resolved = resolveRealTargetPath(abs)
    const key = resolved.targetPath.toLowerCase()
    const existing = merged.get(key)
    const hunks = normalizeHunks(spec)
    if (existing) {
      if (typeof spec.content === 'string') existing.createContent = spec.content
      existing.hunks.push(...hunks)
    } else {
      merged.set(key, {
        displayPath: toPosixPath(path.relative(ws.repoRoot, abs) || spec.path),
        targetPath: resolved.targetPath,
        exists: resolved.exists,
        mode: resolved.mode,
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
  const verifyArg =
    args.verify && typeof args.verify === 'object' && !Array.isArray(args.verify)
      ? (args.verify as unknown as VerifySpec)
      : null

  if (verifyArg && typeof verifyArg.command === 'string' && verifyArg.command.trim() && !dryRun) {
    const rawCmd = verifyArg.command.trim()
    const baseCmd = path.basename(rawCmd).toLowerCase()
    if (DISALLOWED_VERIFY_SHELLS.has(baseCmd)) {
      verifyFailed = true
      sections.push({
        header: `Verify REFUSED (${rawCmd})`,
        body: 'Shell interpreters are disallowed in verify.command; invoke the target binary directly with args[].',
      })
    } else {
      const verifyArgs = Array.isArray(verifyArg.args)
        ? verifyArg.args.map((a) => String(a))
        : []
      const invoked =
        baseCmd === 'npm' || baseCmd === 'npm.cmd'
          ? resolveNpmInvocation(verifyArgs)
          : { command: rawCmd, args: verifyArgs }
      const verifyCwd = verifyArg.cwd ? resolveSafePath(ws.cwd, verifyArg.cwd) : ws.cwd
      const timeoutMs =
        typeof verifyArg.timeoutSec === 'number' && Number.isFinite(verifyArg.timeoutSec)
          ? Math.max(2, Math.min(120, Math.floor(verifyArg.timeoutSec))) * 1000
          : 60_000
      const res = await runCommand(invoked.command, invoked.args, { cwd: verifyCwd, timeoutMs })
      const ok = res.exitCode === 0 && !res.timedOut
      if (!ok) verifyFailed = true
      const combined = stripAnsi([res.stdout.trim(), res.stderr.trim()].filter(Boolean).join('\n'))
      sections.push({
        header: `Verify [${rawCmd} ${verifyArgs.join(' ')}] ${ok ? 'PASS' : `FAIL(exit=${res.exitCode})`} ${res.durationMs}ms`,
        body: ok ? '(passed)' : foldHeadTail(combined || '(no output)', 2400),
      })
    }
  }

  const preamble = `apply_edits: ${validFiles.length}/${validated.length} ok${
    failedFiles.length > 0 ? `, ${failedFiles.length} failed` : ''
  }${dryRun ? ' (dry-run)' : ''}`

  return {
    text: fitSectionsToByteBudget(sections, maxBytes, preamble),
    isError: failedFiles.length > 0 || verifyFailed,
  }
}

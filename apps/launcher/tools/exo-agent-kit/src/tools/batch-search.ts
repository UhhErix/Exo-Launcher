import fs from 'node:fs'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  fitSectionsToByteBudget,
  stripAnsi,
  type BudgetSection,
} from '../utils/budget.js'
import { runCommand } from '../utils/exec.js'
import { runReadOnlyGit, splitCommandArgs } from '../utils/git.js'
import {
  parseMaybeJsonArray,
  resolveSafePath,
  resolveWorkspacePaths,
  toPosixPath,
} from '../utils/paths.js'

interface SearchQuery {
  id?: string
  type: 'text' | 'regex' | 'glob' | 'fuzzy' | 'git'
  query: string
  path?: string
  glob?: string
  caseSensitive?: boolean
  maxResults?: number
  args?: string[]
}

const SKIP_DIRS = new Set([
  '.git',
  'node_modules',
  'bin',
  'obj',
  'target',
  'dist',
  '.vs',
  'artifacts',
  'e2e-reports',
])

function walkFiles(dir: string, root: string, out: string[], limit = 15_000): void {
  if (out.length >= limit) return
  let entries: fs.Dirent[]
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true })
  } catch {
    return
  }
  for (const entry of entries) {
    if (out.length >= limit) return
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue
      walkFiles(path.join(dir, entry.name), root, out, limit)
    } else if (entry.isFile()) {
      out.push(toPosixPath(path.relative(root, path.join(dir, entry.name))))
    }
  }
}

function globToRegex(pattern: string): RegExp {
  const norm = toPosixPath(pattern)
  let rx = '^'
  for (let i = 0; i < norm.length; i++) {
    const ch = norm[i]!
    if (ch === '*') {
      if (norm[i + 1] === '*') {
        rx += '.*'
        i++
        if (norm[i + 1] === '/') i++
      } else {
        rx += '[^/]*'
      }
    } else if (ch === '?') {
      rx += '[^/]'
    } else if ('\\^$.|+()[]{}'.includes(ch)) {
      rx += `\\${ch}`
    } else {
      rx += ch
    }
  }
  rx += '$'
  return new RegExp(rx, 'i')
}

function fuzzyScore(candidate: string, needle: string): number {
  const c = candidate.toLowerCase()
  const n = needle.toLowerCase().trim()
  if (!n) return 0
  if (c === n) return 1000
  const base = path.posix.basename(c)
  if (base === n) return 900
  if (base.includes(n)) return 750 - (base.length - n.length)
  if (c.includes(n)) return 600 - (c.length - n.length) * 0.25

  let qi = 0
  let consecutive = 0
  let score = 0
  for (let i = 0; i < c.length && qi < n.length; i++) {
    if (c[i] === n[qi]) {
      qi++
      consecutive++
      score += 10 + consecutive * 4
    } else {
      consecutive = 0
    }
  }
  if (qi < n.length) return -1
  return score - c.length * 0.1
}

async function runTextOrRegexQuery(q: SearchQuery, baseDir: string): Promise<string> {
  const maxResults = Math.max(1, Math.min(200, q.maxResults ?? 40))
  const searchTarget = q.path ? resolveSafePath(baseDir, q.path) : baseDir
  const rgArgs = [
    '--no-heading',
    '--line-number',
    '--color=never',
    '--max-count',
    String(maxResults),
    '--max-columns',
    '240',
    '--max-columns-preview',
  ]
  if (q.type === 'text') rgArgs.push('-F')
  if (!q.caseSensitive) rgArgs.push('-i')
  if (q.glob) rgArgs.push('-g', q.glob)
  rgArgs.push('--', q.query, searchTarget)

  const res = await runCommand('rg', rgArgs, { cwd: baseDir, timeoutMs: 15_000 })
  if (res.errorCode !== 'ENOENT') {
    if (res.exitCode === 1 && !res.stdout.trim()) return '(0 matches)'
    if (res.exitCode !== 0 && res.exitCode !== 1) {
      return `rg error (exit ${res.exitCode}): ${stripAnsi(res.stderr).trim()}`
    }
    const lines = stripAnsi(res.stdout)
      .split(/\r?\n/)
      .filter(Boolean)
      .slice(0, maxResults)
      .map((line) => {
        if (line.startsWith(baseDir)) {
          return toPosixPath(line.slice(baseDir.length).replace(/^[\\/]/, ''))
        }
        return line
      })
    return lines.length > 0 ? lines.join('\n') : '(0 matches)'
  }

  const files: string[] = []
  walkFiles(searchTarget, baseDir, files)
  const globRx = q.glob ? globToRegex(q.glob) : null
  const rx =
    q.type === 'regex'
      ? new RegExp(q.query, q.caseSensitive ? '' : 'i')
      : new RegExp(q.query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), q.caseSensitive ? '' : 'i')
  const hits: string[] = []
  for (const rel of files) {
    if (hits.length >= maxResults) break
    if (globRx && !globRx.test(rel) && !globRx.test(path.posix.basename(rel))) continue
    let text: string
    try {
      const full = path.join(baseDir, rel)
      const stat = fs.statSync(full)
      if (stat.size > 1_000_000) continue
      text = fs.readFileSync(full, 'utf8')
    } catch {
      continue
    }
    const lines = text.split(/\r?\n/)
    for (let i = 0; i < lines.length && hits.length < maxResults; i++) {
      if (rx.test(lines[i]!)) {
        hits.push(`${rel}:${i + 1}:${lines[i]!.slice(0, 220)}`)
      }
    }
  }
  return hits.length > 0 ? hits.join('\n') : '(0 matches)'
}

async function runGlobQuery(q: SearchQuery, baseDir: string): Promise<string> {
  const maxResults = Math.max(1, Math.min(200, q.maxResults ?? 60))
  const searchTarget = q.path ? resolveSafePath(baseDir, q.path) : baseDir
  const res = await runCommand('rg', ['--files', '--color=never', '--glob', q.query, searchTarget], {
    cwd: baseDir,
    timeoutMs: 15_000,
  })
  if (res.errorCode !== 'ENOENT') {
    const lines = stripAnsi(res.stdout)
      .split(/\r?\n/)
      .filter(Boolean)
      .map((p) => {
        const rel = path.isAbsolute(p) ? path.relative(baseDir, p) : p
        return toPosixPath(rel)
      })
      .sort()
    const sliced = lines.slice(0, maxResults)
    const suffix = lines.length > maxResults ? `\n... (+${lines.length - maxResults} more)` : ''
    return sliced.length > 0 ? sliced.join('\n') + suffix : '(0 files)'
  }

  const files: string[] = []
  walkFiles(searchTarget, baseDir, files)
  const rx = globToRegex(q.query)
  const matched = files.filter((f) => rx.test(f) || rx.test(path.posix.basename(f))).sort()
  const sliced = matched.slice(0, maxResults)
  return sliced.length > 0 ? sliced.join('\n') : '(0 files)'
}

async function runFuzzyQuery(q: SearchQuery, baseDir: string): Promise<string> {
  const maxResults = Math.max(1, Math.min(100, q.maxResults ?? 25))
  const searchTarget = q.path ? resolveSafePath(baseDir, q.path) : baseDir
  let files: string[] = []
  const res = await runCommand('rg', ['--files', '--color=never', searchTarget], {
    cwd: baseDir,
    timeoutMs: 15_000,
  })
  if (res.errorCode !== 'ENOENT' && res.exitCode === 0) {
    files = res.stdout
      .split(/\r?\n/)
      .filter(Boolean)
      .map((p) => toPosixPath(path.isAbsolute(p) ? path.relative(baseDir, p) : p))
  } else {
    walkFiles(searchTarget, baseDir, files)
  }

  const scored: { file: string; score: number }[] = []
  for (const file of files) {
    const score = fuzzyScore(file, q.query)
    if (score >= 0) scored.push({ file, score })
  }
  scored.sort((a, b) => b.score - a.score || a.file.localeCompare(b.file))
  const top = scored.slice(0, maxResults).map((s) => s.file)
  return top.length > 0 ? top.join('\n') : '(0 matches)'
}

async function runGitQuery(q: SearchQuery, baseDir: string): Promise<string> {
  const searchDir = q.path ? resolveSafePath(baseDir, q.path) : baseDir
  const tokens = q.args && q.args.length > 0 ? [q.query, ...q.args] : splitCommandArgs(q.query)
  const subcommand = tokens[0] ?? ''
  const rest = tokens.slice(1)
  if (!subcommand) return 'Error: empty git query'
  const res = await runReadOnlyGit(searchDir, subcommand, rest, 10_000)
  if (res.exitCode !== 0) {
    return `git error: ${stripAnsi(res.stderr || res.stdout).trim()}`
  }
  const out = stripAnsi(res.stdout).trim()
  return out || '(empty output)'
}

export async function runBatchSearch(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const rawQueries = parseMaybeJsonArray<SearchQuery>(args.queries)
  if (!rawQueries || rawQueries.length === 0) {
    return { text: 'batch_search requires a non-empty queries array (1..20 items).', isError: true }
  }
  if (rawQueries.length > 20) {
    return { text: `batch_search accepts at most 20 queries per call (got ${rawQueries.length}).`, isError: true }
  }

  const ws = resolveWorkspacePaths(typeof args.cwd === 'string' ? args.cwd : undefined)
  const baseDir = ws.cwd

  const results = await Promise.all(
    rawQueries.map(async (q, i): Promise<BudgetSection> => {
      const label = q.id || `q${i + 1}`
      const header = `### [${label}] ${q.type}: ${q.query}${q.path ? ` (in ${q.path})` : ''}`
      try {
        let body = ''
        switch (q.type) {
          case 'text':
          case 'regex':
            body = await runTextOrRegexQuery(q, baseDir)
            break
          case 'glob':
            body = await runGlobQuery(q, baseDir)
            break
          case 'fuzzy':
            body = await runFuzzyQuery(q, baseDir)
            break
          case 'git':
            body = await runGitQuery(q, baseDir)
            break
          default:
            body = `Unsupported query type: ${String((q as { type?: unknown }).type)}`
        }
        return { header, body }
      } catch (err) {
        return {
          header,
          body: `Query failed: ${err instanceof Error ? err.message : String(err)}`,
        }
      }
    }),
  )

  return {
    text: fitSectionsToByteBudget(
      results,
      maxBytes,
      `batch_search: ${results.length} query result(s) in ${toPosixPath(baseDir)}`,
    ),
  }
}

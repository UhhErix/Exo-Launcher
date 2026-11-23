import fs from 'node:fs'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  fitSectionsToByteBudget,
} from '../utils/budget.js'
import {
  resolveCacheDir,
  resolveCargoHome,
  resolveWorkspacePaths,
  toPosixPath,
} from '../utils/paths.js'

interface RuneSymbol {
  kind: 'fn' | 'struct' | 'enum' | 'trait' | 'type' | 'const' | 'impl'
  name: string
  owner: string | null
  crateName: string
  file: string
  line: number
  signature: string
  doc: string
}

interface RuneApiCache {
  schemaVersion: number
  rev: string
  checkoutDir: string
  symbolCount: number
  symbols: RuneSymbol[]
}

const CACHE_SCHEMA_VERSION = 1
const memoryCache = new Map<string, RuneApiCache>()

function resolvePinnedRuneRev(launcherRoot: string): string | null {
  const cargoToml = path.join(launcherRoot, 'native-ui', 'Cargo.toml')
  if (fs.existsSync(cargoToml)) {
    const text = fs.readFileSync(cargoToml, 'utf8')
    const m = /rune\s*=\s*\{[^}]*\brev\s*=\s*"([0-9a-f]{7,40})"/i.exec(text)
    if (m) return m[1]!
  }

  const cargoLock = path.join(launcherRoot, 'native-ui', 'Cargo.lock')
  if (fs.existsSync(cargoLock)) {
    const text = fs.readFileSync(cargoLock, 'utf8')
    const m = /name\s*=\s*"rune"[\s\S]{0,400}?source\s*=\s*"git\+[^"#]+#([0-9a-f]{7,40})"/i.exec(text)
    if (m) return m[1]!
  }

  return null
}

function findRuneCheckoutDir(rev: string): string | null {
  const checkoutsRoot = path.join(resolveCargoHome(), 'git', 'checkouts')
  if (!fs.existsSync(checkoutsRoot)) return null

  const short7 = rev.slice(0, 7).toLowerCase()
  const revLow = rev.toLowerCase()
  const runeDirs = fs
    .readdirSync(checkoutsRoot, { withFileTypes: true })
    .filter((d) => d.isDirectory() && d.name.toLowerCase().startsWith('rune-'))
    .map((d) => path.join(checkoutsRoot, d.name))

  for (const runeDir of runeDirs) {
    let revDirs: fs.Dirent[]
    try {
      revDirs = fs.readdirSync(runeDir, { withFileTypes: true })
    } catch {
      continue
    }
    for (const sub of revDirs) {
      if (!sub.isDirectory()) continue
      const subLow = sub.name.toLowerCase()
      if (subLow === revLow || revLow.startsWith(subLow) || subLow.startsWith(short7)) {
        return path.join(runeDir, sub.name)
      }
    }
  }
  return null
}

function collectRustFiles(checkoutDir: string): string[] {
  const files: string[] = []
  const skipDirs = new Set(['target', '.git', 'tests', 'benches', 'examples', 'node_modules'])
  const walk = (dir: string): void => {
    let entries: fs.Dirent[]
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true })
    } catch {
      return
    }
    for (const entry of entries) {
      if (entry.isDirectory()) {
        if (skipDirs.has(entry.name)) continue
        walk(path.join(dir, entry.name))
      } else if (entry.isFile() && entry.name.endsWith('.rs')) {
        files.push(path.join(dir, entry.name))
      }
    }
  }
  walk(checkoutDir)
  return files.sort()
}

function inferCrateName(relFile: string): string {
  const m = /^crates\/([^/]+)\//.exec(relFile)
  if (m) return m[1]!
  return 'rune'
}

function extractImplOwner(header: string): string | null {
  let clean = header
    .replace(/\bwhere\b[\s\S]*$/, '')
    .replace(/\{[\s\S]*$/, '')
    .trim()
  clean = clean.replace(/^unsafe\s+/, '').replace(/^impl\b/, '').trim()
  if (clean.startsWith('<')) {
    let depth = 0
    let end = -1
    for (let i = 0; i < clean.length; i++) {
      if (clean[i] === '<') depth++
      else if (clean[i] === '>') {
        depth--
        if (depth === 0) {
          end = i
          break
        }
      }
    }
    if (end >= 0) clean = clean.slice(end + 1).trim()
  }
  const forIdx = clean.indexOf(' for ')
  if (forIdx >= 0) {
    clean = clean.slice(forIdx + 5).trim()
  }
  const typeMatch = /([A-Za-z_][A-Za-z0-9_:]*)/.exec(clean)
  if (!typeMatch) return null
  const raw = typeMatch[1]!
  const parts = raw.split('::')
  return parts[parts.length - 1] ?? raw
}

function countBracesOutsideStrings(line: string): { open: number; close: number } {
  let open = 0
  let close = 0
  let inStr = false
  let inChar = false
  for (let i = 0; i < line.length; i++) {
    const ch = line[i]!
    const next = line[i + 1]
    if (!inStr && !inChar && ch === '/' && next === '/') break
    if (inStr) {
      if (ch === '\\') i++
      else if (ch === '"') inStr = false
      continue
    }
    if (inChar) {
      if (ch === '\\') i++
      else if (ch === "'") inChar = false
      continue
    }
    if (ch === '"') {
      inStr = true
    } else if (ch === "'" && (line[i + 2] === "'" || (next === '\\' && line[i + 3] === "'"))) {
      inChar = true
    } else if (ch === '{') {
      open++
    } else if (ch === '}') {
      close++
    }
  }
  return { open, close }
}

function collectSignature(lines: string[], startIdx: number, stopChars: string): string {
  const parts: string[] = []
  for (let i = startIdx; i < Math.min(lines.length, startIdx + 12); i++) {
    const stripped = lines[i]!.replace(/\/\/[^\n]*$/, '').trim()
    if (!stripped) continue
    parts.push(stripped)
    if (stopChars.split('').some((c) => stripped.includes(c))) break
  }
  let joined = parts.join(' ').replace(/\s+/g, ' ').trim()
  const braceIdx = joined.indexOf('{')
  if (braceIdx >= 0) joined = joined.slice(0, braceIdx).trim()
  const semiIdx = joined.indexOf(';')
  if (semiIdx >= 0 && !joined.includes('(')) joined = joined.slice(0, semiIdx).trim()
  return joined.slice(0, 220)
}

function parseRustFileSymbols(absFile: string, checkoutDir: string): RuneSymbol[] {
  const text = fs.readFileSync(absFile, 'utf8')
  const relFile = toPosixPath(path.relative(checkoutDir, absFile))
  const crateName = inferCrateName(relFile)
  const lines = text.split(/\r?\n/)
  const symbols: RuneSymbol[] = []

  let braceDepth = 0
  const implStack: { owner: string; depth: number }[] = []
  let pendingDoc: string[] = []

  for (let i = 0; i < lines.length; i++) {
    const rawLine = lines[i]!
    const trimmed = rawLine.trim()

    if (trimmed.startsWith('///')) {
      const docLine = trimmed.slice(3).trim()
      if (docLine && pendingDoc.length < 2) pendingDoc.push(docLine)
      continue
    }

    if (trimmed.startsWith('#[')) {
      continue
    }

    const docSummary = pendingDoc.join(' ').slice(0, 140)
    if (trimmed.length > 0) {
      pendingDoc = []
    }

    const currentOwner = implStack.length > 0 ? implStack[implStack.length - 1]!.owner : null

    const implMatch = /^(?:unsafe\s+)?impl(?:\s*<[^>]*>)?\s+/.exec(trimmed)
    if (implMatch) {
      const sig = collectSignature(lines, i, '{')
      const owner = extractImplOwner(sig)
      if (owner) {
        symbols.push({
          kind: 'impl',
          name: owner,
          owner: null,
          crateName,
          file: relFile,
          line: i + 1,
          signature: sig,
          doc: docSummary,
        })
        const braces = countBracesOutsideStrings(rawLine)
        const targetDepth = braceDepth + 1
        implStack.push({ owner, depth: targetDepth })
        braceDepth = Math.max(0, braceDepth + braces.open - braces.close)
        while (implStack.length > 0 && braceDepth < implStack[implStack.length - 1]!.depth) {
          implStack.pop()
        }
        continue
      }
    }

    const fnMatch = /^pub\s+(?:(const|async|unsafe)\s+)*fn\s+([A-Za-z_]\w*)\b/.exec(trimmed)
    if (fnMatch) {
      const name = fnMatch[2]!
      const sig = collectSignature(lines, i, '{;')
      symbols.push({
        kind: 'fn',
        name,
        owner: currentOwner,
        crateName,
        file: relFile,
        line: i + 1,
        signature: sig,
        doc: docSummary,
      })
    } else {
      const itemMatch = /^pub\s+(struct|enum|trait|type|const)\s+([A-Za-z_]\w*)\b/.exec(trimmed)
      if (itemMatch) {
        const kind = itemMatch[1] as 'struct' | 'enum' | 'trait' | 'type' | 'const'
        const name = itemMatch[2]!
        const sig = collectSignature(lines, i, '{;=')
        symbols.push({
          kind,
          name,
          owner: currentOwner,
          crateName,
          file: relFile,
          line: i + 1,
          signature: sig,
          doc: docSummary,
        })
      }
    }

    const braces = countBracesOutsideStrings(rawLine)
    braceDepth = Math.max(0, braceDepth + braces.open - braces.close)
    while (implStack.length > 0 && braceDepth < implStack[implStack.length - 1]!.depth) {
      implStack.pop()
    }
  }

  return symbols
}

function loadOrBuildRuneIndex(rev: string, refresh: boolean): RuneApiCache | { error: string } {
  const shortRev = rev.slice(0, 12).toLowerCase()
  if (!refresh && memoryCache.has(shortRev)) {
    return memoryCache.get(shortRev)!
  }

  const cacheDir = resolveCacheDir()
  const cacheFile = path.join(cacheDir, `rune-api-${shortRev}.json`)

  if (!refresh && fs.existsSync(cacheFile)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(cacheFile, 'utf8')) as RuneApiCache
      if (
        parsed &&
        parsed.schemaVersion === CACHE_SCHEMA_VERSION &&
        parsed.rev === rev &&
        Array.isArray(parsed.symbols)
      ) {
        memoryCache.set(shortRev, parsed)
        return parsed
      }
    } catch {}
  }

  const checkoutDir = findRuneCheckoutDir(rev)
  if (!checkoutDir) {
    return {
      error: `Pinned Rune rev ${rev} not found under ${path.join(resolveCargoHome(), 'git', 'checkouts', 'rune-*')}.`,
    }
  }

  const rsFiles = collectRustFiles(checkoutDir)
  const symbols: RuneSymbol[] = []
  for (const file of rsFiles) {
    try {
      symbols.push(...parseRustFileSymbols(file, checkoutDir))
    } catch {}
  }

  const index: RuneApiCache = {
    schemaVersion: CACHE_SCHEMA_VERSION,
    rev,
    checkoutDir,
    symbolCount: symbols.length,
    symbols,
  }

  memoryCache.set(shortRev, index)
  try {
    fs.mkdirSync(cacheDir, { recursive: true })
    const tmpFile = `${cacheFile}.${process.pid}.tmp`
    fs.writeFileSync(tmpFile, JSON.stringify(index), 'utf8')
    fs.renameSync(tmpFile, cacheFile)
  } catch {}

  return index
}

function scoreSymbol(sym: RuneSymbol, qLow: string, ownerLow: string): number {
  const nameLow = sym.name.toLowerCase()
  const sigLow = sym.signature.toLowerCase()
  const ownLow = (sym.owner ?? '').toLowerCase()

  if (ownerLow && !ownLow.includes(ownerLow) && !(sym.kind === 'struct' && nameLow.includes(ownerLow))) {
    return -1
  }
  if (!qLow) {
    return sym.kind === 'fn' ? 20 : 10
  }

  let score = -1
  if (nameLow === qLow) score = 1000
  else if (nameLow.startsWith(qLow)) score = 700 - (nameLow.length - qLow.length)
  else if (nameLow.includes(qLow)) score = 500 - (nameLow.length - qLow.length)
  else if (ownLow === qLow) score = 400
  else if (sigLow.includes(qLow)) score = 250
  else if (sym.doc.toLowerCase().includes(qLow)) score = 100

  if (score < 0) return -1
  if (sym.kind === 'fn') score += 40
  if (sym.kind === 'impl') score -= 30
  if (sym.crateName === 'rune-core' || sym.crateName === 'rune') score += 15
  return score
}

export async function runExoRuneApi(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const ws = resolveWorkspacePaths(typeof args.cwd === 'string' ? args.cwd : undefined)
  if (!ws.launcherRoot) {
    return {
      text: `Not an Exo Launcher workspace (native-ui/Cargo.toml not found from ${ws.cwd}).`,
      isError: true,
    }
  }

  const rev = resolvePinnedRuneRev(ws.launcherRoot)
  if (!rev) {
    return {
      text: 'Could not determine pinned Rune rev from apps/launcher/native-ui/Cargo.toml or Cargo.lock.',
      isError: true,
    }
  }

  const refresh = args.refresh === true
  const loaded = loadOrBuildRuneIndex(rev, refresh)
  if ('error' in loaded) {
    return { text: loaded.error, isError: true }
  }

  const query = typeof args.query === 'string' ? args.query.trim() : ''
  const owner = typeof args.owner === 'string' ? args.owner.trim() : ''
  const kind = typeof args.kind === 'string' ? args.kind.trim().toLowerCase() : 'all'
  const crateFilter = typeof args.crate === 'string' ? args.crate.trim().toLowerCase() : ''
  const limit =
    typeof args.limit === 'number' && Number.isFinite(args.limit)
      ? Math.max(1, Math.min(80, Math.floor(args.limit)))
      : 25

  if (!query && !owner) {
    return {
      text: 'exo_rune_api requires at least "query" or "owner".',
      isError: true,
    }
  }

  const qLow = query.toLowerCase()
  const ownerLow = owner.toLowerCase()
  const scored: { sym: RuneSymbol; score: number }[] = []

  for (const sym of loaded.symbols) {
    if (kind !== 'all' && sym.kind !== kind) continue
    if (crateFilter && sym.crateName.toLowerCase() !== crateFilter) continue
    const score = scoreSymbol(sym, qLow, ownerLow)
    if (score >= 0) scored.push({ sym, score })
  }

  scored.sort((a, b) => b.score - a.score || a.sym.file.localeCompare(b.sym.file) || a.sym.line - b.sym.line)

  const header = `exo_rune_api rev=${rev.slice(0, 7)} (${loaded.symbolCount} indexed symbols, ${scored.length} match(es) for query="${query}"${owner ? ` owner="${owner}"` : ''})`
  if (scored.length === 0) {
    return { text: `${header}\n(0 matches)` }
  }

  const lines = scored.slice(0, limit).map(({ sym }) => {
    const ownerTag = sym.owner ? ` [${sym.owner}]` : ''
    const docTag = sym.doc ? ` — ${sym.doc}` : ''
    return `${sym.file}:${sym.line}${ownerTag} | ${sym.signature}${docTag}`
  })
  if (scored.length > limit) {
    lines.push(`... (+${scored.length - limit} more matches)`)
  }

  return {
    text: fitSectionsToByteBudget([{ header, body: lines.join('\n') }], maxBytes),
  }
}

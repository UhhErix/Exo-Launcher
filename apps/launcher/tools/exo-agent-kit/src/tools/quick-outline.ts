import fs from 'node:fs'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  fitSectionsToByteBudget,
  type BudgetSection,
} from '../utils/budget.js'
import {
  parseMaybeJsonArray,
  resolveSafePath,
  resolveWorkspacePaths,
  toPosixPath,
} from '../utils/paths.js'

interface OutlineSymbol {
  kind: string
  name: string
  startLine: number
  endLine: number
  depth: number
  signature: string
}

interface PendingBlock {
  sym: OutlineSymbol
  openDepth: number
  sawBrace: boolean
}

function stripStringsAndComments(
  line: string,
  inBlockComment: boolean,
): { code: string; inBlockComment: boolean } {
  let out = ''
  let i = 0
  let inBlock = inBlockComment
  let quote: string | null = null

  while (i < line.length) {
    const ch = line[i]!
    const next = line[i + 1]

    if (inBlock) {
      if (ch === '*' && next === '/') {
        inBlock = false
        i += 2
      } else {
        i++
      }
      continue
    }

    if (quote) {
      if (ch === '\\' && quote !== '`') {
        i += 2
        continue
      }
      if (ch === quote) quote = null
      i++
      continue
    }

    if (ch === '/' && next === '/') break
    if (ch === '/' && next === '*') {
      inBlock = true
      i += 2
      continue
    }
    if (ch === '"' || ch === "'" || ch === '`') {
      quote = ch
      out += ' '
      i++
      continue
    }

    out += ch
    i++
  }

  return { code: out, inBlockComment: inBlock }
}

function countBraces(code: string): { open: number; close: number } {
  let open = 0
  let close = 0
  for (let i = 0; i < code.length; i++) {
    if (code[i] === '{') open++
    else if (code[i] === '}') close++
  }
  return { open, close }
}

const CS_KEYWORDS = new Set([
  'if',
  'for',
  'foreach',
  'while',
  'switch',
  'catch',
  'using',
  'lock',
  'fixed',
  'return',
  'throw',
  'new',
  'else',
  'try',
  'finally',
  'where',
  'when',
  'get',
  'set',
  'init',
  'add',
  'remove',
  'yield',
  'case',
])

function outlineCSharp(lines: string[]): OutlineSymbol[] {
  const symbols: OutlineSymbol[] = []
  const stack: PendingBlock[] = []
  let braceDepth = 0
  let inBlockComment = false

  const nsRx = /^namespace\s+([A-Za-z_][\w.]*)/
  const typeRx =
    /^(?:(?:public|private|internal|protected|static|abstract|sealed|partial|readonly|ref|file|new)\s+)*(class|struct|record(?:\s+(?:class|struct))?|interface|enum)\s+([A-Za-z_]\w*(?:<[^>]+>)?)/
  const delegateRx =
    /^(?:(?:public|private|internal|protected|new)\s+)*delegate\s+[\w<>?.\[\],\s]+\s+([A-Za-z_]\w*)\s*\(/
  const methodRx =
    /^(?:(?:public|private|internal|protected|static|async|virtual|override|abstract|sealed|partial|extern|unsafe|new|readonly)\s+)*(?:[\w<>?.\[\],\(\)\s]+?\s+)?([A-Za-z_]\w*(?:<[^>]+>)?)\s*\([^;]*$/
  const propRx =
    /^(?:(?:public|private|internal|protected|static|virtual|override|abstract|sealed|new|required|readonly)\s+)+[\w<>?.\[\],\(\)]+\s+([A-Za-z_]\w*)\s*(?:\{|=>)/
  const eventRx =
    /^(?:(?:public|private|internal|protected|static|virtual|override|abstract|sealed|new)\s+)*event\s+[\w<>?.\[\],]+\s+([A-Za-z_]\w*)/

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i]!
    const { code, inBlockComment: nextBlock } = stripStringsAndComments(raw, inBlockComment)
    inBlockComment = nextBlock
    const trimmed = code.trim()
    const braces = countBraces(code)
    const lineNum = i + 1

    let created: OutlineSymbol | null = null
    if (trimmed && !trimmed.startsWith('[') && !trimmed.startsWith('#')) {
      const mNs = nsRx.exec(trimmed)
      if (mNs) {
        created = {
          kind: 'namespace',
          name: mNs[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else {
        const mType = typeRx.exec(trimmed)
        if (mType) {
          created = {
            kind: mType[1]!.replace(/\s+/g, ' '),
            name: mType[2]!,
            startLine: lineNum,
            endLine: lineNum,
            depth: stack.length,
            signature: raw.trim().slice(0, 120),
          }
        } else {
          const mDel = delegateRx.exec(trimmed)
          const mEv = !mDel ? eventRx.exec(trimmed) : null
          const mProp = !mDel && !mEv ? propRx.exec(trimmed) : null
          const mMethod = !mDel && !mEv && !mProp ? methodRx.exec(trimmed) : null

          if (mDel) {
            created = {
              kind: 'delegate',
              name: mDel[1]!,
              startLine: lineNum,
              endLine: lineNum,
              depth: stack.length,
              signature: raw.trim().slice(0, 120),
            }
          } else if (mEv) {
            created = {
              kind: 'event',
              name: mEv[1]!,
              startLine: lineNum,
              endLine: lineNum,
              depth: stack.length,
              signature: raw.trim().slice(0, 120),
            }
          } else if (mProp && !CS_KEYWORDS.has(mProp[1]!)) {
            created = {
              kind: 'prop',
              name: mProp[1]!,
              startLine: lineNum,
              endLine: lineNum,
              depth: stack.length,
              signature: raw.trim().slice(0, 120),
            }
          } else if (mMethod) {
            const name = mMethod[1]!
            const baseName = name.replace(/<.*$/, '')
            const looksLikeDecl =
              !CS_KEYWORDS.has(baseName) &&
              !/^(?:await|return|throw|yield|var)\b/.test(trimmed) &&
              !trimmed.includes('=') &&
              (trimmed.includes('=>') ||
                trimmed.endsWith('{') ||
                trimmed.endsWith(')') ||
                /^(?:public|private|internal|protected|static|async|virtual|override|abstract|sealed|partial)\b/.test(
                  trimmed,
                ))
            const isExprMethod =
              !CS_KEYWORDS.has(baseName) &&
              trimmed.includes('=>') &&
              /^(?:public|private|internal|protected|static|async|virtual|override|abstract|sealed|partial|[A-Za-z_][\w<>?.\[\]]*\s+)/.test(
                trimmed,
              )
            if (looksLikeDecl || isExprMethod) {
              created = {
                kind: 'method',
                name,
                startLine: lineNum,
                endLine: lineNum,
                depth: stack.length,
                signature: raw.trim().slice(0, 120),
              }
            }
          }
        }
      }
    }

    if (created) {
      symbols.push(created)
      const isSingleLineSemi = trimmed.endsWith(';') && braces.open === 0
      const isClosedSameLine = braces.open > 0 && braces.open === braces.close
      if (!isSingleLineSemi && !isClosedSameLine) {
        stack.push({
          sym: created,
          openDepth: braceDepth,
          sawBrace: braces.open > 0,
        })
      }
    }

    braceDepth = Math.max(0, braceDepth + braces.open - braces.close)
    for (const item of stack) {
      if (braces.open > 0 && braceDepth > item.openDepth) item.sawBrace = true
    }
    while (stack.length > 0) {
      const top = stack[stack.length - 1]!
      if (top.sawBrace && braceDepth <= top.openDepth) {
        top.sym.endLine = lineNum
        stack.pop()
      } else if (!top.sawBrace && trimmed.endsWith(';')) {
        top.sym.endLine = lineNum
        stack.pop()
      } else {
        break
      }
    }
  }

  for (const rem of stack) {
    rem.sym.endLine = lines.length
  }
  return symbols
}

function outlineRust(lines: string[]): OutlineSymbol[] {
  const symbols: OutlineSymbol[] = []
  const stack: PendingBlock[] = []
  let braceDepth = 0
  let inBlockComment = false

  const vis = '(?:pub(?:\\s*\\([^)]*\\))?\\s+)?'
  const modRx = new RegExp(`^${vis}mod\\s+([A-Za-z_]\\w*)`)
  const implRx = /^(?:unsafe\s+)?impl(?:\s*<[^>]*>)?\s+(.+?)(?:\s+where\b|\s*\{|$)/
  const fnRx = new RegExp(`^${vis}(?:(?:const|async|unsafe|extern(?:\\s+"[^"]+")?)\\s+)*fn\\s+([A-Za-z_]\\w*)`)
  const itemRx = new RegExp(`^${vis}(struct|enum|trait|type|const|static)\\s+([A-Za-z_]\\w*)`)
  const macroRx = /^macro_rules!\s+([A-Za-z_]\w*)/

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i]!
    const { code, inBlockComment: nextBlock } = stripStringsAndComments(raw, inBlockComment)
    inBlockComment = nextBlock
    const trimmed = code.trim()
    const braces = countBraces(code)
    const lineNum = i + 1

    let created: OutlineSymbol | null = null
    if (trimmed && !trimmed.startsWith('#[')) {
      const mMod = modRx.exec(trimmed)
      const mImpl = !mMod ? implRx.exec(trimmed) : null
      const mFn = !mMod && !mImpl ? fnRx.exec(trimmed) : null
      const mItem = !mMod && !mImpl && !mFn ? itemRx.exec(trimmed) : null
      const mMac = !mMod && !mImpl && !mFn && !mItem ? macroRx.exec(trimmed) : null

      if (mMod) {
        created = {
          kind: 'mod',
          name: mMod[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mImpl) {
        created = {
          kind: 'impl',
          name: mImpl[1]!.replace(/\{.*$/, '').trim().slice(0, 80),
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mFn) {
        created = {
          kind: 'fn',
          name: mFn[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mItem) {
        created = {
          kind: mItem[1]!,
          name: mItem[2]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mMac) {
        created = {
          kind: 'macro',
          name: mMac[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      }
    }

    if (created) {
      symbols.push(created)
      const isSingleLineSemi = trimmed.endsWith(';') && braces.open === 0
      const isClosedSameLine = braces.open > 0 && braces.open === braces.close
      if (!isSingleLineSemi && !isClosedSameLine) {
        stack.push({
          sym: created,
          openDepth: braceDepth,
          sawBrace: braces.open > 0,
        })
      }
    }

    braceDepth = Math.max(0, braceDepth + braces.open - braces.close)
    for (const item of stack) {
      if (braces.open > 0 && braceDepth > item.openDepth) item.sawBrace = true
    }
    while (stack.length > 0) {
      const top = stack[stack.length - 1]!
      if (top.sawBrace && braceDepth <= top.openDepth) {
        top.sym.endLine = lineNum
        stack.pop()
      } else if (!top.sawBrace && trimmed.endsWith(';')) {
        top.sym.endLine = lineNum
        stack.pop()
      } else {
        break
      }
    }
  }

  for (const rem of stack) {
    rem.sym.endLine = lines.length
  }
  return symbols
}

function outlineTsJs(lines: string[]): OutlineSymbol[] {
  const symbols: OutlineSymbol[] = []
  const stack: PendingBlock[] = []
  let braceDepth = 0
  let inBlockComment = false

  const fnRx = /^(?:export\s+(?:default\s+)?)?(?:declare\s+)?(?:async\s+)?function\*?\s+([A-Za-z_$][\w$]*)/
  const classRx = /^(?:export\s+(?:default\s+)?)?(?:declare\s+)?(?:abstract\s+)?class\s+([A-Za-z_$][\w$]*)/
  const ifaceRx = /^(?:export\s+)?(?:declare\s+)?(interface|type|enum)\s+([A-Za-z_$][\w$]*)/
  const varRx = /^(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\(|function\b|[A-Za-z_$][\w$]*\s*=>|\{)/
  const memberMethodRx =
    /^(?:(?:public|private|protected|static|async|readonly|override|get|set)\s+)*([A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*\([^;]*\)\s*(?::[^{=>]+)?\{/
  const objFnPropRx = /^([A-Za-z_$][\w$]*)\s*:\s*(?:async\s*)?\([^)]*\)\s*=>/

  const jsSkip = new Set(['if', 'for', 'while', 'switch', 'catch', 'return', 'else', 'try', 'finally'])

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i]!
    const { code, inBlockComment: nextBlock } = stripStringsAndComments(raw, inBlockComment)
    inBlockComment = nextBlock
    const trimmed = code.trim()
    const braces = countBraces(code)
    const lineNum = i + 1

    let created: OutlineSymbol | null = null
    if (trimmed) {
      const mFn = fnRx.exec(trimmed)
      const mClass = !mFn ? classRx.exec(trimmed) : null
      const mIface = !mFn && !mClass ? ifaceRx.exec(trimmed) : null
      const mVar = !mFn && !mClass && !mIface && braceDepth === 0 ? varRx.exec(trimmed) : null
      const parentKind = stack.length > 0 ? stack[stack.length - 1]!.sym.kind : ''
      const mMember =
        !mFn && !mClass && !mIface && !mVar && parentKind === 'class' ? memberMethodRx.exec(trimmed) : null
      const mObjFn =
        !mFn && !mClass && !mIface && !mVar && !mMember && braceDepth === 1
          ? objFnPropRx.exec(trimmed)
          : null

      if (mFn) {
        created = {
          kind: 'fn',
          name: mFn[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mClass) {
        created = {
          kind: 'class',
          name: mClass[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mIface) {
        created = {
          kind: mIface[1]!,
          name: mIface[2]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mVar) {
        created = {
          kind: trimmed.includes('=>') || trimmed.includes('function') ? 'fn' : 'const',
          name: mVar[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mMember && !jsSkip.has(mMember[1]!)) {
        created = {
          kind: 'method',
          name: mMember[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      } else if (mObjFn && !jsSkip.has(mObjFn[1]!)) {
        created = {
          kind: 'prop-fn',
          name: mObjFn[1]!,
          startLine: lineNum,
          endLine: lineNum,
          depth: stack.length,
          signature: raw.trim().slice(0, 120),
        }
      }
    }

    if (created) {
      symbols.push(created)
      const isClosedSameLine = braces.open > 0 && braces.open === braces.close
      if (braces.open > 0 && !isClosedSameLine) {
        stack.push({
          sym: created,
          openDepth: braceDepth,
          sawBrace: true,
        })
      }
    }

    braceDepth = Math.max(0, braceDepth + braces.open - braces.close)
    while (stack.length > 0) {
      const top = stack[stack.length - 1]!
      if (top.sawBrace && braceDepth <= top.openDepth) {
        top.sym.endLine = lineNum
        stack.pop()
      } else {
        break
      }
    }
  }

  for (const rem of stack) {
    rem.sym.endLine = lines.length
  }
  return symbols
}

function outlineFallback(lines: string[], ext: string): OutlineSymbol[] {
  const symbols: OutlineSymbol[] = []
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!
    const trimmed = line.trim()
    if (ext === '.md') {
      const m = /^(#{1,4})\s+(.+)$/.exec(trimmed)
      if (m) {
        symbols.push({
          kind: `h${m[1]!.length}`,
          name: m[2]!.trim(),
          startLine: i + 1,
          endLine: i + 1,
          depth: m[1]!.length - 1,
          signature: trimmed.slice(0, 120),
        })
      }
    } else if (ext === '.py') {
      const m = /^(\s*)(class|def|async\s+def)\s+([A-Za-z_]\w*)/.exec(line)
      if (m) {
        symbols.push({
          kind: m[2]!.replace(/\s+/g, ' '),
          name: m[3]!,
          startLine: i + 1,
          endLine: i + 1,
          depth: Math.floor(m[1]!.length / 4),
          signature: trimmed.slice(0, 120),
        })
      }
    } else if (ext === '.ps1' || ext === '.psm1') {
      const m = /^\s*function\s+([A-Za-z_][\w-]*)/i.exec(line)
      if (m) {
        symbols.push({
          kind: 'function',
          name: m[1]!,
          startLine: i + 1,
          endLine: i + 1,
          depth: 0,
          signature: trimmed.slice(0, 120),
        })
      }
    }
  }
  return symbols
}

export function extractOutline(absPath: string): { lineCount: number; symbols: OutlineSymbol[] } {
  const text = fs.readFileSync(absPath, 'utf8')
  const lines = text.split(/\r?\n/)
  const ext = path.extname(absPath).toLowerCase()
  let symbols: OutlineSymbol[]
  if (ext === '.cs') {
    symbols = outlineCSharp(lines)
  } else if (ext === '.rs') {
    symbols = outlineRust(lines)
  } else if (['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs'].includes(ext)) {
    symbols = outlineTsJs(lines)
  } else {
    symbols = outlineFallback(lines, ext)
  }
  return { lineCount: lines.length, symbols }
}

export async function runQuickOutline(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const paths = parseMaybeJsonArray<string>(args.paths)
  if (!paths || paths.length === 0) {
    return { text: 'quick_outline requires a non-empty paths array (1..20 files).', isError: true }
  }
  if (paths.length > 20) {
    return { text: `quick_outline accepts at most 20 files per call (got ${paths.length}).`, isError: true }
  }

  let filterRx: RegExp | null = null
  if (typeof args.filter === 'string' && args.filter.trim()) {
    try {
      filterRx = new RegExp(args.filter.trim(), 'i')
    } catch (err) {
      return {
        text: `Invalid filter regex: ${err instanceof Error ? err.message : String(err)}`,
        isError: true,
      }
    }
  }

  const maxSymbolsPerFile =
    typeof args.maxSymbolsPerFile === 'number' && Number.isFinite(args.maxSymbolsPerFile)
      ? Math.max(5, Math.min(200, Math.floor(args.maxSymbolsPerFile)))
      : 80

  const ws = resolveWorkspacePaths(typeof args.cwd === 'string' ? args.cwd : undefined)
  const sections: BudgetSection[] = []

  for (const rawPath of paths) {
    const abs = resolveSafePath(ws.cwd, rawPath)
    const rel = toPosixPath(path.relative(ws.repoRoot, abs))
    if (!fs.existsSync(abs)) {
      sections.push({ header: `### ${rel}`, body: '(file not found)' })
      continue
    }
    const stat = fs.statSync(abs)
    if (!stat.isFile()) {
      sections.push({ header: `### ${rel}`, body: '(not a file)' })
      continue
    }

    const { lineCount, symbols } = extractOutline(abs)
    const filtered = filterRx
      ? symbols.filter((s) => filterRx!.test(s.name) || filterRx!.test(s.signature))
      : symbols
    const shown = filtered.slice(0, maxSymbolsPerFile)
    const bodyLines = shown.map((s) => {
      const indent = '  '.repeat(Math.min(4, s.depth))
      const range = s.endLine > s.startLine ? `L${s.startLine}-${s.endLine}` : `L${s.startLine}`
      return `${indent}[${range}] ${s.kind} ${s.name}`
    })
    if (filtered.length > shown.length) {
      bodyLines.push(`... (+${filtered.length - shown.length} more symbols)`)
    }
    sections.push({
      header: `### ${rel} (${lineCount} lines, ${filtered.length} symbol${filtered.length === 1 ? '' : 's'})`,
      body: bodyLines.length > 0 ? bodyLines.join('\n') : '(no matching symbols)',
    })
  }

  return {
    text: fitSectionsToByteBudget(
      sections,
      maxBytes,
      `quick_outline: ${paths.length} file(s)${filterRx ? ` filter=/${filterRx.source}/i` : ''}`,
    ),
  }
}

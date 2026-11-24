import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const APP_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
export const RATCHET_PATH = path.join(APP_ROOT, 'tools', 'comments-ratchet.json')

let tsApi = null
try {
  const tsMod = await import('../ui/node_modules/typescript/lib/typescript.js')
  tsApi = tsMod.default ?? tsMod
} catch {}

const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.mjs', '.cjs', '.cs', '.css', '.ps1', '.psm1', '.rs', '.c', '.cc', '.cpp', '.h'])
const EXCLUDED_PREFIXES = [
  'mouse/vendor/',
  'capture/third_party/',
  'ExoLauncher/wwwroot/',
  'ui/dist/',
  'mouse/dist/',
  'node_modules/',
  'native-ui/target/',
  'voice/build/',
  'voice/out/',
  'voice/vcpkg/',
  'voice/downloads/',
  'capture/out/',
  'shaders/engine/third_party/',
]
const EXCLUDED_SEGMENTS = ['/node_modules/', '/bin/', '/obj/', '/target/']
const REGEX_PREFIX_KEYWORDS = new Set([
  'return', 'throw', 'case', 'delete', 'void', 'typeof', 'instanceof', 'in', 'of',
  'await', 'yield', 'new', 'else', 'do', 'export', 'default', 'extends', 'implements',
])
const JS_BLOCK_PREFIX_WORDS = new Set([
  'catch', 'try', 'finally', 'else', 'do', 'return', 'export', 'import', 'type', 'interface', 'default', 'await', 'yield',
])
const DIRECTIVE_PATTERNS = [
  /\bGenerated from\b/,
  /\beslint-(disable|enable)\b/,
  /^\s*\/\*\s*(eslint|global)\b/,
  /@ts-(expect-error|ignore|nocheck)\b/,
  /^\s*\/\/\/\s*<reference\b/,
  /\bdesign-disable-next-line\b/,
  /\b(v8|c8|istanbul)\s+ignore\b/,
  /\bSPDX-License-Identifier:/,
  /^\s*(?:\/\/\/?|\/\*+|\*+|#)\s*Copyright\b/i,
  /\bLicensed under\b/i,
  /^#!/,
  /^#Requires\b/i,
]

export function isAllowedComment(raw, startLine = 1) {
  const body = String(raw ?? '')
  for (let i = 0; i < DIRECTIVE_PATTERNS.length; i++) {
    const pattern = DIRECTIVE_PATTERNS[i]
    if (pattern.source === '^#!' && startLine !== 1) continue
    if (pattern.test(body)) return true
  }
  return false
}

export function isExcludedPath(relPath) {
  const norm = relPath.replaceAll('\\', '/')
  for (const prefix of EXCLUDED_PREFIXES) {
    if (norm.startsWith(prefix)) return true
  }
  for (const segment of EXCLUDED_SEGMENTS) {
    if (norm.includes(segment)) return true
  }
  return false
}

function buildLineStarts(text) {
  const starts = [0]
  for (let i = 0; i < text.length; i++) {
    if (text.charCodeAt(i) === 10) starts.push(i + 1)
  }
  return starts
}

function lineAtOffset(starts, offset) {
  let lo = 0
  let hi = starts.length - 1
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (starts[mid] <= offset) lo = mid + 1
    else hi = mid - 1
  }
  return hi + 1
}

function isIdentStart(code) {
  return (code >= 65 && code <= 90)
    || (code >= 97 && code <= 122)
    || code === 95
    || code === 36
}

function isIdentPart(code) {
  return isIdentStart(code) || (code >= 48 && code <= 57)
}

function prevSignificantContext(text, beforePos) {
  let i = beforePos - 1
  while (i >= 0 && (text[i] === ' ' || text[i] === '\t' || text[i] === '\r' || text[i] === '\n')) i--
  if (i < 0) return { char: '', two: '', word: '' }
  const char = text[i]
  const two = i > 0 ? text.slice(i - 1, i + 1) : char
  let word = ''
  if (isIdentPart(text.charCodeAt(i))) {
    let start = i
    while (start > 0 && isIdentPart(text.charCodeAt(start - 1))) start--
    word = text.slice(start, i + 1)
  }
  return { char, two, word }
}

function maybeExpandJsxCommentSpan(text, ext, commentStart, commentEnd) {
  if (ext !== '.tsx' && ext !== '.jsx') return { start: commentStart, end: commentEnd, isJsx: false }
  let left = commentStart - 1
  while (left >= 0 && (text[left] === ' ' || text[left] === '\t' || text[left] === '\r' || text[left] === '\n')) left--
  if (left < 0 || text[left] !== '{') return { start: commentStart, end: commentEnd, isJsx: false }
  let right = commentEnd
  while (right < text.length && (text[right] === ' ' || text[right] === '\t' || text[right] === '\r' || text[right] === '\n')) right++
  if (right >= text.length || text[right] !== '}') return { start: commentStart, end: commentEnd, isJsx: false }
  const ctx = prevSignificantContext(text, left)
  if (ctx.two === '=>') return { start: commentStart, end: commentEnd, isJsx: false }
  if (['=', ':', ',', '(', '[', '?', '&', '|', '{', ';', ')'].includes(ctx.char)) {
    return { start: commentStart, end: commentEnd, isJsx: false }
  }
  if (ctx.word && JS_BLOCK_PREFIX_WORDS.has(ctx.word)) {
    return { start: commentStart, end: commentEnd, isJsx: false }
  }
  return { start: left, end: right + 1, isJsx: true }
}

function scanQuoted(text, i, quote, allowBackslash) {
  const n = text.length
  i++
  while (i < n) {
    const ch = text[i]
    if (allowBackslash && ch === '\\') {
      if (text[i + 1] === '\r' && text[i + 2] === '\n') { i += 3; continue }
      i += 2
      continue
    }
    if (ch === quote) return i + 1
    if (ch === '\n' || ch === '\r') return i
    i++
  }
  return n
}

function scanRegex(text, i) {
  const n = text.length
  let j = i + 1
  let inClass = false
  while (j < n) {
    const ch = text[j]
    if (ch === '\r' || ch === '\n') return -1
    if (ch === '\\') {
      j += 2
      continue
    }
    if (ch === '[' && !inClass) {
      inClass = true
      j++
      continue
    }
    if (ch === ']' && inClass) {
      inClass = false
      j++
      continue
    }
    if (ch === '/' && !inClass) {
      j++
      while (j < n && /[a-zA-Z]/.test(text[j])) j++
      return j
    }
    j++
  }
  return -1
}

function scanJsWithTs(text, ext, filePath, starts) {
  const kindMap = {
    '.tsx': tsApi.ScriptKind.TSX,
    '.jsx': tsApi.ScriptKind.JSX,
    '.ts': tsApi.ScriptKind.TS,
    '.mts': tsApi.ScriptKind.TS,
    '.cts': tsApi.ScriptKind.TS,
  }
  const sf = tsApi.createSourceFile(filePath, text, tsApi.ScriptTarget.Latest, true, kindMap[ext] ?? tsApi.ScriptKind.JS)
  const prot = []
  const jsxEmpty = []
  const literalKinds = new Set([
    tsApi.SyntaxKind.StringLiteral,
    tsApi.SyntaxKind.NoSubstitutionTemplateLiteral,
    tsApi.SyntaxKind.TemplateHead,
    tsApi.SyntaxKind.TemplateMiddle,
    tsApi.SyntaxKind.TemplateTail,
    tsApi.SyntaxKind.RegularExpressionLiteral,
    tsApi.SyntaxKind.JsxText,
    tsApi.SyntaxKind.JsxTextAllWhiteSpaces,
  ])

  const walk = (node) => {
    if (literalKinds.has(node.kind)) {
      prot.push([node.getStart(sf), node.getEnd()])
      return
    }
    if (node.kind === tsApi.SyntaxKind.JsxExpression && !node.expression) {
      jsxEmpty.push([node.getStart(sf), node.getEnd()])
    }
    tsApi.forEachChild(node, walk)
  }
  walk(sf)
  prot.sort((a, b) => a[0] - b[0])

  const comments = []
  let i = 0
  if (text.startsWith('#!')) {
    const nl = text.indexOf('\n')
    const end = nl < 0 ? text.length : nl
    comments.push({ start: 0, end, stripStart: 0, stripEnd: end, raw: text.slice(0, end), kind: 'line', startLine: 1, endLine: 1 })
    i = end
  }

  let p = 0
  while (i < text.length) {
    while (p < prot.length && prot[p][1] <= i) p++
    if (p < prot.length && i >= prot[p][0]) {
      i = prot[p][1]
      continue
    }
    const bound = p < prot.length ? prot[p][0] : text.length
    if (text[i] === '/' && text[i + 1] === '/' && i + 1 < bound) {
      const start = i
      i += 2
      while (i < bound && text[i] !== '\r' && text[i] !== '\n') i++
      const line = lineAtOffset(starts, start)
      comments.push({
        start,
        end: i,
        stripStart: start,
        stripEnd: i,
        raw: text.slice(start, i),
        kind: text.slice(start, start + 3) === '///' ? 'doc' : 'line',
        startLine: line,
        endLine: line,
      })
      continue
    }
    if (text[i] === '/' && text[i + 1] === '*' && i + 1 < bound) {
      const start = i
      const close = text.indexOf('*/', i + 2)
      const end = close < 0 || close + 2 > bound ? bound : close + 2
      let stripStart = start
      let stripEnd = end
      let isJsx = false
      for (const [jStart, jEnd] of jsxEmpty) {
        if (start >= jStart && end <= jEnd) {
          stripStart = jStart
          stripEnd = jEnd
          isJsx = true
          break
        }
      }
      comments.push({
        start,
        end,
        stripStart,
        stripEnd,
        raw: text.slice(start, end),
        kind: isJsx ? 'jsx' : 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, end - 1)),
      })
      i = end
      continue
    }
    i++
  }

  return comments
}

function scanJsLike(text, ext, starts) {
  const n = text.length
  const comments = []
  const templateStack = []
  let braceDepth = 0
  let canBeRegex = true
  let i = 0

  if (text.startsWith('#!')) {
    const nl = text.indexOf('\n')
    const end = nl < 0 ? n : nl
    comments.push({ start: 0, end, stripStart: 0, stripEnd: end, raw: text.slice(0, end), kind: 'line', startLine: 1, endLine: 1 })
    i = end
  }

  const enterTemplate = (pos) => {
    let j = pos
    while (j < n) {
      const ch = text[j]
      if (ch === '\\') {
        j += 2
        continue
      }
      if (ch === '$' && text[j + 1] === '{') {
        templateStack.push(braceDepth)
        braceDepth++
        canBeRegex = true
        return j + 2
      }
      if (ch === '`') {
        canBeRegex = false
        return j + 1
      }
      j++
    }
    return n
  }

  while (i < n) {
    const ch = text[i]
    if (ch === ' ' || ch === '\t' || ch === '\r' || ch === '\n') {
      i++
      continue
    }
    if (ch === '\'' || ch === '"') {
      if (ch === '\'' && (ext === '.tsx' || ext === '.jsx') && i > 0 && i + 1 < n && isIdentPart(text.charCodeAt(i - 1)) && isIdentPart(text.charCodeAt(i + 1))) {
        i++
        continue
      }
      i = scanQuoted(text, i, ch, true)
      canBeRegex = false
      continue
    }
    if (ch === '`') {
      i = enterTemplate(i + 1)
      continue
    }
    if (ch === '/' && text[i + 1] === '/') {
      const start = i
      i += 2
      while (i < n && text[i] !== '\r' && text[i] !== '\n') i++
      const line = lineAtOffset(starts, start)
      comments.push({
        start,
        end: i,
        stripStart: start,
        stripEnd: i,
        raw: text.slice(start, i),
        kind: text.slice(start, start + 3) === '///' ? 'doc' : 'line',
        startLine: line,
        endLine: line,
      })
      continue
    }
    if (ch === '/' && text[i + 1] === '*') {
      const start = i
      const close = text.indexOf('*/', i + 2)
      const end = close < 0 ? n : close + 2
      const span = maybeExpandJsxCommentSpan(text, ext, start, end)
      comments.push({
        start,
        end,
        stripStart: span.start,
        stripEnd: span.end,
        raw: text.slice(start, end),
        kind: span.isJsx ? 'jsx' : 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, end - 1)),
      })
      i = end
      continue
    }
    if (ch === '/') {
      if (canBeRegex) {
        const regexEnd = scanRegex(text, i)
        if (regexEnd > 0) {
          i = regexEnd
          canBeRegex = false
          continue
        }
      }
      i += text[i + 1] === '=' ? 2 : 1
      canBeRegex = true
      continue
    }
    if (ch === '{') {
      braceDepth++
      canBeRegex = true
      i++
      continue
    }
    if (ch === '}') {
      braceDepth--
      if (templateStack.length > 0 && braceDepth === templateStack[templateStack.length - 1]) {
        templateStack.pop()
        i = enterTemplate(i + 1)
        continue
      }
      canBeRegex = true
      i++
      continue
    }
    if (ch === ')' || ch === ']') {
      canBeRegex = false
      i++
      continue
    }
    if ((ch === '+' && text[i + 1] === '+') || (ch === '-' && text[i + 1] === '-')) {
      i += 2
      continue
    }
    if ('([,;:=?!~&|^*%<>+-'.includes(ch)) {
      canBeRegex = true
      i++
      continue
    }
    if (ch === '.') {
      canBeRegex = text[i + 1] === '.' && text[i + 2] === '.'
      i += canBeRegex ? 3 : 1
      continue
    }
    const code = text.charCodeAt(i)
    if (isIdentStart(code)) {
      const start = i
      i++
      while (i < n && isIdentPart(text.charCodeAt(i))) i++
      const word = text.slice(start, i)
      canBeRegex = REGEX_PREFIX_KEYWORDS.has(word)
      continue
    }
    if (code >= 48 && code <= 57) {
      while (i < n && (isIdentPart(text.charCodeAt(i)) || text[i] === '.')) i++
      canBeRegex = false
      continue
    }
    i++
  }

  return comments
}

function scanCSharp(text, starts) {
  const n = text.length
  const comments = []
  const interpStack = []
  let i = 0

  while (i < n) {
    const ch = text[i]
    if (ch === '#' && (i === 0 || text[i - 1] === '\n')) {
      while (i < n && text[i] !== '\r' && text[i] !== '\n') i++
      continue
    }
    if (ch === '$' || ch === '@') {
      let j = i
      let dollars = 0
      let hasAt = false
      while (j < n && (text[j] === '$' || text[j] === '@')) {
        if (text[j] === '$') dollars++
        else hasAt = true
        j++
      }
      if (text[j] === '"') {
        let quotes = 0
        while (j + quotes < n && text[j + quotes] === '"') quotes++
        if (quotes >= 3 && !hasAt) {
          j += quotes
          while (j < n) {
            if (text[j] === '"') {
              let run = 0
              while (j + run < n && text[j + run] === '"') run++
              j += run
              if (run >= quotes) break
              continue
            }
            j++
          }
          i = j
          continue
        }
        if (hasAt) {
          j++
          while (j < n) {
            if (text[j] === '"') {
              if (text[j + 1] === '"') { j += 2; continue }
              j++
              break
            }
            j++
          }
          i = j
          continue
        }
        if (dollars > 0) {
          j++
          while (j < n) {
            if (text[j] === '\\') { j += 2; continue }
            if (text[j] === '{') {
              if (text[j + 1] === '{') { j += 2; continue }
              interpStack.push({ depth: 1 })
              j++
              break
            }
            if (text[j] === '"') { j++; break }
            j++
          }
          i = j
          continue
        }
      }
    }
    if (ch === '"') {
      let quotes = 0
      while (i + quotes < n && text[i + quotes] === '"') quotes++
      if (quotes >= 3) {
        let j = i + quotes
        while (j < n) {
          if (text[j] === '"') {
            let run = 0
            while (j + run < n && text[j + run] === '"') run++
            j += run
            if (run >= quotes) break
            continue
          }
          j++
        }
        i = j
        continue
      }
      i = scanQuoted(text, i, '"', true)
      continue
    }
    if (ch === '\'') {
      i = scanQuoted(text, i, '\'', true)
      continue
    }
    if (ch === '{' && interpStack.length > 0) {
      interpStack[interpStack.length - 1].depth++
      i++
      continue
    }
    if (ch === '}' && interpStack.length > 0) {
      const top = interpStack[interpStack.length - 1]
      top.depth--
      if (top.depth === 0) {
        interpStack.pop()
        let j = i + 1
        while (j < n) {
          if (text[j] === '\\') { j += 2; continue }
          if (text[j] === '{') {
            if (text[j + 1] === '{') { j += 2; continue }
            interpStack.push({ depth: 1 })
            j++
            break
          }
          if (text[j] === '"') { j++; break }
          j++
        }
        i = j
        continue
      }
      i++
      continue
    }
    if (ch === '/' && text[i + 1] === '/') {
      const start = i
      i += 2
      while (i < n && text[i] !== '\r' && text[i] !== '\n') i++
      const line = lineAtOffset(starts, start)
      comments.push({
        start,
        end: i,
        stripStart: start,
        stripEnd: i,
        raw: text.slice(start, i),
        kind: text.slice(start, start + 3) === '///' ? 'doc' : 'line',
        startLine: line,
        endLine: line,
      })
      continue
    }
    if (ch === '/' && text[i + 1] === '*') {
      const start = i
      const close = text.indexOf('*/', i + 2)
      const end = close < 0 ? n : close + 2
      comments.push({
        start,
        end,
        stripStart: start,
        stripEnd: end,
        raw: text.slice(start, end),
        kind: 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, end - 1)),
      })
      i = end
      continue
    }
    i++
  }

  return comments
}

function scanCss(text, starts) {
  const n = text.length
  const comments = []
  let i = 0
  while (i < n) {
    const ch = text[i]
    if (ch === '\'' || ch === '"') {
      i = scanQuoted(text, i, ch, true)
      continue
    }
    if (ch === '/' && text[i + 1] === '*') {
      const start = i
      const close = text.indexOf('*/', i + 2)
      const end = close < 0 ? n : close + 2
      comments.push({
        start,
        end,
        stripStart: start,
        stripEnd: end,
        raw: text.slice(start, end),
        kind: 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, end - 1)),
      })
      i = end
      continue
    }
    if ((ch === 'u' || ch === 'U') && /^url\(\s*[^'"]/i.test(text.slice(i, i + 12))) {
      let j = i + 4
      while (j < n && text[j] !== ')' && text[j] !== '\n') j++
      i = j < n ? j + 1 : j
      continue
    }
    i++
  }
  return comments
}

function scanPowerShell(text, starts) {
  const n = text.length
  const comments = []
  let i = 0
  while (i < n) {
    const ch = text[i]
    if (ch === '@' && (text[i + 1] === '"' || text[i + 1] === '\'')) {
      const quote = text[i + 1]
      const lineEnd = text.indexOf('\n', i + 2)
      if (lineEnd >= 0 && /^[\t \r]*$/.test(text.slice(i + 2, lineEnd))) {
        let j = lineEnd + 1
        while (j < n) {
          const nextNl = text.indexOf('\n', j)
          const rowEnd = nextNl < 0 ? n : nextNl
          const row = text.slice(j, rowEnd).replace(/\r$/, '')
          if (row === `${quote}@` || row.startsWith(`${quote}@`)) {
            j = rowEnd < n ? rowEnd + 1 : n
            break
          }
          j = rowEnd < n ? rowEnd + 1 : n
        }
        i = j
        continue
      }
    }
    if (ch === '\'') {
      i++
      while (i < n) {
        if (text[i] === '\'') {
          if (text[i + 1] === '\'') { i += 2; continue }
          i++
          break
        }
        i++
      }
      continue
    }
    if (ch === '"') {
      i++
      while (i < n) {
        if (text[i] === '`') { i += 2; continue }
        if (text[i] === '"') {
          if (text[i + 1] === '"') { i += 2; continue }
          i++
          break
        }
        i++
      }
      continue
    }
    if (ch === '<' && text[i + 1] === '#') {
      const start = i
      const close = text.indexOf('#>', i + 2)
      const end = close < 0 ? n : close + 2
      comments.push({
        start,
        end,
        stripStart: start,
        stripEnd: end,
        raw: text.slice(start, end),
        kind: 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, end - 1)),
      })
      i = end
      continue
    }
    if (ch === '#' && (i === 0 || /[\s;({|&,=]/.test(text[i - 1]))) {
      const start = i
      while (i < n && text[i] !== '\r' && text[i] !== '\n') i++
      const line = lineAtOffset(starts, start)
      comments.push({
        start,
        end: i,
        stripStart: start,
        stripEnd: i,
        raw: text.slice(start, i),
        kind: 'line',
        startLine: line,
        endLine: line,
      })
      continue
    }
    i++
  }
  return comments
}

function scanCStyle(text, starts) {
  const n = text.length
  const comments = []
  let i = 0
  while (i < n) {
    const ch = text[i]
    if (ch === '"' || ch === '\'') {
      i = scanQuoted(text, i, ch, true)
      continue
    }
    if (ch === '/' && text[i + 1] === '/') {
      const start = i
      i += 2
      while (i < n && text[i] !== '\r' && text[i] !== '\n') i++
      const line = lineAtOffset(starts, start)
      comments.push({
        start,
        end: i,
        stripStart: start,
        stripEnd: i,
        raw: text.slice(start, i),
        kind: text.slice(start, start + 3) === '///' ? 'doc' : 'line',
        startLine: line,
        endLine: line,
      })
      continue
    }
    if (ch === '/' && text[i + 1] === '*') {
      const start = i
      const close = text.indexOf('*/', i + 2)
      const end = close < 0 ? n : close + 2
      comments.push({
        start,
        end,
        stripStart: start,
        stripEnd: end,
        raw: text.slice(start, end),
        kind: 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, end - 1)),
      })
      i = end
      continue
    }
    i++
  }
  return comments
}

function scanRust(text, starts) {
  const n = text.length
  const comments = []
  let i = 0
  while (i < n) {
    const ch = text[i]
    if ((ch === 'r' || (ch === 'b' && text[i + 1] === 'r')) && (i === 0 || !isIdentPart(text.charCodeAt(i - 1)))) {
      let j = ch === 'b' ? i + 2 : i + 1
      let hashes = 0
      while (j < n && text[j] === '#') { hashes++; j++ }
      if (j < n && text[j] === '"') {
        j++
        const closeToken = '"' + '#'.repeat(hashes)
        const closeIdx = text.indexOf(closeToken, j)
        i = closeIdx < 0 ? n : closeIdx + closeToken.length
        continue
      }
    }
    if (ch === '"') {
      i++
      while (i < n) {
        if (text[i] === '\\') { i += 2; continue }
        if (text[i] === '"') { i++; break }
        i++
      }
      continue
    }
    if (ch === '\'') {
      if (i + 1 < n && text[i + 1] === '\\') {
        const end = text.indexOf('\'', i + 2)
        i = end >= 0 && end - i <= 12 ? end + 1 : i + 1
        continue
      }
      if (i + 2 < n && text[i + 2] === '\'') {
        i += 3
        continue
      }
      i++
      continue
    }
    if (ch === '/' && text[i + 1] === '/') {
      const start = i
      i += 2
      while (i < n && text[i] !== '\r' && text[i] !== '\n') i++
      const line = lineAtOffset(starts, start)
      comments.push({
        start,
        end: i,
        stripStart: start,
        stripEnd: i,
        raw: text.slice(start, i),
        kind: text.slice(start, start + 3) === '///' || text.slice(start, start + 3) === '//!' ? 'doc' : 'line',
        startLine: line,
        endLine: line,
      })
      continue
    }
    if (ch === '/' && text[i + 1] === '*') {
      const start = i
      let depth = 1
      let j = i + 2
      while (j < n && depth > 0) {
        if (text[j] === '/' && text[j + 1] === '*') { depth++; j += 2; continue }
        if (text[j] === '*' && text[j + 1] === '/') { depth--; j += 2; continue }
        j++
      }
      comments.push({
        start,
        end: j,
        stripStart: start,
        stripEnd: j,
        raw: text.slice(start, j),
        kind: 'block',
        startLine: lineAtOffset(starts, start),
        endLine: lineAtOffset(starts, Math.max(start, j - 1)),
      })
      i = j
      continue
    }
    i++
  }
  return comments
}

export function scanComments(text, filePath, options = {}) {
  const ext = path.extname(filePath).toLowerCase()
  const starts = buildLineStarts(text)
  let rawComments
  if (ext === '.cs') rawComments = scanCSharp(text, starts)
  else if (ext === '.css') rawComments = scanCss(text, starts)
  else if (ext === '.ps1' || ext === '.psm1') rawComments = scanPowerShell(text, starts)
  else if (ext === '.rs') rawComments = scanRust(text, starts)
  else if (ext === '.c' || ext === '.cc' || ext === '.cpp' || ext === '.h') rawComments = scanCStyle(text, starts)
  else if (SOURCE_EXTENSIONS.has(ext)) rawComments = tsApi && !options.fallback ? scanJsWithTs(text, ext, filePath, starts) : scanJsLike(text, ext, starts)
  else return []

  return rawComments.map((c) => ({
    ...c,
    allowed: isAllowedComment(c.raw, c.startLine),
  }))
}

export function analyzeFile(text, filePath) {
  const comments = scanComments(text, filePath)
  const disallowed = comments.filter((c) => !c.allowed)
  const lines = new Set()
  const fullOnlyLines = new Set()
  const xmlDocLines = new Set()
  const inlineLines = new Set()

  const textLines = text.split(/\r?\n/)
  const lineStartOffsets = buildLineStarts(text)

  for (const c of disallowed) {
    for (let ln = c.startLine; ln <= c.endLine; ln++) {
      lines.add(ln)
      const row = textLines[ln - 1] ?? ''
      const rowStart = lineStartOffsets[ln - 1]
      const beforeInLine = ln === c.startLine ? text.slice(rowStart, c.stripStart) : ''
      const afterInLine = ln === c.endLine ? text.slice(c.stripEnd, rowStart + row.length) : ''
      if (beforeInLine.trim() === '' && afterInLine.trim() === '') {
        fullOnlyLines.add(ln)
        if (c.kind === 'doc') xmlDocLines.add(ln)
      } else {
        inlineLines.add(ln)
      }
    }
  }

  return {
    totalLoc: textLines.length,
    commentLineCount: lines.size,
    fullLineCount: fullOnlyLines.size,
    xmlDocLineCount: xmlDocLines.size,
    inlineOnlyLineCount: [...inlineLines].filter((ln) => !fullOnlyLines.has(ln)).length,
    firstLine: lines.size ? Math.min(...lines) : 0,
    disallowed,
  }
}

function lineBoundsAt(starts, text, line) {
  const start = starts[line - 1]
  const nextStart = line < starts.length ? starts[line] : text.length
  let contentEnd = nextStart
  if (contentEnd > start && text[contentEnd - 1] === '\n') contentEnd--
  if (contentEnd > start && text[contentEnd - 1] === '\r') contentEnd--
  return { start, contentEnd, nextStart }
}

export function stripComments(text, filePath) {
  const { disallowed } = analyzeFile(text, filePath)
  if (!disallowed.length) return text

  const lineEnding = text.includes('\r\n') ? '\r\n' : '\n'
  const starts = buildLineStarts(text)
  const sorted = [...disallowed].sort((a, b) => a.stripStart - b.stripStart)
  const regions = []
  let prevEnd = 0

  for (let idx = 0; idx < sorted.length; idx++) {
    const c = sorted[idx]
    if (c.stripStart < prevEnd) continue
    let delStart = c.stripStart
    let delEnd = c.stripEnd
    let endLine = c.endLine

    while (idx + 1 < sorted.length) {
      const next = sorted[idx + 1]
      const gap = text.slice(delEnd, next.stripStart)
      if (/^[ \t\r\n]*$/.test(gap) && lineAtOffset(starts, delEnd) >= next.startLine - 1 && lineAtOffset(starts, delEnd) <= next.startLine && !gap.includes('\n\n') && !gap.includes('\r\n\r\n')) {
        const curEndBounds = lineBoundsAt(starts, text, endLine)
        const nextStartBounds = lineBoundsAt(starts, text, next.startLine)
        const curHasAfter = text.slice(delEnd, curEndBounds.contentEnd).trim() !== ''
        const nextHasBefore = text.slice(nextStartBounds.start, next.stripStart).trim() !== ''
        if (!curHasAfter && !nextHasBefore) {
          delEnd = next.stripEnd
          endLine = next.endLine
          idx++
          continue
        }
      }
      break
    }

    const firstBounds = lineBoundsAt(starts, text, c.startLine)
    const lastBounds = lineBoundsAt(starts, text, endLine)
    const hasBefore = text.slice(firstBounds.start, delStart).trim() !== ''
    const hasAfter = text.slice(delEnd, lastBounds.contentEnd).trim() !== ''

    let removeWholeLine = false
    if (!hasBefore && !hasAfter) {
      delStart = firstBounds.start
      delEnd = lastBounds.nextStart
      removeWholeLine = true
      if (lastBounds.nextStart === text.length && delStart > 0) {
        if (text[delStart - 1] === '\n') delStart--
        if (delStart > 0 && text[delStart - 1] === '\r') delStart--
      }
    } else if (hasBefore && !hasAfter) {
      while (delStart > firstBounds.start && (text[delStart - 1] === ' ' || text[delStart - 1] === '\t')) delStart--
    } else if (!hasBefore && hasAfter) {
      while (delEnd < lastBounds.contentEnd && (text[delEnd] === ' ' || text[delEnd] === '\t')) delEnd++
    }

    regions.push({ delStart, delEnd, removeWholeLine })
    prevEnd = delEnd
  }

  let out = ''
  let pos = 0
  for (const r of regions) {
    if (r.delStart < pos) continue
    let prefix = text.slice(pos, r.delStart)
    if (r.removeWholeLine) {
      const m = text.slice(r.delEnd).match(/^(?:\r?\n)/)
      const combined = out + prefix
      if (m && (combined === '' || combined.endsWith(`${lineEnding}${lineEnding}`))) {
        r.delEnd += m[0].length
      }
    }
    out += prefix
    pos = r.delEnd
  }
  out += text.slice(pos)
  out = out.replace(/catch\s*\{\s+\}/g, 'catch {}')
  return out
}

function listFilesFallback(dir, base = dir, acc = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name)
    const rel = path.relative(base, full).replaceAll('\\', '/')
    if (entry.isDirectory()) {
      if (!isExcludedPath(rel + '/')) listFilesFallback(full, base, acc)
    } else if (SOURCE_EXTENSIONS.has(path.extname(entry.name).toLowerCase()) && !isExcludedPath(rel)) {
      acc.push(rel)
    }
  }
  return acc
}

export function listTrackedSourceFiles(root = APP_ROOT) {
  try {
    const raw = execFileSync('git', ['-C', root, 'ls-files', '--', '.'], { encoding: 'utf8' })
    return raw
      .split(/\r?\n/)
      .filter(Boolean)
      .map((p) => p.replaceAll('\\', '/'))
      .filter((rel) => SOURCE_EXTENSIONS.has(path.extname(rel).toLowerCase()) && !isExcludedPath(rel))
      .filter((rel) => fs.existsSync(path.join(root, rel)))
      .sort()
  } catch {
    return listFilesFallback(root).sort()
  }
}

export function readRatchet(ratchetPath = RATCHET_PATH) {
  if (!fs.existsSync(ratchetPath)) return { files: {} }
  const data = JSON.parse(fs.readFileSync(ratchetPath, 'utf8'))
  return { files: data.files ?? {} }
}

function writeRatchet(filesMap, ratchetPath = RATCHET_PATH) {
  const sorted = {}
  for (const key of Object.keys(filesMap).sort()) {
    if (filesMap[key] > 0) sorted[key] = filesMap[key]
  }
  fs.writeFileSync(ratchetPath, `${JSON.stringify({ files: sorted }, null, 2)}\n`, 'utf8')
  return sorted
}

export function collectStats(root = APP_ROOT) {
  const files = listTrackedSourceFiles(root)
  const counts = {}
  const perFile = []
  let totalLoc = 0
  let commentLineCount = 0
  let fullLineCount = 0
  let xmlDocLineCount = 0
  let inlineOnlyLineCount = 0
  const byArea = {}

  for (const rel of files) {
    const text = fs.readFileSync(path.join(root, rel), 'utf8')
    const info = analyzeFile(text, rel)
    totalLoc += info.totalLoc
    commentLineCount += info.commentLineCount
    fullLineCount += info.fullLineCount
    xmlDocLineCount += info.xmlDocLineCount
    inlineOnlyLineCount += info.inlineOnlyLineCount
    if (info.commentLineCount > 0) {
      counts[rel] = info.commentLineCount
      perFile.push({ rel, ...info })
    }
    const area = rel.split('/').slice(0, 2).join('/')
    const bucket = byArea[area] ??= { files: 0, totalLoc: 0, commentLines: 0, fullLines: 0, xmlDocLines: 0 }
    bucket.files++
    bucket.totalLoc += info.totalLoc
    bucket.commentLines += info.commentLineCount
    bucket.fullLines += info.fullLineCount
    bucket.xmlDocLines += info.xmlDocLineCount
  }

  return {
    scannedFiles: files.length,
    totalLoc,
    commentLineCount,
    fullLineCount,
    xmlDocLineCount,
    inlineOnlyLineCount,
    counts,
    perFile,
    byArea,
  }
}

export function evaluateRatchet(counts, baselineFiles) {
  const unbaselined = []
  const increased = []
  const stale = []

  for (const [rel, count] of Object.entries(counts)) {
    if (!(rel in baselineFiles)) {
      unbaselined.push({ rel, count })
    } else if (count > baselineFiles[rel]) {
      increased.push({ rel, count, baseline: baselineFiles[rel] })
    }
  }
  for (const [rel, baseline] of Object.entries(baselineFiles)) {
    const current = counts[rel] ?? 0
    if (current < baseline) stale.push({ rel, count: current, baseline })
  }

  return {
    ok: unbaselined.length === 0 && increased.length === 0 && stale.length === 0,
    unbaselined,
    increased,
    stale,
  }
}

export function tightenRatchet(counts, baselineFiles) {
  const refused = []
  const next = {}

  for (const [rel, count] of Object.entries(counts)) {
    if (!(rel in baselineFiles)) {
      refused.push({ rel, reason: 'new-file', count })
    } else if (count > baselineFiles[rel]) {
      refused.push({ rel, reason: 'increased', count, baseline: baselineFiles[rel] })
      next[rel] = baselineFiles[rel]
    } else if (count > 0) {
      next[rel] = count
    }
  }

  return { ok: refused.length === 0, refused, next }
}

function runCli() {
  const args = process.argv.slice(2)
  if (args.includes('--strip')) {
    const targets = args.filter((a) => !a.startsWith('--'))
    for (const target of targets) {
      const abs = path.isAbsolute(target) ? target : path.resolve(APP_ROOT, target)
      const rel = path.relative(APP_ROOT, abs).replaceAll('\\', '/')
      const before = fs.readFileSync(abs, 'utf8')
      const after = stripComments(before, rel)
      if (after !== before) fs.writeFileSync(abs, after, 'utf8')
      const remain = analyzeFile(after, rel).commentLineCount
      console.log(`${rel}: ${remain} comment lines left`)
    }
    return
  }

  const stats = collectStats(APP_ROOT)

  if (args.includes('--stats')) {
    const topIdx = args.indexOf('--top')
    const topN = topIdx >= 0 ? Math.max(1, Number(args[topIdx + 1]) || 20) : 20
    const pct = stats.totalLoc ? ((stats.fullLineCount / stats.totalLoc) * 100).toFixed(2) : '0.00'
    console.log(`Scanned files: ${stats.scannedFiles}, total LOC: ${stats.totalLoc}`)
    console.log(`Remaining comment lines (ratcheted): ${stats.commentLineCount}`)
    console.log(`Sheddable full-line comments: ${stats.fullLineCount} (${pct}% of LOC; includes ${stats.xmlDocLineCount} /// XML doc lines)`)
    console.log(`Inline-only comment lines: ${stats.inlineOnlyLineCount}`)
    console.log('\nBy area:')
    for (const [area, b] of Object.entries(stats.byArea).sort((a, b) => b[1].commentLines - a[1].commentLines)) {
      if (b.commentLines === 0) continue
      console.log(`  ${area.padEnd(28)} files:${String(b.files).padStart(4)}  ratchet:${String(b.commentLines).padStart(6)}  full:${String(b.fullLines).padStart(6)}  xml:${String(b.xmlDocLines).padStart(6)}  loc:${b.totalLoc}`)
    }
    console.log(`\nTop ${topN} files by remaining comment lines:`)
    for (const item of [...stats.perFile].sort((a, b) => b.commentLineCount - a.commentLineCount).slice(0, topN)) {
      console.log(`  ${String(item.commentLineCount).padStart(4)}  ${item.rel}`)
    }
    return
  }

  if (args.includes('--init')) {
    const written = writeRatchet(stats.counts)
    console.log(`Initialized ${path.relative(APP_ROOT, RATCHET_PATH)}: ${Object.keys(written).length} files, ${stats.commentLineCount} comment lines`)
    return
  }

  const ratchet = readRatchet()

  if (args.includes('--update')) {
    const result = tightenRatchet(stats.counts, ratchet.files)
    const written = writeRatchet(result.next)
    for (const r of result.refused) {
      console.error(`REFUSED ${r.rel} (${r.reason}): count=${r.count}${r.baseline != null ? ` baseline=${r.baseline}` : ''}`)
    }
    console.log(`Updated ratchet: ${Object.keys(written).length} files, ${Object.values(written).reduce((a, b) => a + b, 0)} comment lines`)
    if (!result.ok) process.exit(1)
    return
  }

  const check = evaluateRatchet(stats.counts, ratchet.files)
  for (const item of check.unbaselined) {
    const text = fs.readFileSync(path.join(APP_ROOT, item.rel), 'utf8')
    const firstLine = analyzeFile(text, item.rel).firstLine
    console.error(`${item.rel}:${firstLine}: ${item.count} comment line(s) not allowed in unbaselined/cleaned files`)
  }
  for (const item of check.increased) {
    const text = fs.readFileSync(path.join(APP_ROOT, item.rel), 'utf8')
    const firstLine = analyzeFile(text, item.rel).firstLine
    console.error(`${item.rel}:${firstLine}: comment lines increased (${item.baseline} -> ${item.count})`)
  }
  for (const item of check.stale) {
    console.error(`${item.rel}: stale ratchet entry (${item.baseline} -> ${item.count}); run node tools/check-comments.mjs --update`)
  }
  if (!check.ok) process.exit(1)
  console.log(`Comment ratchet ok: ${Object.keys(ratchet.files).length} legacy files (${stats.commentLineCount} comment lines), ${stats.scannedFiles - Object.keys(ratchet.files).length} zero-comment files`)
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runCli()
}

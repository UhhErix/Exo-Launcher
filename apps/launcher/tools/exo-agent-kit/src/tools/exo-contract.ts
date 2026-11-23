import fs from 'node:fs'
import path from 'node:path'
import type { ToolResult } from '../protocol/mcp.js'
import {
  clampByteBudget,
  DEFAULT_INLINE_MAX_BYTES,
  fitSectionsToByteBudget,
  type BudgetSection,
} from '../utils/budget.js'
import { resolveWorkspacePaths, toPosixPath } from '../utils/paths.js'

interface TsMethodInfo {
  wrapperName: string
  params: string
  file: string
  line: number
}

interface TsEventInfo {
  typeText: string
  file: string
  line: number
}

interface CsArmInfo {
  method: string
  expression: string
  file: string
  line: number
  handlerName: string | null
  handlerLoc: { file: string; line: number; signature: string } | null
}

interface RustMethodInfo {
  constName: string | null
  constLoc: string | null
  clientFns: { name: string; signature: string; file: string; line: number }[]
}

interface RustEventInfo {
  refs: { file: string; line: number; text: string }[]
}

interface ContractIndex {
  stamp: string
  version: number
  methods: Record<string, string>
  events: Record<string, string>
  tsMethods: Map<string, TsMethodInfo[]>
  tsEvents: Map<string, TsEventInfo>
  csArms: Map<string, CsArmInfo>
  csEventEmitters: Map<string, { file: string; line: number; snippet: string }[]>
  rustMethods: Map<string, RustMethodInfo>
  rustEvents: Map<string, RustEventInfo>
}

let cachedIndex: ContractIndex | null = null

function buildLineStarts(text: string): number[] {
  const starts = [0]
  for (let i = 0; i < text.length; i++) {
    if (text.charCodeAt(i) === 10) starts.push(i + 1)
  }
  return starts
}

function lineAtOffset(starts: number[], offset: number): number {
  let lo = 0
  let hi = starts.length - 1
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (starts[mid]! <= offset) lo = mid + 1
    else hi = mid - 1
  }
  return hi + 1
}

function balancedRange(text: string, open: number): { start: number; end: number; body: string } | null {
  const pairs: Record<string, string> = { '{': '}', '(': ')', '[': ']' }
  const stack: string[] = []
  for (let i = open; i < text.length; i++) {
    const ch = text[i]!
    if (ch === '/' && text[i + 1] === '/') {
      const nl = text.indexOf('\n', i)
      if (nl < 0) return null
      i = nl
      continue
    }
    if (ch === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2)
      if (end < 0) return null
      i = end + 1
      continue
    }
    if (ch === '"' || ch === "'") {
      for (i++; i < text.length && text[i] !== ch; i++) {
        if (text[i] === '\\') i++
      }
      continue
    }
    const close = pairs[ch]
    if (close) {
      stack.push(close)
    } else if (stack.length > 0 && ch === stack[stack.length - 1]) {
      stack.pop()
      if (stack.length === 0) {
        return { start: open + 1, end: i, body: text.slice(open + 1, i) }
      }
    }
  }
  return null
}

function splitTopWithOffsets(body: string, baseOffset: number): { text: string; offset: number }[] {
  const out: { text: string; offset: number }[] = []
  let depth = 0
  let start = 0
  for (let i = 0; i < body.length; i++) {
    const ch = body[i]!
    if (ch === '/' && body[i + 1] === '/') {
      const end = body.indexOf('\n', i)
      i = end < 0 ? body.length : end
      continue
    }
    if (ch === '/' && body[i + 1] === '*') {
      const end = body.indexOf('*/', i + 2)
      i = end < 0 ? body.length : end + 1
      continue
    }
    if (ch === '"' || ch === "'") {
      for (i++; i < body.length && body[i] !== ch; i++) {
        if (body[i] === '\\') i++
      }
      continue
    }
    if (ch === '(' || ch === '{' || ch === '[') depth++
    else if (ch === ')' || ch === '}' || ch === ']') depth--
    if (ch === ',' && depth === 0) {
      out.push({ text: body.slice(start, i), offset: baseOffset + start })
      start = i + 1
    }
  }
  if (start < body.length) {
    out.push({ text: body.slice(start), offset: baseOffset + start })
  }
  return out
}

function stripLineComments(s: string): string {
  return s.replace(/\/\/[^\n]*/g, '').trim()
}

function parseTsHost(launcherRoot: string): {
  tsMethods: Map<string, TsMethodInfo[]>
  tsEvents: Map<string, TsEventInfo>
} {
  const tsMethods = new Map<string, TsMethodInfo[]>()
  const tsEvents = new Map<string, TsEventInfo>()
  const relPath = 'apps/launcher/ui/src/lib/host.ts'
  const absPath = path.join(launcherRoot, 'ui', 'src', 'lib', 'host.ts')
  if (!fs.existsSync(absPath)) return { tsMethods, tsEvents }

  const text = fs.readFileSync(absPath, 'utf8')
  const starts = buildLineStarts(text)

  const eventIdx = text.indexOf('export interface HostEventMap')
  if (eventIdx >= 0) {
    const open = text.indexOf('{', eventIdx)
    const block = open >= 0 ? balancedRange(text, open) : null
    if (block) {
      const lines = block.body.split(/\r?\n/)
      let offset = block.start
      for (const rawLine of lines) {
        const trimmed = rawLine.trim()
        const m = /^['"]([^'"]+)['"]\s*:\s*(.+?)\s*;?$/.exec(trimmed)
        if (m) {
          tsEvents.set(m[1]!, {
            typeText: m[2]!.trim(),
            file: relPath,
            line: lineAtOffset(starts, offset),
          })
        }
        offset += rawLine.length + 1
      }
    }
  }

  const hostIdx = text.indexOf('export const host = {')
  if (hostIdx >= 0) {
    const open = text.indexOf('{', hostIdx)
    const block = open >= 0 ? balancedRange(text, open) : null
    if (block) {
      const entries = splitTopWithOffsets(block.body, block.start)
      for (const entry of entries) {
        const cleaned = entry.text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '')
        const propMatch = /^\s*([A-Za-z_]\w*)\s*:\s*(?:async\s*)?\(([\s\S]*?)\)\s*=>/.exec(cleaned)
        if (!propMatch) continue
        const wrapperName = propMatch[1]!
        const params = propMatch[2]!.replace(/\s+/g, ' ').trim()
        const propOffset = entry.offset + Math.max(0, entry.text.indexOf(wrapperName))
        const line = lineAtOffset(starts, propOffset)

        const callMatches = cleaned.matchAll(/rawCall(?:<[\s\S]*?>)?\s*\(([\s\S]*?)(?:,|\))/g)
        const foundMethods = new Set<string>()
        for (const cm of callMatches) {
          const argExpr = cm[1] ?? ''
          for (const strMatch of argExpr.matchAll(/['"]([a-zA-Z0-9_]+\.[a-zA-Z0-9_.]+)['"]/g)) {
            foundMethods.add(strMatch[1]!)
          }
        }
        for (const methodName of foundMethods) {
          const list = tsMethods.get(methodName) ?? []
          list.push({ wrapperName, params: `(${params})`, file: relPath, line })
          tsMethods.set(methodName, list)
        }
      }
    }
  }

  return { tsMethods, tsEvents }
}

function collectBridgeFiles(launcherRoot: string): string[] {
  const servicesDir = path.join(launcherRoot, 'ExoLauncher', 'Services')
  if (!fs.existsSync(servicesDir)) return []
  const files: string[] = []
  const walk = (dir: string): void => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.isFile() && entry.name.endsWith('.cs')) files.push(full)
    }
  }
  walk(servicesDir)
  return files.sort()
}

function extractPrimaryHandlerName(expression: string): string | null {
  const cleaned = expression.replace(/\s+/g, ' ').trim()
  const taskRun = /Task\.Run\(\s*\(\)\s*=>\s*([A-Za-z_]\w*)\s*\(/.exec(cleaned)
  if (taskRun) return taskRun[1]!
  const direct = /^(?:await\s+)?([A-Za-z_]\w*)\s*\(/.exec(cleaned)
  if (direct && direct[1] !== 'Task' && direct[1] !== 'ValueTask') return direct[1]!
  const member = /(?:await\s+)?(_[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)\s*\(/.exec(cleaned)
  if (member) return member[1]!
  return null
}

function parseCsBridge(
  launcherRoot: string,
  repoRoot: string,
  knownEvents: string[],
): {
  csArms: Map<string, CsArmInfo>
  csEventEmitters: Map<string, { file: string; line: number; snippet: string }[]>
} {
  const csArms = new Map<string, CsArmInfo>()
  const csEventEmitters = new Map<string, { file: string; line: number; snippet: string }[]>()
  const bridgeMain = path.join(launcherRoot, 'ExoLauncher', 'Services', 'WebHostBridge.cs')
  if (!fs.existsSync(bridgeMain)) return { csArms, csEventEmitters }

  const allCsFiles = collectBridgeFiles(launcherRoot)
  const bridgeFiles = allCsFiles.filter((f) => path.basename(f).includes('Bridge'))

  const methodDeclIndex = new Map<string, { file: string; line: number; signature: string }>()
  const declRx =
    /^\s*(?:(?:public|private|internal|protected|static|async|virtual|override|sealed|partial)\s+)+(?:[\w<>?,\s\[\]\(\)]+?)\s+([A-Za-z_]\w*)\s*(?:<[^>]+>)?\s*\(/

  for (const absFile of bridgeFiles) {
    const content = fs.readFileSync(absFile, 'utf8')
    const rel = toPosixPath(path.relative(repoRoot, absFile))
    const lines = content.split(/\r?\n/)
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!
      const m = declRx.exec(line)
      if (m) {
        const name = m[1]!
        if (!methodDeclIndex.has(name)) {
          methodDeclIndex.set(name, {
            file: rel,
            line: i + 1,
            signature: line.trim().slice(0, 140),
          })
        }
      }
    }
  }

  const mainText = fs.readFileSync(bridgeMain, 'utf8')
  const mainRel = toPosixPath(path.relative(repoRoot, bridgeMain))
  const mainStarts = buildLineStarts(mainText)
  const switchStart = mainText.search(/object\?\s+result\s*=\s*method\s+switch\s*\{/)
  if (switchStart >= 0) {
    const open = mainText.indexOf('{', switchStart)
    const block = balancedRange(mainText, open)
    if (block) {
      const arms = splitTopWithOffsets(block.body, block.start)
      for (const arm of arms) {
        const stripped = stripLineComments(arm.text)
        const m = /^((?:"[^"]+"\s*(?:or\s*)?)+)\s*=>\s*([\s\S]+)$/.exec(stripped)
        if (!m) continue
        const expr = m[2]!.replace(/\s+/g, ' ').trim()
        const handlerName = extractPrimaryHandlerName(expr)
        const handlerLoc = handlerName ? (methodDeclIndex.get(handlerName) ?? null) : null
        for (const labelMatch of m[1]!.matchAll(/"([^"]+)"/g)) {
          const label = labelMatch[1]!
          const labelPos = arm.text.indexOf(`"${label}"`)
          const line = lineAtOffset(mainStarts, arm.offset + Math.max(0, labelPos))
          csArms.set(label, {
            method: label,
            expression: expr,
            file: mainRel,
            line,
            handlerName,
            handlerLoc,
          })
        }
      }
    }
  }

  const eventSet = new Set(knownEvents)
  const emitterRx =
    /(?:\w*Event\(\s*"([^"]+)"\s*,|Invoke\(\s*"([^"]+)"\s*,|Publish\(\s*"([^"]+)"\s*,|\.Add\(\(\s*"([^"]+)"\s*,|@event\s*=\s*"([^"]+)")/g

  for (const absFile of allCsFiles) {
    const content = fs.readFileSync(absFile, 'utf8')
    const rel = toPosixPath(path.relative(repoRoot, absFile))
    const lines = content.split(/\r?\n/)
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!
      emitterRx.lastIndex = 0
      let m: RegExpExecArray | null
      while ((m = emitterRx.exec(line)) !== null) {
        const ev = m[1] ?? m[2] ?? m[3] ?? m[4] ?? m[5]
        if (!ev || !eventSet.has(ev)) continue
        const list = csEventEmitters.get(ev) ?? []
        if (list.length < 8) {
          list.push({ file: rel, line: i + 1, snippet: line.trim().slice(0, 120) })
          csEventEmitters.set(ev, list)
        }
      }
    }
  }

  return { csArms, csEventEmitters }
}

function parseRustRpc(
  launcherRoot: string,
  repoRoot: string,
  knownEvents: string[],
): {
  rustMethods: Map<string, RustMethodInfo>
  rustEvents: Map<string, RustEventInfo>
} {
  const rustMethods = new Map<string, RustMethodInfo>()
  const rustEvents = new Map<string, RustEventInfo>()
  const rpcSrcDir = path.join(launcherRoot, 'native-ui', 'crates', 'exo-rpc', 'src')
  if (!fs.existsSync(rpcSrcDir)) return { rustMethods, rustEvents }

  const rsFiles = fs
    .readdirSync(rpcSrcDir)
    .filter((f) => f.endsWith('.rs'))
    .sort()
    .map((f) => path.join(rpcSrcDir, f))

  const constToMethod = new Map<string, string>()

  for (const absFile of rsFiles) {
    const text = fs.readFileSync(absFile, 'utf8')
    const rel = toPosixPath(path.relative(repoRoot, absFile))
    const lines = text.split(/\r?\n/)

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!
      const cm = /^\s*pub\s+const\s+(METHOD_[A-Z0-9_]+)\s*:\s*&str\s*=\s*"([^"]+)"\s*;/.exec(line)
      if (cm) {
        const constName = cm[1]!
        const method = cm[2]!
        constToMethod.set(constName, method)
        const existing = rustMethods.get(method) ?? { constName: null, constLoc: null, clientFns: [] }
        existing.constName = constName
        existing.constLoc = `${rel}:${i + 1}`
        rustMethods.set(method, existing)
      }
    }

    let currentFn: { name: string; signature: string; line: number; body: string } | null = null
    let braceDepth = 0
    let fnStartDepth = -1

    const flushFn = (): void => {
      if (!currentFn) return
      const hits = new Set<string>()
      for (const m of currentFn.body.matchAll(/\b(METHOD_[A-Z0-9_]+)\b/g)) {
        const method = constToMethod.get(m[1]!)
        if (method) hits.add(method)
      }
      for (const m of currentFn.body.matchAll(/"([a-zA-Z0-9_]+\.[a-zA-Z0-9_.]+)"/g)) {
        hits.add(m[1]!)
      }
      for (const method of hits) {
        const existing = rustMethods.get(method) ?? { constName: null, constLoc: null, clientFns: [] }
        if (!existing.clientFns.some((f) => f.name === currentFn!.name && f.line === currentFn!.line)) {
          existing.clientFns.push({
            name: currentFn.name,
            signature: currentFn.signature,
            file: rel,
            line: currentFn.line,
          })
        }
        rustMethods.set(method, existing)
      }
      currentFn = null
      fnStartDepth = -1
    }

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!
      if (!currentFn) {
        const fm = /^\s*pub\s+(?:const\s+|async\s+|unsafe\s+)*fn\s+([A-Za-z_]\w*)\s*\(/.exec(line)
        if (fm) {
          let sig = line.trim()
          let j = i
          while (!sig.includes('{') && j + 1 < lines.length && j - i < 6) {
            j++
            sig += ' ' + lines[j]!.trim()
          }
          const sigClean = sig.replace(/\s*\{[\s\S]*$/, '').replace(/\s+/g, ' ').trim()
          currentFn = { name: fm[1]!, signature: sigClean.slice(0, 140), line: i + 1, body: '' }
          fnStartDepth = braceDepth
        }
      }
      if (currentFn) {
        currentFn.body += line + '\n'
      }
      for (let k = 0; k < line.length; k++) {
        if (line[k] === '{') braceDepth++
        else if (line[k] === '}') {
          braceDepth = Math.max(0, braceDepth - 1)
          if (currentFn && braceDepth <= fnStartDepth && currentFn.body.includes('{')) {
            flushFn()
          }
        }
      }
    }
    flushFn()

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!
      for (const ev of knownEvents) {
        if (line.includes(`"${ev}"`)) {
          const entry = rustEvents.get(ev) ?? { refs: [] }
          if (entry.refs.length < 8) {
            entry.refs.push({ file: rel, line: i + 1, text: line.trim().slice(0, 120) })
            rustEvents.set(ev, entry)
          }
        }
      }
    }
  }

  return { rustMethods, rustEvents }
}

function loadContractIndex(launcherRoot: string, repoRoot: string): ContractIndex {
  const manifestPath = path.join(launcherRoot, 'ui', 'contracts', 'host-v1.json')
  const hostTsPath = path.join(launcherRoot, 'ui', 'src', 'lib', 'host.ts')
  const bridgeCsPath = path.join(launcherRoot, 'ExoLauncher', 'Services', 'WebHostBridge.cs')
  const rpcLibPath = path.join(launcherRoot, 'native-ui', 'crates', 'exo-rpc', 'src', 'lib.rs')

  const stamp = [manifestPath, hostTsPath, bridgeCsPath, rpcLibPath]
    .map((p) => {
      try {
        const st = fs.statSync(p)
        return `${p}:${st.mtimeMs}:${st.size}`
      } catch {
        return `${p}:missing`
      }
    })
    .join('|')

  if (cachedIndex && cachedIndex.stamp === stamp) return cachedIndex

  const parsed = JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as {
    version?: number
    methods?: Record<string, string>
    events?: Record<string, string>
  }
  const methods = parsed.methods ?? {}
  const events = parsed.events ?? {}
  const knownEvents = Object.keys(events)

  const { tsMethods, tsEvents } = parseTsHost(launcherRoot)
  const { csArms, csEventEmitters } = parseCsBridge(launcherRoot, repoRoot, knownEvents)
  const { rustMethods, rustEvents } = parseRustRpc(launcherRoot, repoRoot, knownEvents)

  cachedIndex = {
    stamp,
    version: parsed.version ?? 1,
    methods,
    events,
    tsMethods,
    tsEvents,
    csArms,
    csEventEmitters,
    rustMethods,
    rustEvents,
  }
  return cachedIndex
}

function formatMethodDetail(name: string, idx: ContractIndex): string {
  const replyType = idx.methods[name]
  const tsList = idx.tsMethods.get(name) ?? []
  const cs = idx.csArms.get(name)
  const rust = idx.rustMethods.get(name)

  const lines: string[] = [`METHOD: ${name}`]
  lines.push(`  host-v1.json reply: ${replyType ?? '(not in host-v1.json)'}`)
  if (tsList.length > 0) {
    for (const ts of tsList) {
      lines.push(`  TS host.ts: host.${ts.wrapperName}${ts.params} (${ts.file}:${ts.line})`)
    }
  } else {
    lines.push('  TS host.ts: (no wrapper found)')
  }
  if (cs) {
    lines.push(`  C# dispatch: ${cs.file}:${cs.line} => ${cs.expression}`)
    if (cs.handlerLoc) {
      lines.push(`  C# handler: ${cs.handlerLoc.file}:${cs.handlerLoc.line} | ${cs.handlerLoc.signature}`)
    } else if (cs.handlerName) {
      lines.push(`  C# handler: ${cs.handlerName} (defined outside *Bridge*.cs)`)
    }
  } else {
    lines.push('  C# dispatch: (no dispatcher arm found)')
  }
  if (rust) {
    if (rust.constName && rust.constLoc) {
      lines.push(`  Rust const: ${rust.constName} (${rust.constLoc})`)
    }
    if (rust.clientFns.length > 0) {
      for (const fn of rust.clientFns.slice(0, 4)) {
        lines.push(`  Rust fn: ${fn.file}:${fn.line} | ${fn.signature}`)
      }
    } else {
      lines.push('  Rust fn: (no client wrapper in exo-rpc)')
    }
  } else {
    lines.push('  Rust exo-rpc: (not wired in exo-rpc)')
  }
  return lines.join('\n')
}

function formatEventDetail(name: string, idx: ContractIndex): string {
  const payload = idx.events[name]
  const ts = idx.tsEvents.get(name)
  const emitters = idx.csEventEmitters.get(name) ?? []
  const rust = idx.rustEvents.get(name)

  const lines: string[] = [`EVENT: ${name}`]
  lines.push(`  host-v1.json payload: ${payload ?? '(not in host-v1.json)'}`)
  if (ts) {
    lines.push(`  TS HostEventMap: ${ts.file}:${ts.line} | ${ts.typeText}`)
  } else {
    lines.push('  TS HostEventMap: (not found)')
  }
  if (emitters.length > 0) {
    for (const em of emitters) {
      lines.push(`  C# emitter: ${em.file}:${em.line} | ${em.snippet}`)
    }
  } else {
    lines.push('  C# emitters: (none matched)')
  }
  if (rust && rust.refs.length > 0) {
    for (const r of rust.refs) {
      lines.push(`  Rust exo-rpc: ${r.file}:${r.line} | ${r.text}`)
    }
  } else {
    lines.push('  Rust exo-rpc: (not wired in exo-rpc)')
  }
  return lines.join('\n')
}

export async function runExoContract(args: Record<string, unknown>): Promise<ToolResult> {
  const maxBytes = clampByteBudget(
    typeof args.maxBytes === 'number' ? args.maxBytes : DEFAULT_INLINE_MAX_BYTES,
  )
  const ws = resolveWorkspacePaths(typeof args.cwd === 'string' ? args.cwd : undefined)
  if (!ws.launcherRoot) {
    return {
      text: `Not an Exo Launcher workspace (host-v1.json not found from ${ws.cwd}).`,
      isError: true,
    }
  }

  const idx = loadContractIndex(ws.launcherRoot, ws.repoRoot)
  const method = typeof args.method === 'string' ? args.method.trim() : ''
  const event = typeof args.event === 'string' ? args.event.trim() : ''
  const prefix = typeof args.prefix === 'string' ? args.prefix.trim().toLowerCase() : ''
  const query = typeof args.query === 'string' ? args.query.trim().toLowerCase() : ''
  const limit =
    typeof args.limit === 'number' && Number.isFinite(args.limit)
      ? Math.max(1, Math.min(80, Math.floor(args.limit)))
      : 25

  if (method) {
    const exactExists =
      method in idx.methods || idx.csArms.has(method) || idx.tsMethods.has(method) || idx.rustMethods.has(method)
    if (exactExists) {
      return { text: fitSectionsToByteBudget([{ header: `exo_contract v${idx.version}`, body: formatMethodDetail(method, idx) }], maxBytes) }
    }
    const suggestions = Object.keys(idx.methods)
      .filter((m) => m.toLowerCase().includes(method.toLowerCase()))
      .slice(0, 15)
    return {
      text: `Method "${method}" not found in host-v1.json (${Object.keys(idx.methods).length} methods).${
        suggestions.length ? `\nDid you mean:\n${suggestions.map((s) => `  - ${s}`).join('\n')}` : ''
      }`,
      isError: true,
    }
  }

  if (event) {
    const exactExists = event in idx.events || idx.tsEvents.has(event) || idx.csEventEmitters.has(event)
    if (exactExists) {
      return { text: fitSectionsToByteBudget([{ header: `exo_contract v${idx.version}`, body: formatEventDetail(event, idx) }], maxBytes) }
    }
    const suggestions = Object.keys(idx.events)
      .filter((e) => e.toLowerCase().includes(event.toLowerCase()))
      .slice(0, 15)
    return {
      text: `Event "${event}" not found in host-v1.json (${Object.keys(idx.events).length} events).${
        suggestions.length ? `\nDid you mean:\n${suggestions.map((s) => `  - ${s}`).join('\n')}` : ''
      }`,
      isError: true,
    }
  }

  const allMethods = Object.keys(idx.methods).sort()
  const allEvents = Object.keys(idx.events).sort()
  const matcher = (name: string, typeText: string): boolean => {
    const lowName = name.toLowerCase()
    if (prefix && !lowName.startsWith(prefix)) return false
    if (query && !lowName.includes(query) && !typeText.toLowerCase().includes(query)) return false
    return true
  }

  const matchedMethods = allMethods.filter((m) => matcher(m, idx.methods[m] ?? ''))
  const matchedEvents = allEvents.filter((e) => matcher(e, idx.events[e] ?? ''))

  const sections: BudgetSection[] = []
  if (matchedMethods.length > 0) {
    const bodyLines = matchedMethods.slice(0, limit).map((m) => {
      const cs = idx.csArms.get(m)
      const rust = idx.rustMethods.has(m) ? 'rust' : '-'
      const csTag = cs ? `${cs.file}:${cs.line}` : 'no-cs'
      return `${m} -> ${idx.methods[m]} [${csTag}, ${rust}]`
    })
    if (matchedMethods.length > limit) {
      bodyLines.push(`... (+${matchedMethods.length - limit} more methods)`)
    }
    sections.push({
      header: `Methods (${matchedMethods.length}/${allMethods.length})`,
      body: bodyLines.join('\n'),
    })
  }
  if (matchedEvents.length > 0) {
    const bodyLines = matchedEvents.slice(0, limit).map((e) => {
      const em = idx.csEventEmitters.get(e)?.length ?? 0
      const rust = idx.rustEvents.has(e) ? 'rust' : '-'
      return `${e} -> ${idx.events[e]} [emitters:${em}, ${rust}]`
    })
    if (matchedEvents.length > limit) {
      bodyLines.push(`... (+${matchedEvents.length - limit} more events)`)
    }
    sections.push({
      header: `Events (${matchedEvents.length}/${allEvents.length})`,
      body: bodyLines.join('\n'),
    })
  }

  if (sections.length === 0) {
    return {
      text: `exo_contract: 0 matches for prefix="${prefix}" query="${query}" (${allMethods.length} methods, ${allEvents.length} events total).`,
    }
  }

  return {
    text: fitSectionsToByteBudget(
      sections,
      maxBytes,
      `exo_contract v${idx.version} (${allMethods.length} methods, ${allEvents.length} events)`,
    ),
  }
}

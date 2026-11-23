export const DEFAULT_INLINE_MAX_BYTES = 8800

export const MAX_SNAPSHOT_BYTES = 1500

export function clampByteBudget(requested?: number, hardMax = DEFAULT_INLINE_MAX_BYTES): number {
  if (typeof requested !== 'number' || !Number.isFinite(requested) || requested <= 0) {
    return hardMax
  }
  return Math.max(256, Math.min(Math.floor(requested), hardMax))
}

export function jsonByteLength(text: string): number {
  return Buffer.byteLength(JSON.stringify(text), 'utf8')
}

export function stripAnsi(text: string): string {
  return text.replace(/\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))/g, '')
}

function safeSliceByCodeUnits(text: string, end: number): string {
  if (end <= 0) return ''
  if (end >= text.length) return text
  const last = text.charCodeAt(end - 1)
  if (last >= 0xd800 && last <= 0xdbff) {
    return text.slice(0, end - 1)
  }
  return text.slice(0, end)
}

function safeTailByCodeUnits(text: string, start: number): string {
  if (start <= 0) return text
  if (start >= text.length) return ''
  const first = text.charCodeAt(start)
  if (first >= 0xdc00 && first <= 0xdfff) {
    return text.slice(start + 1)
  }
  return text.slice(start)
}

export function truncateToJsonBytes(
  text: string,
  maxBytes: number,
  marker = '\n[... truncated to byte budget ...]',
): string {
  const budget = Math.max(32, Math.floor(maxBytes))
  if (jsonByteLength(text) <= budget) return text
  const markerBytes = jsonByteLength(marker)
  if (markerBytes >= budget) {
    let lo = 0
    let hi = text.length
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1
      const candidate = safeSliceByCodeUnits(text, mid)
      if (jsonByteLength(candidate) <= budget) lo = mid
      else hi = mid - 1
    }
    return safeSliceByCodeUnits(text, lo)
  }
  let lo = 0
  let hi = text.length
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1
    const candidate = safeSliceByCodeUnits(text, mid) + marker
    if (jsonByteLength(candidate) <= budget) lo = mid
    else hi = mid - 1
  }
  return safeSliceByCodeUnits(text, lo) + marker
}

export function foldHeadTail(raw: string, maxBytes: number): string {
  const text = stripAnsi(raw).replace(/\r\n/g, '\n')
  const budget = Math.max(64, Math.floor(maxBytes))
  if (jsonByteLength(text) <= budget) return text

  const lines = text.split('\n')
  const markerReserve = 96
  const usable = Math.max(32, budget - markerReserve)
  const headBudget = Math.floor(usable * 0.45)
  const tailBudget = usable - headBudget

  const headLines: string[] = []
  let headEndIdx = 0
  while (headEndIdx < lines.length) {
    const next = headLines.length === 0 ? lines[headEndIdx]! : `${headLines.join('\n')}\n${lines[headEndIdx]!}`
    if (jsonByteLength(next) > headBudget) break
    headLines.push(lines[headEndIdx]!)
    headEndIdx++
  }

  const tailLines: string[] = []
  let tailStartIdx = lines.length - 1
  while (tailStartIdx >= headEndIdx) {
    const candidate = tailLines.length === 0
      ? lines[tailStartIdx]!
      : `${lines[tailStartIdx]!}\n${tailLines.join('\n')}`
    if (jsonByteLength(candidate) > tailBudget) break
    tailLines.unshift(lines[tailStartIdx]!)
    tailStartIdx--
  }

  if (headLines.length === 0 && tailLines.length === 0) {
    const headPart = truncateToJsonBytes(text, Math.floor(usable * 0.45), '')
    const tailLen = Math.min(text.length, Math.floor(usable * 0.45))
    let lo = Math.max(headPart.length, text.length - tailLen)
    let hi = text.length
    while (lo < hi) {
      const mid = (lo + hi) >> 1
      const candidate = safeTailByCodeUnits(text, mid)
      if (jsonByteLength(candidate) <= tailBudget) hi = mid
      else lo = mid + 1
    }
    const tailPart = safeTailByCodeUnits(text, hi)
    const omittedBytes = Math.max(0, Buffer.byteLength(text, 'utf8') - Buffer.byteLength(headPart, 'utf8') - Buffer.byteLength(tailPart, 'utf8'))
    const joined = `${headPart}\n[... ${omittedBytes} bytes omitted ...]\n${tailPart}`
    return truncateToJsonBytes(joined, budget)
  }

  const omittedLines = Math.max(0, tailStartIdx - headEndIdx + 1)
  const omittedText = lines.slice(headEndIdx, tailStartIdx + 1).join('\n')
  const omittedBytes = Buffer.byteLength(omittedText, 'utf8')
  const marker = `\n[... ${omittedLines} lines / ${omittedBytes} bytes omitted ...]\n`
  const joined = `${headLines.join('\n')}${marker}${tailLines.join('\n')}`
  return truncateToJsonBytes(joined, budget)
}

export interface BudgetSection {
  header: string
  body: string
}

export function computeWaterFillBudgets(sizes: number[], totalBudget: number): number[] {
  if (sizes.length === 0) return []
  const indexed = sizes.map((size, idx) => ({ size: Math.max(0, size), idx }))
  indexed.sort((a, b) => a.size - b.size)
  const alloc = new Array<number>(sizes.length).fill(0)
  let remaining = Math.max(0, totalBudget)
  for (let i = 0; i < indexed.length; i++) {
    const leftCount = indexed.length - i
    const fairShare = Math.floor(remaining / leftCount)
    const item = indexed[i]!
    const grant = Math.min(item.size, fairShare)
    alloc[item.idx] = grant
    remaining -= grant
  }
  return alloc
}

export function fitSectionsToByteBudget(
  sections: BudgetSection[],
  maxBytes = DEFAULT_INLINE_MAX_BYTES,
  preamble = '',
): string {
  const budget = clampByteBudget(maxBytes)
  if (sections.length === 0) {
    return truncateToJsonBytes(preamble, budget)
  }

  const sep = '\n\n'
  const prefix = preamble ? `${preamble}${sep}` : ''
  const rawFull = prefix + sections.map((s) => (s.body ? `${s.header}\n${s.body}` : s.header)).join(sep)
  if (jsonByteLength(rawFull) <= budget) return rawFull

  const fixedOverhead =
    jsonByteLength(prefix) +
    sections.reduce((sum, s) => sum + jsonByteLength(`${s.header}\n`) + jsonByteLength(sep), 0) +
    64

  const bodyBudget = Math.max(sections.length * 64, budget - fixedOverhead)
  const bodySizes = sections.map((s) => jsonByteLength(s.body))
  const grants = computeWaterFillBudgets(bodySizes, bodyBudget)

  const rendered = sections.map((s, i) => {
    const grant = grants[i] ?? 64
    if (!s.body) return s.header
    if (jsonByteLength(s.body) <= grant) return `${s.header}\n${s.body}`
    const trimmed = truncateToJsonBytes(s.body, Math.max(48, grant), '\n[... section truncated ...]')
    return `${s.header}\n${trimmed}`
  })

  return truncateToJsonBytes(prefix + rendered.join(sep), budget)
}

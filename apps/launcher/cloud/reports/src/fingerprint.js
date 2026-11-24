export const TOP_FRAMES = 3
export const VERSION_SKEW = 'version-skew'

const FRAME = /^\s*at\s+(.+?)\s*$/
const ERROR_LINE = /^Error \([^)]*\):\s*(.*)$/
const PLUMBING_FRAMES = [
  /^System\.Runtime\.CompilerServices\./,
  /^System\.Runtime\.ExceptionServices\.ExceptionDispatchInfo\./,
  /^System\.Threading\.Tasks\.(Task|ValueTask)\b/,
  /^System\.Threading\.ExecutionContext\./,
  /^Microsoft\.UI\.Dispatching\./,
  /^WinRT\.ExceptionHelpers\./,
  /^ABI\.System\./,
]

function isPlumbingFrame(frame) {
  return PLUMBING_FRAMES.some((pattern) => pattern.test(frame))
}

export function normalizeFrame(frame) {
  return frame
    .replace(/\s+in\s+.*$/, '')
    .replace(/\s*\(.*$/, '')
    .replace(/:\d+(:\d+)?$/, '')
    .replace(/\+0x[0-9a-f]+/gi, '')
    .replace(/0x[0-9a-f]+/gi, '#')
    .replace(/<([A-Za-z_][\w]*)>[a-z]__\w+/g, '<$1>')
    .replace(/`\d+/g, '')
    .replace(/\d+/g, '#')
    .trim()
}

export function normalizeMessage(line) {
  return line
    .replace(/\[[0-9T:.\-]+Z\]/g, '')
    .replace(/"[^"]*"|'[^']*'|“[^”]*”/g, '…')
    .replace(/\d+/g, '#')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 160)
}

export function signatureOf(report) {
  const lines = report.excerpt.split('\n')
  const frames = []
  const fallback = []
  for (const line of lines) {
    const match = FRAME.exec(line)
    if (!match) continue
    const frame = normalizeFrame(match[1])
    if (!frame) continue
    if (fallback.length < TOP_FRAMES && !fallback.includes(frame)) fallback.push(frame)
    if (isPlumbingFrame(frame)) continue
    if (!frames.includes(frame)) frames.push(frame)
    if (frames.length === TOP_FRAMES) break
  }
  const picked = frames.length ? frames : fallback
  const errorLine = lines.map((line) => ERROR_LINE.exec(line)?.[1]).find(Boolean)
    ?? lines.find((line) => line.includes(report.errorType))
    ?? ''
  return { frames: picked, message: normalizeMessage(errorLine) }
}

export function legacySignatureOf(report) {
  const lines = report.excerpt.split('\n')
  const frames = []
  for (const line of lines) {
    const match = FRAME.exec(line)
    if (!match) continue
    const frame = normalizeFrame(match[1])
    if (frame && !frames.includes(frame)) frames.push(frame)
    if (frames.length === TOP_FRAMES) break
  }
  const errorLine = lines.map((line) => ERROR_LINE.exec(line)?.[1]).find(Boolean)
    ?? lines.find((line) => line.includes(report.errorType))
    ?? ''
  return { frames, message: normalizeMessage(errorLine) }
}

export function errorMessageOf(report) {
  const lines = String(report?.excerpt ?? '').split('\n')
  const errorLine = lines.map((line) => ERROR_LINE.exec(line)?.[1]).find(Boolean)
    ?? lines.find((line) => report?.errorType && line.includes(report.errorType))
    ?? ''
  return errorLine.trim()
}

export function isVersionSkew(report) {
  if (report?.errorType !== 'HostCallFailed') return false
  const message = errorMessageOf(report)
  return /^Unknown method:/i.test(message) || /^HostCallFailed:\s*Unknown method:/i.test(message)
}

export function groupTypeOf(report) {
  return isVersionSkew(report) ? VERSION_SKEW : report.errorType
}

async function hashSignature(app, errorType, { frames, message }) {
  const basis = [app, errorType, ...(frames.length ? frames : [message])].join('\n')
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(basis)))
  return [...digest.slice(0, 8)].map((byte) => byte.toString(16).padStart(2, '0')).join('')
}

export async function fingerprintOf(report) {
  return hashSignature(report.app, groupTypeOf(report), signatureOf(report))
}

export async function legacyFingerprintOf(report) {
  return hashSignature(report.app, report.errorType, legacySignatureOf(report))
}

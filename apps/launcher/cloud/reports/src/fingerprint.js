export const TOP_FRAMES = 3

const FRAME = /^\s*at\s+(.+?)\s*$/
const ERROR_LINE = /^Error \([^)]*\):\s*(.*)$/

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

export async function fingerprintOf(report) {
  const { frames, message } = signatureOf(report)
  const basis = [report.app, report.errorType, ...(frames.length ? frames : [message])].join('\n')
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(basis)))
  return [...digest.slice(0, 8)].map((byte) => byte.toString(16).padStart(2, '0')).join('')
}

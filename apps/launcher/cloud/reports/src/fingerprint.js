/**
 * Which error group a crash report belongs to (README.md › Issues): the app, the error type and the report's top frames,
 * so one bug is one issue across versions and PCs, and two bugs of the same type are two. The frames are normalised first:
 * line numbers, offsets, addresses, ids, lambda and generic numbering go, so a rebuild or another PC does not split a group.
 * A report with no stack is grouped by its error line with every number and quoted value taken out.
 */

/** Frames that name a group; more than this and an unrelated caller far down would split it. */
export const TOP_FRAMES = 3

// .NET: "   at Namespace.Type.Method(Args) in <path>:line 12"; JS: "    at fn (<url>:1:2)" or "    at <url>:1:2".
const FRAME = /^\s*at\s+(.+?)\s*$/
const ERROR_LINE = /^Error \([^)]*\):\s*(.*)$/

/** One frame as the group sees it: the method, without its arguments, place, offsets or compiler numbering. */
export function normalizeFrame(frame) {
  return frame
    .replace(/\s+in\s+.*$/, '')                // " in <path>:line 12"
    .replace(/\s*\(.*$/, '')                   // the arguments, or a JS frame's "(<url>:1:2)"
    .replace(/:\d+(:\d+)?$/, '')               // a bare JS frame's ":1:2"
    .replace(/\+0x[0-9a-f]+/gi, '')            // native offsets
    .replace(/0x[0-9a-f]+/gi, '#')
    .replace(/<([A-Za-z_][\w]*)>[a-z]__\w+/g, '<$1>') // lambdas and local functions: <Run>b__12_0 -> <Run>
    .replace(/`\d+/g, '')                      // generic arity
    .replace(/\d+/g, '#')
    .trim()
}

/** The error line with what differs per report taken out: numbers, quoted values, ids and placeholders kept as they are. */
export function normalizeMessage(line) {
  return line
    .replace(/\[[0-9T:.\-]+Z\]/g, '')
    .replace(/"[^"]*"|'[^']*'|“[^”]*”/g, '…')
    .replace(/\d+/g, '#')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 160)
}

/** The report's top frames (normalised, at most TOP_FRAMES) and the error line, for the group and the issue's title. */
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

/** The group's id: 16 hex characters of SHA-256 over app, error type and the frames (or the error line when there are none). */
export async function fingerprintOf(report) {
  const { frames, message } = signatureOf(report)
  const basis = [report.app, report.errorType, ...(frames.length ? frames : [message])].join('\n')
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(basis)))
  return [...digest.slice(0, 8)].map((byte) => byte.toString(16).padStart(2, '0')).join('')
}

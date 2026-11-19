/**
 * The one place a failure becomes words for the player (AGENTS.md › How to work 6). Whatever was thrown or refused (an
 * Error, the host's sentence, a Discord or Spotify answer, a browser's TypeError) comes out as one plain sentence that
 * says what happened and the one thing to do, with the original kept as `detail` for the log and Copy details.
 *
 * The host shapes its own exceptions the same way first (WebHostBridge.UserMessage: a full disk, no network, access
 * refused, a file in use); this maps what reaches the page from anywhere else, and anything raw that still slips
 * through: an exception's name, a stack frame, a code, a path, "Something went wrong". Tests: test/errorWords.test.ts.
 */

export type PlainError = {
  /** One plain sentence: what happened, and what to do. */
  text: string
  /** What was actually said, for the log and Copy details; null when it was the sentence itself. */
  detail: string | null
}

/** What a failure says when nothing better is known: the caller's own sentence ("Exo couldn't remove it."), else this. */
export const GENERIC = 'Exo couldn’t finish that. Try again.'

const known: { test: RegExp; text: string }[] = [
  {
    test: /access (?:is |was )?denied|access to .{0,240}? is denied|unauthori[sz]edaccess|requires elevation|not have permission|permission denied|E_ACCESSDENIED|0x80070005|EACCES|EPERM|Windows refused access/i,
    text: 'Windows refused access. Try again, and allow Windows’ prompt if it asks.',
  },
  {
    test: /being used by another process|sharing violation|file (?:is )?in use|is locked|EBUSY|0x80070020|0x80070021|A file is in use/i,
    text: 'A file is in use. Close the game or app using it, then try again.',
  },
  {
    test: /not enough (?:free )?(?:disk )?space|disk (?:is )?full|drive is full|ENOSPC|0x80070070|0x80070027/i,
    text: 'The drive is full. Free up some space, then try again.',
  },
  {
    test: /prompt was declined|operation was canceled by the user|0x800704c7|ERROR_CANCELLED|error 1223/i,
    text: 'Windows’ prompt was declined. Try again and choose Yes.',
  },
  {
    test: /failed to fetch|networkerror|network ?error|network request failed|could not reach the internet|couldn’t reach the internet|ENOTFOUND|ECONNRESET|ECONNREFUSED|EAI_AGAIN|ERR_INTERNET_DISCONNECTED|ERR_NAME_NOT_RESOLVED|no such host|you(?:'|’)re offline|you are offline/i,
    text: 'Exo couldn’t reach the internet. Check your connection, then try again.',
  },
  {
    test: /did not answer in time|didn(?:'|’)t answer in time|timed? ?out|ETIMEDOUT|taking too long|took too long|deadline exceeded/i,
    text: 'That is taking too long. Try again.',
  },
  {
    test: /\b429\b|rate.?limit|too many requests|you are being rate limited/i,
    text: 'Too many tries in a row. Wait a moment, then try again.',
  },
  {
    test: /\b401\b|unauthori[sz]ed|token (?:has )?expired|invalid token|sign(?:ed)? ?(?:in|out) again|session expired/i,
    text: 'You were signed out. Sign in again, then try again.',
  },
  {
    test: /\b50[0234]\b|service unavailable|bad gateway|internal server error|gateway time/i,
    text: 'The service isn’t answering right now. Try again in a moment.',
  },
  {
    test: /launcher and UI versions do not match/i,
    text: 'Exo needs a restart to finish updating. Restart Exo, then try again.',
  },
]

/** Words no one can act on: they become the caller's sentence. */
const vague = /^(?:something went wrong|an? (?:unknown|unexpected) error(?: occurred)?|unknown error|error|failed|failure|request failed|undefined|null|exo could not finish that|refused)\b/i

/** Raw internals: an exception's type, a stack frame, a code, a path, JSON, a browser's own wording. */
const raw = /\b\w*(?:Exception|Error)\b:?|^\s*at\s|\bat [\w.$<>]+ ?\(|\bstack\b|\bHRESULT\b|\b0x[0-9a-f]{6,}\b|\berrno\b|[A-Za-z]:\\|\\\\|\/(?:Users|home)\/|[{}[\]<>]|undefined is not|cannot read propert|is not a function|null reference|\bE[A-Z]{3,}\b|\bstatus(?: code)? \d{3}\b/

function said(error: unknown): string {
  if (error == null) return ''
  if (typeof error === 'string') return error
  if (error instanceof Error) return error.message || error.name
  if (typeof error === 'object') {
    const { message, error: inner } = error as { message?: unknown; error?: unknown }
    if (typeof message === 'string') return message
    if (typeof inner === 'string') return inner
  }
  return String(error)
}

function sentence(text: string): string {
  const line = text.replace(/\s+/g, ' ').trim()
  return /[.!?…]$/.test(line) ? line : `${line}.`
}

/**
 * The player's words for a failure. `fallback` is the caller's own sentence for when nothing better is known
 * ("Exo couldn't remove it. Try again."). A plain sentence (the host's, a service's) is kept as it is.
 */
export function plainError(error: unknown, fallback: string = GENERIC): PlainError {
  const original = said(error).trim()
  const keep = (text: string): PlainError => ({ text, detail: original && original !== text ? original : null })
  if (!original) return keep(fallback)
  for (const rule of known) if (rule.test.test(original)) return keep(rule.text)
  if (vague.test(original) || raw.test(original) || original.length > 160) return keep(fallback)
  return keep(sentence(original))
}

/** The sentence alone, for a status line that already has its own place. */
export function errorText(error: unknown, fallback?: string): string {
  return plainError(error, fallback).text
}

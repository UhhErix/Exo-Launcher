/**
 * Report a problem (Settings › About; components/settings/ReportProblem.tsx). Send goes through Exo's report service to
 * the team's private tracker, so no GitHub account is needed (host `report.send`, cloud/reports › Report a problem).
 * Open on GitHub is the public way: Exo Launcher's bug form, prefilled with the version and what was written, never the
 * logs or a contact, and with anything that looks personal taken out of the words first.
 */

export const MAX_MESSAGE = 4000
export const MAX_CONTACT = 200
/** GitHub's form takes long prefills, but a browser address bar is happier short. */
const MAX_URL_MESSAGE = 1500

export type ReportDraft = { message: string; attachLogs: boolean; contact: string; excerpt: string | null }
export type ReportPayload = { message: string; excerpt?: string; contact?: string }
export type ReportResult = { ok: boolean; issue?: number | null; ref?: string | null; message?: string | null }

/** What Send posts to the host, or null when there is nothing to send. The excerpt goes only with Attach logs on. */
export function reportPayload(draft: ReportDraft): ReportPayload | null {
  const message = draft.message.replace(/\r\n?/g, '\n').trim().slice(0, MAX_MESSAGE)
  if (!message) return null
  const contact = draft.contact.replace(/\s+/g, ' ').trim().slice(0, MAX_CONTACT)
  return {
    message,
    ...(draft.attachLogs && draft.excerpt ? { excerpt: draft.excerpt } : {}),
    ...(contact ? { contact } : {}),
  }
}

/** The button's words once sent: the issue's number, or the reference while it waits for the team's tracker. */
export function sentWords(result: ReportResult): string {
  if (result.issue) return `Sent, #${result.issue}`
  if (result.ref) return `Sent, ref ${result.ref}`
  return 'Sent'
}

/**
 * Words safe to put in a public address: emails, paths, web addresses, long numbers (ids) and Windows user folders out.
 * The player sees the result in GitHub's form before posting it.
 */
export function scrubForUrl(text: string): string {
  return text
    .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, '<email>')
    .replace(/\b[a-z][a-z0-9+.-]{1,15}:\/\/\S+/gi, '<url>')
    .replace(/\\\\[^\s\\]+\\\S*/g, '<path>')
    .replace(/\b[A-Za-z]:[\\/]\S*/g, '<path>')
    .replace(/(^|[^A-Za-z0-9])\/(?:home|Users)\/\S*/g, '$1<path>')
    .replace(/\d{9,}/g, '<id>')
}

/** GitHub's bug report form with Exo's version filled in (the form's `version` field), and the words when given. */
export function bugReportUrl(version: string | null, message = ''): string {
  const url = new URL('https://github.com/UhhErix/Exo-Launcher/issues/new')
  url.searchParams.set('template', 'bug_report.yml')
  const words = scrubForUrl(message.replace(/\r\n?/g, '\n').trim()).slice(0, MAX_URL_MESSAGE)
  if (words) {
    const first = words.split('\n')[0]
    url.searchParams.set('title', first.length > 80 ? `${first.slice(0, 79)}…` : first)
    url.searchParams.set('what-happened', words)
  }
  if (version?.trim()) url.searchParams.set('version', version.trim())
  return url.toString()
}

import { APPS, ReportError, personalIn, reportInstallId } from './validate.js'
import { limits } from './limits.js'

export const USER_FIELDS = ['app', 'version', 'osBuild', 'message', 'excerpt', 'contact']
export const userLimits = {
  messageChars: 4000,
  contactChars: 200,
  bodyBytes: 48 * 1024,
}

const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/
const OS_BUILD = /^\d{1,5}\.\d{1,6}(\.\d{1,6}){0,2}$/
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/
const LINE_BREAK = new RegExp('[' + String.fromCharCode(10, 13, 0x2028, 0x2029) + ']')

export function parseUserReport(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ReportError('Not a report')
  for (const key of Object.keys(value)) if (!USER_FIELDS.includes(key) && key !== 'installId') throw new ReportError('Unknown field')
  reportInstallId(value)
  for (const key of ['app', 'version', 'osBuild', 'message']) if (typeof value[key] !== 'string') throw new ReportError(`Field ${key} must be text`)
  for (const key of ['excerpt', 'contact']) if (value[key] !== undefined && typeof value[key] !== 'string') throw new ReportError(`Field ${key} must be text`)

  const { app, version, osBuild } = value
  const message = value.message.replace(/\r\n?/g, '\n').trim()
  const excerpt = (value.excerpt ?? '').replace(/\r\n?/g, '\n')
  const contact = (value.contact ?? '').trim() || null
  if (!APPS.has(app)) throw new ReportError('Unknown app')
  if (!VERSION.test(version)) throw new ReportError('Bad version')
  if (!OS_BUILD.test(osBuild)) throw new ReportError('Bad Windows build')
  if (message === '') throw new ReportError('Write what happened')
  if (message.length > userLimits.messageChars) throw new ReportError('Message too long')
  if (CONTROL.test(message)) throw new ReportError('Message has control characters')
  if (excerpt.length > limits.excerptChars) throw new ReportError('Excerpt too long')
  if (CONTROL.test(excerpt)) throw new ReportError('Excerpt has control characters')
  const personal = personalIn(excerpt)
  if (personal) throw new ReportError(`Report holds ${personal}`, 422)
  if (contact !== null && (contact.length > userLimits.contactChars || CONTROL.test(contact) || LINE_BREAK.test(contact))) throw new ReportError('Bad contact')
  return { app, version, osBuild, message, excerpt, contact }
}

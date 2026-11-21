import { limits } from './limits.js'
import { installKey } from './telemetry.js'

export class ReportError extends Error {
  constructor(message, status = 400) {
    super(message)
    this.status = status
  }
}

export const APPS = new Set(['launcher', 'cinema', 'browser'])
export const FIELDS = ['app', 'version', 'osBuild', 'errorType', 'excerpt']
export const OPTIONAL_FIELDS = ['installId']

const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,4}$/
const OS_BUILD = /^\d{1,5}\.\d{1,6}(\.\d{1,6}){0,2}$/
const ERROR_TYPE = /^[A-Za-z][A-Za-z0-9_.]{0,79}$/
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/

const PERSONAL = [
  [/[A-Za-z]:\\/, 'a file path'],
  [/\\\\[^\s\\]+\\/, 'a network path'],
  [/(^|[^A-Za-z0-9])\/(home|Users)\//i, 'a file path'],
  [/[a-z][a-z0-9+.-]{1,15}:\/\//i, 'a URL'],
  [/[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}/, 'an email address'],
  [/\d{15,}/, 'a long number'],
  [/[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{16,}/, 'a token'],
]

export function reportInstallId(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ReportError('Not a report')
  if (!Object.hasOwn(value, 'installId')) return null
  const id = installKey(value.installId)
  if (id === null) throw new ReportError('Bad install ID')
  return id
}

export function parseReport(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ReportError('Not a report')
  for (const key of Object.keys(value)) if (!FIELDS.includes(key) && !OPTIONAL_FIELDS.includes(key)) throw new ReportError('Unknown field')
  reportInstallId(value)
  for (const key of FIELDS) if (typeof value[key] !== 'string') throw new ReportError(`Field ${key} must be text`)

  const { app, version, osBuild, errorType, excerpt } = value
  if (!APPS.has(app)) throw new ReportError('Unknown app')
  if (!VERSION.test(version)) throw new ReportError('Bad version')
  if (!OS_BUILD.test(osBuild)) throw new ReportError('Bad Windows build')
  if (!ERROR_TYPE.test(errorType)) throw new ReportError('Bad error type')
  if (excerpt.length > limits.excerptChars) throw new ReportError('Excerpt too long')
  if (CONTROL.test(excerpt)) throw new ReportError('Excerpt has control characters')
  const personal = personalIn(excerpt) ?? personalIn(errorType)
  if (personal) throw new ReportError(`Report holds ${personal}`, 422)
  return { app, version, osBuild, errorType, excerpt }
}

export function personalIn(text) {
  for (const [pattern, what] of PERSONAL) if (pattern.test(text)) return what
  return null
}

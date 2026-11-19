/**
 * The contact form's message (POST /v1/contact, from https://getexo.app/help). Strict like the crash reports: an unknown
 * field, a wrong type or a value outside its shape refuses the message, and a refusal names the field and the rule, never
 * the text the person wrote. The honeypot field `website` is for bots: a person's browser sends it empty and never sees it.
 */
export class ContactError extends Error {
  constructor(field, problem) {
    super(problem)
    this.field = field
    this.problem = problem
  }
}

export const CONTACT_APPS = new Set(['launcher', 'cinema', 'browser', 'other'])
export const CONTACT_FIELDS = ['email', 'app', 'message']
export const HONEYPOT = 'website'

export const contactLimits = {
  // The message: required, at most this many characters (what the form's maxlength allows).
  messageChars: 4000,
  // An address per RFC 5321's longest path; anything longer is not one.
  emailChars: 254,
  // The whole request body: a 4000-character message in UTF-8 is at most 16 KB, plus the other fields.
  bodyBytes: 16 * 1024,
}

// One address, no spaces, no quotes, no angle brackets, no line breaks: nothing that could start a header or a second address.
const EMAIL = /^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$/
// Control characters other than tab, carriage return and newline.
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/

/**
 * The message, checked. Returns { spam: true } when the honeypot is filled (the caller answers as if it were stored and
 * stores nothing), or { spam: false, email, app, message } with email null when none was given. Throws ContactError.
 */
export function parseContact(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ContactError(null, 'Send a message.')
  for (const key of Object.keys(value)) {
    if (!CONTACT_FIELDS.includes(key) && key !== HONEYPOT) throw new ContactError(null, 'Unknown field.')
  }
  if (value[HONEYPOT] !== undefined && value[HONEYPOT] !== '') {
    if (typeof value[HONEYPOT] !== 'string') throw new ContactError(null, 'Unknown field.')
    return { spam: true }
  }

  const { app, message } = value
  let { email } = value
  if (typeof app !== 'string' || !CONTACT_APPS.has(app)) throw new ContactError('app', 'Choose an app.')
  if (typeof message !== 'string' || message.trim() === '') throw new ContactError('message', 'Write a message.')
  if (message.length > contactLimits.messageChars) throw new ContactError('message', `Keep the message to ${contactLimits.messageChars} characters.`)
  if (CONTROL.test(message)) throw new ContactError('message', 'The message has characters that cannot be sent.')

  if (email === undefined || email === '') email = null
  else if (typeof email !== 'string' || email.length > contactLimits.emailChars || !EMAIL.test(email)) throw new ContactError('email', 'Check the email address.')

  return { spam: false, email, app, message }
}

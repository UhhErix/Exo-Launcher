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
  messageChars: 4000,
  emailChars: 254,
  bodyBytes: 16 * 1024,
}

const EMAIL = /^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$/
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/

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

import { telemetryLimits as L } from './limits.js'

/**
 * Telemetry checks (misc/diagnostics/SPEC.md section 5): one envelope per event, batched, checked before anything is stored.
 * Any refusal is the same empty answer (the caller maps TelemetryError to a status), so an error never says which field
 * was wrong. Props are scrubbed again here (section 3): a value that looks like personal data drops that prop, not the event.
 */
export class TelemetryError extends Error {}

const bad = () => new TelemetryError('Bad request')

export const TELEMETRY_APPS = new Set(['launcher', 'cinema', 'cinema1', 'browser', 'mouse', 'shaders'])
const CHANNELS = new Set(['stable', 'beta', 'alpha'])
const VERSION = /^\d{1,4}\.\d{1,4}\.\d{1,4}(?:-[0-9A-Za-z.-]{1,20})?$/
const SESSION = /^[0-9a-f]{16,32}$/
const TS = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/
const EVENT = /^[a-z0-9_.]{1,64}$/
const PROP_KEY = /^[a-z][a-z0-9_]{0,39}$/
const INSTALL = /^(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})$/i

// What the scrub drops from a string prop (spec section 3, the server's second check). Any match drops the prop.
const PERSONAL_TEXT = [
  /[A-Za-z]:[\\/]/, // a Windows path, C:\Users\... or D:/...
  /\\\\[^\s\\]+\\/, // a UNC path
  /%[A-Za-z_]{3,}%/, // %USERPROFILE% and friends
  /(^|[^A-Za-z0-9])\/(home|Users|root)\//i, // a Unix home folder
  /[a-z][a-z0-9+.-]{1,15}:\/\//i, // a URL
  /\bwww\./i,
  /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}/, // an email address
  /(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])/, // an IPv4 address
  /(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{0,4}/i, // an IPv6 address
  /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i, // a GUID
  /[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/, // a JWT-like token
  /\bbearer\s+\S+/i,
  /(secret|token|password|cookie|auth)\s*[=:]/i,
  /[A-Za-z0-9]{32,}/, // a long run of letters and digits
  /\d{15,}/, // a long number (a card, an account, a Steam id)
  /\.(mkv|mp4|avi|webm|srt|m2ts)\b/i, // a media file name
  /[\u0000-\u001F\u007F]/, // a control character
]

/** The install ID as the Worker keeps it (8-4-4-4-12, lower case), or null. Any 128-bit hex is accepted. */
export function installKey(value) {
  if (typeof value !== 'string' || !INSTALL.test(value)) return null
  const hex = value.toLowerCase().replace(/-/g, '')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

const isPlainObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value)

/** The props, checked for shape (not content). Throws on anything but flat scalars within the limits. */
function checkProps(value) {
  if (!isPlainObject(value)) throw bad()
  const keys = Object.keys(value)
  if (keys.length > L.maxProps) throw bad()
  for (const key of keys) {
    if (!PROP_KEY.test(key)) throw bad()
    const v = value[key]
    if (typeof v === 'number') {
      if (!Number.isFinite(v)) throw bad()
    } else if (typeof v === 'string') {
      if (v.length > L.maxStringChars) throw bad()
    } else if (typeof v !== 'boolean') {
      throw bad()
    }
  }
  return value
}

function checkEnvelope(raw, now) {
  if (!isPlainObject(raw) || raw.schema !== 1) throw bad()
  if (!TELEMETRY_APPS.has(raw.app)) throw bad()
  if (typeof raw.version !== 'string' || !VERSION.test(raw.version)) throw bad()
  if (!CHANNELS.has(raw.channel)) throw bad()
  const install = installKey(raw.install_id)
  if (install === null) throw bad()
  if (typeof raw.session_id !== 'string' || !SESSION.test(raw.session_id)) throw bad()
  if (typeof raw.ts !== 'string' || !TS.test(raw.ts)) throw bad()
  const at = Date.parse(raw.ts)
  if (!Number.isFinite(at) || Math.abs(now - at) > L.skewMs) throw bad()
  if (typeof raw.event !== 'string' || !EVENT.test(raw.event)) throw bad()
  checkProps(raw.props)
  return {
    app: raw.app,
    version: raw.version,
    channel: raw.channel,
    install_id: install,
    session_id: raw.session_id,
    ts: raw.ts,
    event: raw.event,
    props: raw.props,
  }
}

/**
 * Scrubs one event's props (spec section 3). A string that looks personal drops that prop; a number of 15 digits or more
 * is dropped too. Returns the kept props.
 */
export function scrubProps(props) {
  const kept = {}
  for (const [key, value] of Object.entries(props)) {
    if (typeof value === 'string' && PERSONAL_TEXT.some((pattern) => pattern.test(value))) continue
    if (typeof value === 'number' && Math.abs(value) >= 1e15) continue
    kept[key] = value
  }
  return kept
}

/**
 * The batch as rows to store, or throws TelemetryError. Accepts a JSON array of envelopes, or the spec's
 * {"schema":1,"events":[...]} wrapper. An event over 1 KB is dropped whole (not refused); an event whose props all
 * scrub away is dropped (spec section 3). Any other problem refuses the whole batch.
 */
export function parseBatch(value, now) {
  let list
  if (Array.isArray(value)) list = value
  else if (isPlainObject(value) && value.schema === 1 && Array.isArray(value.events)) list = value.events
  else throw bad()
  if (list.length === 0 || list.length > L.maxEvents) throw bad()

  const rows = []
  for (const raw of list) {
    const event = checkEnvelope(raw, now)
    if (JSON.stringify(raw).length > L.maxEventBytes) continue
    const kept = scrubProps(event.props)
    if (Object.keys(event.props).length > 0 && Object.keys(kept).length === 0) continue
    const { props, ...fields } = event
    rows.push({ ...fields, props_json: JSON.stringify(kept) })
  }
  return rows
}

/** The install ID of a delete request, or throws TelemetryError. */
export function parseDelete(value) {
  if (!isPlainObject(value)) throw bad()
  if (value.schema !== undefined && value.schema !== 1) throw bad()
  if (value.app !== undefined && !TELEMETRY_APPS.has(value.app)) throw bad()
  const install = installKey(value.install_id)
  if (install === null) throw bad()
  return install
}

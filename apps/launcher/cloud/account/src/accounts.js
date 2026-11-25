/**
 * Accounts (README.md): an address kept as a keyed digest, a password as Argon2id (a self-describing hash, rehashed when its parameters change), a recovery code kept as a keyed
 * digest, each account's item key (random, sealed under the keyring), the lockout on wrong tries, and sessions.
 *
 * The keyring is ACCOUNT_SECRETS: "id:hex,id:hex" (up to 4 keys, each 32 bytes or more, ids unique). The first key is
 * current: everything new is made under it, and every key still listed opens what an older one made. A row made under
 * an older key moves to the current one when its account signs in (upgradeRecord), so a key can be retired once no row
 * names it.
 */
import { argon2id } from '@noble/hashes/argon2.js'
import { b64url, bytesOf, hex, mac, macKey, random, same, seal, sealKey, sha256, unb64url, unseal, utf8 } from './crypto.js'
import { COMMON_PASSWORDS } from './common-passwords.js'
import { Refusal, refuse } from './http.js'
import { limits } from './limits.js'

export const seconds = () => Math.floor(Date.now() / 1000)

const KEY = /^([a-z0-9]{1,8}):((?:[0-9a-fA-F]{2}){32,})$/
const rings = new Map()

/** ACCOUNT_SECRETS parsed: { current, secrets: Map(id -> bytes) }, or null when it is missing or not a valid keyring. */
export function ringOf(env) {
  const text = env.ACCOUNT_SECRETS
  if (!rings.has(text)) rings.set(text, parseRing(text))
  return rings.get(text)
}

function parseRing(text) {
  if (typeof text !== 'string') return null
  const parts = text.split(',').map(part => KEY.exec(part.trim()))
  if (!parts.length || parts.length > 4 || parts.some(part => part === null)) return null
  const secrets = new Map(parts.map(([, id, digits]) => [id, Uint8Array.from(digits.match(/../g), pair => parseInt(pair, 16))]))
  return secrets.size === parts.length ? { current: parts[0][1], secrets } : null
}

const info = label => `exo-account/${label} v1`

/** text's keyed digest under one key of the ring, as "id:hex". */
async function digestWith(id, secret, label, text) {
  return `${id}:${hex(await mac(await macKey(secret, info(label)), text))}`
}

/** text's keyed digest under the current key, as "id:hex": what new rows store. */
export async function digestOf(env, label, text) {
  const { current, secrets } = ringOf(env)
  return digestWith(current, secrets.get(current), label, text)
}

/**
 * Whether text's digest matches the one stored with it, made under the key the stored value names (or the current key
 * when it names none). The same work whether or not there is a stored value.
 */
export async function digestMatches(env, label, text, stored) {
  const { current, secrets } = ringOf(env)
  const [id, digest] = typeof stored === 'string' ? stored.split(':') : []
  const mine = hex(await mac(await macKey(secrets.get(id) ?? secrets.get(current), info(label)), text))
  return secrets.has(id) && same(mine, digest)
}

/**
 * Each key id of the ring has its check value stored the first time it is seen (an HMAC of a constant under that key), and
 * every later request must find the same value under it. A configured secret that gives another value is a server error
 * (keyring-mismatch), so a wrong secret under a known id can't make a second set of accounts or wrong codes. Checked once
 * per keyring text per database in an isolate.
 */
const checked = new WeakMap()

/** Opens one stored item key sealed under key id (of the first 1,000 accounts, if any): a wrong secret for it is refused. */
async function proveKeyId(env, id) {
  const { results } = await env.DB.prepare('SELECT id, item_key FROM accounts LIMIT 1000').all()
  const row = results.find(candidate => splitWrapped(candidate.item_key).id === id)
  if (row === undefined) return
  try {
    await unwrapItemKey(env, row.id, row.item_key)
  } catch {
    refuse(500, 'keyring-mismatch')
  }
}
export async function keyringChecked(env) {
  const text = env.ACCOUNT_SECRETS
  const done = checked.get(env.DB) ?? new Set()
  if (done.has(text)) return
  for (const [id, secret] of ringOf(env).secrets) {
    const check = hex(await mac(await macKey(secret, info('key check')), 'key check'))
    const stored = await env.DB.prepare('SELECT check_mac FROM keyring WHERE key_id = ?').bind(id).first('check_mac')
    if (stored === null) {
      // First sight of this key id: an account whose item key is sealed under it must open with this secret, before the check is kept.
      await proveKeyId(env, id)
      await env.DB.prepare('INSERT INTO keyring (key_id, check_mac) VALUES (?, ?) ON CONFLICT (key_id) DO NOTHING').bind(id, check).run()
      const kept = await env.DB.prepare('SELECT check_mac FROM keyring WHERE key_id = ?').bind(id).first('check_mac')
      if (!same(kept, check)) refuse(500, 'keyring-mismatch')
    } else if (!same(stored, check)) refuse(500, 'keyring-mismatch')
  }
  done.add(text)
  checked.set(env.DB, done)
}

/** The address's digest under every key of the ring, current first: a row made under any of them is found. */
export async function emailLookups(env, email) {
  const found = []
  for (const [id, secret] of ringOf(env).secrets) found.push(await digestWith(id, secret, 'email', email))
  return found
}

/** The address trimmed, lowercased and NFC, or null when it doesn't look like one (loosely: name@domain.tld). */
export function normalEmail(text) {
  if (typeof text !== 'string') return null
  const email = text.trim().normalize('NFC').toLowerCase()
  const part = '[^\\s\\x00-\\x1f\\x7f@<>()\\[\\]\\\\,;:"]'
  return email.length <= 254 && new RegExp(`^${part}+@${part}+\\.${part}{2,}$`).test(email) && !email.endsWith('.') ? email : null
}

/** The password when it is a string of min to limits.passwordMax characters, else null. */
export const passwordOf = (text, min) => (typeof text === 'string' && text.length >= min && text.length <= limits.passwordMax ? text : null)

/** A password for sign-up, a change or a reset: at least passwordMin characters, and not one of the common passwords. */
export function freshPasswordOf(text) {
  const password = passwordOf(text, limits.passwordMin)
  if (!password) refuse(400, 'bad-password')
  if (COMMON_PASSWORDS.has(password.normalize('NFKC').toLowerCase())) refuse(400, 'common-password')
  return password
}

/**
 * A password's hash: Argon2id from @noble/hashes (pure JavaScript: Workers refuse WebAssembly compiled at run time, so a
 * WASM build can't run there), with the parameters in limits.js, stored as a PHC string:
 * $argon2id$v=19$m=19456,t=2,p=1$<salt, base64>$<hash, base64>. A hash made under other parameters is rehashed at sign-in
 * (rehashIfOld), so the parameters can change later.
 */
const PARAMS = `$argon2id$v=19$m=${limits.argon2.memorySize},t=${limits.argon2.iterations},p=${limits.argon2.parallelism}$`

const base64 = bytes => btoa(String.fromCharCode(...bytes)).replace(/=+$/, '')
const unbase64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0))

/** The password's hash under the current parameters, as a PHC string: the NFKC form of the password, the salt given. */
function hashOf(password, salt) {
  const { memorySize, iterations, parallelism, hashLength } = limits.argon2
  const hash = argon2id(utf8(password.normalize('NFKC')), salt, { t: iterations, m: memorySize, p: parallelism, dkLen: hashLength })
  return `${PARAMS}${base64(salt)}$${base64(hash)}`
}

/** The hash a new password is stored as: a random 16-byte salt under the current parameters. */
export const newPassword = password => withHashSlot(() => hashOf(password, random(16)))

/**
 * The hash slots: at most limits.hashesInFlight hashes run at once in this isolate. Taken only around a hash, which comes
 * after the throttle, the session and the body, so a stalled body holds none. A request that finds them all taken waits
 * up to limits.slotWaitMs for one, handed over in turn, and is then refused 503 busy (Retry-After: 1).
 */
const slots = { held: 0, waiting: [] }
export async function withHashSlot(hash) {
  if (slots.held < limits.hashesInFlight) slots.held++
  else {
    await new Promise((resolve, reject) => {
      const waiter = { resolve, timer: undefined }
      waiter.timer = setTimeout(() => {
        slots.waiting.splice(slots.waiting.indexOf(waiter), 1)
        reject(new Refusal(503, 'busy', { 'Retry-After': '1' }))
      }, limits.slotWaitMs)
      slots.waiting.push(waiter)
    })
  }
  try {
    return await hash()
  } finally {
    // The slot passes straight to the next waiter (held stays the same); with none waiting it is free.
    const next = slots.waiting.shift()
    if (next) {
      clearTimeout(next.timer)
      next.resolve()
    } else slots.held--
  }
}

/** The hash an unknown address is checked against: the current parameters, so the same work as a real account. */
let dummy
export const dummyHash = () => (dummy ??= hashOf('no account has this address', new Uint8Array(16)))

/** Whether the password matches a stored PHC string, under the parameters the string itself names. */
function matches(password, stored) {
  try {
    const [, , , costs, salt, hash] = stored.split(String.fromCharCode(36))
    const { m, t, p } = Object.fromEntries(costs.split(String.fromCharCode(44)).map(part => part.split("=")))
    const candidate = argon2id(utf8(password.normalize("NFKC")), unbase64(salt), { t: Number(t), m: Number(m), p: Number(p), dkLen: unbase64(hash).length })
    return same(base64(candidate), hash)
  } catch {
    // A stored hash this code can't read is a wrong password, with the same work as one it can (no 500 to tell them apart).
    matches(password, dummyHash())
    return false
  }
}

/**
 * Whether the password is the record's. The same work either way: an unknown address (record null) is checked against
 * the dummy hash, which never matches.
 */
export const passwordRight = (record, password) => withHashSlot(() => {
  const right = matches(password, record ? record.password_hash : dummyHash())
  return record !== null && right
})

/** Rehashes an account's password under the current parameters when its stored hash has other ones (at sign-in). */
export async function rehashIfOld(db, record, password) {
  if (record.password_hash.startsWith(PARAMS)) return
  await db.prepare('UPDATE accounts SET password_hash = ? WHERE id = ?').bind(await newPassword(password), record.id).run()
}

const BASE32 = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
const CODE = new RegExp(`^[${BASE32}]{24}$`)

/** A new recovery code: 120 random bits as 24 Crockford base32 characters. */
export function newRecoveryCode() {
  let out = ''
  let value = 0
  let bits = 0
  for (const byte of random(15)) {
    value = ((value << 8) | byte) & 0xffff
    bits += 8
    while (bits >= 5) {
      bits -= 5
      out += BASE32[(value >> bits) & 31]
    }
  }
  return out
}

/** The code as shown: 4-character groups joined by dashes. */
export const shownCode = code => code.match(/.{4}/g).join('-')

/**
 * The code as typed, read back to its 24 characters: dashes, spaces and case aside, O reads 0, and I and L read 1.
 * Null when it isn't one.
 */
export function recoveryCodeOf(text) {
  if (typeof text !== 'string') return null
  const code = text.toUpperCase().replace(/[\s-]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1')
  return CODE.test(code) ? code : null
}

const ITEM_KEY = 'exo-account/item key v1'
const concat = (a, b) => {
  const out = new Uint8Array(a.length + b.length)
  out.set(a)
  out.set(b, a.length)
  return out
}

/** The item key's wrapper: the key id it was sealed under, then the seal. */
const wrapKey = (secret, account) => sealKey(secret, ITEM_KEY, utf8(account))

async function wrapItemKey(env, account, raw) {
  const { current, secrets } = ringOf(env)
  const id = utf8(current)
  const sealed = await seal(await wrapKey(secrets.get(current), account), raw, utf8(account))
  return concat(Uint8Array.of(id.length, ...id), sealed)
}

/** The key id a stored item key names, and the seal after it. */
function splitWrapped(stored) {
  const bytes = bytesOf(stored)
  const length = bytes[0]
  return { id: new TextDecoder().decode(bytes.subarray(1, 1 + length)), sealed: bytes.subarray(1 + length) }
}

async function unwrapItemKey(env, account, stored) {
  const { id, sealed } = splitWrapped(stored)
  const secret = ringOf(env).secrets.get(id)
  // A key id the ring doesn't list is a deployment error: the item is unreadable, the answer is a server error.
  if (!secret) throw new Error(`item key sealed under "${id}", which ACCOUNT_SECRETS does not list`)
  return unseal(await wrapKey(secret, account), sealed, utf8(account))
}

/** A new random item key for the account, sealed under the current key. */
export async function newItemKey(env, account) {
  return wrapItemKey(env, account, b64url(random(32)))
}

/** The account's item key as an AES-GCM key. */
export async function itemKeyOf(env, account, stored) {
  const raw = unb64url(await unwrapItemKey(env, account, stored))
  return crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt', 'decrypt'])
}

/** Moves a row made under an older key to the current one, at sign-in: its address digest and its item key. */
export async function upgradeRecord(env, record, email) {
  const [current] = await emailLookups(env, email)
  if (record.email_mac !== current) await env.DB.prepare('UPDATE accounts SET email_mac = ? WHERE id = ?').bind(current, record.id).run()
  if (splitWrapped(record.item_key).id !== ringOf(env).current) {
    const raw = await unwrapItemKey(env, record.id, record.item_key)
    await env.DB.prepare('UPDATE accounts SET item_key = ? WHERE id = ?').bind(await wrapItemKey(env, record.id, raw), record.id).run()
  }
}

const COLUMNS = 'id, email_mac, password_hash, recovery_mac, item_key, seq, created, item_count, item_bytes'

/** The account row by its id, or null. */
export const recordOf = (db, account) => db.prepare(`SELECT ${COLUMNS} FROM accounts WHERE id = ?`).bind(account).first()

/** The account row for any of the address's digests (from emailLookups), or null. */
export const recordByAddress = (db, lookups) => db.prepare(`SELECT ${COLUMNS} FROM accounts WHERE email_mac IN (${lookups.map(() => '?').join(', ')})`)
  .bind(...lookups).first()

/**
 * One wrong try on a pair (README.md › Limits): its count in the window, and the lock when the count reaches the limit. A
 * lock lasts at least lockSeconds, and to the end of the window: the count is kept, so the pair stays shut until its window
 * ends. Only the pair locks. Nothing refuses an address.
 */
const WRONG = `UPDATE sign_in_failures SET pending = MAX(pending - 1, 0),
    locked_until = CASE WHEN window_start + ?3 > ?2 AND failures + 1 >= ?4 THEN MAX(?2 + ?5, window_start + ?3) ELSE locked_until END,
    failures = CASE WHEN window_start + ?3 <= ?2 THEN 1 ELSE failures + 1 END,
    window_start = CASE WHEN window_start + ?3 <= ?2 THEN ?2 ELSE window_start END
  WHERE scope = ?1
  RETURNING failures, locked_until`

/**
 * Admits one try on a pair: its counted wrong tries and the tries in flight must be under lockFailures, and no lock may
 * run. Admitted, it takes a pending place and gives it back when it is answered. A new pair starts with no count.
 */
const RESERVE = `INSERT INTO sign_in_failures (scope, failures, window_start, locked_until, pending, pending_at) VALUES (?1, 0, ?2, 0, 1, ?2)
  ON CONFLICT (scope) DO UPDATE SET pending = (CASE WHEN pending_at + ?5 <= ?2 THEN 0 ELSE pending END) + 1, pending_at = ?2,
    failures = CASE WHEN window_start + ?3 <= ?2 THEN 0 ELSE failures END,
    window_start = CASE WHEN window_start + ?3 <= ?2 THEN ?2 ELSE window_start END
  WHERE locked_until <= ?2 AND (CASE WHEN window_start + ?3 <= ?2 THEN 0 ELSE failures END)
    + (CASE WHEN pending_at + ?5 <= ?2 THEN 0 ELSE pending END) < ?4
  RETURNING pending`

/** Gives back a place a try held and never answered: a try is answered by RIGHT or by WRONG, or released here. */
const RELEASE = 'UPDATE sign_in_failures SET pending = MAX(pending - 1, 0) WHERE scope = ?'

/** A right try: its place is given back and the pair's count is cleared. */
const RIGHT = 'UPDATE sign_in_failures SET pending = MAX(pending - 1, 0), failures = 0 WHERE scope = ?'

/** Clears the places of tries that never answered (older than pendingSeconds). */
const STALE = 'UPDATE sign_in_failures SET pending = 0 WHERE pending > 0 AND pending_at <= ?2 - ?1'

/**
 * The address-wide count: every wrong try against the address in its window, across client addresses and devices. It never
 * locks; past addressFailures the answer waits (addressDelay). ADDRESS_NOW reads the same count without counting a try.
 */
const ADDRESS = `INSERT INTO sign_in_failures (scope, failures, window_start, locked_until, pending, pending_at) VALUES (?1, 1, ?2, 0, 0, 0)
  ON CONFLICT (scope) DO UPDATE SET
    failures = CASE WHEN window_start + ?3 <= ?2 THEN 1 ELSE failures + 1 END,
    window_start = CASE WHEN window_start + ?3 <= ?2 THEN ?2 ELSE window_start END
  RETURNING failures`
const ADDRESS_NOW = 'SELECT CASE WHEN window_start + ?3 <= ?2 THEN 0 ELSE failures END AS failures FROM sign_in_failures WHERE scope = ?1'

/** Drops up to 100 rows whose count has ended (a pair's window with no lock and nothing in flight, or an address's hour). */
const PRUNE = `DELETE FROM sign_in_failures WHERE scope IN (
    SELECT scope FROM sign_in_failures
    WHERE window_start <= ?1 AND locked_until <= ?2 AND pending = 0
      AND (substr(scope, 1, 2) = 'p|' OR window_start <= ?3)
    LIMIT 100)`

const lockedFor = seconds => refuse(429, 'locked', { 'Retry-After': String(seconds) })

/**
 * The wait the address-wide count asks of a try: 1 s for each wrong try past addressFailures in its window, at most
 * addressMaxDelaySeconds. Waited outside the hash slot, and not at all for a known device (its valid token). Waits go
 * through timing.pause, which tests replace.
 */
export const timing = { pause: ms => new Promise(resolve => setTimeout(resolve, ms)) }
const addressDelay = failures => Math.min(limits.addressMaxDelaySeconds, Math.max(0, failures - limits.addressFailures))

/**
 * One password or recovery-code try (README.md › Limits). It takes a place on its pair while it is checked; the pair alone
 * locks. A wrong try also counts against the address across all clients; past addressFailures it waits a second more each,
 * up to addressMaxDelaySeconds, for a device the account doesn't know (a known device skips the wait, on right tries too).
 * right() always runs, so the time doesn't say whether the address has an account. scope: { mac, ip }: mac the address's
 * digest as stored, ip the client's address. known: the device proved itself with its token (knownDevice).
 */
export async function attempt(env, { mac: address, ip }, right, wrong, known = false, grace = null) {
  const now = seconds()
  await env.DB.batch([
    env.DB.prepare(STALE).bind(limits.pendingSeconds, now),
    env.DB.prepare(PRUNE).bind(now - limits.lockWindowSeconds, now, now - limits.addressWindowSeconds),
  ])
  const pair = `p|${address}|${await digestOf(env, 'ip', ip)}`
  const everyone = `a|${address}`
  if (!(await env.DB.prepare(RESERVE).bind(pair, now, limits.lockWindowSeconds, limits.lockFailures, limits.pendingSeconds).first())) {
    const locked = (await env.DB.prepare('SELECT locked_until FROM sign_in_failures WHERE scope = ?').bind(pair).first('locked_until')) ?? 0
    lockedFor(Math.max(1, locked - now))
  }
  // The place taken above is given back on any path that doesn't answer it.
  let placed = true
  try {
    if (await right()) {
      await env.DB.prepare(RIGHT).bind(pair).run()
      placed = false
      // The previous token is spent only now the password is right, and in one statement: a concurrent use can't spend it too.
      const trusted = grace ? await spendPreviousToken(env.DB, grace) : known
      const wait = addressDelay((await env.DB.prepare(ADDRESS_NOW).bind(everyone, now, limits.addressWindowSeconds).first('failures')) ?? 0)
      if (!trusted && wait > 0) await timing.pause(wait * 1000)
      return
    }
    const row = await env.DB.prepare(WRONG).bind(pair, now, limits.lockWindowSeconds, limits.lockFailures, limits.lockSeconds).first()
    placed = false
    if (row?.locked_until > now) lockedFor(row.locked_until - now)
    const failures = await env.DB.prepare(ADDRESS).bind(everyone, now, limits.addressWindowSeconds).first('failures')
    const wait = addressDelay(failures)
    if (!known && wait > 0) await timing.pause(wait * 1000)
    refuse(401, wrong)
  } finally {
    if (placed) await env.DB.prepare(RELEASE).bind(pair).run()
  }
}

/**
 * A device's token (README.md › Limits): 32 random bytes, given to the client once (base64url), kept only as SHA-256. The
 * device proves it is the account's own with it. Issued at sign-up, sign-in, password change and reset. The previous token
 * is kept once more (prev_token_hash) in case its answer was lost. last_seen moves with the device's sessions; a device
 * unseen for idleSeconds is no longer known.
 */
export async function issueDeviceToken(db, account, device) {
  const token = random(32)
  const now = seconds()
  await db.prepare(`INSERT INTO devices (account, device, token_hash, prev_token_hash, first_seen, last_seen) VALUES (?, ?, ?, NULL, ?, ?)
    ON CONFLICT (account, device) DO UPDATE SET prev_token_hash = devices.token_hash, token_hash = excluded.token_hash, last_seen = excluded.last_seen`)
    .bind(account, device, hex(await sha256(token)), now, now).run()
  return b64url(token)
}

/**
 * What the presented token proves (issueDeviceToken): 'current' for the device's token, { previous } for the one before it
 * (the grace, spent by spendPreviousToken once the password is right), or false. The lookup runs on every call, whether or not
 * the account exists (account '' for an unknown address), so the time it takes says nothing about the address. Nothing is
 * spent here.
 */
export async function knownDevice(db, account, device, presented) {
  const bytes = unb64url(presented) ?? random(32)
  const digest = hex(await sha256(bytes))
  const row = await db.prepare('SELECT token_hash, prev_token_hash, last_seen FROM devices WHERE account = ? AND device = ?').bind(account, device).first()
  if (row === null || unb64url(presented) === null || seconds() - row.last_seen >= limits.idleSeconds) return false
  if (same(row.token_hash, digest)) return 'current'
  if (row.prev_token_hash !== null && same(row.prev_token_hash, digest)) return { previous: row.prev_token_hash }
  return false
}

/** Spends a previous token (knownDevice → { previous }) in one statement: true only for the request that gets it. */
export async function spendPreviousToken(db, grace) {
  return (await db.prepare('UPDATE devices SET prev_token_hash = NULL WHERE account = ? AND device = ? AND prev_token_hash = ? RETURNING device')
    .bind(grace.account, grace.device, grace.prevHash).first()) !== null
}

/** The caller's session from its bearer token; last_used moves at most once a day; 400 days unused and it's over. */
export async function sessionOf(request, env) {
  const text = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(request.headers.get('Authorization') ?? '')?.[1]
  const token = text ? unb64url(text) : null
  if (!token) refuse(401, 'signed-out')
  const tokenHash = hex(await sha256(token))
  const row = await env.DB.prepare('SELECT account, device, last_used FROM sessions WHERE token_hash = ?').bind(tokenHash).first()
  const now = seconds()
  if (!row || now - row.last_used >= limits.idleSeconds) refuse(401, 'signed-out')
  if (now - row.last_used >= limits.touchSeconds) {
    await env.DB.batch([
      env.DB.prepare('UPDATE sessions SET last_used = ? WHERE token_hash = ?').bind(now, tokenHash),
      env.DB.prepare('UPDATE devices SET last_seen = ? WHERE account = ? AND device = ?').bind(now, row.account, row.device),
    ])
  }
  return { account: row.account, device: row.device, tokenHash }
}

/**
 * A new session for this PC on the account, ending the one this PC had there and the sessions idle too long. Answers
 * the token as base64url.
 */
export async function newSession(db, account, device, app = '') {
  const token = random(32)
  const now = seconds()
  await db.batch([
    db.prepare('DELETE FROM sessions WHERE account = ? AND ((device = ? AND app = ?) OR last_used <= ?)').bind(account, device, app, now - limits.idleSeconds),
    db.prepare('INSERT INTO sessions (token_hash, account, device, app, created, last_used) VALUES (?, ?, ?, ?, ?, ?)').bind(hex(await sha256(token)), account, device, app, now, now),
    db.prepare('DELETE FROM sessions WHERE account = ? AND token_hash NOT IN (SELECT token_hash FROM sessions WHERE account = ? ORDER BY last_used DESC, rowid DESC LIMIT ?)')
      .bind(account, account, limits.accountSessions),
    // A device with no session left is no longer known: its token goes with its last session.
    db.prepare('DELETE FROM devices WHERE account = ? AND device NOT IN (SELECT device FROM sessions WHERE account = ?)').bind(account, account),
  ])
  return b64url(token)
}

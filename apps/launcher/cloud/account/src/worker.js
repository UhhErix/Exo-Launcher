/**
 * Exo account (README.md): email and password accounts for Exo's apps, a recovery code, a session per PC, and the
 * synced items of each account, kept encrypted at rest under a key of the account's own. A Cloudflare Worker over one
 * D1 database, plain JavaScript with no build step. It writes no logs and sets no cookies; the rate limit counts
 * addresses at Cloudflare's edge.
 */
import {
  attempt, digestMatches, digestOf, emailLookups, freshPasswordOf, issueDeviceToken, itemKeyOf, keyringChecked, knownDevice, newItemKey, newPassword, newRecoveryCode, newSession, normalEmail,
  passwordOf, passwordRight, rehashIfOld, recordByAddress, recordOf, recoveryCodeOf, ringOf, seconds, sessionOf, shownCode, upgradeRecord,
} from './accounts.js'
import { b64url, hex, random, seal, sha256, unseal, utf8 } from './crypto.js'
import { Refusal, address, body, done, json, notModified, refuse } from './http.js'
import { limits } from './limits.js'
import { verifyAssertion, verifyRegistration } from './webauthn.js'

const DEVICE = /^[0-9a-f]{32}$/
const NAME = /^[a-z][a-z0-9-]{0,31}$/
const ID = /^[A-Za-z0-9._:~@-]{1,128}$/
const AFTER = /^[0-9]{1,15}$/

const ROUTES = [
  ['POST', /^\/v1\/account$/, signUp],
  ['GET', /^\/v1\/account$/, getAccount],
  ['DELETE', /^\/v1\/account$/, deleteAccount],
  ['PUT', /^\/v1\/password$/, changePassword],
  ['POST', /^\/v1\/session$/, signIn],
  ['POST', /^\/v1\/signout$/, signOut],
  ['POST', /^\/v1\/recovery\/reset$/, resetPassword],
  ['POST', /^\/v1\/recovery$/, newCode],
  ['GET', /^\/v1\/sessions$/, getSessions],
  ['DELETE', /^\/v1\/sessions\/([0-9a-f]{32})$/, deleteDeviceSessions],
  ['GET', /^\/v1\/passkeys$/, getPasskeys],
  ['POST', /^\/v1\/passkeys\/register\/start$/, passkeyRegisterStart],
  ['POST', /^\/v1\/passkeys\/register\/finish$/, passkeyRegisterFinish],
  ['DELETE', /^\/v1\/passkeys\/([A-Za-z0-9_-]{8,256})$/, deletePasskey],
  ['POST', /^\/v1\/passkeys\/signin\/start$/, passkeySignInStart],
  ['POST', /^\/v1\/passkeys\/signin\/finish$/, passkeySignInFinish],
  ['PUT', /^\/v1\/sync$/, putItems],
  ['GET', /^\/v1\/sync$/, getItems],
  ['GET', /^\/v1\/export$/, exportItems],
]

export default {
  async fetch(request, env) {
    const url = new URL(request.url)
    const routes = ROUTES.filter(([, path]) => path.test(url.pathname))
    if (!routes.length) return json({ error: 'not-found' }, 404)
    const route = routes.find(([method]) => method === request.method)
    if (!route) return json({ error: 'method-not-allowed' }, 405)
    try {
      if (!ringOf(env)) refuse(503, 'not-configured')
      await keyringChecked(env)
      return await route[2](request, env, url, route[1].exec(url.pathname))
    } catch (error) {
      if (error instanceof Refusal) return json({ error: error.code }, error.status, error.headers)
      return json({ error: 'server-error' }, 500)
    }
  },
}

/**
 * What a device proves with its token (accounts.js knownDevice): known for its current token, or the grace of its previous one,
 * which is spent only once the password is right (attempt).
 */
async function proofOf(env, account, device, presented) {
  const proof = await knownDevice(env.DB, account, device, presented)
  return { known: proof === 'current', grace: typeof proof === 'object' ? { account, device, prevHash: proof.previous } : null }
}

/** The routes that take a password, signed in or not (sign-up, sign-in, recovery, change, delete, a new code): 10 a minute from one address (SESSION_TRIES, counted at Cloudflare's edge). */
async function throttle(request, env) {
  if (!(await env.SESSION_TRIES.limit({ key: address(request) })).success) refuse(429, 'slow-down')
}

const deviceOf = input => (typeof input.device === 'string' && DEVICE.test(input.device) ? input.device : null)
const appOf = input => (typeof input.app === 'string' && NAME.test(input.app) ? input.app : '')

/** A new account with its address, password, recovery code and item key, and a session on this PC. */
async function signUp(request, env) {
  await throttle(request, env)
  const input = await body(request)
  const email = normalEmail(input.email)
  if (!email) refuse(400, 'bad-email')
  const password = freshPasswordOf(input.password)
  const device = deviceOf(input)
  if (!device) refuse(400, 'bad-request')
  const id = hex(random(16))
  // The same work whether or not the address has an account yet.
  const stored = await newPassword(password)
  const code = newRecoveryCode()
  const itemKey = await newItemKey(env, id)
  const lookups = await emailLookups(env, email)
  if (await recordByAddress(env.DB, lookups)) refuse(409, 'email-taken')
  const recoveryMac = await digestOf(env, 'recovery code', code)
  try {
    await env.DB.prepare('INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES (?, ?, ?, ?, ?, ?)')
      .bind(id, lookups[0], stored, recoveryMac, itemKey, seconds()).run()
  } catch (error) {
    // Another sign-up for this address landed after the check above: the same answer as the check would have given.
    if (/UNIQUE constraint failed/.test(error?.message ?? '')) refuse(409, 'email-taken')
    throw error
  }
  const token = await newSession(env.DB, id, device, appOf(input))
  return json({ token, account: { id }, recoveryCode: shownCode(code), deviceToken: await issueDeviceToken(env.DB, id, device) }, 201)
}

/** A session for this PC from the address and password. The same answer for an unknown address and a wrong password. */
async function signIn(request, env) {
  await throttle(request, env)
  const input = await body(request)
  const email = normalEmail(input.email)
  if (!email) refuse(400, 'bad-email')
  const password = passwordOf(input.password, 1)
  const device = deviceOf(input)
  if (!password || !device) refuse(400, 'bad-request')
  const lookups = await emailLookups(env, email)
  const record = await recordByAddress(env.DB, lookups)
  // Looked up for every address, a real one or not, so the time it takes doesn't say whether the account exists.
  const proof = await proofOf(env, record?.id ?? '', device, input.deviceToken)
  await attempt(env, { mac: record?.email_mac ?? lookups[0], ip: address(request) }, () => passwordRight(record, password), 'wrong-credentials', proof.known, proof.grace)
  await upgradeRecord(env, record, email)
  await rehashIfOld(env.DB, record, password)
  return json({
    token: await newSession(env.DB, record.id, device, appOf(input)),
    account: { id: record.id },
    deviceToken: await issueDeviceToken(env.DB, record.id, device),
  })
}

async function signOut(request, env) {
  const { account, device, tokenHash } = await sessionOf(request, env)
  // This PC's token goes with its session: after a sign-out the PC is no longer known to the account.
  await env.DB.batch([
    env.DB.prepare('DELETE FROM sessions WHERE token_hash = ?').bind(tokenHash),
    env.DB.prepare('DELETE FROM devices WHERE account = ? AND device = ? AND NOT EXISTS (SELECT 1 FROM sessions WHERE account = ? AND device = ?)').bind(account, device, account, device),
  ])
  return done()
}

async function getAccount(request, env) {
  const { account } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  return json({ account: { id: record.id, created: record.created } })
}

/** The account with every row it has: the password is needed, and its sessions and items go with it. */
async function deleteAccount(request, env) {
  await throttle(request, env)
  const { account, device } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  const input = await body(request)
  const password = passwordOf(input.password, 1)
  if (!password) refuse(400, 'bad-request')
  const proof = await proofOf(env, account, device, input.deviceToken)
  await attempt(env, { mac: record.email_mac, ip: address(request) }, () => passwordRight(record, password), 'wrong-password', proof.known, proof.grace)
  // The items go first, in chunks of 1,000 rows, each its own statement: one statement over a full account (20,000 items)
  // would outgrow D1's batch limits. Only then the rest, in one batch.
  for (let removed = 1; removed > 0;) {
    removed = (await env.DB.prepare('DELETE FROM items WHERE rowid IN (SELECT rowid FROM items WHERE account = ? LIMIT 1000)').bind(account).run()).meta.changes
  }
  // The final batch starts with the items: a PUT that landed after the chunks above would otherwise fail the account's delete.
  await env.DB.batch([
    env.DB.prepare('DELETE FROM items WHERE account = ?').bind(account),
    env.DB.prepare('DELETE FROM sessions WHERE account = ?').bind(account),
    // The address's counts: 'a|<digest>' and 'p|<digest>|<client digest>' both contain its digest.
    env.DB.prepare('DELETE FROM sign_in_failures WHERE instr(scope, ?) > 0').bind(record.email_mac),
    env.DB.prepare('DELETE FROM passkeys WHERE account = ?').bind(account),
    env.DB.prepare('DELETE FROM webauthn_challenges WHERE account = ?').bind(account),
    env.DB.prepare('DELETE FROM devices WHERE account = ?').bind(account),
    env.DB.prepare('DELETE FROM accounts WHERE id = ?').bind(account),
  ])
  return done()
}

/** A new password, with the current one. Every other PC is signed out; this one stays. */
async function changePassword(request, env) {
  await throttle(request, env)
  const { account, device, tokenHash } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  const input = await body(request)
  const current = passwordOf(input.current, 1)
  if (!current) refuse(400, 'bad-request')
  const next = freshPasswordOf(input.password)
  const proof = await proofOf(env, account, device, input.deviceToken)
  await attempt(env, { mac: record.email_mac, ip: address(request) }, () => passwordRight(record, current), 'wrong-password', proof.known, proof.grace)
  const stored = await newPassword(next)
  // Every other PC is signed out and loses its device token; this one keeps its session and gets a fresh token.
  await env.DB.batch([
    env.DB.prepare('UPDATE accounts SET password_hash = ? WHERE id = ?').bind(stored, account),
    env.DB.prepare('DELETE FROM sessions WHERE account = ? AND token_hash != ?').bind(account, tokenHash),
    env.DB.prepare('DELETE FROM devices WHERE account = ? AND device != ?').bind(account, device),
  ])
  return json({ deviceToken: await issueDeviceToken(env.DB, account, device) })
}

/**
 * A new password for the account, with its recovery code. Every PC is signed out, and this one signs in again, so that
 * the data stays and the code is new (the code is shown once).
 */
async function resetPassword(request, env) {
  await throttle(request, env)
  const input = await body(request)
  const email = normalEmail(input.email)
  if (!email) refuse(400, 'bad-email')
  const code = recoveryCodeOf(input.code)
  if (!code) refuse(400, 'bad-code')
  const password = freshPasswordOf(input.password)
  const device = deviceOf(input)
  if (!device) refuse(400, 'bad-request')
  const lookups = await emailLookups(env, email)
  const record = await recordByAddress(env.DB, lookups)
  const proof = await proofOf(env, record?.id ?? '', device, input.deviceToken)
  await attempt(env, { mac: record?.email_mac ?? lookups[0], ip: address(request) },
    () => digestMatches(env, 'recovery code', code, record?.recovery_mac), 'wrong-code', proof.known, proof.grace)
  const stored = await newPassword(password)
  const next = newRecoveryCode()
  await env.DB.batch([
    env.DB.prepare('UPDATE accounts SET password_hash = ?, recovery_mac = ? WHERE id = ?')
      .bind(stored, await digestOf(env, 'recovery code', next), record.id),
    env.DB.prepare('DELETE FROM sessions WHERE account = ?').bind(record.id),
    env.DB.prepare('DELETE FROM devices WHERE account = ?').bind(record.id),
  ])
  const token = await newSession(env.DB, record.id, device, appOf(input))
  return json({ token, account: { id: record.id }, recoveryCode: shownCode(next), deviceToken: await issueDeviceToken(env.DB, record.id, device) })
}

/** A new recovery code, with the password. The old one stops working. */
async function newCode(request, env) {
  await throttle(request, env)
  const { account, device } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  const input = await body(request)
  const password = passwordOf(input.password, 1)
  if (!password) refuse(400, 'bad-request')
  const proof = await proofOf(env, account, device, input.deviceToken)
  await attempt(env, { mac: record.email_mac, ip: address(request) }, () => passwordRight(record, password), 'wrong-password', proof.known, proof.grace)
  const next = newRecoveryCode()
  await env.DB.prepare('UPDATE accounts SET recovery_mac = ? WHERE id = ?').bind(await digestOf(env, 'recovery code', next), account).run()
  return json({ recoveryCode: shownCode(next) })
}

/** The account's sessions, oldest first; current marks the caller's own. */
async function getSessions(request, env) {
  const { account, tokenHash } = await sessionOf(request, env)
  const { results } = await env.DB.prepare('SELECT token_hash, device, app, created, last_used FROM sessions WHERE account = ? AND last_used > ? ORDER BY created, rowid')
    .bind(account, seconds() - limits.idleSeconds).all()
  const { results: devices } = await env.DB.prepare('SELECT device, first_seen, last_seen FROM devices WHERE account = ? AND last_seen > ? ORDER BY first_seen').bind(account, seconds() - limits.idleSeconds).all()
  return json({
    sessions: results.map(row => ({ device: row.device, ...(row.app ? { app: row.app } : {}), created: row.created, lastUsed: row.last_used, current: row.token_hash === tokenHash })),
    devices: devices.map(row => ({ device: row.device, firstSeen: row.first_seen, lastSeen: row.last_seen })),
  })
}

/** Every session one device has on the caller's account: a PC removed from the profile can't sign straight back in. */
async function deleteDeviceSessions(request, env, url, match) {
  const { account } = await sessionOf(request, env)
  // The device's sessions and its token go together: a revoked PC is unknown from then on.
  await env.DB.batch([
    env.DB.prepare('DELETE FROM sessions WHERE account = ? AND device = ?').bind(account, match[1]),
    env.DB.prepare('DELETE FROM devices WHERE account = ? AND device = ?').bind(account, match[1]),
  ])
  return done()
}

/**
 * An item as sent: app, type and id name it; base is the version the client last saw of it (0 for none); updated is the
 * client's clock, kept as metadata only; the value is JSON text, or deleted: true.
 */
function itemOf(item) {
  if (!item || typeof item !== 'object' || Array.isArray(item)) refuse(400, 'bad-item')
  const { app, type, id, base = 0, updated = 0, value, deleted } = item
  if (typeof app !== 'string' || !NAME.test(app) || typeof type !== 'string' || !NAME.test(type) || typeof id !== 'string' || !ID.test(id)) refuse(400, 'bad-item')
  if (!Number.isSafeInteger(base) || base < 0 || !Number.isSafeInteger(updated) || updated < 0) refuse(400, 'bad-item')
  if (deleted === true) {
    if (value !== undefined) refuse(400, 'bad-item')
    return { app, type, id, base, updated, value: null }
  }
  if (deleted !== undefined && deleted !== false) refuse(400, 'bad-item')
  if (typeof value !== 'string') refuse(400, 'bad-item')
  if (utf8(value).byteLength > limits.syncItemBytes) refuse(413, 'too-big')
  return { app, type, id, base, updated, value }
}

/** The item's identity as sealed text: an item's sealed value can't be moved to another item and open. */
const aad = (account, item) => utf8(`${account}\n${item.app}\n${item.type}\n${item.id}`)

/**
 * One item's write, guarded by its version: it lands only when the stored version is no newer than the base the client
 * saw. A tie-free server order decides, not the client's clock. RETURNING gives the version it was written at; no row
 * means the write lost.
 */
const WRITE = `INSERT INTO items (account, app, type, id, version, updated, written, value)
  VALUES (?, ?, ?, ?, (SELECT seq FROM accounts WHERE id = ?) + ?, ?, ?, ?)
  ON CONFLICT (account, app, type, id) DO UPDATE SET version = excluded.version, updated = excluded.updated,
    written = excluded.written, value = excluded.value
  WHERE items.version <= ?
  RETURNING version`

/** A PUT of up to 100 items, each guarded by its version. Answers what landed and the current value of what didn't. */
async function putItems(request, env) {
  const { account } = await sessionOf(request, env)
  const input = await body(request, limits.syncBodyBytes)
  if (!Array.isArray(input.items) || input.items.length < 1 || input.items.length > limits.syncBatch) refuse(400, 'bad-request')
  const items = input.items.map(itemOf)
  if (putting.has(account)) refuse(503, 'busy', { 'Retry-After': '1' })
  putting.add(account)
  try {
    return await landItems(env, account, items)
  } finally {
    putting.delete(account)
  }
}

/** The accounts with a PUT in flight in this isolate: a second PUT for one of them is answered 503 busy, and retried. */
const putting = new Set()

async function landItems(env, account, items) {
  const record = await recordOf(env.DB, account)
  const key = await itemKeyOf(env, account, record.item_key)
  const now = Date.now()
  const statements = []
  const sizes = []
  for (const [at, item] of items.entries()) {
    const sealed = item.value === null ? null : await seal(key, item.value, aad(account, item))
    sizes.push(sealed?.byteLength ?? 0)
    statements.push(env.DB.prepare(WRITE).bind(account, item.app, item.type, item.id, account, at + 1, item.updated, now, sealed, item.base))
  }
  // The admission check reads each item as it is stored. A write that will lose (its base is older than the stored
  // version) changes nothing, so it isn't counted. What lands adds one item and its size when the item is new, or the
  // change in its size when it is held (an edit to a smaller value, or a tombstone, takes bytes back). Only growth is
  // refused: a shrinking write goes through even on an account over a cap. Best-effort under races: a PUT on another
  // isolate between this read and the batch can still take the account past a cap.
  const before = await env.DB.batch(items.map(item => env.DB.prepare('SELECT version, length(value) AS size FROM items WHERE account = ? AND app = ? AND type = ? AND id = ?')
    .bind(account, item.app, item.type, item.id)))
  let count = 0
  let bytes = 0
  for (const [at, item] of items.entries()) {
    const row = before[at].results[0]
    if (row !== undefined && row.version > item.base) continue
    if (row === undefined) count++
    bytes += sizes[at] - (row === undefined ? 0 : (row.size ?? 0))
  }
  if ((count > 0 && record.item_count + count > limits.accountItems) || (bytes > 0 && record.item_bytes + bytes > limits.accountBytes)) refuse(413, 'account-full')
  statements.push(env.DB.prepare('UPDATE accounts SET seq = seq + ? WHERE id = ?').bind(items.length, account))
  const results = await env.DB.batch(statements)
  const written = []
  const conflicts = []
  for (const [at, item] of items.entries()) {
    const version = results[at].results[0]?.version
    if (version !== undefined) written.push({ app: item.app, type: item.type, id: item.id, version })
    else conflicts.push(await currentOf(env, key, account, item))
  }
  return json({ written, conflicts })
}

/** The item as it is stored now, for a write that lost: its version, its client clock and its value (null when deleted). */
async function currentOf(env, key, account, item) {
  const row = await env.DB.prepare('SELECT version, updated, value FROM items WHERE account = ? AND app = ? AND type = ? AND id = ?')
    .bind(account, item.app, item.type, item.id).first()
  return {
    app: item.app,
    type: item.type,
    id: item.id,
    version: row.version,
    updated: row.updated,
    value: row.value === null ? null : await unseal(key, row.value, aad(account, item)),
  }
}

/**
 * One page of the account's items after a version, oldest first: up to syncPage items whose sealed values add up to no
 * more than syncPageBytes (the first item always goes). Sizes are read first and the values only for the page's
 * versions, so a page never holds more than its bytes. A deleted item is a tombstone: value null.
 */
async function pageAfter(env, account, key, after) {
  const { results: sizes } = await env.DB.prepare('SELECT version, length(value) AS size FROM items WHERE account = ? AND version > ? ORDER BY version LIMIT ?')
    .bind(account, after, limits.syncPage).all()
  let bytes = 0
  let count = 0
  for (const row of sizes) {
    if (count && bytes + (row.size ?? 0) > limits.syncPageBytes) break
    bytes += row.size ?? 0
    count++
  }
  const last = count ? sizes[count - 1].version : after
  const { results } = count ? await env.DB.prepare('SELECT app, type, id, version, updated, value FROM items WHERE account = ? AND version > ? AND version <= ? ORDER BY version')
    .bind(account, after, last).all() : { results: [] }
  const items = []
  for (const row of results) items.push(await itemOfRow(key, account, row))
  return { items, cursor: last, more: count < sizes.length || sizes.length === limits.syncPage }
}

function matchesEtag(asked, etag) {
  if (!asked) return false
  const weak = tag => tag.trim().replace(/^W\//, '')
  const target = weak(etag)
  return asked.trim() === '*' || asked.split(',').some(tag => weak(tag) === target)
}

async function getItems(request, env, url) {
  const { account } = await sessionOf(request, env)
  const after = cursorOf(url)
  const record = await recordOf(env.DB, account)
  const etag = `"seq-${record.seq}"`
  if (record.seq <= after) {
    if (matchesEtag(request.headers.get('if-none-match'), etag)) return notModified({ ETag: etag })
    return json({ items: [], cursor: after, more: false }, 200, { ETag: etag })
  }
  const key = await itemKeyOf(env, account, record.item_key)
  const { items, cursor, more } = await pageAfter(env, account, key, after)
  return json({ items, cursor, more }, 200, more ? {} : { ETag: etag })
}

/**
 * Every item of the account, decrypted, a page at a time: GET /v1/export?after=<cursor> from 0, then from each page's
 * cursor while more is true. Deleted items are tombstones (value null), so an item deleted while the export pages still
 * shows as deleted on a later page. An item changed during the export can appear on a later page at its new version:
 * keep the one with the highest version.
 */
async function exportItems(request, env, url) {
  const { account } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  const key = await itemKeyOf(env, account, record.item_key)
  const { items, cursor, more } = await pageAfter(env, account, key, cursorOf(url))
  return json({
    exported: seconds(),
    account: { id: record.id, created: record.created },
    items,
    cursor,
    more,
  }, 200, { 'Content-Disposition': 'attachment; filename="exo-account.json"' })
}

/** The ?after= version of a pull or an export page (0 when none). */
function cursorOf(url) {
  const text = url.searchParams.get('after') ?? '0'
  if (!AFTER.test(text)) refuse(400, 'bad-request')
  return Number(text)
}

async function itemOfRow(key, account, row) {
  const item = { app: row.app, type: row.type, id: row.id }
  return { ...item, version: row.version, updated: row.updated, value: row.value === null ? null : await unseal(key, row.value, aad(account, item)) }
}

const PASSKEY_MAX = 10
const PASSKEY_CHALLENGE_TTL = 300
const PASSKEY_REAUTH_WINDOW = 300
const PASSKEY_LABEL_MAX = 64

function rpConfig(env) {
  const rpId = typeof env.WEBAUTHN_RP_ID === 'string' ? env.WEBAUTHN_RP_ID.trim() : ''
  const origin = typeof env.WEBAUTHN_ORIGIN === 'string' ? env.WEBAUTHN_ORIGIN.trim() : ''
  if (!rpId || !origin || !origin.startsWith('https://')) refuse(503, 'passkeys-unavailable')
  return { rpId, origin }
}

async function issueChallenge(db, kind, account) {
  const now = seconds()
  const challenge = b64url(random(32))
  const challengeHash = hex(await sha256(utf8(challenge)))
  await db.batch([
    db.prepare('DELETE FROM webauthn_challenges WHERE expires <= ?').bind(now),
    db.prepare('INSERT INTO webauthn_challenges (challenge_hash, kind, account, expires) VALUES (?, ?, ?, ?)')
      .bind(challengeHash, kind, account, now + PASSKEY_CHALLENGE_TTL),
  ])
  return challenge
}

async function consumeChallenge(db, challenge, expectedKind, expectedAccount = null) {
  if (typeof challenge !== 'string' || challenge.length < 32 || challenge.length > 128) refuse(401, 'bad-challenge')
  const now = seconds()
  const challengeHash = hex(await sha256(utf8(challenge)))
  const row = await db.prepare('SELECT kind, account, expires FROM webauthn_challenges WHERE challenge_hash = ?')
    .bind(challengeHash).first()
  if (!row) refuse(401, 'bad-challenge')
  const del = await db.prepare('DELETE FROM webauthn_challenges WHERE challenge_hash = ?').bind(challengeHash).run()
  if (!del.meta.changes) refuse(401, 'bad-challenge')
  if (row.expires <= now || row.kind !== expectedKind) refuse(401, 'bad-challenge')
  if (expectedAccount !== null && row.account !== expectedAccount) refuse(401, 'bad-challenge')
  return row
}

async function getPasskeys(request, env) {
  rpConfig(env)
  const { account } = await sessionOf(request, env)
  const { results } = await env.DB.prepare(
    'SELECT credential_id, label, created, last_used, sign_count_flagged FROM passkeys WHERE account = ? ORDER BY created DESC'
  ).bind(account).all()
  return json({
    passkeys: results.map(row => ({
      id: row.credential_id,
      label: row.label,
      created: row.created,
      lastUsed: row.last_used,
      flagged: Boolean(row.sign_count_flagged),
    })),
  })
}

async function passkeyRegisterStart(request, env) {
  const { rpId, origin } = rpConfig(env)
  await throttle(request, env)
  const { account, device, tokenHash } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  const input = await body(request)
  const sess = await env.DB.prepare('SELECT created FROM sessions WHERE token_hash = ?').bind(tokenHash).first()
  const hasPassword = typeof input.password === 'string' && input.password.length > 0
  if (hasPassword) {
    const pw = passwordOf(input.password, 1)
    if (!pw) refuse(400, 'bad-request')
    const proof = await proofOf(env, account, device, input.deviceToken)
    await attempt(env, { mac: record.email_mac, ip: address(request) }, () => passwordRight(record, pw), 'wrong-password', proof.known, proof.grace)
  } else if (!sess || seconds() - sess.created > PASSKEY_REAUTH_WINDOW) {
    refuse(403, 'reauth-required')
  }
  const { results: existing } = await env.DB.prepare('SELECT credential_id FROM passkeys WHERE account = ?').bind(account).all()
  if (existing.length >= PASSKEY_MAX) refuse(409, 'passkey-limit')
  const challenge = await issueChallenge(env.DB, 'register', account)
  return json({
    challenge,
    rp: { id: rpId, name: 'Exo Account' },
    origin,
    user: { id: b64url(utf8(account)) },
    userVerification: 'required',
    excludeCredentials: existing.map(r => r.credential_id),
    pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
    timeout: 60000,
  })
}

async function passkeyRegisterFinish(request, env) {
  const { rpId, origin } = rpConfig(env)
  await throttle(request, env)
  const { account } = await sessionOf(request, env)
  const input = await body(request)
  if (typeof input.challenge !== 'string' || typeof input.clientDataJSON !== 'string' || typeof input.attestationObject !== 'string') {
    refuse(400, 'bad-request')
  }
  const label = typeof input.label === 'string' ? input.label.trim().slice(0, PASSKEY_LABEL_MAX) : 'Windows passkey'
  const { count } = await env.DB.prepare('SELECT COUNT(*) AS count FROM passkeys WHERE account = ?').bind(account).first()
  if (count >= PASSKEY_MAX) refuse(409, 'passkey-limit')
  await consumeChallenge(env.DB, input.challenge, 'register', account)
  const verified = await verifyRegistration({
    rpId,
    expectedOrigin: origin,
    expectedChallenge: input.challenge,
    clientDataJSON: input.clientDataJSON,
    attestationObject: input.attestationObject,
  })
  const now = seconds()
  try {
    await env.DB.prepare(
      'INSERT INTO passkeys (credential_id, account, public_key_spki, alg, sign_count, sign_count_flagged, label, created, last_used) VALUES (?, ?, ?, ?, ?, 0, ?, ?, ?)'
    ).bind(verified.credentialId, account, verified.publicKeySpki, verified.alg, verified.signCount, label || 'Windows passkey', now, now).run()
  } catch (error) {
    if (/UNIQUE constraint failed/.test(error?.message ?? '')) refuse(409, 'passkey-exists')
    throw error
  }
  return json({
    passkey: { id: verified.credentialId, label: label || 'Windows passkey', created: now, lastUsed: now, flagged: false },
  }, 201)
}

async function deletePasskey(request, env, _url, match) {
  rpConfig(env)
  const { account } = await sessionOf(request, env)
  const record = await recordOf(env.DB, account)
  const existing = await env.DB.prepare('SELECT credential_id FROM passkeys WHERE credential_id = ? AND account = ?')
    .bind(match[1], account).first()
  if (!existing) refuse(404, 'not-found')
  const { count } = await env.DB.prepare('SELECT COUNT(*) AS count FROM passkeys WHERE account = ?').bind(account).first()
  const hasPassword = typeof record.password_hash === 'string' && record.password_hash.startsWith('$argon2id$')
  if (!hasPassword && count <= 1) refuse(409, 'last-sign-in-method')
  await env.DB.prepare('DELETE FROM passkeys WHERE credential_id = ? AND account = ?').bind(match[1], account).run()
  return done()
}

async function passkeySignInStart(request, env) {
  const { rpId, origin } = rpConfig(env)
  await throttle(request, env)
  await body(request)
  const challenge = await issueChallenge(env.DB, 'signin', null)
  return json({
    challenge,
    rpId,
    origin,
    userVerification: 'required',
    allowCredentials: [],
    timeout: 60000,
  })
}

async function passkeySignInFinish(request, env) {
  const { rpId, origin } = rpConfig(env)
  await throttle(request, env)
  const input = await body(request)
  const device = deviceOf(input)
  if (
    !device
    || typeof input.challenge !== 'string'
    || typeof input.credentialId !== 'string'
    || typeof input.clientDataJSON !== 'string'
    || typeof input.authenticatorData !== 'string'
    || typeof input.signature !== 'string'
  ) {
    refuse(400, 'bad-request')
  }
  const chRow = await consumeChallenge(env.DB, input.challenge, 'signin', null)
  const row = await env.DB.prepare(
    'SELECT credential_id, account, public_key_spki, alg, sign_count FROM passkeys WHERE credential_id = ?'
  ).bind(input.credentialId).first()
  if (!row) refuse(401, 'wrong-credentials')
  if (chRow.account !== null && row.account !== chRow.account) refuse(401, 'wrong-credentials')
  const verified = await verifyAssertion({
    rpId,
    expectedOrigin: origin,
    expectedChallenge: input.challenge,
    storedSpki: row.public_key_spki,
    storedAlg: row.alg,
    storedSignCount: row.sign_count,
    clientDataJSON: input.clientDataJSON,
    authenticatorData: input.authenticatorData,
    signature: input.signature,
  })
  if (verified.signCountRegression) {
    await env.DB.prepare('UPDATE passkeys SET sign_count_flagged = 1 WHERE credential_id = ?')
      .bind(row.credential_id).run()
    refuse(401, 'passkey-replay')
  }
  const now = seconds()
  await env.DB.prepare('UPDATE passkeys SET sign_count = ?, last_used = ? WHERE credential_id = ?')
    .bind(verified.signCount, now, row.credential_id).run()
  return json({
    token: await newSession(env.DB, row.account, device, appOf(input)),
    account: { id: row.account },
    deviceToken: await issueDeviceToken(env.DB, row.account, device),
  })
}

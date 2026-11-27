/**
 * Sign in with Apple and Google (README.md › Sign-in methods), beside the email and password.
 *
 * An app asks POST /v1/oauth/start for the provider's page, with a PKCE S256 challenge, a 127.0.0.1 port and what it is
 * for: 'signin' (signed out), 'link' (signed in: add this provider to the account) or 'reauth' (signed in: prove it is
 * you, to delete an account that has no password). The Worker answers the provider's URL with a state only it could
 * have made (an HMAC under the keyring), which the app opens in the person's browser. The provider comes back to
 * /v1/oauth/callback/<provider>; the Worker swaps the code, checks the ID token, and sends the browser to the app's
 * loopback with a one-time grant (or ?error=). The app takes the grant with its verifier at POST /v1/oauth/redeem.
 *
 * An identity is its provider and subject, kept only as a keyed digest. One identity leads to one account, which it
 * never changes: a sign-in with an identity no account has makes a new account; joining an account that already
 * exists is a link, made while signed in to it. An address is never used to join accounts.
 */
import { issueDeviceToken, newItemKey, newSession, recordOf, ringMacKey, sealWithRing, seconds, sessionOf, subjectLookups, unsealWithRing } from './accounts.js'
import { b64url, bytesOf, hex, random, same, sha256, unb64url, utf8 } from './crypto.js'
import { address, body, done, formOf, json, refuse } from './http.js'
import { limits } from './limits.js'
import { PROVIDERS, SignInFailure, appleNotice, authorizeUrl, identityFrom, providerNames, providerOn, revokeApple } from './providers.js'

const DEVICE = /^[0-9a-f]{32}$/
const CHALLENGE = /^[A-Za-z0-9_-]{43}$/
const VERIFIER = /^[A-Za-z0-9._~-]{43,128}$/
const TOKEN = /^[A-Za-z0-9_-]{43}$/
const PURPOSES = ['signin', 'link', 'reauth']

/** A password-less account's placeholders: never a digest an address, a password or a recovery code can match. */
export const NO_ADDRESS = '-:'
const NO_PASSWORD = '!'

/** Whether the account signs in with an email and password (a provider-made account has the placeholders). */
export const hasPassword = record => typeof record?.email_mac === 'string' && !record.email_mac.startsWith(NO_ADDRESS)

export const OAUTH_ROUTES = [
  ['GET', /^\/v1\/providers$/, getProviders],
  ['POST', /^\/v1\/oauth\/start$/, startFlow],
  ['GET', /^\/v1\/oauth\/callback\/(apple|google)$/, (request, env, url, match) => finish(env, match[1], url.searchParams)],
  ['POST', /^\/v1\/oauth\/callback\/(apple|google)$/, async (request, env, url, match) => finish(env, match[1], await formOf(request))],
  ['POST', /^\/v1\/oauth\/redeem$/, redeem],
  ['GET', /^\/v1\/methods$/, getMethods],
  ['DELETE', /^\/v1\/methods\/(apple|google)$/, removeMethod],
  ['POST', /^\/v1\/oauth\/notify\/apple$/, appleNotify],
]

/** The providers this Worker has a client for: the app shows a button for each. */
async function getProviders(request, env) {
  return json({ providers: providerNames.filter(name => providerOn(env, name)) })
}

async function throttle(request, env) {
  if (!(await env.SESSION_TRIES.limit({ key: address(request) })).success) refuse(429, 'slow-down')
}

/** The provider's page for this sign-in, link or reauth, with the state the callback checks. */
async function startFlow(request, env) {
  await throttle(request, env)
  const input = await body(request)
  const provider = typeof input.provider === 'string' ? input.provider : ''
  if (!Object.hasOwn(PROVIDERS, provider)) refuse(400, 'bad-provider')
  const purpose = PURPOSES.includes(input.purpose) ? input.purpose : null
  const port = Number.isInteger(input.port) && input.port >= 1024 && input.port <= 65535 ? input.port : 0
  if (!purpose || !port || typeof input.challenge !== 'string' || !CHALLENGE.test(input.challenge)) refuse(400, 'bad-request')
  if (!providerOn(env, provider)) refuse(503, 'provider-off')
  // A link or a reauth belongs to the account signed in now: its state names it, and only that account can take it.
  const account = purpose === 'signin' ? null : (await sessionOf(request, env)).account
  const nonce = b64url(random(16))
  const fields = { p: provider, port, ch: input.challenge, n: nonce, pu: purpose, a: account, exp: seconds() + limits.stateSeconds }
  return json({ url: authorizeUrl(env, provider, await stateOf(env, fields), nonce) })
}

async function stateOf(env, fields) {
  const text = b64url(utf8(JSON.stringify(fields)))
  const tag = new Uint8Array(await crypto.subtle.sign('HMAC', await ringMacKey(env, 'oauth state'), utf8(text)))
  return `${text}.${b64url(tag)}`
}

/** The start's fields when the state carries this Worker's tag, names this provider and is within its time; else null. */
async function readState(env, text, provider) {
  const [body, tag, ...rest] = typeof text === 'string' ? text.split('.') : []
  const tagBytes = tag ? unb64url(tag) : null
  if (!body || !tagBytes || rest.length) return null
  if (!(await crypto.subtle.verify('HMAC', await ringMacKey(env, 'oauth state'), tagBytes, utf8(body)))) return null
  let fields
  try { fields = JSON.parse(new TextDecoder().decode(unb64url(body))) } catch { return null }
  return fields?.p === provider && Number.isInteger(fields.exp) && fields.exp > seconds() ? fields : null
}

/** Where the browser goes back to: the app's own listener on this PC. */
const loopbackUrl = (port, query) => `http://127.0.0.1:${port}/exo-account?${new URLSearchParams(query)}`

const redirect = url => new Response(null, { status: 302, headers: { Location: url, 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer' } })

/** The provider's answer: a grant to the app's loopback, or ?error=<code> there once the state checks out. */
async function finish(env, provider, params) {
  const state = await readState(env, params.get('state'), provider)
  if (!state) return new Response('This sign-in link is no longer valid. Start again in Exo.\n', { status: 400, headers: { 'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store' } })
  const back = query => redirect(loopbackUrl(state.port, query))
  try {
    if (!providerOn(env, provider)) return back({ error: 'provider-off' })
    const error = params.get('error')
    if (error) return back({ error: error === 'access_denied' || error === 'user_cancelled_authorize' ? 'cancelled' : 'provider-error' })
    const code = params.get('code')
    if (!code || code.length > 2048) return back({ error: 'provider-error' })
    const who = await identityFrom(env, provider, code, state.n)
    const subjectMac = await currentSubject(env, provider, who.subject)
    const grant = random(32)
    const refresh = who.refresh ? await sealWithRing(env, 'provider token', provider, who.refresh) : null
    await env.DB.prepare('INSERT INTO grants (grant_hash, purpose, provider, subject_mac, refresh, challenge, account, expires) VALUES (?, ?, ?, ?, ?, ?, ?, ?)')
      .bind(hex(await sha256(grant)), state.pu, provider, subjectMac, refresh, state.ch, state.a, seconds() + limits.grantSeconds).run()
    // Old grants go as new ones come: there is no cron.
    await env.DB.prepare('DELETE FROM grants WHERE expires <= ?').bind(seconds()).run()
    return back({ grant: b64url(grant), purpose: state.pu })
  } catch (error) {
    return back({ error: error instanceof SignInFailure ? error.code : 'server-error' })
  }
}

/**
 * The identity's digest under the current key. One stored under an older key of the ring (found by the digest each key
 * gives) is moved to the current one first, so the grant and the row agree.
 */
async function currentSubject(env, provider, subject) {
  const lookups = await subjectLookups(env, provider, subject)
  const stored = await env.DB.prepare(`SELECT subject_mac FROM identities WHERE provider = ? AND subject_mac IN (${lookups.map(() => '?').join(', ')})`)
    .bind(provider, ...lookups).first()
  if (stored && stored.subject_mac !== lookups[0]) {
    await env.DB.prepare('UPDATE identities SET subject_mac = ? WHERE provider = ? AND subject_mac = ?').bind(lookups[0], provider, stored.subject_mac).run()
  }
  return lookups[0]
}

/** The identity's stored row, or null. */
async function identityRow(env, provider, subjectMac) {
  return env.DB.prepare('SELECT provider, subject_mac, account, refresh FROM identities WHERE provider = ? AND subject_mac = ?').bind(provider, subjectMac).first()
}

/** A grant and its PKCE verifier, swapped once: a session (sign-in), the provider added (link), or a proof (reauth). */
async function redeem(request, env) {
  await throttle(request, env)
  const input = await body(request)
  const grant = typeof input.grant === 'string' && TOKEN.test(input.grant) ? unb64url(input.grant) : null
  if (!grant || typeof input.verifier !== 'string' || !VERIFIER.test(input.verifier)) refuse(400, 'bad-request')
  // Taken and deleted in one statement: a grant works once, whether the verifier is right or not.
  const taken = await env.DB.prepare('DELETE FROM grants WHERE grant_hash = ? AND purpose != ? RETURNING purpose, provider, subject_mac, refresh, challenge, account, expires')
    .bind(hex(await sha256(grant)), 'proof').first()
  if (!taken || taken.expires <= seconds()) refuse(410, 'grant-expired')
  // A blob comes back from D1 as an ArrayBuffer: bytes again before it is written anywhere.
  const row = { ...taken, refresh: taken.refresh ? bytesOf(taken.refresh) : null }
  if (!same(b64url(await sha256(utf8(input.verifier))), row.challenge)) refuse(403, 'wrong-verifier')
  if (row.purpose === 'signin') return signInWith(env, row, input)
  // A link or a reauth is taken by the account it was started for, signed in now.
  const { account } = await sessionOf(request, env)
  if (account !== row.account) refuse(403, 'wrong-account')
  return row.purpose === 'link' ? linkTo(env, account, row) : proofFor(env, account, row)
}

/** The account the identity leads to, or a new one made for it; a session on this PC. */
async function signInWith(env, row, input) {
  const device = typeof input.device === 'string' && DEVICE.test(input.device) ? input.device : null
  if (!device) refuse(400, 'bad-request')
  let identity = await identityRow(env, row.provider, row.subject_mac)
  let created = false
  if (!identity) {
    const id = hex(random(16))
    try {
      await env.DB.batch([
        env.DB.prepare('INSERT INTO accounts (id, email_mac, password_hash, recovery_mac, item_key, created) VALUES (?, ?, ?, ?, ?, ?)')
          .bind(id, `${NO_ADDRESS}${hex(random(32))}`, NO_PASSWORD, `${NO_ADDRESS}none`, await newItemKey(env, id), seconds()),
        env.DB.prepare('INSERT INTO identities (provider, subject_mac, account, refresh, created) VALUES (?, ?, ?, ?, ?)')
          .bind(row.provider, row.subject_mac, id, row.refresh, seconds()),
      ])
      identity = { account: id }
      created = true
    } catch (error) {
      // Two first sign-ins of one identity at once: the second batch breaks the identity's key, and the first account stands.
      if (!/UNIQUE constraint failed/.test(error?.message ?? '')) throw error
      identity = await identityRow(env, row.provider, row.subject_mac)
      if (!identity) throw error
    }
  } else if (row.refresh) {
    // Apple's newest refresh token is the one to revoke later.
    await env.DB.prepare('UPDATE identities SET refresh = ? WHERE provider = ? AND subject_mac = ?').bind(row.refresh, row.provider, row.subject_mac).run()
  }
  const account = identity.account
  return json({
    token: await newSession(env.DB, account, device),
    account: { id: account },
    deviceToken: await issueDeviceToken(env.DB, account, device),
    created,
  }, created ? 201 : 200)
}

/** The provider added to the signed-in account. An identity already on another account is refused, never moved. */
async function linkTo(env, account, row) {
  const identity = await identityRow(env, row.provider, row.subject_mac)
  if (identity && identity.account !== account) refuse(409, 'identity-taken')
  if (!identity) {
    const has = await env.DB.prepare('SELECT 1 FROM identities WHERE account = ? AND provider = ?').bind(account, row.provider).first()
    if (has) refuse(409, 'provider-linked')
    try {
      await env.DB.prepare('INSERT INTO identities (provider, subject_mac, account, refresh, created) VALUES (?, ?, ?, ?, ?)')
        .bind(row.provider, row.subject_mac, account, row.refresh, seconds()).run()
    } catch (error) {
      if (/UNIQUE constraint failed/.test(error?.message ?? '')) refuse(409, 'identity-taken')
      throw error
    }
  }
  return json(await methodsOf(env, account))
}

/** A short proof that the account's owner just signed in with a provider it has: what deletes an account with no password. */
async function proofFor(env, account, row) {
  const identity = await identityRow(env, row.provider, row.subject_mac)
  if (!identity || identity.account !== account) refuse(403, 'wrong-account')
  const proof = random(32)
  await env.DB.prepare('INSERT INTO grants (grant_hash, purpose, provider, subject_mac, refresh, challenge, account, expires) VALUES (?, ?, ?, ?, NULL, NULL, ?, ?)')
    .bind(hex(await sha256(proof)), 'proof', row.provider, row.subject_mac, account, seconds() + limits.proofSeconds).run()
  return json({ proof: b64url(proof) })
}

/** Whether the proof is good for this account now; it is spent either way. */
export async function proofRight(env, account, text) {
  const proof = typeof text === 'string' && TOKEN.test(text) ? unb64url(text) : null
  if (!proof) return false
  const row = await env.DB.prepare('DELETE FROM grants WHERE grant_hash = ? AND purpose = ? RETURNING account, expires').bind(hex(await sha256(proof)), 'proof').first()
  return !!row && row.account === account && row.expires > seconds()
}

/** How the account signs in: an email and password, and which providers. */
export async function methodsOf(env, account) {
  const record = await recordOf(env.DB, account)
  const { results } = await env.DB.prepare('SELECT provider FROM identities WHERE account = ? ORDER BY provider').bind(account).all()
  return { password: hasPassword(record), providers: results.map(row => row.provider) }
}

async function getMethods(request, env) {
  const { account } = await sessionOf(request, env)
  return json({ ...(await methodsOf(env, account)), available: providerNames.filter(name => providerOn(env, name)) })
}

/** A provider taken off the account, unless it is the only way left to sign in. Apple's token is revoked. */
async function removeMethod(request, env, url, match) {
  const { account } = await sessionOf(request, env)
  const provider = match[1]
  const methods = await methodsOf(env, account)
  if (!methods.providers.includes(provider)) refuse(404, 'not-found')
  if (!methods.password && methods.providers.length === 1) refuse(409, 'last-method')
  const row = await env.DB.prepare('DELETE FROM identities WHERE account = ? AND provider = ? RETURNING subject_mac, refresh').bind(account, provider).first()
  if (provider === 'apple' && row?.refresh) await revokeApple(env, await unsealWithRing(env, 'provider token', provider, row.refresh))
  return done()
}

/**
 * Apple telling the Worker that someone stopped using their Apple ID with Exo ('consent-revoked') or deleted it
 * ('account-delete'): that Apple sign-in leaves the account it was on, and with it Apple's token. When it was the
 * account's only way in, its sessions end too; its items stay until the account is deleted. Email notices are ignored:
 * the account keeps no address from Apple. Always 200 for a notice Apple signed, so Apple never retries one.
 */
async function appleNotify(request, env) {
  if (!providerOn(env, 'apple')) refuse(503, 'provider-off')
  let notice
  try { notice = await appleNotice(env, (await body(request)).payload) } catch (error) {
    if (error instanceof SignInFailure) refuse(400, 'bad-request')
    throw error
  }
  if (notice.type !== 'consent-revoked' && notice.type !== 'account-delete') return json({ ok: true })
  const lookups = await subjectLookups(env, 'apple', notice.sub)
  const gone = await env.DB.prepare(`DELETE FROM identities WHERE provider = 'apple' AND subject_mac IN (${lookups.map(() => '?').join(', ')}) RETURNING account`)
    .bind(...lookups).first()
  if (gone?.account) {
    const methods = await methodsOf(env, gone.account)
    if (!methods.password && methods.providers.length === 0) await env.DB.prepare('DELETE FROM sessions WHERE account = ?').bind(gone.account).run()
  }
  return json({ ok: true })
}

/** Every Apple token the account holds revoked, then its identities and grants gone (with the account's own delete). */
export async function forgetIdentities(env, account) {
  const { results } = await env.DB.prepare('SELECT provider, subject_mac, refresh FROM identities WHERE account = ?').bind(account).all()
  for (const row of results) if (row.provider === 'apple' && row.refresh) await revokeApple(env, await unsealWithRing(env, 'provider token', row.provider, row.refresh))
  return [
    env.DB.prepare('DELETE FROM identities WHERE account = ?').bind(account),
    env.DB.prepare('DELETE FROM grants WHERE account = ?').bind(account),
  ]
}


/**
 * Sign in with Apple and Google (README.md › Sign-in methods): the authorize URL, the code swapped at the provider's token
 * endpoint with the Worker's own client, and the ID token checked: RS256 against the provider's JWKS, iss, aud, exp, iat
 * and nonce. Apple's client secret is an ES256 JWT minted for each call from the Services ID's key. Apple's refresh token
 * is revoked when the account or its Apple sign-in goes (Apple requires it).
 */
import { b64url, same, unb64url, utf8 } from './crypto.js'
import { limits } from './limits.js'

/** Every request the Worker makes to a provider goes through here, so the tests fake Apple and Google with no network. */
export const outbound = { fetch: (input, init) => fetch(input, init) }

const seconds = () => Math.floor(Date.now() / 1000)

export const PROVIDERS = {
  google: {
    needs: ['GOOGLE_CLIENT_ID', 'GOOGLE_CLIENT_SECRET'],
    authorize: 'https://accounts.google.com/o/oauth2/v2/auth',
    token: 'https://oauth2.googleapis.com/token',
    jwks: 'https://www.googleapis.com/oauth2/v3/certs',
    issuers: ['https://accounts.google.com', 'accounts.google.com'],
    clientId: env => env.GOOGLE_CLIENT_ID,
    clientSecret: async env => env.GOOGLE_CLIENT_SECRET,
    // The account chooser every time, so nobody lands in whichever Google account the browser had open. Only `openid`:
    // the account needs Google's subject, not the address or the name.
    scope: 'openid',
    extra: { prompt: 'select_account' },
  },
  apple: {
    needs: ['APPLE_SERVICES_ID', 'APPLE_TEAM_ID', 'APPLE_KEY_ID', 'APPLE_PRIVATE_KEY'],
    authorize: 'https://appleid.apple.com/auth/authorize',
    token: 'https://appleid.apple.com/auth/token',
    revoke: 'https://appleid.apple.com/auth/revoke',
    jwks: 'https://appleid.apple.com/auth/keys',
    issuers: ['https://appleid.apple.com'],
    clientId: env => env.APPLE_SERVICES_ID,
    clientSecret: appleClientSecret,
    // No name or email scope: the account keeps Apple's subject only, and with no scope Apple answers by redirect; a
    // form post is still accepted (Apple's own default for some clients).
    scope: '',
    extra: { response_mode: 'form_post' },
  },
}

export const providerNames = Object.keys(PROVIDERS)

/** A sign-in the provider or its ID token refused; code goes back to the app as ?error=. */
export class SignInFailure extends Error {
  constructor(code) {
    super(code)
    this.code = code
  }
}

const fail = code => {
  throw new SignInFailure(code)
}

/** On when PUBLIC_URL and every value the provider needs are set. */
export const providerOn = (env, name) => Object.hasOwn(PROVIDERS, name) && Boolean(env.PUBLIC_URL) && PROVIDERS[name].needs.every(key => Boolean(env[key]))

const redirectUri = (env, name) => new URL(`/v1/oauth/callback/${name}`, env.PUBLIC_URL).href

export function authorizeUrl(env, name, state, nonce) {
  const p = PROVIDERS[name]
  const query = new URLSearchParams({ client_id: p.clientId(env), redirect_uri: redirectUri(env, name), response_type: 'code', state, nonce, ...p.extra })
  if (p.scope) query.set('scope', p.scope)
  // %20, not +, between scopes: a + here can only have come from a space.
  return `${p.authorize}?${query.toString().replaceAll('+', '%20')}`
}

/**
 * The code swapped for tokens and the ID token checked. Answers { provider, subject, refresh }: refresh is Apple's
 * refresh token (kept sealed only to revoke it), null for Google.
 */
export async function identityFrom(env, name, code, nonce) {
  const p = PROVIDERS[name]
  const clientId = p.clientId(env)
  const body = new URLSearchParams({ grant_type: 'authorization_code', code, redirect_uri: redirectUri(env, name), client_id: clientId, client_secret: await p.clientSecret(env) })
  let answer = null
  try {
    const response = await outbound.fetch(p.token, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' }, body })
    if (response.ok) answer = await response.json()
  } catch { /* unreachable, or not JSON: refused below */ }
  if (typeof answer?.id_token !== 'string') fail('token-failed')
  const claims = await checkIdToken(p, answer.id_token, clientId, nonce)
  const refresh = name === 'apple' && typeof answer.refresh_token === 'string' && answer.refresh_token.length <= 1024 ? answer.refresh_token : null
  return { provider: name, subject: claims.sub, refresh }
}

/** Apple's refresh token revoked (README.md › Sign-in methods). True when Apple said 200; never throws. */
export async function revokeApple(env, refresh) {
  if (!refresh || !providerOn(env, 'apple')) return false
  const p = PROVIDERS.apple
  try {
    const body = new URLSearchParams({ client_id: p.clientId(env), client_secret: await p.clientSecret(env), token: refresh, token_type_hint: 'refresh_token' })
    const response = await outbound.fetch(p.revoke, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body })
    return response.status === 200
  } catch {
    return false
  }
}

/**
 * Apple's server-to-server notice (README.md › Sign-in methods): a JWT signed with Apple's keys, for this Services ID or the
 * primary App ID it is grouped under (APPLE_APP_ID), whose
 * events claim names what the person did with their Apple ID ('consent-revoked', 'account-delete', 'email-disabled',
 * 'email-enabled') and their subject. Answers { type, sub }, or throws SignInFailure('bad-id-token').
 */
export async function appleNotice(env, payload) {
  if (typeof payload !== 'string' || payload.length > 8192) fail('bad-id-token')
  const claims = await checkIdToken(PROVIDERS.apple, payload, [PROVIDERS.apple.clientId(env), env.APPLE_APP_ID].filter(Boolean), null, { nonce: false, subject: false })
  let events = claims.events
  if (typeof events === 'string') {
    try { events = JSON.parse(events) } catch { fail('bad-id-token') }
  }
  if (typeof events?.type !== 'string' || typeof events.sub !== 'string' || !events.sub || events.sub.length > 255) fail('bad-id-token')
  return { type: events.type, sub: events.sub }
}

async function checkIdToken(p, token, audience, nonce, { nonce: checkNonce = true, subject = true } = {}) {
  const parts = token.split('.')
  const [header, claims] = parts.slice(0, 2).map(part => {
    try { return JSON.parse(new TextDecoder().decode(unb64url(part))) } catch { return null }
  })
  const signature = parts.length === 3 ? unb64url(parts[2]) : null
  if (parts.length !== 3 || header?.alg !== 'RS256' || typeof header.kid !== 'string' || typeof claims !== 'object' || !claims || !signature) fail('bad-id-token')
  const key = await signingKey(p.jwks, header.kid)
  if (!key || !(await crypto.subtle.verify('RSASSA-PKCS1-v1_5', key, signature, utf8(`${parts[0]}.${parts[1]}`)))) fail('bad-id-token')
  const now = seconds()
  if (!p.issuers.includes(claims.iss) || !(Array.isArray(audience) ? audience.includes(claims.aud) : claims.aud === audience)) fail('bad-id-token')
  if (typeof claims.exp !== 'number' || claims.exp + limits.skewSeconds <= now) fail('bad-id-token')
  if (typeof claims.iat === 'number' && claims.iat > now + limits.skewSeconds) fail('bad-id-token')
  if (subject && (typeof claims.sub !== 'string' || !claims.sub || claims.sub.length > 255)) fail('bad-id-token')
  if (checkNonce && !same(claims.nonce, nonce)) fail('nonce-mismatch')
  return claims
}

const jwksCache = new Map()

/** The provider's key for kid from its JWKS, kept in memory an hour; an unknown kid fetches again, at most once a minute. */
async function signingKey(url, kid) {
  const now = Date.now()
  const cached = jwksCache.get(url)
  if (cached && now - cached.at < (cached.keys.has(kid) ? limits.jwksMs : limits.jwksRetryMs)) return cached.keys.get(kid) ?? null
  try {
    const response = await outbound.fetch(url, { headers: { Accept: 'application/json' } })
    if (response.ok) {
      const keys = new Map()
      for (const jwk of (await response.json())?.keys ?? []) {
        if (jwk?.kty !== 'RSA' || typeof jwk.kid !== 'string' || (jwk.alg && jwk.alg !== 'RS256') || (jwk.use && jwk.use !== 'sig')) continue
        try {
          keys.set(jwk.kid, await crypto.subtle.importKey('jwk', { kty: 'RSA', n: jwk.n, e: jwk.e }, { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['verify']))
        } catch { /* not a usable key */ }
      }
      jwksCache.set(url, { keys, at: now })
      return keys.get(kid) ?? null
    }
  } catch { /* unreachable: the keys already held still count */ }
  return cached?.keys.get(kid) ?? null
}

/** For the tests: the JWKS kept in memory forgotten. */
export const forgetKeys = () => jwksCache.clear()

const appleKeys = new Map()

/**
 * Apple's client secret: a JWT signed ES256 with the Services ID's key (the .p8, PKCS8 PEM), header kid the key's id,
 * iss the team, sub the Services ID, aud Apple, good for appleSecretSeconds.
 */
async function appleClientSecret(env) {
  let key = appleKeys.get(env.APPLE_PRIVATE_KEY)
  if (!key) {
    // A secret pasted with literal \n line breaks reads the same as the file.
    const body = env.APPLE_PRIVATE_KEY.replaceAll('\\n', '\n').replace(/-----(?:BEGIN|END) PRIVATE KEY-----/g, '').replace(/\s+/g, '')
    key = await crypto.subtle.importKey('pkcs8', Uint8Array.from(atob(body), c => c.charCodeAt(0)), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign'])
    appleKeys.set(env.APPLE_PRIVATE_KEY, key)
  }
  const now = seconds()
  const part = value => b64url(utf8(JSON.stringify(value)))
  const signed = `${part({ alg: 'ES256', kid: env.APPLE_KEY_ID })}.${part({ iss: env.APPLE_TEAM_ID, iat: now, exp: now + limits.appleSecretSeconds, aud: 'https://appleid.apple.com', sub: env.APPLE_SERVICES_ID })}`
  // WebCrypto's ECDSA signature is r || s, 64 bytes: the form a JWS wants.
  const signature = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key, utf8(signed)))
  return `${signed}.${b64url(signature)}`
}

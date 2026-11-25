const MAX_BODY_BYTES = 8192
const ROUTE_RE = /^\/oauth\/([a-z0-9-]+)\/token$/
const FIXED_LOOPBACK_RE = /^http:\/\/127\.0\.0\.1:4782[3-5]\/callback$/
const GOOGLE_LOOPBACK_RE = /^http:\/\/(?:127\.0\.0\.1|\[::1\]|localhost):([1-9]\d{0,4})(\/callback|\/)?$/
const PKCE_VERIFIER_RE = /^[A-Za-z0-9\-._~]{43,128}$/
const CLIENT_ID_RE = /^[^\s]{8,256}$/

export const PROVIDERS = Object.freeze({
  google: Object.freeze({
    tokenUrl: 'https://oauth2.googleapis.com/token',
    secretEnv: 'EXO_OAUTH_GOOGLE_CLIENT_SECRET',
    authMode: 'body',
  }),
  battlenet: Object.freeze({
    tokenUrl: 'https://oauth.battle.net/token',
    secretEnv: 'EXO_OAUTH_BATTLENET_CLIENT_SECRET',
    authMode: 'basic',
  }),
  riot: Object.freeze({
    tokenUrl: 'https://auth.riotgames.com/token',
    secretEnv: 'EXO_OAUTH_RIOT_CLIENT_SECRET',
    authMode: 'basic',
  }),
  epic: Object.freeze({
    tokenUrl: 'https://api.epicgames.dev/epic/oauth/v2/token',
    secretEnv: 'EXO_OAUTH_EPIC_CLIENT_SECRET',
    authMode: 'basic',
  }),
})

const SECURITY_HEADERS = Object.freeze({
  'Strict-Transport-Security': 'max-age=31536000',
  'X-Content-Type-Options': 'nosniff',
  'Cache-Control': 'no-store',
  Pragma: 'no-cache',
})

function jsonError(status, error, extraHeaders = {}) {
  return new Response(JSON.stringify({ error }), {
    status,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      ...SECURITY_HEADERS,
      ...extraHeaders,
    },
  })
}

function ipKey(request) {
  const raw = (request.headers.get('CF-Connecting-IP') || 'unknown').trim()
  if (!raw.includes(':')) return raw
  const lower = raw.toLowerCase()
  const parts = lower.split('::')
  if (parts.length > 2) return lower
  const left = parts[0] ? parts[0].split(':') : []
  const right = parts.length === 2 && parts[1] ? parts[1].split(':') : []
  const missing = 8 - (left.length + right.length)
  if (missing < 0) return lower
  const full = [...left, ...Array(missing).fill('0'), ...right]
    .slice(0, 8)
    .map(g => g.padStart(4, '0'))
  return `${full[0]}:${full[1]}:${full[2]}:${full[3].slice(0, 2)}00::/56`
}

export function isAllowedRedirectUri(providerName, uri) {
  if (typeof uri !== 'string' || uri.length === 0 || uri.length > 256) return false
  if (uri.includes('?') || uri.includes('#') || uri.includes('@')) return false
  if (providerName === 'google') {
    const match = GOOGLE_LOOPBACK_RE.exec(uri)
    if (!match) return false
    const port = Number.parseInt(match[1], 10)
    return Number.isInteger(port) && port >= 1024 && port <= 65535
  }
  return FIXED_LOOPBACK_RE.test(uri)
}

async function readBoundedText(request) {
  const declared = request.headers.get('Content-Length')
  if (declared !== null) {
    const len = Number.parseInt(declared, 10)
    if (Number.isFinite(len) && len > MAX_BODY_BYTES) return { tooLarge: true, text: '' }
  }
  if (!request.body) return { tooLarge: false, text: '' }
  const reader = request.body.getReader()
  const chunks = []
  let total = 0
  while (true) {
    const { done, value } = await reader.read()
    if (done) break
    total += value.byteLength
    if (total > MAX_BODY_BYTES) {
      await reader.cancel().catch(() => {})
      return { tooLarge: true, text: '' }
    }
    chunks.push(value)
  }
  const merged = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    merged.set(chunk, offset)
    offset += chunk.byteLength
  }
  return { tooLarge: false, text: new TextDecoder().decode(merged) }
}

function encodeBasic(clientId, clientSecret) {
  const bytes = new TextEncoder().encode(`${clientId}:${clientSecret}`)
  let binary = ''
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i])
  return `Basic ${btoa(binary)}`
}

export function createHandler(fetchImpl = fetch) {
  return async function handle(request, env = {}) {
    const url = new URL(request.url)
    const match = ROUTE_RE.exec(url.pathname)
    if (!match) return jsonError(404, 'not-found')

    if (request.method !== 'POST') {
      return jsonError(405, 'method-not-allowed', { Allow: 'POST' })
    }

    const providerName = match[1]
    const provider = Object.hasOwn(PROVIDERS, providerName) ? PROVIDERS[providerName] : null
    if (!provider) return jsonError(404, 'unsupported-provider')

    if (env.TOKEN_EXCHANGE_LIMIT && typeof env.TOKEN_EXCHANGE_LIMIT.limit === 'function') {
      const verdict = await env.TOKEN_EXCHANGE_LIMIT.limit({ key: `${providerName}:${ipKey(request)}` })
      if (!verdict?.success) return jsonError(429, 'slow-down')
    }

    const contentType = (request.headers.get('Content-Type') || '').toLowerCase()
    if (!contentType.startsWith('application/x-www-form-urlencoded')) {
      return jsonError(415, 'unsupported-media-type')
    }

    const { tooLarge, text } = await readBoundedText(request)
    if (tooLarge) return jsonError(413, 'body-too-large')

    const incoming = new URLSearchParams(text)
    const grantType = incoming.get('grant_type') || ''
    if (grantType !== 'authorization_code' && grantType !== 'refresh_token') {
      return jsonError(400, 'unsupported-grant-type')
    }

    const clientId = (incoming.get('client_id') || '').trim()
    if (!CLIENT_ID_RE.test(clientId)) {
      return jsonError(400, 'invalid-client-id')
    }

    const secretRaw = env[provider.secretEnv]
    const clientSecret = typeof secretRaw === 'string' ? secretRaw.trim() : ''
    if (!clientSecret) {
      return jsonError(503, 'provider-not-configured')
    }

    const forward = new URLSearchParams()
    forward.set('grant_type', grantType)

    if (grantType === 'authorization_code') {
      const code = (incoming.get('code') || '').trim()
      if (!code || code.length > 2048) return jsonError(400, 'missing-code')

      const codeVerifier = (incoming.get('code_verifier') || '').trim()
      if (!PKCE_VERIFIER_RE.test(codeVerifier)) {
        return jsonError(400, 'missing-code-verifier')
      }

      const redirectUri = (incoming.get('redirect_uri') || '').trim()
      if (!isAllowedRedirectUri(providerName, redirectUri)) {
        return jsonError(400, 'invalid-redirect-uri')
      }

      forward.set('code', code)
      forward.set('code_verifier', codeVerifier)
      forward.set('redirect_uri', redirectUri)
    } else {
      const refreshToken = (incoming.get('refresh_token') || '').trim()
      if (!refreshToken || refreshToken.length > 4096) {
        return jsonError(400, 'missing-refresh-token')
      }
      forward.set('refresh_token', refreshToken)
    }

    const scope = (incoming.get('scope') || '').trim()
    if (scope && scope.length <= 512) {
      forward.set('scope', scope)
    }

    const upstreamHeaders = {
      'Content-Type': 'application/x-www-form-urlencoded',
      Accept: 'application/json',
    }
    if (provider.authMode === 'body') {
      forward.set('client_id', clientId)
      forward.set('client_secret', clientSecret)
    } else {
      upstreamHeaders.Authorization = encodeBasic(clientId, clientSecret)
    }

    let upstream
    try {
      upstream = await fetchImpl(provider.tokenUrl, {
        method: 'POST',
        headers: upstreamHeaders,
        body: forward.toString(),
      })
    } catch {
      return jsonError(502, 'upstream-unreachable')
    }

    const upstreamBody = await upstream.arrayBuffer()
    const upstreamContentType = upstream.headers.get('Content-Type') || 'application/json; charset=utf-8'
    return new Response(upstreamBody, {
      status: upstream.status,
      headers: {
        'Content-Type': upstreamContentType,
        ...SECURITY_HEADERS,
      },
    })
  }
}

export default {
  fetch: createHandler(),
}

import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { PROVIDERS, createHandler, isAllowedRedirectUri } from '../src/worker.js'

const VALID_VERIFIER = 'abcdefghijklmnopqrstuvwxyz0123456789-._~ABCD'

class MockLimiter {
  constructor(limit = 30) {
    this.limitCount = limit
    this.counts = new Map()
  }

  async limit({ key }) {
    const n = (this.counts.get(key) || 0) + 1
    this.counts.set(key, n)
    return { success: n <= this.limitCount }
  }
}

function makeEnv(overrides = {}) {
  return {
    EXO_OAUTH_GOOGLE_CLIENT_SECRET: 'google_super_secret_value_1',
    EXO_OAUTH_BATTLENET_CLIENT_SECRET: 'bnet_super_secret_value_2',
    EXO_OAUTH_RIOT_CLIENT_SECRET: 'riot_super_secret_value_3',
    EXO_OAUTH_EPIC_CLIENT_SECRET: 'epic_super_secret_value_4',
    TOKEN_EXCHANGE_LIMIT: new MockLimiter(30),
    ...overrides,
  }
}

function formRequest(path, params, { method = 'POST', ip = '203.0.113.10', contentType = 'application/x-www-form-urlencoded' } = {}) {
  const body = typeof params === 'string' ? params : new URLSearchParams(params).toString()
  return new Request(`https://auth.getexo.app${path}`, {
    method,
    headers: {
      'Content-Type': contentType,
      'CF-Connecting-IP': ip,
    },
    body: method === 'POST' ? body : undefined,
  })
}

test('all 4 confidential providers attach Wrangler secret, hit exact upstream URL, forward fields, and return response as-is without echoing secret', async () => {
  const cases = [
    {
      provider: 'google',
      expectedUrl: 'https://oauth2.googleapis.com/token',
      redirectUri: 'http://127.0.0.1:54321/callback',
      authMode: 'body',
      secret: 'google_super_secret_value_1',
    },
    {
      provider: 'battlenet',
      expectedUrl: 'https://oauth.battle.net/token',
      redirectUri: 'http://127.0.0.1:47823/callback',
      authMode: 'basic',
      secret: 'bnet_super_secret_value_2',
    },
    {
      provider: 'riot',
      expectedUrl: 'https://auth.riotgames.com/token',
      redirectUri: 'http://127.0.0.1:47824/callback',
      authMode: 'basic',
      secret: 'riot_super_secret_value_3',
    },
    {
      provider: 'epic',
      expectedUrl: 'https://api.epicgames.dev/epic/oauth/v2/token',
      redirectUri: 'http://127.0.0.1:47825/callback',
      authMode: 'basic',
      secret: 'epic_super_secret_value_4',
    },
  ]

  for (const tc of cases) {
    const calls = []
    const handler = createHandler(async (url, init) => {
      calls.push({ url, init })
      return new Response(
        JSON.stringify({ access_token: `tok_${tc.provider}`, token_type: 'Bearer', expires_in: 3600 }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })

    const res = await handler(
      formRequest(`/oauth/${tc.provider}/token`, {
        grant_type: 'authorization_code',
        client_id: 'official_client_id_1234',
        client_secret: 'attacker_injected_secret',
        code: 'auth_code_xyz',
        code_verifier: VALID_VERIFIER,
        redirect_uri: tc.redirectUri,
      }),
      makeEnv(),
    )

    assert.equal(res.status, 200)
    assert.equal(res.headers.get('Access-Control-Allow-Origin'), null)
    assert.equal(res.headers.get('Cache-Control'), 'no-store')
    assert.equal(res.headers.get('X-Content-Type-Options'), 'nosniff')
    assert.equal(res.headers.get('Strict-Transport-Security'), 'max-age=31536000')

    const text = await res.text()
    assert.ok(!text.includes(tc.secret), 'secret must never be echoed back')
    const json = JSON.parse(text)
    assert.equal(json.access_token, `tok_${tc.provider}`)

    assert.equal(calls.length, 1)
    assert.equal(calls[0].url, tc.expectedUrl)
    const sentBody = new URLSearchParams(calls[0].init.body)
    assert.equal(sentBody.get('grant_type'), 'authorization_code')
    assert.equal(sentBody.get('code'), 'auth_code_xyz')
    assert.equal(sentBody.get('code_verifier'), VALID_VERIFIER)
    assert.equal(sentBody.get('redirect_uri'), tc.redirectUri)

    if (tc.authMode === 'body') {
      assert.equal(sentBody.get('client_id'), 'official_client_id_1234')
      assert.equal(sentBody.get('client_secret'), tc.secret)
      assert.equal(calls[0].init.headers.Authorization, undefined)
    } else {
      assert.equal(sentBody.get('client_secret'), null)
      const expectedAuth = `Basic ${btoa(`official_client_id_1234:${tc.secret}`)}`
      assert.equal(calls[0].init.headers.Authorization, expectedAuth)
    }
  }
})

test('refresh_token grant forwards refresh_token with secret attached and preserves upstream error status as-is', async () => {
  let seen = null
  const handler = createHandler(async (url, init) => {
    seen = { url, init }
    return new Response(JSON.stringify({ error: 'invalid_grant' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    })
  })

  const res = await handler(
    formRequest('/oauth/epic/token', {
      grant_type: 'refresh_token',
      client_id: 'official_epic_1234',
      refresh_token: 'ref_tok_abc123',
    }),
    makeEnv(),
  )

  assert.equal(res.status, 400)
  const payload = await res.json()
  assert.deepEqual(payload, { error: 'invalid_grant' })
  assert.equal(seen.url, PROVIDERS.epic.tokenUrl)
  assert.equal(seen.init.headers.Authorization, `Basic ${btoa('official_epic_1234:epic_super_secret_value_4')}`)
  const body = new URLSearchParams(seen.init.body)
  assert.equal(body.get('grant_type'), 'refresh_token')
  assert.equal(body.get('refresh_token'), 'ref_tok_abc123')
})

test('disallowed providers, redirects, grants, and missing PKCE code_verifier are rejected before calling upstream', async () => {
  let upstreamCalls = 0
  const handler = createHandler(async () => {
    upstreamCalls++
    return new Response('{}', { status: 200 })
  })
  const env = makeEnv()

  for (const badProvider of ['spotify', 'discord', 'twitch', 'microsoft', 'steam', 'unknown']) {
    const res = await handler(
      formRequest(`/oauth/${badProvider}/token`, {
        grant_type: 'authorization_code',
        client_id: 'official_client_1234',
        code: 'c',
        code_verifier: VALID_VERIFIER,
        redirect_uri: 'http://127.0.0.1:47823/callback',
      }),
      env,
    )
    assert.equal(res.status, 404, `provider ${badProvider} should be rejected`)
  }

  for (const badGrant of ['client_credentials', 'password', 'implicit', 'device_code', '']) {
    const res = await handler(
      formRequest('/oauth/riot/token', {
        grant_type: badGrant,
        client_id: 'official_client_1234',
      }),
      env,
    )
    assert.equal(res.status, 400)
    assert.equal((await res.json()).error, 'unsupported-grant-type')
  }

  for (const badVerifier of ['', 'too_short', 'invalid spaces '.repeat(4)]) {
    const res = await handler(
      formRequest('/oauth/battlenet/token', {
        grant_type: 'authorization_code',
        client_id: 'official_client_1234',
        code: 'code_123',
        code_verifier: badVerifier,
        redirect_uri: 'http://127.0.0.1:47823/callback',
      }),
      env,
    )
    assert.equal(res.status, 400)
    assert.equal((await res.json()).error, 'missing-code-verifier')
  }

  const badRedirects = [
    ['battlenet', 'http://127.0.0.1:47822/callback'],
    ['battlenet', 'http://127.0.0.1:47826/callback'],
    ['riot', 'http://localhost:47823/callback'],
    ['epic', 'https://evil.example/callback'],
    ['epic', 'http://127.0.0.1:47823/callback?extra=1'],
    ['google', 'https://evil.example/callback'],
    ['google', 'http://127.0.0.1:80/callback'],
    ['google', 'https://127.0.0.1:54321/callback'],
    ['google', 'http://127.0.0.1:54321/other'],
  ]
  for (const [provider, uri] of badRedirects) {
    const res = await handler(
      formRequest(`/oauth/${provider}/token`, {
        grant_type: 'authorization_code',
        client_id: 'official_client_1234',
        code: 'code_123',
        code_verifier: VALID_VERIFIER,
        redirect_uri: uri,
      }),
      env,
    )
    assert.equal(res.status, 400, `${provider} should reject ${uri}`)
    assert.equal((await res.json()).error, 'invalid-redirect-uri')
  }

  assert.equal(isAllowedRedirectUri('google', 'http://127.0.0.1:51234'), true)
  assert.equal(isAllowedRedirectUri('google', 'http://127.0.0.1:51234/'), true)
  assert.equal(isAllowedRedirectUri('google', 'http://127.0.0.1:51234/callback'), true)
  assert.equal(isAllowedRedirectUri('google', 'http://[::1]:51234/callback'), true)
  assert.equal(isAllowedRedirectUri('google', 'http://localhost:51234/callback'), true)
  assert.equal(upstreamCalls, 0)
})

test('missing Wrangler secret fails closed with 503, per-IP rate limit returns 429, oversized body returns 413, and CORS is off', async () => {
  let upstreamCalls = 0
  const handler = createHandler(async () => {
    upstreamCalls++
    return new Response(JSON.stringify({ access_token: 'ok' }), { status: 200 })
  })

  const missingSecretRes = await handler(
    formRequest('/oauth/google/token', {
      grant_type: 'authorization_code',
      client_id: 'official_google_1234',
      code: 'c1',
      code_verifier: VALID_VERIFIER,
      redirect_uri: 'http://127.0.0.1:50000/callback',
    }),
    makeEnv({ EXO_OAUTH_GOOGLE_CLIENT_SECRET: '' }),
  )
  assert.equal(missingSecretRes.status, 503)
  assert.equal((await missingSecretRes.json()).error, 'provider-not-configured')
  assert.equal(upstreamCalls, 0)

  const optRes = await handler(
    new Request('https://auth.getexo.app/oauth/google/token', { method: 'OPTIONS' }),
    makeEnv(),
  )
  assert.equal(optRes.status, 405)
  assert.equal(optRes.headers.get('Access-Control-Allow-Origin'), null)
  assert.equal(optRes.headers.get('Access-Control-Allow-Methods'), null)

  const bigBody = `grant_type=authorization_code&client_id=official_google_1234&code=${'a'.repeat(9000)}`
  const bigRes = await handler(
    formRequest('/oauth/google/token', bigBody),
    makeEnv(),
  )
  assert.equal(bigRes.status, 413)
  assert.equal((await bigRes.json()).error, 'body-too-large')

  const limitedEnv = makeEnv({ TOKEN_EXCHANGE_LIMIT: new MockLimiter(2) })
  const makeGood = () => formRequest('/oauth/riot/token', {
    grant_type: 'authorization_code',
    client_id: 'official_riot_1234',
    code: 'c1',
    code_verifier: VALID_VERIFIER,
    redirect_uri: 'http://127.0.0.1:47823/callback',
  }, { ip: '198.51.100.7' })

  assert.equal((await handler(makeGood(), limitedEnv)).status, 200)
  assert.equal((await handler(makeGood(), limitedEnv)).status, 200)
  const limited = await handler(makeGood(), limitedEnv)
  assert.equal(limited.status, 429)
  assert.equal((await limited.json()).error, 'slow-down')
})

test('worker source has zero console logging and wrangler.toml disables observability', () => {
  const src = readFileSync(new URL('../src/worker.js', import.meta.url), 'utf8')
  assert.doesNotMatch(src, /\bconsole\s*\./)
  const toml = readFileSync(new URL('../wrangler.toml', import.meta.url), 'utf8')
  assert.match(toml, /\[observability\]\s*\r?\nenabled = false/)
  assert.doesNotMatch(toml, /CLIENT_SECRET\s*=/)
})

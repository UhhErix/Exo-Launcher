# Exo OAuth Token-Exchange Proxy (`auth.getexo.app`)

Cloudflare Worker that performs confidential OAuth 2.0 `/token` exchanges on behalf of Exo's native desktop client for the 4 providers that enforce a `client_secret` on `/token` even when PKCE is used:

| Provider | Proxy Route | Upstream Token Endpoint | Secret Name (`wrangler secret put`) | Secret Auth Mode |
|---|---|---|---|---|
| **Google** | `POST /oauth/google/token` | `https://oauth2.googleapis.com/token` | `EXO_OAUTH_GOOGLE_CLIENT_SECRET` | Form body (`client_secret`) |
| **Battle.net** | `POST /oauth/battlenet/token` | `https://oauth.battle.net/token` | `EXO_OAUTH_BATTLENET_CLIENT_SECRET` | HTTP Basic (`client_id:client_secret`) |
| **Riot Games (RSO)** | `POST /oauth/riot/token` | `https://auth.riotgames.com/token` | `EXO_OAUTH_RIOT_CLIENT_SECRET` | HTTP Basic (`client_id:client_secret`) |
| **Epic Games (EOS/EAS)** | `POST /oauth/epic/token` | `https://api.epicgames.dev/epic/oauth/v2/token` | `EXO_OAUTH_EPIC_CLIENT_SECRET` | HTTP Basic (`client_id:client_secret`) |

Providers that work natively without a secret (`Spotify`, `Discord`, `Microsoft / Xbox`, `Twitch`) talk directly to their own OAuth endpoints and are rejected with `404` here.

## Security Invariants

- **Strict allowlists:** Only the 4 providers above, only their exact upstream token URLs, only `grant_type=authorization_code` or `refresh_token`, and only `redirect_uri` matching `http://127.0.0.1:4782[3-5]/callback` (or documented Google loopback `http://(127.0.0.1|[::1]|localhost):<1024..65535>[/|/callback]`).
- **PKCE enforced:** `authorization_code` requires a valid RFC 7636 `code_verifier` (43–128 unreserved characters).
- **Secrets isolated:** Every `client_secret` lives exclusively in Cloudflare Wrangler secrets (`wrangler secret put`), never in GitHub repo variables/secrets, never in `wrangler.toml`, never echoed in responses, and never logged. Missing secrets fail closed with `503 provider-not-configured`.
- **No logging, no CORS, bounded bodies, per-IP rate limit:** `[observability] enabled = false`, zero `console.*` calls, no `Access-Control-Allow-*` headers, 8 KiB request body cap (`413`), and Cloudflare edge `TOKEN_EXCHANGE_LIMIT` (`30/min` per provider + IP / IPv6 `/56`).

## Local Verification

```sh
cd apps/launcher/cloud/auth-proxy
npm test
```

## Deploy Steps (Owner Approval Required)

Do **not** store any `EXO_OAUTH_*_CLIENT_SECRET` in GitHub repo variables or GitHub secrets. Set each secret directly in Cloudflare Wrangler before deploying:

1. **Test on dev first (`exo-auth-proxy-dev`):**
   ```sh
   cd apps/launcher/cloud/auth-proxy
   npx wrangler@4.168.0 secret put EXO_OAUTH_GOOGLE_CLIENT_SECRET --env dev
   npx wrangler@4.168.0 secret put EXO_OAUTH_BATTLENET_CLIENT_SECRET --env dev
   npx wrangler@4.168.0 secret put EXO_OAUTH_RIOT_CLIENT_SECRET --env dev
   npx wrangler@4.168.0 secret put EXO_OAUTH_EPIC_CLIENT_SECRET --env dev
   npx wrangler@4.168.0 deploy --env dev
   ```
2. **Set production secrets (`exo-auth-proxy` → `auth.getexo.app`):**
   ```sh
   npx wrangler@4.168.0 secret put EXO_OAUTH_GOOGLE_CLIENT_SECRET
   npx wrangler@4.168.0 secret put EXO_OAUTH_BATTLENET_CLIENT_SECRET
   npx wrangler@4.168.0 secret put EXO_OAUTH_RIOT_CLIENT_SECRET
   npx wrangler@4.168.0 secret put EXO_OAUTH_EPIC_CLIENT_SECRET
   ```
3. **Deploy production (only with owner OK):**
   ```sh
   npx wrangler@4.168.0 deploy
   ```
4. **Live smoke check (read-only, no credentials):**
   - `POST https://auth.getexo.app/oauth/spotify/token` → `404 {"error":"unsupported-provider"}`
   - `OPTIONS https://auth.getexo.app/oauth/google/token` → `405 {"error":"method-not-allowed"}` with zero `Access-Control-Allow-*` headers
5. **Rollback (if needed):**
   ```sh
   npx wrangler@4.168.0 rollback
   ```

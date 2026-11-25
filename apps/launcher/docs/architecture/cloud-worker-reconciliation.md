# Cloud Worker Drift Audit & Reconciliation Plan (`exo-account` & `exo-updates`)

Read-only audit conducted on **2026-11-25** ahead of the Exo Launcher `4.0.0` release. No Cloudflare Workers, D1 databases, secrets, or routes were mutated during this audit.

---

## 1. Live `exo-account` (`b28c727d-af5d-4be3-be15-cd684d4835c6`) Drift Findings

### Provenance
- **Deployed version:** `b28c727d-af5d-4be3-be15-cd684d4835c6` (`2026-11-15T02:19:04Z`, `Source: Unknown (version_upload)`, `Tag: -`, `Message: -`, `Compatibility Date: 2026-10-01`). Preceded on `2026-11-15` by 6 secret-put versions (`01:44Z`–`01:49Z`) and 3 deployments (`31d7ba50`, `66bd40ad`, `34be4b14`).
- **Repository search:** Exhaustive search across every branch, commit, and PR in `UhhErix/Exo-Launcher` and `UhhErix/Exo` confirmed that `0008_identities.sql`, `src/oauth.js`, `src/providers.js`, `APPLE_APP_ID`, `PUBLIC_URL`, and `SYNC_TRIES` **do not exist in either repository**.
- **Source map inspection (read-only):** The deployed bundle's source map references standalone relative paths (`src\providers.js`, `src\oauth.js`, `src\worker.js`, `src\accounts.js`, `src\http.js`, `src\limits.js`, `src\crypto.js`, `src\common-passwords.js`), confirming `b28c727d` was uploaded from a standalone checkout that added **Sign in with Apple and Sign in with Google** on top of the 2026-11-09 `exo-account` baseline (`0001`–`0007`).

### Live D1 Schema & Data (`exo-account`)
- Applied `d1_migrations`: `0001_init.sql` through `0007_devices.sql`, **plus `0008_identities.sql`**.
- `0008_identities.sql` creates:
  - `identities(provider, subject_mac, account REFERENCES accounts(id), refresh BLOB, created)` with `PRIMARY KEY (provider, subject_mac)` and `UNIQUE INDEX identities_by_account ON identities(account, provider)` — **2 active live rows** as of 2026-11-25.
  - `grants(grant_hash PRIMARY KEY, purpose, provider, subject_mac, refresh BLOB, challenge, account, expires)` with `INDEX grants_by_expiry ON grants(expires)` — 0 rows.
- `passkeys` and `webauthn_challenges` do not exist yet; `sessions` does not yet have an `app` column.

### Live Worker Bindings, Secrets & Routes Missing from `apps/launcher/cloud/account`
- **Secrets (never overwrite `ACCOUNT_SECRETS`):** `ACCOUNT_SECRETS`, `APPLE_KEY_ID`, `APPLE_PRIVATE_KEY`, `APPLE_SERVICES_ID`, `APPLE_TEAM_ID`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`.
- **Bindings / Vars:** `env.DB` (`exo-account`), `env.SESSION_TRIES` (`10/60s`), `env.SYNC_TRIES` (`120/60s`), `env.APPLE_APP_ID` (`"app.getexo.exo"`), `env.PUBLIC_URL` (`"https://accounts.getexo.app"`), custom domain `accounts.getexo.app`.
- **8 live OAuth routes (`OAUTH_ROUTES` in `src/oauth.js` + `src/providers.js`):**
  - `GET /v1/providers`
  - `POST /v1/oauth/start`
  - `GET /v1/oauth/callback/(apple|google)`
  - `POST /v1/oauth/callback/(apple|google)` (requires `formOf` in `src/http.js` for Apple `form_post`)
  - `POST /v1/oauth/redeem`
  - `GET /v1/methods`
  - `DELETE /v1/methods/(apple|google)`
  - `POST /v1/oauth/notify/apple`
- **Behavioural deltas in live `b28c727d`:**
  - `GET /v1/account` includes `methods: await methodsOf(env, account)`.
  - `DELETE /v1/account` supports passwordless Apple/Google accounts via `input.proof` (`proofRight`), revokes Apple refresh tokens, and deletes `identities` and `grants` (`forgetIdentities`).
  - `PUT /v1/password` and `POST /v1/recovery` return `409 no-password` when `!hasPassword(record)` (`email_mac` starts with `"-:"`).
  - `PUT /v1/sync` enforces per-account `env.SYNC_TRIES` (`120/60s`).
  - `src/accounts.js` exports `subjectLookups`, `sealWithRing`, `unsealWithRing`, and `ringMacKey`.
  - `src/limits.js` defines `stateSeconds`, `grantSeconds`, `proofSeconds`, `skewSeconds`, `jwksMs`, `jwksRetryMs`, and `appleSecretSeconds`.
- **Impact of deploying `apps/launcher/cloud/account` as-is:** **BLOCKED.** Deploying the current `release/4.0` folder over `b28c727d` would remove Sign in with Apple/Google, break the 2 active `identities` accounts, drop `SYNC_TRIES`, and collide on migration number `0008`.

### What `release/4.0` `apps/launcher/cloud/account` Adds Over Live `b28c727d`
- PR `#156`: `notModified` (`304`) ETag short-circuit on `GET /v1/sync?after=N`.
- PR `#167`: WebAuthn passkeys (`src/webauthn.js`, `WEBAUTHN_RP_ID`, `WEBAUTHN_ORIGIN`, passkey routes, `passkeys` + `webauthn_challenges` cleanup in `deleteAccount`, migration now renumbered to `0010_passkeys.sql`).
- PR `#223`/`#224`: Per-app session scoping (`sessions.app`, `appOf(input)` in `newSession`, `GET /v1/sessions` returning `app`, migration now renumbered to `0011_session_app.sql`).
- Compatibility date bump `2026-10-01` → `2026-11-18`.

---

## 2. Live `exo-updates` (`f75047c2-996a-448e-bc10-01879ce99f55`) Drift Findings

### Provenance
- **Deployed version:** `f75047c2-996a-448e-bc10-01879ce99f55` (`2026-11-21T03:48:31Z`, `Source: Unknown (version_upload)`, `Tag: -`, `Message: -`, `Compatibility Date: 2026-11-18`, handlers `fetch, scheduled`).
- **Secrets & Bindings:** `env.MIRROR` (`exo-releases` R2 bucket), plus 6 Discord secrets (`DISCORD_WEBHOOK_LAUNCHER`, `DISCORD_ROLE_LAUNCHER`, `DISCORD_WEBHOOK_CINEMA`, `DISCORD_ROLE_CINEMA`, `DISCORD_WEBHOOK_BROWSER`, `DISCORD_ROLE_BROWSER`).
- **Source map & commit match:** Source map paths point directly to `Exo-Launcher/apps/launcher/cloud/updates/src/`. Diffing against `UhhErix/Exo-Launcher` commit `64af4e8b` (PR `#107`, merged `2026-11-21T08:45Z`) shows:
  - `src/announce.js`, `src/watch.js`, and `src/worker.js` are **byte-identical** to `64af4e8b`.
  - `src/github.js` differs only by `isGitHubApiUrl(url)`, which `64af4e8b` added right after `f75047c2` was uploaded.
- **Note on `UhhErix/Exo`:** `workers/exo-updates` in `UhhErix/Exo` is only the older read-only R2 snapshot (`d124cde`, no `announce.js`, `github.js`, or `watch.js`); `apps/launcher/cloud/updates` in `UhhErix/Exo-Launcher` is the canonical source of live `exo-updates`.

### Behavioural Diff (`f75047c2` → `release/4.0` `apps/launcher/cloud/updates`)
**Verdict: `release/4.0` is a strict superset of live `f75047c2` with zero regressions.**

| File | Change from live `f75047c2` to `release/4.0` | Risk / Regression |
| --- | --- | --- |
| `src/watch.js` | Identical (0 diff) | None |
| `src/announce.js` | Adds `AbortSignal.timeout(10_000)` on Discord `POST`/`DELETE` and 1 retry on HTTP `429` (`retry_after <= 5s`) (PR `#156`) | None; strictly more resilient |
| `src/worker.js` | Splits `IMMUTABLE_SECONDS = 3600` into `TAG_SECONDS = 86400` (immutable tag files) and `ADVISORIES_SECONDS = 3600` (`advisories.json`); adds weak ETag `W/` comparison in `revalidated()` (PR `#156`); serves optional `delta.exopack` / `delta.exopack.sig` and `full.exopack` / `full.exopack.sig` for `launcher` (PR `#197`) | None; older clients ignore extra manifest keys, `cinema` & `/v1/cache/` & `/v1/advisories.json` unchanged |
| `src/github.js` | Adds `isGitHubApiUrl(url)` hostname check (`#107`); adds `deltaPack`/`deltaSig`/`fullPack`/`fullSig` to `NAMES`, `LARGE`, `assetsOf`, `materialize`, and `exoPackFiles` Ed25519 signature check (`#197`) | **Release-day caveat (below):** `materializeDelta` does not backfill `.exopack` |

**Release-day caveat caught in `src/github.js`:** `materializeDelta` (same-tag backfill) only backfills `files.json` and `files.pack`, not `delta.exopack` / `full.exopack`. If a GitHub release is published before `release-delta-sign.yml` finishes attaching `.exopack` + `.sig`, `exo-updates` may promote `launcher/latest.json` without `deltaPack` / `fullPack`. Either:
1. Create the `v4.0.0` GitHub release as `--draft`, run `release-delta-sign.yml` against it (or attach signed packs before un-drafting), and publish once all assets are attached; **or**
2. Extend `materializeDelta` in a small follow-up PR to backfill `deltaPack`/`fullPack` as well.

---

## 3. Reconciliation Plan

### 3.1 Merging `identities`/`grants` with `passkeys` & `session_app`
1. **Migration numbering (completed in this PR):**
   - Added `apps/launcher/cloud/account/migrations/0008_identities.sql` matching the exact live D1 schema (with `IF NOT EXISTS` so fresh/dev databases match live while Wrangler skips `0008_identities.sql` on live `exo-account` because it is already recorded in `d1_migrations`).
   - Renumbered `0008_passkeys.sql` → `0010_passkeys.sql` and `0009_session_app.sql` → `0011_session_app.sql` (`0009` left intentionally unused to avoid any clash with unmerged standalone OAuth work).
   - Updated `apps/launcher/cloud/account/test/migrations.test.mjs` to test `0008_identities.sql` → `0010_passkeys.sql` → `0011_session_app.sql` in sequence with live-like rows in `accounts`, `identities`, and `sessions`.
2. **Code reconciliation (required before any live `exo-account` deploy):**
   - In a dedicated follow-up PR (after owner confirms the canonical source of `src/oauth.js` and `src/providers.js` — either from the original standalone checkout or recovered from live `b28c727d`'s source map):
     - Port `src/providers.js`, `src/oauth.js`, `accounts.js` ring helpers (`subjectLookups`, `sealWithRing`, `unsealWithRing`, `ringMacKey`), `http.js` (`formOf`), and `limits.js` OAuth timeouts into `apps/launcher/cloud/account/src/`.
     - Merge `src/worker.js` so **all** of the following coexist:
       - `OAUTH_ROUTES` + `GET /v1/account` returning `methods` + `DELETE /v1/account` supporting `input.proof`, calling `forgetIdentities`, and deleting `passkeys` + `webauthn_challenges` + `identities` + `grants` + `devices` + `sessions` + `accounts`.
       - `PUT /v1/password` and `POST /v1/recovery` guarding `!hasPassword(record)` with `409 no-password`.
       - `PUT /v1/sync` enforcing `env.SYNC_TRIES` (`120/60s`) and `GET /v1/sync` returning `304` on matching ETag.
       - `newSession(db, account, device, app)` passing `appOf(input)` from password, reset, passkey, and OAuth `redeem` sign-ins.
       - WebAuthn passkey routes (`src/webauthn.js`) reading `WEBAUTHN_RP_ID` / `WEBAUTHN_ORIGIN`.
     - Add unit + workerd tests covering OAuth routes (`GET /v1/providers`, passwordless account deletion with `proof`, `409 no-password`, `SYNC_TRIES`) alongside existing 127 passkey/sync/account tests.
     - Keep `wrangler.toml` in git using placeholder IDs only (`REPLACE_WITH_RESOURCE_ID`, `routes = []`), while documenting the required live bindings (`DB`, `SESSION_TRIES`, `SYNC_TRIES`, `APPLE_APP_ID`, `PUBLIC_URL`, `WEBAUTHN_RP_ID`, `WEBAUTHN_ORIGIN`).

### 3.2 `exo-account-dev` Worker & D1: Needed and Safe Creation Procedure
- **Do we need `exo-account-dev`?** **Yes.** Live `exo-account` holds active user accounts (`identities` rows and sealed items under an irreplaceable `ACCOUNT_SECRETS` keyring), and `wrangler dev --local` does not exercise deployed Cloudflare Workers runtime constraints or bindings (`SESSION_TRIES`, `SYNC_TRIES`, D1 remote migrations). Currently neither the `exo-account-dev` Worker nor the `exo-account-dev` D1 database exists on the Cloudflare account.
- **How to create `exo-account-dev` safely (zero risk to live):**
  1. Never target the default (live) environment; always pass `--env dev` and an explicit out-of-repo `--config <TEMP_TOML>`.
  2. Create a separate dev D1 database:
     ```powershell
     npx wrangler@4.165.0 d1 create exo-account-dev
     ```
  3. Put the new `exo-account-dev` D1 UUID only in `[env.dev]` of an out-of-repo temporary `wrangler.toml` (with `workers_dev = true`, `routes = []`, `WEBAUTHN_RP_ID`, `WEBAUTHN_ORIGIN`, `PUBLIC_URL` pointing at the dev `workers.dev` URL, and dev rate-limit namespaces `SESSION_TRIES` / `SYNC_TRIES`).
  4. Apply all migrations `0001`–`0011` to dev D1:
     ```powershell
     npx wrangler@4.165.0 d1 migrations apply exo-account-dev --remote --env dev --config <TEMP_TOML>
     ```
  5. Deploy the Worker to dev first so the dev script exists:
     ```powershell
     npx wrangler@4.165.0 deploy --env dev --config <TEMP_TOML>
     ```
  6. Provision a **throwaway dev-only** keyring secret **strictly on `--env dev`** (generated via stdin, never printed, and **NEVER** run without `--env dev`):
     ```powershell
     node -e "const c=require('crypto'); process.stdout.write('d1:'+c.randomBytes(32).toString('hex'))" |
       npx wrangler@4.165.0 secret put ACCOUNT_SECRETS --env dev --config <TEMP_TOML>
     ```
  7. Verify dev end-to-end (sign-up, sign-in returning `deviceToken`, `GET /v1/account` returning `methods`, `GET /v1/providers`, sync PUT/GET + 304 ETag, `GET /v1/sessions` returning `app`, passkey register/sign-in routes, password change, recovery reset, account delete; zero 500s).

### 3.3 Exact Ordered Release-Day Deploy Steps (With Backups & Rollback)

> **Hard rules:** Never run `wrangler secret put ACCOUNT_SECRETS` on live `exo-account`. Never run `wrangler deploy` from `UhhErix/Exo`. Keep all out-of-repo D1 exports and temporary `wrangler.toml` files outside any git repository.

#### Step A — `exo-account` (ONLY after Section 3.1 code merge PR is merged & green)
1. **Read-only live migration check:**
   ```powershell
   npx wrangler@4.165.0 d1 execute exo-account --remote --config <TEMP_LIVE_ACCOUNT_TOML> `
     --command "SELECT name FROM d1_migrations ORDER BY id"
   ```
   Expect exactly `0001_init.sql` through `0008_identities.sql`. Stop immediately if any unexpected migration appears.
2. **Export full live D1 backup outside the repository:**
   ```powershell
   npx wrangler@4.165.0 d1 export exo-account --remote --config <TEMP_LIVE_ACCOUNT_TOML> `
     --output <BACKUP_DIR_OUTSIDE_REPO>\exo-account-before-0010-0011.sql
   ```
   Verify the export file is non-empty before continuing. Retain until Step A.6 passes; never commit or delete early.
3. **Deploy and verify `exo-account-dev` first** (Section 3.2 steps 4–7).
4. **Apply live D1 migrations (`0010_passkeys.sql` and `0011_session_app.sql`):**
   ```powershell
   npx wrangler@4.165.0 d1 migrations apply exo-account --remote --config <TEMP_LIVE_ACCOUNT_TOML>
   ```
   Confirm Wrangler skips `0001`–`0008_identities.sql` and applies only `0010_passkeys.sql` and `0011_session_app.sql`.
5. **Deploy live `exo-account` code:**
   Ensure `<TEMP_LIVE_ACCOUNT_TOML>` preserves all live bindings (`DB`, `SESSION_TRIES`, `SYNC_TRIES`, `APPLE_APP_ID = "app.getexo.exo"`, `PUBLIC_URL = "https://accounts.getexo.app"`, `WEBAUTHN_RP_ID = "accounts.getexo.app"`, `WEBAUTHN_ORIGIN = "https://accounts.getexo.app"`, custom domain `accounts.getexo.app`, `observability.enabled = false`). Existing secrets stay untouched.
   ```powershell
   npx wrangler@4.165.0 deploy --config <TEMP_LIVE_ACCOUNT_TOML>
   ```
6. **Live smoke checks:**
   - `GET https://accounts.getexo.app/v1/account` → `401 {"error":"signed-out"}`.
   - `GET https://accounts.getexo.app/v1/providers` → `200 {"providers":["google","apple"]}`.
   - Test account sign-in (`200` with `deviceToken`), `GET /v1/sessions` (includes `app`), sync PUT/GET round-trip (confirm no `500 keyring-mismatch`), and `DELETE /v1/account` cleanup for the throwaway test account.
   - Verify `identities` row count is unchanged (`2`).
7. **Rollback (`exo-account`):**
   ```powershell
   npx wrangler@4.165.0 rollback b28c727d-af5d-4be3-be15-cd684d4835c6 --name exo-account --config <TEMP_LIVE_ACCOUNT_TOML>
   ```
   Both `0010_passkeys.sql` (new tables) and `0011_session_app.sql` (`sessions.app` with `DEFAULT ''`) are strictly additive and safe for `b28c727d`.

#### Step B — `exo-updates` (Ready after owner approval; strict superset of `f75047c2`)
1. **Run test suite locally:** `npm test` in `apps/launcher/cloud/updates`.
2. **Prepare `<TEMP_LIVE_UPDATES_TOML>` outside the repo:**
   `name = "exo-updates"`, `main = "src/worker.js"`, `compatibility_date = "2026-11-18"`, `workers_dev = true`, `routes = [{ pattern = "updates.getexo.app", custom_domain = true }]`, `observability.enabled = false`, `[[r2_buckets]] binding = "MIRROR" bucket_name = "exo-releases"`, `[triggers] crons = ["*/15 * * * *"]`. All 6 existing Discord secrets stay untouched.
3. **Deploy live `exo-updates`:**
   ```powershell
   npx wrangler@4.165.0 deploy --config <TEMP_LIVE_UPDATES_TOML>
   ```
4. **Live smoke checks:**
   - `GET https://updates.getexo.app/v1/launcher/latest` → `200`, current tag (`v3.31.1`), valid ETag + `304` on `If-None-Match` (both strong and `W/` weak form).
   - `GET https://updates.getexo.app/v1/cinema/latest` → `200` (`v1.5.2`).
   - `GET https://updates.getexo.app/v1/advisories.json` → `200` (`max-age=3600`).
   - `GET https://updates.getexo.app/v1/launcher/changelog` → `200`.
5. **Rollback (`exo-updates`):**
   ```powershell
   npx wrangler@4.165.0 rollback f75047c2-996a-448e-bc10-01879ce99f55 --name exo-updates --config <TEMP_LIVE_UPDATES_TOML>
   ```

#### Step C — `exo-auth-proxy`
- Skipped until PR `#212` is merged into `main`.

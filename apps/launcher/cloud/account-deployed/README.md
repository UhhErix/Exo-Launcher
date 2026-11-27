# exo-account, as deployed

The source of the live `exo-account` Worker (version `b28c727d-af5d-4be3-be15-cd684d4835c6`, uploaded 2026-11-15 from a standalone checkout that never reached git), recovered on 2026-11-26 from the `sourcesContent` of the deployed bundle's source map. It adds Sign in with Apple and Google (`src/providers.js`, `src/oauth.js`) and a per-account sync limit (`SYNC_TRIES`) to the 2026-11-09 account Worker. The files in `src/` are the deployed ones, byte for byte, and are excluded from the comment ratchet for that reason.

**Proof:** `npm ci` here, then `wrangler deploy --dry-run --outdir <dir>` bundles to the same code as the live script: after dropping the `// <path>` marker lines and the source-map comment (the live ones carry the uploader's folder), the live `worker.js` and the bundle are identical (122,945 characters on 2026-11-26).

**What this is not:** `../account` is the line that continues to v4 (passkeys, per-app sessions, a 304 on sync) and does not have the Apple and Google code, so deploying it over live would remove them (`docs/architecture/cloud-worker-reconciliation.md` on the `chore/cloud-worker-drift-reconciliation` branch). This folder is the reference for merging the two; nothing deploys from it, and its `wrangler.toml` keeps the placeholder database id. `migrations/0008_identities.sql` is the live schema of the identities and grants tables, taken from that branch.

Secrets the live Worker holds (names only): `ACCOUNT_SECRETS`, `APPLE_KEY_ID`, `APPLE_PRIVATE_KEY`, `APPLE_SERVICES_ID`, `APPLE_TEAM_ID`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`.

# ADR-0011: Reports Worker privacy, indexing, triage, and edge rate limits

## Status

Accepted — 2026-11-20. Extracted from design comments across `apps/launcher/cloud/reports/src/`.

## Context

The `exo-reports` Cloudflare Worker ingests anonymous crash reports (`POST /v1/report`), "Report a problem" user reports (`POST /v1/user-report`), scrubbed telemetry batches (`POST /v1/telemetry`, `POST /v1/telemetry/delete`), and website contact messages (`POST /v1/contact`). None of these flows may leak PII, break user-requested data deletion, or allow flood traffic to exhaust GitHub Issues, D1, R2, or email quotas.

## Decision

1. **Strict schema checks and double-layer PII scrubbing:** Every endpoint rejects unknown fields, wrong types, or out-of-bounds payloads before storing anything (`validate.js`, `user-report.js`, `telemetry.js`, `contact.js`). Client code scrubs crash logs first and the Worker re-scrubs/rejects Windows or UNC paths, home folders, URLs, emails, GUIDs, JWT-like tokens, long numbers, media filenames, and control characters so a buggy client cannot persist PII. On `POST /v1/contact`, a filled honeypot `website` field returns HTTP `202` identically to a stored message without writing to R2 or sending email.
2. **D1 index and R2 lifecycle ordering:** Crash and user-report objects live in R2 under UTC date prefixes (`reports/<UTC date>/<id>.json`) with a 30-day bucket expiration rule. Whenever a report carries an install ID, ingest inserts its D1 index row before or alongside storing so "Delete my data" can always find it (returning `503` if the index is unavailable); `deleteInstall` deletes the R2 objects *before* deleting the D1 index rows so any partial failure leaves index pointers intact for safe retry (`crash-store.js`).
3. **Telemetry batch storage and retention:** Accepted events are inserted in a single D1 JSON-array statement (`telemetry-store.js`) so a 500-event batch takes one query. Oversized single envelopes (`> 1 KB`) or events whose properties all scrub away are dropped individually rather than failing the whole batch (`telemetry.js`). Raw events are retained for 90 days; the daily cron atomically computes anonymous daily aggregates (`events`, distinct installs per app/version/event) and deletes expired raw rows in one batch.
4. **Crash fingerprinting, GitHub issues, and Discord-only error alerts:** Crashes normalize stack frames (`TOP_FRAMES` methods without arguments, file paths, offsets, lambda numbering, or generic arity; `fingerprint.js`) so rebuilds do not split an error group. `dueNow` batches GitHub edits (filing immediately on new group, new version regression, first 10 reports, 10/100/1000 thresholds, or 25% growth) within daily write budgets (`DAILY` caps in `issues.js`), escaping markdown fences dynamically and capping Workers AI `ai.run` diagnoses at 8 seconds without failing report storage. Alert claims use one SQL `ON CONFLICT` increment (`ALERTS_PER_DAY = 50`, returning `'send' | 'pause' | 'hold'`; `triage.js`). Error notifications route strictly to Discord (when not filed to GitHub) and never email the owner; `CONTACT_MAIL` is reserved exclusively for `POST /v1/contact` (`worker.js`).
5. **Tiered edge rate limiting:** Rate limiters run in cheapest-fail-first order at the edge (`worker.js`): sender network key first (IPv6 normalized to `/64` so single-host address rotation cannot bypass limits; IPv4-mapped IPv6 normalized to IPv4), then per-install key, then the global route ceiling (`REPORTS_CEILING`). Requests refused by network or install limits never spend the global ceiling.

## Consequences

- Error-group alerts depend on the Discord webhook or `GITHUB_REPORTS_TOKEN`; email fallback code stays inactive for errors.
- Follow-up improvements tracked in GitHub issues [#100](https://github.com/UhhErix/Exo-Launcher/issues/100) (atomic per-install hourly telemetry cap) and [#101](https://github.com/UhhErix/Exo-Launcher/issues/101) (TTL lease replacing sentinel `FILING = -1` crash-group lock).

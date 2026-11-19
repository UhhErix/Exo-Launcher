# Exo's crash reports (`cloud/reports`)

A Worker that takes crash and freeze reports from Exo Launcher, Cinema and Browser and stores them in R2, and the help page's contact form. Exo Launcher sends crash reports from every install from 3.26.0 (the owner's decision, 2026-11-09: essential, no switch; 3.24.4 and 3.25.0 asked first, and Cinema and Browser still do until they follow). Each app sends only the five fields below, stripped of anything personal, at most a few a day and the same error once. It runs on this PC with `npx wrangler@4.158.0 dev`; deployed at `reports.getexo.app` since November 5, 2026 (the owner's OK), redeployed 7 November 2026 with the contact endpoint (version `5dc2d4e3-4a6d-43c1-8f1a-dc2c27aceee0`).

## What it takes

`POST /v1/report`, `Content-Type: application/json`, exactly these five text fields (`src/validate.js`):

| Field | Shape |
| --- | --- |
| `app` | `launcher`, `cinema` or `browser` |
| `version` | `1.2.3` (up to 4 digits a part) |
| `osBuild` | Windows build, e.g. `10.0.26300.1` |
| `errorType` | an exception or error name, e.g. `SteamGameException` (letters, digits, `_` and `.`; 80 at most) |
| `excerpt` | up to 8192 characters of log lines, with no control characters but tab and newline |

Anything else refuses the report: an unknown field, a wrong type, a value outside its shape, a body over 32 KiB, a request
that is not JSON, or an excerpt that still holds a file path, a URL, an email, a number of 15 or more digits, or a token
(422). The Launcher strips these first (`ExoLauncher/Helpers/CrashReport.cs`); this check is the second line, so a client
bug cannot store them.

Optional sixth field `installId`: the telemetry install ID (8-4-4-4-12 or 32 hex, any case, stored lower-case with hyphens).
Reports without it are stored as before; a malformed one refuses the report (400). With it, the report is also indexed by
install in D1 (`crash_index`, `migrations/0003_crash_index.sql`) so `/v1/telemetry/delete` can remove it. The Launcher does not
send it yet.

Answers: `202` stored, `400` not a report (or a bad `installId`), `404` any other path, `405` not POST, `413` too large,
`415` not JSON, `422` personal data, `429` slow down, `503` the index is unavailable (a report with `installId` is stored only
after its index row is written; the dev copy has no index, so it answers 503 for one).

## What it keeps

- One R2 object per report: `reports/<UTC date>/<random id>.json`, holding only the five fields. The date is the server's
  day; no time, no sender, no IP address.
- With an `installId`, a D1 row in `crash_index`: the install ID, the object's key and the UTC day (so the delete can find the
  objects). Without one, nothing is written to D1 for a crash report.
- The Worker writes no IP address, cookie or client id beyond that install ID. It reads `CF-Connecting-IP` only as a rate
  limiter's key. The rate limits are Cloudflare's rate limiting bindings, counted at the edge per route (`report`,
  `telemetry`, `delete`): `REPORTS_PER_INSTALL` keyed by the install ID, 20 a minute for each install, so any number of
  players online at once never share a count; and `REPORTS_PER_NETWORK` keyed by the sender's address (an IPv6 address by
  its /64), 300 a minute, the backstop against one sender making up install IDs. A report from an old client without an
  install ID meets the network's alone. Last comes `REPORTS_CEILING`, one count per route for every sender at a Cloudflare
  location (Cloudflare's limiter counts per location, not worldwide), 10,000 a minute: the
  cost ceiling, so senders spread over many addresses cannot run up R2 and D1 writes, deletes and alert mails without bound
  (a request refused by its own install or network never spends it). Cloudflare's limiter holds the keys for the minute;
  the Worker stores none of them. A 429 loses nothing: the Launcher keeps a refused telemetry batch and resends it, and
  keeps a crash report refused with 429 or 5xx (or with no answer) as a pending file sent at the next start, at most five.
- Logs are off (`[observability] enabled = false`).

## Lifecycle: 30 days

Reports expire 30 days after they are stored. R2 lifecycle rules are set on the bucket, not in `wrangler.toml`. Once per
bucket, after it exists (done for both):

```
npx wrangler@4.158.0 r2 bucket lifecycle add exo-crash-reports reports-30-days reports/ --expire-days 30
npx wrangler@4.158.0 r2 bucket lifecycle add exo-crash-reports-dev reports-30-days reports/ --expire-days 30
```

`wrangler r2 bucket lifecycle add --help` lists the flags. Check the rule with `wrangler r2 bucket lifecycle list`.

## Triage: the team is told

So an error is worked on the day it happens (owner, 2026-11-09: "auto sent to us so we can work on it immediately"):

- **Counted.** Every stored report is counted in D1 (`crash_days`, `migrations/0004_crash_days.sql`) by app, version, error
  type and UTC day, with the key of the group's newest report. No install ID, no time finer than the day.
- **Told at once.** The first report of an error type in a version, and its 10th, 100th and 1000th, mail the team: the
  contact form's own binding and address (`CONTACT_MAIL` to `CONTACT_TO`), subject `Exo Launcher 3.26.0: UiError (new error)`,
  body the Windows build and the end of the scrubbed excerpt. With the secret `DISCORD_WEBHOOK_REPORTS` (a webhook of a
  private channel; `npx wrangler@4.158.0 secret put DISCORD_WEBHOOK_REPORTS`) the same message is posted there. It runs
  after the answer and never fails a report: the dev copy (no D1), a failed count and a failed send still store it. At most
  50 alerts go out a UTC day (`alert_days`, `migrations/0005_alert_days.sql`), so invented error types cannot mail without
  end; past that the digest still lists every group. Apply 0005 before deploying this code
  (`npx wrangler@4.158.0 d1 migrations apply exo-telemetry --remote`).
- **Digest.** The daily job mails yesterday's groups, most reports first, new ones marked, and removes counts older than 90 days.
- **From this PC.** `node triage.mjs [--days 7] [--app launcher] [--version 3.26.0] [--show 3]` lists the groups and prints
  the newest sample of the top ones, through wrangler's own login. Read only.

What the Launcher sends on its own, with no Copy logs (`ExoLauncher/Helpers/CrashReport.cs`, `FailureReportRules.cs`): a
crash (at the next start), a freeze, a WebView2 exit, Discord failing five times, Exo Capture exiting with an error, a store
launch or install failing, an update failing, and from 3.26.0 every page error (`UiError`: an uncaught error, a rejected
promise, a failure a room caught) and every host call that failed in a way nobody planned for (`HostCallFailed`). The
client's limits hold: one report per error type and cause a day, five a day in all.

## Contact (`POST /v1/contact`)

The help page's form (https://getexo.app/help) posts here as JSON: `email` (optional, for a reply), `app`
(`launcher`, `cinema`, `browser` or `other`) and `message` (required, at most 4000 characters). A hidden `website`
field is the honeypot: a bot that fills it gets `202` and nothing is kept or sent.

- Answers: `202` stored, `400` with `{"field","problem"}` naming the rule (never the text sent), `403` for any `Origin`
  but `https://getexo.app`, `405`, `413`, `415`, `429` (5 a minute per sender IP, rate limit `CONTACT_PER_IP`), `503`
  without R2 (the dev copy has R2, so this is for a bare copy). CORS allows that origin only.
- Stored in R2 at `contact/<UTC date>/<id>.json` as `{app, email, message}`: no IP address, no time, no cookie. The IP is
  read once, as the rate limiter's key, and goes nowhere else.
- Email: the `CONTACT_MAIL` send_email binding (unrestricted, so no address is written in the repo), to `CONTACT_TO` (a secret,
  set on 2026-11-07 to the account's login address, a verified Email Routing destination). From `contact@getexo.app`. Plain text;
  the subject holds the app's name only; `Reply-To` is the sender's address when one was given. The message is stored in R2 first,
  so a failed send still answers 202 and loses nothing; the send's outcome is not logged (the Worker logs are off, and the send's
  error is caught on purpose), so a delivered test is confirmed by the owner's inbox, not by logs.
- Email Routing on getexo.app: enabled 2026-11-07 (status ready). The public null MX (`0 .`) and SPF `v=spf1 -all` were replaced:
  the null MX deleted, and the apex SPF is now `v=spf1 include:_spf.mx.cloudflare.net -all`. Cloudflare's MX records (route1, route2,
  route3) were added by enabling. The catch-all rule is disabled with action drop, so inbound mail to the domain goes nowhere; no
  forwarding rules exist. DMARC (`_dmarc`, `p=reject`) was already in place and was kept.
- Retention: `contact/` expires 90 days after it is stored, by a lifecycle rule on `exo-crash-reports`
  (`contact-90-days`, prefix `contact/`, added 2026-11-07). `reports/` keeps its 30-day rule.
## Telemetry (`/v1/telemetry`, `/v1/telemetry/delete`)

The app telemetry of `misc/diagnostics/SPEC.md` section 5. The crash index (`installId`, below) was deployed 2026-11-06 as version `83cb80f6-6ba6-4f5f-999f-6365c9fef766` (the previous live version was `692cf838-694d-434c-90b5-80ea51c2fafb`, the rollback target).
Raw events are in D1 `exo-telemetry` (`events`), daily aggregates in `aggregates`; the schema is `migrations/0002_telemetry.sql`.

- `POST /v1/telemetry`: a JSON array of envelopes, or `{"schema":1,"events":[...]}`. Answers `202` (also when every event scrubs
  away), `400` for any refusal (empty body, no field named), `403` for any `Origin` header, `413` over 64 KB, `429` over the
  per-install or per-network limit (above) or over 500 events per install per hour, `503` when D1 is down or the
  binding is absent (the dev copy).
- Apps: `launcher`, `cinema`, `cinema1`, `browser`, `mouse`, `shaders`. `cinema1` is in the spec's envelope (5.4), so it is accepted.
- Props: a string that looks personal (a path, URL, email, IP, GUID, token, long number, media file name) drops that prop, not the
  event; an event over 1 KB is dropped whole. Nothing stores an IP address, a user agent or a header, and no body is logged.
- `POST /v1/telemetry/delete` with `{"install_id"}` (`schema` and `app` optional): `204` after its raw events are deleted and
  every crash report indexed under that install is removed (its R2 objects first, then its `crash_index` rows). A failure
  answers `503` and keeps the index, so a retry finishes the job.
- The daily job also deletes `crash_index` rows older than 30 days, the same age as their R2 objects.
- Retention: a cron at `20 4 * * *` UTC. For each UTC day older than 90 days, one batch writes that day's aggregates (day, app,
  version, event, count, distinct installs, no install ID) and deletes its raw rows, so a failed day is rolled back whole.
- Per-install cap: 500 events an hour, counted from `received_at` (best effort: count and insert are two statements).

Cost: one D1 write per stored event and one more at retention, plus the index writes (three indexes on `events`). Storage is
the risk, not writes: D1's database limit is 10 GB, and at roughly 300 bytes a row, 90 days of raw events fit at about 25 events
per install per day across 10,000 installs (about 3 GB at 10 per day). Above that, shorten `keepDays` or sample perf events.

## Tests

`npm test` (Node 22.13+, no packages): `node --test test`. The tests run the real `src/worker.js` and `src/validate.js`
against fake R2 and rate limiter bindings. They cover the shape of each field, the personal-data refusals on realistic
samples, the size caps, the rate limits' keys (10,000 installs in one minute all pass; one sender making up install IDs
is stopped), and that no IP address, cookie or client id reaches R2, and the address no limiter but the network's.

## Before deploy

1. Create the buckets `exo-crash-reports` and `exo-crash-reports-dev` (`wrangler r2 bucket create`), then add the two
   lifecycle rules above.
2. Create the rate limiting namespaces `4002`, `4003` and `4004` (live) and `4102`, `4103` and `4104` (dev) in the account;
   `4001` and `4101` (the per-app limit they replace) are no longer bound. The ids are chosen here; if one is taken on
   the account, change it in `wrangler.toml`.
3. `reports.getexo.app` is a custom domain on the `getexo.app` zone: its DNS record is created on deploy. The updates
   address (`updates.getexo.app`) and the account service use the same zone.
4. Deploy with `npx wrangler@4.158.0 deploy` from this folder (the dev copy with `--env dev`), after the owner's OK.
5. Then `PRIVACY.md` and `NetworkDestinations.cs` already name `reports.getexo.app`; a host change must update both.

Cost on the paid Workers plan: one Worker request and one R2 write per report that passes; nothing is read back.

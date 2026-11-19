# Exo's crash reports (`cloud/reports`)

A Worker that takes crash and freeze reports from Exo Launcher, Cinema and Browser, stores them in R2 and files them as issues in the private GitHub repository UhhErix/Exo-Reports, takes Report a problem messages from the Launcher the same way, and runs the help page's contact form. Exo Launcher sends crash reports from every install from 3.26.0 (the owner's decision, 2026-11-09: essential, no switch; 3.24.4 and 3.25.0 asked first, and Cinema and Browser still do until they follow). Each app sends only the five fields below, stripped of anything personal, at most a few a day and the same error once. It runs on this PC with `npx wrangler@4.165.0 dev`; deployed at `reports.getexo.app` since November 5, 2026 (the owner's OK), redeployed 7 November 2026 with the contact endpoint (version `5dc2d4e3-4a6d-43c1-8f1a-dc2c27aceee0`).

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
  cost ceiling, so senders spread over many addresses cannot run up R2 and D1 writes, deletes and GitHub calls without bound
  (a request refused by its own install or network never spends it). Cloudflare's limiter holds the keys for the minute;
  the Worker stores none of them. A 429 loses nothing: the Launcher keeps a refused telemetry batch and resends it, and
  keeps a crash report refused with 429 or 5xx (or with no answer) as a pending file sent at the next start, at most five.
- Logs are off (`[observability] enabled = false`).

## Lifecycle: 30 days

Reports expire 30 days after they are stored. R2 lifecycle rules are set on the bucket, not in `wrangler.toml`. Once per
bucket, after it exists (done for both):

```
npx wrangler@4.165.0 r2 bucket lifecycle add exo-crash-reports reports-30-days reports/ --expire-days 30
npx wrangler@4.165.0 r2 bucket lifecycle add exo-crash-reports-dev reports-30-days reports/ --expire-days 30
```

`wrangler r2 bucket lifecycle add --help` lists the flags. Check the rule with `wrangler r2 bucket lifecycle list`.

## Issues: the team is told on GitHub

So an error is worked on the day it happens (owner, 2026-11-09: "auto sent to us so we can work on it immediately"), every
error group is an issue in the **private** repository [UhhErix/Exo-Reports](https://github.com/UhhErix/Exo-Reports)
(`src/issues.js`). Private, because UhhErix/Exo-Launcher is public and automatic reports must not land in public issues.
GitHub's own notifications (watch the repository) tell the team. That starts when the secret `GITHUB_REPORTS_TOKEN` is
set; until then the Worker tells the team as it did before (below, **Before the token**), so nothing goes quiet between
the deploy and the token.

- **Grouped.** A report's group is its app, error type and top three stack frames (`src/fingerprint.js`), normalised so a
  rebuild or another PC does not split it: line numbers, offsets, addresses, lambda and generic numbering and arguments are
  taken out. A report with no stack is grouped by its error line with numbers and quoted values taken out. One group is one
  issue across versions; D1 `crash_groups` (`migrations/0006_github_issues.sql`) holds its issue number, count, versions and
  days, with no install ID and no excerpt.
- **The first report opens the issue**, titled `[launcher] NullReferenceException: Namespace.Type.Method`, with the scrubbed
  excerpt, the version and the Windows build, the top frames, and, from Workers AI (the `AI` binding), three lines on the
  likely cause. Labels: `app:launcher|cinema|browser`, `kind:crash` (or `kind:freeze` for a `Hang`), `v<version>` (made on
  the fly) and `new`.
- **Later reports edit its count**, the `<!-- exo:stats -->` block in the body ("Seen N times · versions … · first seen …
  · last seen … · newest version …"); the rest of the body is left as people wrote it. A report tells GitHub at once while
  the group has 10 reports or fewer, at a new version, at a threshold, or when it has a quarter more reports than the issue
  shows; the rest waits for the daily job, so a flood is a few edits. Only 10, 100 and 1000 reports comment, each once, and
  add `count-10`, `count-100` or `count-1000`.
- **Regressions reopen.** When an issue is found closed, the newest version it showed is taken as the version it was
  closed in. A report from a newer version reopens it, labels it `regression` and the new version, and says so in a
  comment; reports from that version or older (players who haven't updated) only move the count.
- **Caps** against a sender inventing errors, versions or messages (`issue_days`): 50 new crash issues, 100 Report a problem
  issues and 2000 edits or comments a UTC day. Past a cap nothing is lost: the group stays pending for the next day.
- **GitHub down never fails a report.** The report is in R2 and answered 202 before GitHub is called (8 seconds at most a
  call). What GitHub did not take stays pending in D1 (`crash_groups.pending`, `issue_queue`) and the daily job sends it,
  at most 100 groups and 100 messages a run, stopping at the first failure.
- **Before the token** (`GITHUB_REPORTS_TOKEN` not set) nothing reaches GitHub and the team is told as before GitHub
  (`src/triage.js`): the first report of an error type in a version, and its 10th, 100th and 1000th, mail the team (the
  contact form's binding and address, `CONTACT_MAIL` to `CONTACT_TO`) and post to the private Discord channel when the
  secret `DISCORD_WEBHOOK_REPORTS` is set; at most 50 a UTC day (`alert_days`, `migrations/0005_alert_days.sql`), and the
  daily job mails yesterday's groups. A Report a problem message is mailed and posted the same way (the player's words
  cut to 1,500 characters, the contact, the reference and its R2 key). Groups and messages are still recorded, and the
  daily job files the backlog as issues once the token is set (a group already past 10 reports is labelled, not
  commented); from then on no alert, digest or message is mailed or posted. The Discord channel and its webhook can be
  removed after that; the Worker no longer uses them.
- **Counted** as before in `crash_days` (`migrations/0004_crash_days.sql`) by app, version, error type and UTC day, for
  `triage.mjs`. The daily job removes counts and cap tallies older than 90 days.
- **From this PC.** `node triage.mjs [--days 7] [--app launcher] [--version 3.26.0] [--show 3]` lists the groups, what is
  waiting for GitHub, and prints the newest sample of the top ones, through wrangler's own login. Read only.

The token: a fine-grained personal access token limited to UhhErix/Exo-Reports with Issues read and write (nothing else),
set with `npx wrangler@4.165.0 secret put GITHUB_REPORTS_TOKEN` from this folder (it asks for the value; nothing is
written to disk). Apply 0006 before deploying this code (`npx wrangler@4.165.0 d1 migrations apply exo-telemetry
--remote`); it keeps 0005's `alert_days`, which the alerts before the token still use.

What the Launcher sends on its own, with no Copy logs (`ExoLauncher/Helpers/CrashReport.cs`, `FailureReportRules.cs`): a
crash (at the next start), a freeze, a WebView2 exit, Discord failing five times, Exo Capture exiting with an error, a store
launch or install failing, an update failing, and from 3.26.0 every page error (`UiError`: an uncaught error, a rejected
promise, a failure a room caught) and every host call that failed in a way nobody planned for (`HostCallFailed`). The
client's limits hold: one report per error type and cause a day, five a day in all.

## Report a problem (`POST /v1/user-report`)

Settings › About › Report a problem in the Launcher posts here, so players don't need a GitHub account
(`src/user-report.js`). JSON: `app`, `version`, `osBuild` (as a crash report), `message` (required, at most 4000 characters,
what the player wrote), `excerpt` (optional: the scrubbed log when Attach logs is on, at most 8192 characters, refused with
422 when it still holds a path, URL, email, long number or token), `contact` (optional, one line, at most 200 characters)
and `installId` (optional, as a crash report).

- Stored in R2 at `reports/<UTC date>/user-<id>.json` (the same 30-day rule) and indexed by install, so Delete my data
  removes it; then filed at once as one issue in Exo-Reports, labelled `app:…`, `kind:user-report`, `v<version>` and `new`.
  The message sits in a code block and the contact in a code span, so neither mentions anyone; the title drops `@` and `#`.
- Answers `202 {"issue": 123}` when filed, or `202 {"ref": "1a2b3c4d"}` when it waits (no token, GitHub down or the day's
  cap; the daily job files it, at most 500 waiting; with no token it is also mailed and posted, as under **Before the
  token**), and `400`, `403` (any `Origin`: only the apps send), `405`, `413`,
  `415`, `422`, `429` (the same per-install, per-network and ceiling limits as crash reports), `503`.
- The issue itself is not removed by Delete my data; the team deletes one on request.

The public alternative, with no logs, is Open on GitHub beside it: UhhErix/Exo-Launcher's bug form, prefilled with the
version and the message only.

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
`test/issues.test.mjs` runs the Worker against a fake GitHub API in memory: grouping by fingerprint, one issue per group
and edits instead of comments, the 10/100/1000 comments, regression reopening, GitHub down and no token (queued, filed by
the daily job), the daily caps, and Report a problem's issue, refusals and Delete my data.

## Before deploy

1. Create the buckets `exo-crash-reports` and `exo-crash-reports-dev` (`wrangler r2 bucket create`), then add the two
   lifecycle rules above.
2. Create the rate limiting namespaces `4002`, `4003` and `4004` (live) and `4102`, `4103` and `4104` (dev) in the account;
   `4001` and `4101` (the per-app limit they replace) are no longer bound. The ids are chosen here; if one is taken on
   the account, change it in `wrangler.toml`.
3. `reports.getexo.app` is a custom domain on the `getexo.app` zone: its DNS record is created on deploy. The updates
   address (`updates.getexo.app`) and the account service use the same zone.
4. Deploy with `npx wrangler@4.165.0 deploy` from this folder (the dev copy with `--env dev`), after the owner's OK.
5. Then `PRIVACY.md` and `NetworkDestinations.cs` already name `reports.getexo.app`; a host change must update both.

Cost on the paid Workers plan: one Worker request and one R2 write per report that passes, a few D1 rows, and GitHub calls
within the caps above.

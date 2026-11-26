# Exo account

> **Live service with real accounts.** Never run `wrangler secret put ACCOUNT_SECRETS` against it: a new keyring makes
> every account's items and recovery codes unreadable for good. A change is migrations then deploy, as in
> [Redeploy](#redeploy).

The account for Exo's apps (Cinema, Launcher): email and password, a recovery code, one session per PC, and the
items each app syncs. A Cloudflare Worker over one D1 database (`src/worker.js`, plain JavaScript, no build step), for
`accounts.getexo.app`.

**Live at `accounts.getexo.app`.** `wrangler.toml` is the live Worker; `[env.dev]` defines a dev copy (`exo-account-dev`, the
same code) that is not deployed today. `npm run dev` is `wrangler dev --local` and touches nothing remote. The steps for a change to the deployed
Worker or its database are in [Before deploy](#before-deploy).

**Which deployed version this is.** Copied on 2026-11-10 from `exo-accounts-wt` (branch `feature/accounts`, commit
`5e5172df`). That `src/` is what Cloudflare runs as `exo-account`, deployed 2026-11-09 10:16 UTC: the deployed bundle's
`worker.js`, `accounts.js`, `crypto.js`, `http.js`, `limits.js` and `common-passwords.js` match it, line endings aside
(`misc/site-audit/final-check/privacy-recheck.json`; `rotation.js` is a key-retirement helper and is not in the bundle).
This folder replaced an older Google, Apple and email-code design that was never deployed but carried the same Worker and
database names. Change the live Worker from here, and record the new version in this paragraph when it is deployed.

## What it can and can't see

| The account server and Cloudflare see | They never see |
| --- | --- |
| The synced items, decrypted while a request handles them. Each account's item key is sealed under a key of the server's keyring, so the server can open them, as with other synced services | Your password, in any form the server keeps: only its Argon2id hash (OWASP parameters, a salt of its own) |
| Your email address, only while a request carries it | The address in clear: rows keep a keyed digest of it |
| Each signed-in PC's device id, when its session began and when it was last used | The recovery code in clear. It is shown once and kept as a keyed digest |
| Wrong sign-in tries per address and per client address (counts and lock times, under keyed digests). A failed sign-in keeps the keyed digest of its client address for 15 minutes from its first wrong try, and 15 more when those tries lock it | Your IP address stored: the rate limit counts addresses at Cloudflare's edge |

**No address is verified.** The email address is only an identifier. Nothing sends it a code, and no app may trust it for anything: it is whatever was typed at sign-up. (Email codes were removed on purpose.)

It writes no logs (`observability.enabled = false`, no `console` calls), sets no cookies and has no cron. Sessions and
recovery codes are never stored in clear.

## API

JSON in and out: a body needs `Content-Type: application/json`. Errors are `{"error":"<code>"}`. Every answer is
`Cache-Control: no-store`, `Strict-Transport-Security: max-age=31536000` (no includeSubDomains, no preload) and `X-Content-Type-Options: nosniff`, and there are no CORS headers. Times are Unix seconds. A device id is 32 lowercase hex
characters. A session token is 32 random bytes as base64url (43 characters), sent as `Authorization: Bearer <token>`.

| Request | Answer |
| --- | --- |
| `POST /v1/account {email, password, device}` | 201 `{token, account: {id}, recoveryCode, deviceToken}`: a new account, this PC's session and its device token. The code is shown once |
| `POST /v1/session {email, password, device, deviceToken?}` | `{token, account: {id}, deviceToken}`: this PC's session (the PC's one before ends) and a new device token that replaces the one before |
| `POST /v1/signout` (bearer) | 204. Ends this session and this PC's device token (the PC is no longer known until its next sign-in) |
| `GET /v1/account` (bearer) | `{account: {id, created}}` |
| `DELETE /v1/account {password, deviceToken?}` (bearer) | 204. Every row of the account goes: sessions, items, the key, the lock counts |
| `PUT /v1/password {current, password, deviceToken?}` (bearer) | 200 `{deviceToken}`. Every other PC is signed out and loses its device token; this one stays, with a fresh device token |
| `POST /v1/recovery/reset {email, code, password, device, deviceToken?}` | 200 `{token, account, recoveryCode, deviceToken}`. Every PC is signed out and loses its device token; the data stays; the code is new |
| `POST /v1/recovery {password, deviceToken?}` (bearer) | `{recoveryCode}`: a new code; the old one stops working |
| `GET /v1/sessions` (bearer) | `{sessions: [{device, created, lastUsed, current}], devices: [{device, firstSeen, lastSeen}]}`, sessions oldest first. `devices` lists the devices whose token is still within idleSeconds (`lastSeen` is their last sign-in or use) |
| `DELETE /v1/sessions/<device>` (bearer) | 204. Ends every session of that device and its device token (a PC removed from the profile) |

A device token is 32 random bytes as base64url, given once in an answer and kept by the server only as its SHA-256. The client
sends the newest one as `deviceToken` in the body of a sign-in or a reset, and in the body of a password, recovery-code or
delete call. See CLIENT.md, Device token.
| `PUT /v1/sync {items}` (bearer) | 200 `{written, conflicts}`: up to 100 items (below) |
| `GET /v1/sync?after=N` (bearer) | `{items, cursor, more}`: the items whose version is after N, oldest version first, 500 at most and 4 MiB of values a page (the first item always goes) |
| `GET /v1/export?after=N` (bearer) | `{exported, account, items, cursor, more}`: a page of the items, decrypted, as a JSON file, paged as a pull is. A deleted item is a tombstone (`value: null`), so one deleted while the export pages shows as deleted on a later page. Start at 0 and continue from each page's cursor while `more` is true |

**Items.** `{app, type, id, base, updated, value}`, or `{app, type, id, base, updated, deleted: true}`. `value` is the
item's JSON as text. `app` and `type` are lowercase names (`[a-z][a-z0-9-]*`, up to 32 characters); `id` is up to 128
characters of `A-Z a-z 0-9 . _ : ~ @ -`. The server does not know the apps or types: a new item type needs no server change.

- **Order is the server's.** Every write that lands gets the account's next version (its change number), and the item
  records it. A write lands only when the stored version is no newer than `base`, the version the client last saw of
  that item (0 when it has seen none). Otherwise it is a **conflict**: the answer carries the current version and value,
  the client re-applies its change on top, and nothing is overwritten.
- **Writes to different items never conflict.** The check is per item.
- **Clocks are metadata.** `updated` is the device's own clock, stored and returned, and never decides an order. A device
  a day behind or ahead writes exactly as one that is right: what matters is the version it saw.
- **Logs.** A log type (for example Launcher's per-session lifetime entries) keeps one item per entry, keyed by the entry's
  own id. Two PCs that add different entries both keep them; the same entry sent again is a conflict that changes nothing.
- **Deletions** are tombstones with a version: a deletion on the current version lands; a stale one is a conflict, and a
  later write (on the deletion's version) brings the item back.
- **Pulls** go by version: a cursor returns what changed after it. A PUT is one D1 batch, so a pull never sees part of it.
- **Sealing.** Each value is AES-256-GCM sealed under the account's item key, with the item's identity as associated data:
  a value moved to another item does not open.

**Errors.** 400 `bad-request`, `bad-email`, `bad-password`, `common-password` (sign-up, a change or a reset: on the list below),
`bad-code`, `bad-item`, `bad-json`; 401 `signed-out`, `wrong-credentials` (sign-in: the same answer for an unknown address),
`wrong-password`, `wrong-code` (recovery); 404 `not-found`; 405 `method-not-allowed`; 409 `email-taken` (sign-up, also when it
loses a race to another sign-up for the address); 413 `too-big` (a body or a value over its cap) or `account-full` (the
account's item or byte cap); 415 `json-only`; 429 `locked` (with `Retry-After` in seconds: a pair's lock, or its tries already
in flight, which waits 1 second or more) or `slow-down` (the per-address limit); 503 `not-configured` (`ACCOUNT_SECRETS`
missing or not a valid keyring); 500 `server-error` (also a stored item under a key the keyring no longer lists).

## Limits

In `src/limits.js`.

| Limit | Value |
| --- | --- |
| Password | 12 to 1,024 characters (a sign-in accepts any 1 to 1,024). Sign-up, a change and a reset refuse the common passwords below |
| Wrong tries, per address and client address | 10 in 15 minutes lock that pair for 15 minutes. A try is counted before it is checked, so tries still in flight count too; the tenth wrong try is the one that locks. A right try clears its count. A client address is an IPv4 address, or the /56 of an IPv6 one (keying by /56 rather than /64 blunts cheap rotation through IPv6). A place held by a try that never answers is cleared after 5 minutes |
| Wrong tries, per address across all client addresses and devices | 100 in an hour: each further wrong try waits a second more, at most 10 s, outside the hash slot. Nothing is refused for it, and the address never locks. A known device (a valid device token) doesn't wait. The right password from an unknown device waits the same way; it is not refused by this count. Pair locks and `busy` (503) can still refuse it |

| Argon2 hashes in flight | 4 per isolate, each taken only around a hash (after the throttle, the session and the body). A request that finds them taken waits up to 2 s, then gets 503 `busy` with `Retry-After: 1`. A hash is 19 MiB while it runs |
| Request body | Each chunk must arrive within 10 s of the one before (an idle deadline), and the whole body within 60 s, or 408 `slow-body`. A PUT for an account already in flight in this isolate is answered 503 `busy` |
| Requests that take a password | 10 a minute per client address (`SESSION_TRIES`, at Cloudflare's edge): sign-up, sign-in, recovery, and the signed-in change, delete and new recovery code |
| Session | One per account and PC; `last_used` moves at most once a day; 400 days unused and it ends. 50 per account: beyond that, the least recently used ends |
| Account | 20,000 items and 64 MiB of sealed values, counted on the account row (triggers on `items`, migration 0005). A PUT is refused (`account-full`) only when what it adds grows an account past a cap: an edit to a smaller value or a tombstone goes through, even over a cap, and a write that loses adds nothing. Best-effort under races between isolates: two PUTs on different isolates can both pass it |
| Sync | 100 items a PUT, 64 KiB a value, 8 MiB a PUT body as sent (a control character takes six bytes of body), 500 items and 4 MiB of values a pull or export page (the first item always goes) |
| Other bodies | 16 KiB |

The lock is per pair, so one client address cannot lock an account for everyone: an attacker can only lock their own pair.
Address-wide pressure never locks anything. Past 100 wrong tries an hour, each further wrong try waits a second more (at most
10 s), so a flood of guesses is slowed, not refused. A device the account knows (its token) doesn't wait. The right password
is not refused by this count, but a pair lock or a busy answer can still refuse it, and a flood from many networks is held to
its delay pace only. The bound against that is the password itself: a 12-character minimum, the common-password refusal and
Argon2id. Someone who shares a client address with the attacker can be locked out with it for 15 minutes.


**Failed sign-ins keep a digest.** A pair's keyed client-address digest is kept for 15 minutes from its first wrong try,
and for 15 more when those tries lock the pair (so up to 30 minutes). Each try prunes up to 100 rows whose count has
ended, so a flood of distinct pairs can keep rows a little longer. Nothing else keeps a client address.

**Common passwords.** `src/common-passwords.js` holds 1,197 passwords of 12 to 1,024 characters, from the 100,000 most
used passwords list (SecLists, `Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt`, MIT licence, Copyright
(c) 2018 Daniel Miessler). The shorter ones on that list are refused by length already. The list's own source terms
were not checked. Matching ignores case and the NFKC form.

## Keys

`ACCOUNT_SECRETS` is a keyring: `id:hex,id:hex`, up to four keys, each 32 bytes or more, ids of 1 to 8 lowercase letters or
digits and unique. The first key is current. Everything new is made under it: address and recovery-code digests
(HMAC-SHA256, `exo-account/email v1`, `exo-account/recovery code v1`, and `exo-account/ip v1` for client addresses), and
each account's item key, sealed as AES-256-GCM under a key derived from the named key (`exo-account/item key v1`, salt the
account id). Every key still listed opens what an older one made. Each stored digest or item key names its key id.
Each key id also has a check value, an HMAC of a constant under that key, stored in `keyring` the first time the key is
seen. A configured secret that gives another value is refused as `keyring-mismatch` (500) before anything is read or
made, so a wrong secret under a known id can't split an account in two.

Rotation, with no downtime. **Not possible yet**: step 1 needs every current key, Cloudflare never gives a secret back, and
no human holds them, so a `secret put` today could only drop them. Rotating needs a way to add a key without knowing the
others (a design still to make); until then the keyring is left as it is. The steps, for then:

1. Put a new key first, keeping every current one, `ACCOUNT_SECRETS=<new>,<old>`, given on standard input to
   `npx wrangler@4.165.0 secret put ACCOUNT_SECRETS`.
2. Accounts keep working. A row made under the old key moves to the new one when its account next signs in (its address
   digest and its item key). A recovery code moves only when it is next made or reset, so it is the last to move.
3. Retire the old key only when the check passes. `scripts/retire-key.mjs` refuses while any row names the key, in its
   address digest, its recovery code digest or its item key. Read the rows (this only reads; the file holds account ids,
   so delete it afterwards):
   `npx wrangler@4.165.0 d1 execute exo-account --remote --json --command "SELECT id, email_mac, recovery_mac, hex(item_key) AS item_key FROM accounts" > rows.json`
   then `node scripts/retire-key.mjs <current id> <old id> < rows.json`. It exits 0 when no row names the old key, and 1
   with the count per column (no account ids) when one does. It also refuses the current key. Until it passes, removing
   the old key leaves those accounts unable to sign in (a server error, not a wrong password) and their items unreadable.

Passwords are Argon2id (19 MiB, 2 passes, 1 lane, a random 16-byte salt per account, a 32-byte hash) and do not depend on the keyring. The hash is stored as a PHC string, so its parameters are in it: a hash made under other parameters is rehashed at sign-in, and the parameters can change later. The Argon2id code is @noble/hashes (pinned, pure JavaScript): Workers refuse WebAssembly compiled at run time, and WebCrypto's PBKDF2 refuses more than 100,000 rounds there. Sign-in does the
same work whether or not the address has an account: the hash is always computed (against a fixed salt for an unknown
address), and the answer is the same. Sign-up's `email-taken` does reveal that an address has an account; the hash runs
before that check so the time does not.

**Owed: rotation and survival without rewriting the live secret.** Not built yet; the design to build:

- One secret per key, `ACCOUNT_KEY_<id>` (for example `ACCOUNT_KEY_k2`), plus a plain variable naming the current key id.
  Adding a key is a new secret beside the others; no existing secret is ever written again.
- The Worker builds its keyring from every such binding and reads the legacy `ACCOUNT_SECRETS` as well, so the key the
  live accounts were made under keeps working untouched while new rows move to the current key.
- If the keyring is lost anyway (`keyring-mismatch`, or an account that no longer exists), the apps re-upload their local
  items after the next sign-up, so losing the keyring costs a sign-up, not data: every PC keeps its own copy.
**No human holds the keyring.** It was generated once, at the first deploy, and given to Wrangler on standard input, so it
was never printed, typed or stored outside the Worker's secrets. It is never generated again (see [Redeploy](#redeploy)).

## Tests

```bash
npm ci            # in cloud/account: installs the pinned wrangler (a dev dependency only)
npm test          # node --test test (the unit tests, then the workerd run)
npm run e2e       # the same Worker under workerd, see below
```

- `test/account.test.mjs`: the real `src/worker.js` over the D1 stand-in (`test/d1.mjs`, node:sqlite in memory) and a rate
  limit of its own (`test/harness.mjs`). Covers sign-up and sign-in (a racing sign-up answers 409), the Argon2id parameters and the rehash of an old hash, the same work for unknown addresses (the dummy hash has the current parameters), the lockout per pair and its window, tries reserved before
  they are checked, pruning of expired counts, the slowdown per address, the common passwords, sessions and their cap, the
  per-account caps, password change, recovery, deletion, the rate limit on every password route, and the removed features
  and test hooks.
- `test/sync.test.mjs`: server-ordered writes, stale writes to the same and to other items, a device whose clock is a day
  behind or ahead, a new app type, log merges by entry id, tombstones, pulls and pages by count and bytes, what is refused,
  the PUT cap as sent, sealing, paged export with its tombstones, and deletion.
- `test/keys.test.mjs`: the keyring's rules, rotation keeping accounts and items, what a dropped key does, the retirement
  check and `scripts/retire-key.mjs`.
- `test/migrations.test.mjs`: 0003 keeps the 0001 and 0002 tables as `legacy_*`, with their rows; 0004 drops only those; 0005
  counts the rows; 0006 and 0007 add the keyring, the pending age and the devices table.
- `test/caps.test.mjs`: the item and byte caps at exactly their edge: an edit or a tombstone at the cap goes through, a new
  item doesn't, and the counters match the rows after every write.
- `test/pending.test.mjs`: a try's place is given back when a D1 error stops its answer; a place older than 5 minutes is cleared.
- `test/http.test.mjs`: an IPv6 client is keyed by its /56; the pair lock and the edge rate limit both use it; the body has an idle and a total deadline.
- `test/ceiling.test.mjs`: the soft ceiling locks the address for every client until its window ends; one client address
  can't reach it alone; the places in flight count.
- `test/known-device.test.mjs`: the device token: a known device goes without the address-wide wait, an unknown one or a
  token of another device waits, a revoked, signed-out, changed or reset device loses it, the previous token counts once,
  a token expires after idleSeconds, the devices are listed with the sessions, and a device with no session left is dropped.
- `test/ceiling.test.mjs`: the address-wide wait: past 100 wrong tries an hour each one waits a second more (at most 10 s),
  nothing is refused, the window restarts the count.
- `test/hardening.test.mjs`: a stored hash this code can't read is a wrong password, and a key id seen first must open a
  stored item key.
- `test/admission.test.mjs`: a shrinking edit goes through on an account over a cap, a losing write counts for nothing, and a
  second PUT for an account in flight in the isolate is answered 503 busy.
- `test/delete.test.mjs`: a full account (20,000 items) is deleted in chunks of 1,000.
- `test/keyring-check.test.mjs`: a configured key id whose secret gives another check value is a server error, and nothing is made under it.
- `test/busy.test.mjs`: at most 4 hashes at once, a waiter gets a slot in turn and is refused busy after 2 s, a stalled body
  holds no slot, and a body still arriving after 10 s is answered 408.
- `test/workerd.test.mjs`: `wrangler dev --local` with a local D1 that has the migrations applied and a throwaway keyring.
  Two simulated PCs create the account, sync both ways with the server's order, export and delete with the password. Every
  password route (sign-up, change, a new recovery code, a reset) runs there too, and the time for one hash is reported.
  Nothing remote is touched. The local runtime is not the deployed one: it refuses WebAssembly compiled at run time, as
  Workers does, but it accepts the PBKDF2 rounds the deployed runtime refuses. The live dev check below covers what it can't.

## Redeploy

The Worker, its D1 and its keyring exist and hold real accounts. A change goes out as: migrations (only new ones), then the
deploy. **Never run `wrangler secret put ACCOUNT_SECRETS` here.** It replaces the keyring: every account's item key and
recovery code was made under the keys it holds, so a new secret leaves every account's items and recovery codes unreadable
for good (Cloudflare never gives a secret back, so the old one cannot be restored). The one place that changes the keyring
is [Keys](#keys), and it says why that is not possible yet.

What live holds now (read-only checks of 2026-11-09, `misc/site-audit/final-check/privacy-recheck.json`): the D1
`exo-account` with migrations 0001 to 0007 applied (the `keyring`, `devices` and `pending_at` of 0006 and 0007 are in its
schema), the secret `ACCOUNT_SECRETS`, the `SESSION_TRIES` rate limit (namespace 3002) and the custom domain
`accounts.getexo.app`. Workers Logs and Logpush are off and stay off.

1. **Read what live has applied**:
   `npx wrangler@4.165.0 d1 execute exo-account --remote --command "SELECT name FROM d1_migrations ORDER BY id"`.
   It lists 0001 to 0007 today. Stop if it lists something this folder does not have.
2. **Export first** when a new migration changes or drops anything:
   `npx wrangler@4.165.0 d1 export exo-account --remote --output <a path outside the repo>/exo-account-before-<n>.sql`.
   Keep it until the checks in step 6 pass, and never in the repository (it holds the hashes and the sealed items).
3. **Dev first**: `npx wrangler@4.165.0 d1 migrations apply exo-account-dev --remote --env dev`, then
   `npx wrangler@4.165.0 deploy --env dev`. On dev run sign-up, sign-in (the answer has `deviceToken`), password change (200
   with `deviceToken`), new recovery code and reset (the answer has `deviceToken`). Any 500 is a failure. The lock and the
   token rules are tested in the harness, not on dev: `node --test test/ceiling.test.mjs test/known-device.test.mjs`.
4. **Live migrations**, only when step 1 shows one this folder adds:
   `npx wrangler@4.165.0 d1 migrations apply exo-account --remote`. Wrangler applies only those `d1_migrations` does not
   list. If an apply stops part way: stop, read `d1_migrations` and `PRAGMA table_info(<table>)`, and run the rest of that
   file by hand (CREATE, triggers and DROP use IF EXISTS / IF NOT EXISTS; ALTER TABLE ADD COLUMN does not re-run).
5. **Deploy** (with the owner's OK), from this folder: `npx wrangler@4.165.0 deploy`. It replaces the code only: the secret,
   the D1, the rate limit and the domain stay as they are.
6. **Checks live**: `GET https://accounts.getexo.app/v1/account` answers 401 `signed-out`; a sign-in answers 200 with a
   `deviceToken`; a sync of a test account reads back its items (a `keyring-mismatch` 500 means the secret is not the one
   the accounts were made under. A rollback (`npx wrangler@4.165.0 rollback`) restores code, never a secret: the cause
   is a replaced `ACCOUNT_SECRETS`, and a replaced secret cannot be recovered, so every account's items and recovery codes
   stay unreadable. Stop and tell the owner; the way back is in [Keys](#keys) › Owed).
7. **Record it**: the deployed version id in the paragraph "Which deployed version this is" at the top of this file.

The dev copy `exo-account-dev` (workers.dev, its own D1, `[env.dev]` in `wrangler.toml`) is the same code, but it is not
deployed: on 2026-11-26 the Cloudflare account listed no `exo-account-dev` Worker and no `exo-account-dev` D1. Step 3 and
CLIENT.md section 0 need it created first (`d1 create exo-account-dev`, put its id in `[env.dev]`, `deploy --env dev`, then give it its own throwaway keyring with
`npx wrangler@4.165.0 secret put ACCOUNT_SECRETS --env dev`: without one every route answers 503 `not-configured`. The
`--env dev` flag is required; never run that command without it, because it would replace the live keyring).
Once it exists it is deployed before live, as in step 3, and gets its migrations the same way. The apps' rows in PRIVACY.md and `ExoLauncher/NetworkDestinations.cs` describe the account service as live; an
app that signs in sends the device token and stores the one each answer gives (CLIENT.md, Device token).
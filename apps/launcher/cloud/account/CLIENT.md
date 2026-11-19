# Exo account: client spec

How an Exo app signs in to the account service, stores its token, syncs its items and shows the account in Settings.
The Launcher is the first client and is specified in full (sections 1 to 11). Cinema is parked; section 12 only says
what it inherits. The service's own contract is `README.md` next to this file; the replayable live answers
are in `client-fixtures/`.

Service: `https://accounts.getexo.app` (HTTPS only, no CORS, JSON in and out, `Cache-Control: no-store`).

---

## 0. Status and evidence

- **The service answers.** Probed 2026-11-06: `GET /v1/account` gives 401 `signed-out`, an unknown route 404 `not-found`, and a
  POST without a JSON content type 415 `json-only`, each with the headers the README lists. `README.md` still says "not
  deployed, and not live"; that line is stale and PRIVACY.md and `NetworkDestinations.cs` must not keep saying it.
- **Live walk.** One throwaway account, `test+<random>@example.invalid`, took 67 requests: sign-up, sign-in, sessions,
  items (write, stale write, conflict, tombstone, pull, export), password change, recovery code, recovery reset, sign-out,
  delete, and a lock test on a separate unknown address. Every status and error code in the README's error list was seen
  except `413 account-full`, `503 not-configured` and `500 server-error`. Those are marked "not exercised live" in
  `client-fixtures/errors.json`.
- **Cleanup.** The throwaway account was deleted by the run (`DELETE /v1/account` 204). The API has no row count, so the
  check is behavioural: the sign-in afterwards answers `401 wrong-credentials`, and a sign-up with the same address
  answers 201, which only happens when the old `accounts` row is gone. That second account was deleted too (204). The
  D1 row count was not queried; this is API evidence, not a row count.
- **Fixtures.** Built from that run. Passwords, bearer tokens, recovery codes, addresses and device ids are `{{kind-n}}`
  placeholders. Account and item ids are not secrets and are kept. A scan found no secret value left in the files.
- **Dev service for tests.** `https://exo-account-dev.exo-erix.workers.dev` (Worker `exo-account-dev`, its own D1
  `exo-account-dev`, the same code as live). Deployed 2026-11-07 with `wrangler deploy --env dev`, version
  `664e29b7-866b-43d9-8d66-0b0a03304439`. Smoke test that day, all against the dev address: sign-up 201, whoami 200,
  sign-in 200, PUT item 200 (version 1), pull 200 with the same value, sessions 200 (current row marked), delete 204,
  old token afterwards 401 `signed-out`. Afterwards the dev D1 held 0 accounts, 0 sessions, 0 items, 0 failure rows.
- **Test-account rules** (every app's end-to-end and sync test):
  - Address: `exo-test-<random>@example.invalid`. The service verifies no address and sends no mail, so no email is needed
    and no dev switch exists (the dev and live config differ only in name, database and rate-limit namespace).
  - Password: random, at least 12 characters, not on the common list. Never a real password, never a key or token from a
    real account.
  - One throwaway account per run. The run deletes it with `DELETE /v1/account` (204) even when it fails part-way, and
    checks the account's rows are gone (`wrangler d1 execute exo-account-dev --remote --env dev`), not only the status.
  - Test data only: no real person's name, address or game library in items.
  - Tests never point at `accounts.getexo.app`. Dev is the only host a test may write to.
  - The dev D1 is a test database. Back it up to `misc/backups/` before a migration or a reset.

## 1. Scope and rules that apply to every client

- One email and password account for all Exo apps. It is the first onboarding step after Welcome, with no skip while the
  app is online (section 4 says what offline means).
- The address is only an identifier. Nothing verifies it, and no client may treat it as verified.
- A client never sends or stores a password, except typed into the form that sends it, and never logs one. Exo's rule that
  secrets never reach React applies: the UI gets status, the account id if needed, and the email as typed.
- Every password route is a button press. A client never retries a password route on its own: the service allows 10 per
  minute per client address.
- Sync values are JSON text. A value is at most 64 KiB. Anything bigger stays on the PC (section 8).

## 2. What the Launcher has today (3.24.3 on release/3.24.0; the 3.24.4 changes on integrate/launcher-next are in section 9)

Checked in code on `release/3.24.0`, the current release branch.

- **The account was removed in Exo 3.4.** PRIVACY.md section "Exo accounts (removed)" says the app never contacts the
  account service, and `ExoLauncher/Services/LegacyProfileDataCleanup.cs` deletes the old token file `auth.bin` (the
  constant `ExoAccountSession`) on start. A new token file must not use that name: the cleanup would delete it.
- **Exo.Identity is not the account.** `Exo.Identity/` holds device keys, a device list, a sync key and a mailbox client for
  ExoStore (`ExoLauncher/Services/ExoStore/*`). Its `AccountClient.cs` targets an older API (`v1/providers`, `v1/start`,
  an email code, a dev key) that the README no longer lists. Do not reuse it. The new client lives in
  `ExoLauncher/Services/Account/` and does not touch the identity folder.
- **Data the Launcher keeps** (AppDataDir is `%LocalAppData%\ExoLauncher`, `PathHelper.cs`):
  - `settings.json` (`PathHelper.SettingsPath`);
  - `playtime.json` (`PlaytimeService`: Exo's measured sessions, checkpointed every 5 minutes while a game runs);
  - the store lifetime readings: Steam from `localconfig.vdf`, Epic, GOG, Xbox (`ReportedPlaytime.cs`, Sources Store, Xbox, Galaxy, Tracker);
  - the Riot account files, DPAPI-wrapped per account (`RiotAccounts.cs`), which must never sync;
  - tracker.gg one-time Valorant and League hours (`TrackerGgPlaytime.cs`);
  - covers: `GameCoverImageStore.cs`, `FolderArtStore.cs`, and custom covers named `custom-cover-*` (`CoverArtService.CacheMaintenance.cs`);
  - Discord settings and keys (`ExoLauncher/Services/DiscordClient/`).
- **Collections do not exist in 3.24.3.** No feature or file of that name was found in the Launcher. The `collection` item
  type is reserved here and wired when the feature lands.
- **Key stores in 3.24.3** (user-supplied, DPAPI-wrapped) were removed in 3.24.4; section 9.1 lists the replacement for each.

## 3. The service calls the Launcher uses

The routes, bodies and errors are in `README.md`. The client-side rules:

| Need | Call | Client rule |
| --- | --- | --- |
| Create account | `POST /v1/account {email, password, device}` | 201; show the recovery code once (section 4); store the `deviceToken` in the answer with the session token |
| Sign in | `POST /v1/session {email, password, device, deviceToken?}` | Same answer for unknown address and wrong password. Send the stored `deviceToken` (see below); the answer's new `deviceToken` replaces it |
| Who am I | `GET /v1/account` | 401 `signed-out` means: sign in again, keep local data |
| Sign out | `POST /v1/signout` | Delete the token file and the device token; keep local data |
| Change password | `PUT /v1/password {current, password, deviceToken?}` | 200 `{deviceToken}`: other PCs are signed out and lose their device token; this PC keeps its session, and stores the new `deviceToken` |
| New recovery code | `POST /v1/recovery {password, deviceToken?}` | The old code stops working |
| Reset | `POST /v1/recovery/reset {email, code, password, device, deviceToken?}` | Every PC is signed out, data stays, a new code comes back; the answer's `deviceToken` replaces the stored one |
| Sessions | `GET /v1/sessions`, `DELETE /v1/sessions/<device>` | Current row is marked; removing a PC ends its sessions and its device token |
| Delete | `DELETE /v1/account {password, deviceToken?}` | 204 means the account and its items are gone; local data stays on the PC |
| Items | `PUT /v1/sync`, `GET /v1/sync?after=N` | Section 7 |
| Export | `GET /v1/export?after=N` | Paged like a pull; section 7 |

**Device token.** A long-lived random token the server issues at sign-up, sign-in, change password and reset, kept only
as its hash. While an address has had many wrong tries in its hour, a device that doesn't send its own valid token waits a
second more per try (at most 10 s); a device that does is not held back. Nothing is refused for it. Signing out ends the
PC's token too. The client stores it in the same DPAPI-wrapped `account-session.bin`
as the session token (never in settings, logs or backups), replaces it with each answer's `deviceToken`, and sends it as
`deviceToken` in the body of the sign-in, change, new-code, delete and reset calls. A device id alone is not enough.

Headers every request sends: `Content-Type: application/json` for a body, `Authorization: Bearer <token>` once signed in,
and a `device` of 32 lowercase hex characters, generated once per install (section 6).

## 4. Onboarding account step (after Welcome)

Layout and motion follow `misc/onboarding/SPEC-v2.md`: void background, no top bar or window buttons, the segmented progress
bar, the footer with Back and the primary pill, 8 px spacing grid, and the same motion tokens. The account step is one step
in the list, so the step list becomes:

`welcome, account, stores, apps, discord, capture, cleanup, pc`

(`ui/src/components/onboarding/model.ts`, `STEPS`; the body map in `ui/src/components/OnboardingPanel.tsx`.) The step label
reads "Step 2 of N", where N is the visible step count. `visibleSteps` does not hide `account`.

### 4.1 Screens

**A. Choose.** Title: "Make your Exo account". Body: "One email and password for every Exo app. Your library, settings
and playtime stay in step between your PCs." Two tiles: "Create an account" (detail "New to Exo") and "Sign in" (detail
"I already have one"). Primary: Continue.

**B. Create.** Title: "Create your account". Fields: Email (label "Email", autocomplete off), Password (label "Password",
hint under the field "At least 12 characters", show/hide toggle). Primary: "Create account". Enter submits.

**C. Recovery code (shown once).** Title: "Save your recovery code". Body: "This is the only way back in if you forget
your password. Exo doesn't keep it, and you won't see it again." The code appears as six groups of four characters with
dashes (`shownCode` in the service). Two buttons: "Copy" (copies the code to the clipboard) and "Save as text file"
(opens the Windows save dialog with no default folder, so the user picks where it goes). Then a confirmation field:
label "Type the code to confirm", 48 px field, accepts dashes, spaces and case. Primary "Continue" stays disabled until
the typed text reads back to the same 24 characters (the service's rule: O is 0, I and L are 1). Nothing else continues.
Exo keeps the code nowhere: not in settings, not in logs, not in backups.

**D. Sign in.** Title: "Sign in to Exo". Fields: Email, Password. Primary "Sign in". A text link "Forgot your password?"
opens E. A send of "email-taken" from B shows a link "Sign in instead" that opens D with the email kept.

**E. Reset.** Title: "Reset your password". Fields: Email, Recovery code, New password (at least 12 characters). Primary
"Reset password". Success goes to C with the new code. Note under the form: "Every PC signed in to this account will sign
in again."

**F. Offline.** Shown when the service can't be reached (network error, timeout, 500, 503). Title: "Can't reach Exo's
account service". Body: "Exo works without an account for now. It asks again each time it starts, and your data syncs
once you sign in." Buttons: "Try again" (primary) and "Continue without an account" (ghost). This is the one exception to
no-skip: AGENTS.md says the app works locally on first run and keeps asking, so an offline PC can continue. The step is
then pinned as a non-blocking banner in the library until the account exists. This choice is made here and is the
spec's, not a question for the owner.

**G. Done.** Title "You're all set", the check mark drawn in 400 ms, and the primary "Continue" to the next step. The
button text follows SPEC-v2 for the Launcher: "Open library" is the last step's button.

### 4.2 Copy for each error code

Every error is shown inline under the field it concerns, or as one status line on the screen. Text is sentence case, with
no emoji. `retryAfter` is the `Retry-After` header in seconds.

| Error (status) | Where | Text |
| --- | --- | --- |
| `bad-email` (400) | B, D, E | "Enter an email address, like name@example.com." |
| `bad-password` (400) | B, E, Settings | "Use at least 12 characters." (sign-in never shows this: it accepts any length) |
| `common-password` (400) | B, E, Settings | "That password is too common. Try a longer one that's harder to guess." |
| `email-taken` (409) | B | "An account already exists for this email. Sign in instead." with the link in section 4.1 D |
| `wrong-credentials` (401) | D | "That email and password don't match. Check both and try again." Never says whether the email has an account. |
| `wrong-code` (401) | E | "That recovery code doesn't match this account." |
| `wrong-password` (401) | Settings | "That password isn't right." |
| `locked` (429) | D, E, Settings | "Too many tries for this email from this PC. Wait {m:ss} and try again." The button is disabled until the countdown ends; the countdown uses `retryAfter`. This one is not a fixed sentence: clients build it from `retryAfter` with their own `Locked(seconds)` helper, and the generic `For(code)` table does not cover it. |
| `slow-down` (429) | D, E, Settings | "Too many attempts. Wait a minute and try again." (no `Retry-After`: the client waits 60 s) |
| `signed-out` (401) on any signed-in route | Settings, sync status | "Your session ended. Sign in again to keep syncing." Local data stays. |
| `account-full` (413, on a PUT) | Sync status | "Your sync storage is full. Your data stays on this PC." Section 7.6 |
| `server-error` (500), `not-configured` (503) | any | Screen F's text |
| `keyring-mismatch` (500) | any | "Exo's account service has a problem. Try again later." A calm status line, never Screen F: the service is between its own keys, nothing is wrong on this PC, and the same action works later. |
| `busy` (503, `Retry-After: 1`) | any | Treated like a network failure: the normal backoff (section 7.6), then the request is sent again |
| `slow-body` (408, on a PUT) | sync | The body didn't arrive in time: halve the batch and send each half, as for 413 `too-big` (section 7.6) |
| `json-only`, `bad-request`, `bad-json`, `bad-item`, `not-found`, `method-not-allowed` | any | "Something went wrong on Exo's side. Try again." These are client bugs: they are logged with `AppLog` and never shown with the error code. |
| `too-big` (413) | sync | Not shown; the item stays local and is logged (section 7.6) |
| Network failure or timeout | any | Screen F |

### 4.3 Device name

The API takes a `device` of 32 lowercase hex characters and has no name field. The client generates the id once
(16 random bytes, hex), stores it in `%LocalAppData%\ExoLauncher\device-id.txt` (not a secret, so a plain file), and sends
it on every sign-in. Settings shows this PC by its own name (`Environment.MachineName`) because that is local knowledge
only: the sessions list cannot show it for other PCs (section 13, gap 1).

## 5. Account page in Settings

Settings > Account (a new page in the Settings group, next to Library's store accounts):

- **Signed in as** the email, then "Signed in on this PC", "Last sync {time}", and the sync status line (section 7.6).
- **Sessions**: `GET /v1/sessions`, oldest first. Each row: "This PC" for the current row, otherwise "Another PC", with the
  created and last used dates. Remove on another PC: confirm, then `DELETE /v1/sessions/<device>`. The API cannot name
  the other PC (gap 1), so the row says "Another PC".
- **Change password**: current password, new password (at least 12, common refused). Success: "Other PCs were signed out."
- **New recovery code**: password, then screen C. Copy: "Your old code no longer works."
- **Export**: "Save a copy of your account data" with the warning "This file holds your data as plain text." Runs the export
  pages (section 7.7) and writes JSON through the save dialog.
- **Sign out**: confirm, then `POST /v1/signout`. Local data stays on the PC and its queue waits for the next sign-in.
- **Delete account**: password, then a confirmation that names what goes: "Your account and everything synced to it. The
  data on this PC stays." On 204: remove the token file, forget the cursor, keep local data and the settings.

## 6. Token storage per platform

Windows is the only platform.

- The session token lives in `%LocalAppData%\ExoLauncher\account-session.bin`, wrapped with DPAPI CurrentUser through the
  existing `ExoDpapi` (`ExoLauncher/Services/ExoDpapi.cs`), the same wrap the keys use. Content: the token, the device token,
  the account id and the email as typed. Written atomically (temp file, then rename), as `LegacyProfileDataCleanup` does for its files.
- The file is never in `settings.json`, never in a log, never in a crash report, and never in a daily backup (the backup
  code skips it by name). This is the AGENTS.md rule: secrets stay protected and out of plain backups.
- A reinstall keeps the file, because DPAPI is per Windows user and the file sits in the data folder. If DPAPI cannot open
  it (another user profile, or a damaged file), the app treats the PC as signed out and asks to sign in. It never crashes
  and never deletes local data over it.
- The device id (section 4.3) is a plain file, and so is the sync queue (section 7.5). Neither is secret.

## 7. Sync engine

### 7.1 Where it lives

`ExoLauncher/Services/Account/`:
- `ExoAccountClient.cs`: the HTTP calls in section 3, through `ExoHttp.Create` (`BannedSymbols.txt` forbids `new HttpClient`),
  base `https://accounts.getexo.app/`. Returns DTO records and an error code, never a token to React.
- `ExoAccount.cs`: sign-in state, the token file, sign-out, delete.
- `AccountSync.cs`: the engine described below, and its queue.
- `AccountSyncMap.cs`: one mapper per item type, from the Launcher's own objects to item `(type, id, value)`, and back.

`WebHostBridge.Account.cs` (a `partial class WebHostBridge`) carries the RPC: state, sign-in, create, recovery code, reset,
change password, sessions, remove session, export, delete, sign-out, sync status. Each goes into `ui/contracts/host-v1.json`
(`npm run contracts:write`).

### 7.2 Item naming

Every item: `app` is `launcher`. `type` must match `^[a-z][a-z0-9-]{0,31}$`. `id` must match `^[A-Za-z0-9._:~@-]{1,128}$`.

- A game key (the library's game id) is used as the id when it matches the id rule. If it does not, the id is
  `h.` followed by the first 40 hex characters of SHA-256 of the game key. The mapper does this once and tests it.
- A log entry's id is `s.` followed by the first 24 hex characters of SHA-256 of `<device id>|<session id>`, so two PCs
  can never give the same entry id, and a resent entry is the same item.
- `type` names are fixed in section 8. A new type needs no server change (the README says so).

### 7.3 Values and merge kinds

Each type has one merge kind. The kind decides what a conflict does (7.4).

| Kind | Types | Rule |
| --- | --- | --- |
| log | `playtime`, `riot-ledger` | One item per entry, keyed by its own id. Adding an entry is never a conflict to fix. A duplicate id is dropped locally. Nothing double counts. |
| seed | `playtime-seed` | Written once per game and store, then left alone. The first write for an id wins on the server; later local writes for the same id are dropped. |
| object | `settings` | One item per settings section. A write replaces the section's keys, not the whole section. |
| value | `cover`, `cover-image`, `collection` | One value per id. Newest client change wins among conflicting edits. |

### 7.4 Push and conflicts

- **When to push.** Every local change marks its item dirty. The push waits 2 seconds after the last change (the same
  `PushDelay` `Exo.Identity`'s sync uses), then sends everything dirty in batches.
- **Batches.** At most 100 items and 8 MiB of body per `PUT`, each value at most 64 KiB. The client keeps a settings
  section under 16 KiB and each log entry under 1 KiB, so the cap is never reached in normal use.
- **Base.** Each item's last seen server version is its `base`. A new item has base 0.
- **Written.** The answer's `written` gives each item's new version. The client stores it as the item's base.
- **Conflicts.** The answer gives the server's `version`, `updated` and `value` (null for a tombstone). Then:
  - **log**: the entry is already on the server. Drop it from the queue; if the server copy differs for the same id, keep
    the server copy.
  - **seed**: keep the server copy; drop the local write.
  - **object**: start from the server's object, overlay the keys this PC changed since its base, and send that with base
    set to the server version. At most three tries in a row; after that keep the queue and try that item again after
    60 s (then on the normal backoff), while the rest of the queue keeps going.
  - **value**: if the local change's client `updated` is newer than the server's, send again with the server version as
    base. Otherwise take the server value. The clock decides only this conflict; the server's order decides every landing.
  - **tombstone against an edit**: the edit wins only when its `updated` is later than the tombstone's; otherwise the
    deletion stands and the local item is removed.

### 7.5 Queue and offline

The queue is `account-sync.json` in AppDataDir: dirty items with their values and bases, the cursor, the account id it
belongs to, and the last status. Written atomically. The queue is data, so it is in daily backups; the token is not.
Offline, changes queue and nothing is lost. On start the engine pulls, then pushes.

### 7.6 Limits, retries and account-full

- **Pull on start.** After sign-in, and once each time the app starts while signed in, the engine pulls from its cursor
  until `more` is false. It does not pull while a game is running; it pushes at once when the game exits.
- **Backoff.** Network failure, timeout, 5xx and 503: wait 5 s, 10 s, 20 s, doubling to 10 minutes, each with 20% jitter.
  Status shows "Waiting to sync".
- **Rate limit.** 429 `locked` waits `Retry-After`. 429 `slow-down` waits 60 s. Sync routes have no service limit, so the
  client's own 2-second debounce is the limit.
- **Signed out.** 401 `signed-out` on a sync route stops the engine, keeps the queue, and shows "Sign in again to keep
  syncing". Nothing is retried until the user signs in.
- **Account full.** 413 `account-full` on a PUT stops pushing new items and keeps the queue. The status says "Your sync
  storage is full. Your data stays on this PC." Pulls keep working. The client never deletes items to make room, because
  the service offers no way to see what would go (gap 3).
- **Too big.** The service answers 413 `too-big` for the whole PUT and names no item, so the client finds it: it splits
  the batch in halves and sends each half until the one oversize item is isolated. That item stays local, is logged once
  with `AppLog`, and is not retried until its value changes; the rest goes. Clients should also check each value against
  the 64 KiB limit before sending, so this path is rare.
- **Device list.** `GET /v1/sessions` also returns `devices`: each device with `lastSeen`. A device no longer listed has no
  token any more.
- **Slow body.** 408 `slow-body` on a PUT means the body went quiet for 10 s between chunks, or ran past its 60 s total (each chunk resets the 10 s wait). The client halves the batch and sends each half, the same as for 413; a 408 on a single item keeps that item queued and retries it on the normal backoff.
- **Bad item.** 400 `bad-item` is a client bug: the item is held locally and logged; the rest of the queue continues.

### 7.7 Export

Pages of `GET /v1/export?after=N` from 0, continuing from each page's `cursor` while `more` is true. Tombstones come back as
`value: null` and are written as deleted in the file. Keep the highest `version` seen per item if an item changes during
the export. The file is written only through the save dialog.

## 8. What syncs for the Launcher

Each row is a sync item type, what it holds, and its merge kind. Everything not in this table stays on the PC.

| Type | Holds | Source in 3.24.3 | Kind |
| --- | --- | --- | --- |
| `settings` | Launcher settings sections, one item per section | `settings.json` (`PathHelper.SettingsPath`) | object |
| `playtime` | One entry per play session: game key, minutes, start, end | `PlaytimeService` (`playtime.json`; each session end writes one entry) | log |
| `playtime-seed` | The Xbox hours seed, one item per title (id: game key `xbox:<title id>`): `{"title","name","pfn","minutes","lastPlayed"}`, converted once from `reported-hours.json` (`PlaytimeService`). Steam, Epic and GOG lifetime readings are read live from their clients and are not seeds | `ReportedHours` (Xbox) | seed |
| `riot-ledger` | The tracker.gg hours imported once per Riot account, for Valorant and League: one entry per account key and game, with the minutes and the observed date | `TrackerGgPlaytime`, keyed by `RiotAccounts`' one-way account key | log |
| `collection` | Reserved: a collection's name and its game keys | Not in 3.24.3 | value |
| `cover` | Which cover a game uses, id: the game key (`GameKeyId`). Value: `{"source":"store","game":"<game key>"}` or `{"source":"custom","sha256":"<64 hex>","ext":".png"|".jpg","game":"<game key>"}`. The `game` and `ext` fields are added to the shape above: a hashed game key cannot be reversed, and the picture file is named by its hash and extension | `GameArtworkService` (replace and reset) | value |
| `cover-image` | A custom cover picked by the user. Id: its SHA-256 (64 lowercase hex), the same as the cover's `sha256`. Value: `{"ext":".png"|".jpg","data":"<base64 of the original file bytes>"}`, only when the base64 fits (below) | `custom-cover-<sha256><ext>` files in the cover cache | value |

- **Custom covers.** The picture is sent as the original PNG or JPEG bytes, not re-encoded (no re-encode exists in the
  Launcher). It syncs only if its base64 value is at most 48 KiB, so the item stays under the 64 KiB cap. A larger picture
  is not sent; its choice still syncs, and another PC shows the store's cover until it has the picture. No refetch exists
  for a user's own picture, so that PC has no other source. A receiving PC accepts a picture only when its bytes hash to
  its id and it is PNG or JPEG under 64 KiB; a choice applies only when its picture is on the PC, and a picture that
  arrives after its choice completes it. A picture is never named by anything but its hash.
- **Settings.** The synced sections are an allowlist. A test fails when a new settings property is not classified as
  synced or local, so no setting slips in unreviewed. The machine-bound values are local: paths, window positions, GPU and
  display profiles, the device id, and anything Discord.
- **Game keys** follow section 7.2.

### 8.1 Never sync

- Discord: `DiscordUserSettings`, `DiscordEmojiSettings`, `DiscordFavoriteGifs`, `DiscordSession.Settings`, `DiscordToken`,
  `FriendLinkKeys`, and the voice data (`MyVoice`, `VoiceprintLearning`, the voiceprint). The voiceprint is biometric and
  never leaves the PC.
- Riot sign-in files (`RiotAccounts`): the Riot session files and the DPAPI wraps of them. Riot titles and their launchers
  are never touched by the sync.
- Every store sign-in: the Epic and Legendary sign-ins. No store key exists after 3.24.4.
- The identity folder (`Exo.Identity`, ExoStore's keys and device list).
- The account token and the device id.
- Caches and logs: `library-cache.json`, cover caches other than the chosen covers, achievement caches, `logs\`, crash reports.
- Exo Notes: the note text lives in `Documents\Exo Notes` and is not in v1. Syncing note files is a separate decision
  (size, and the Documents folder is the user's own).

## 9. Exo-held keys: keyless data and the Cloudflare cache

AGENTS.md rules that apply: no keys for the owner or the user, no optional keys in the apps, shared work (store catalog,
cover art) fetched and cached once on Cloudflare, and anything needed offline or instantly stays local.

**Status on integrate/launcher-next (3.24.4).** Every key field is gone. `LegacyProfileDataCleanup.cs` retires the Steam
Web API key, the itch key, the SteamGridDB key, the OpenXBL key and the IGDB Twitch pair, and Exo's own Xbox sign-in is
removed with them: it needed a Microsoft Entra app registration, which would be an app the owner registers. This spec asks
for no key from the owner or the user. Sections 9.1 to 9.3 list what replaces each source.

### 9.1 Removed in 3.24.4, and the keyless replacement

| Removed | Was used for | Keyless replacement on the branch |
| --- | --- | --- |
| Steam Web API key | Owned games, achievement schema | Owned games from the account's public Steam community profile (`SteamCommunityGames.cs`); installed titles and lifetime hours from Steam's local files (`localconfig`, app manifests, `librarycache`, `SteamAdapter.cs`) |
| itch.io API key | itch owned titles | itch's own database, read locally: `%APPDATA%\itch\db\butler.db` (`ItchButlerDb.cs`) |
| SteamGridDB key | Alternative art | Covers from the stores (`CoverArtService.cs`); no SteamGridDB reference remains on the branch |
| IGDB Twitch client id and secret | Covers and metadata for non-Steam titles | Covers from the stores; no IGDB reference remains in `CoverArtService*.cs`. Whether every non-Steam title still gets a cover was not checked |
| OpenXBL key | Xbox time played | Xbox hours from Exo's own session tracking (`PlaytimeService`), plus a one-time seed converted from `reported-hours.json` |
| Exo's Xbox sign-in (Microsoft Entra app registration) | Xbox title history and minutes | Removed with the above. Nothing replaces it: Xbox hours after the seed come from Exo's sessions only |

### 9.2 Keyless shared data that moves to the Exo cache

These are keyless upstream routes that every user gets the same answer from, so Cloudflare fetches and caches them once:

| Source | Used for | Route on the branch | Verdict |
| --- | --- | --- | --- |
| Steam store `appdetails` and `storesearch` | Store names, types, art lookups (`SteamStoreAppTypes.cs`, `CoverArtService.cs`) | `store.steampowered.com`, no key | Move to the cache |
| Steam CDN covers (`cdn.cloudflare.steamstatic.com`, `shared.*`, `steamcdn-a.akamaihd.net`) | Library covers (`CoverArtService.cs`) | No key | Move to the cache (images) |
| Global achievement percentages (`GetGlobalAchievementPercentagesForApp`) | Achievement rarity (`AchievementWatch.cs`) | `api.steampowered.com`, no key (checked on 3.24.3; re-check on the branch before building) | Move to the cache |

- **The cache** is a new Worker, `cloud/catalog/` (not built, not deployed). It proxies the three routes above, caches store
  details for 24 hours and images for 7 days, and holds no key. No account, no cookie and no user identifier goes upstream,
  and it keeps no logs (`observability.enabled = false`, as the account service).
- **Client change.** The Launcher calls the cache for those routes. `NetworkDestinations.cs` gets the cache host with its
  own row. The Steam hosts stay only where the cache does not serve.
- **Blocking terms.** The Steam store and CDN terms were not read in this run. Read them before the cache ships and record
  the result in the cache's README. If they forbid a shared cache, the three routes stay in the client and this table changes.
- **Owner deploy.** Deploying the cache needs the owner's OK. Nothing here deploys.

### 9.3 Stays on the PC

- **Xbox hours**: Exo's sessions plus the seed. No network.
- **Steam owned games** from the public community profile: each player's own page, so a shared cache saves nothing.
  Stays local. Its terms were not read in this run.
- **tracker.gg** (Valorant and League hours): public profile read once, then the Launcher keeps the hours. Each player's
  profile is theirs; this is the AGENTS.md Riot exception. Its terms were not read in this run.

## 10. Wiring into the existing .NET services

- **Playtime.** `PlaytimeService` already knows a session's start and end. On each session end it calls
  `AccountSync.Log("playtime", entry)`. Imported lifetime readings call `AccountSync.Seed(...)`.
- **Settings.** The settings save path calls `AccountSync.Object("settings", section, changedKeys)` for each synced
  section that changed.
- **Riot.** The tracker.gg import calls `AccountSync.Log("riot-ledger", ...)`, keyed by `RiotAccounts`' account key.
- **Covers.** `GameArtworkService` and `FolderArtStore` call `AccountSync.Value("cover", ...)` when a choice changes; custom
  pictures also call `cover-image` when they fit.
- **Start.** `App` startup creates `AccountSync` after `ExoAccount` loads the token and before the library loads, so the
  first pull's results can be applied. Pulls wait for the library to finish loading.
- **Privacy and network.** `PRIVACY.md` replaces "Exo accounts (removed)" with the live account (what it holds, what it never
  sees, the export and deletion). `NetworkDestinations.cs` gets `accounts.getexo.app` with a row that maps to PRIVACY.md's
  "Other network requests" table; `NetworkDestinationsTests` enforces the mapping. The catalog cache host follows the same
  rule (section 9.2).
- **Build rules.** No `new HttpClient`, no `Process.Kill`, no synthetic input (`BannedSymbols.txt`, enforced in build).
  Every new page follows `DESIGN.md`: Void black, the one-switch rule, the one-button-set rule, no native `title=`.
- **Fixtures in the tests.** The fixtures are vendored into `ExoLauncher.Tests/Fixtures/Account/`, copied from
  `cloud/account/client-fixtures/` by a script, with a test that fails when the copy and the source differ when both are
  present. The Launcher's tests load these files; they do not depend on the live service.

## 11. Test checklist for the Launcher

The Launcher's unit tests (`ExoLauncher.Tests`, xUnit, pure logic only) must pass. A test that can't fail does not count.

**Fixtures.**
- [ ] Every case in `client-fixtures/*.json` replays against a fake `HttpMessageHandler`: the status, the listed headers
  and the body shape match. Placeholders are filled by the test.
- [ ] Every error code in `errors.json` maps to a sentence in section 4.2, and every error the client can receive maps to
  one. A test lists the codes in the fixtures and fails on a code with no sentence.

**Token and device.**
- [ ] DPAPI round trip for `account-session.bin`; a damaged or foreign file reads as signed out and deletes nothing.
- [ ] The token never appears in `settings.json`, in a log line (capture `AppLog` output) or in a backup (the backup
  skips the file by name).
- [ ] The device id is 32 lowercase hex and survives a restart.

**Sign-in and screens.**
- [ ] The step list is `welcome, account, stores, apps, discord, capture, cleanup, pc`; the account step cannot be
  skipped while the service answers.
- [ ] Offline (network error) shows screen F, and "Continue without an account" leaves a banner that asks again on the next start.
- [ ] Recovery code confirmation: Continue stays disabled until the typed text matches, with O, I and L read as 0 and 1 and
  dashes ignored.
- [ ] Locked countdown: the button is disabled until `Retry-After` runs out.

**Sync engine.**
- [ ] Debounce: two changes 1 second apart make one `PUT`; a change 3 seconds after the last one makes its own.
- [ ] Batches: at most 100 items and 8 MiB per `PUT`; a value over 64 KiB is never sent.
- [ ] Log idempotence: the same entry id twice is one entry; two PCs with different sessions keep both.
- [ ] Object merge: a conflict keeps the server's keys this PC did not change and sends this PC's.
- [ ] Tombstones: an edit newer than a deletion wins; an older edit loses.
- [ ] Seed: the first server write for an id wins; a later local seed is dropped.
- [ ] Two simulated PCs, shuffled delivery order, same final state (convergence).
- [ ] Paging: a pull with `more: true` continues from `cursor` until `more` is false; the first item of a page always comes.
- [ ] Retry: backoff doubles to the cap with jitter; `Retry-After` is honoured on `locked`; `slow-down` waits 60 s.
- [ ] `signed-out` stops the engine and keeps the queue; `account-full` stops new pushes and keeps pulls working.
- [ ] Too-big and bad-item hold one item and let the rest go.
- [ ] Export follows `more` and writes tombstones as deleted.

**Classification.**
- [ ] Every settings property is classified synced or local; a new property with no class fails the test.
- [ ] Discord, Riot session files, store tokens, the identity folder and the device id never produce an item (a test feeds
  each source and asserts no item).
- [ ] Game keys: a matching id is used as is; a non-matching one becomes `h.<40 hex>`; ids stay within the rule.

**Network.**
- [ ] `NetworkDestinationsTests` passes with `accounts.getexo.app` and its PRIVACY.md row.

**Live check (owner-run, after the change is built and installed).**
- [ ] Create an account on the real service, reach the library, change one setting, sign in on a second install. A
  throwaway address is the owner's to pick; the agent does not create accounts on the real service for the app's UI.

## 12. Later clients (parked)

Cinema inherits sections 1 to 3, 6, 7, 9 and 11 unchanged. Its own data and screens are specified when it resumes: its
watch ledger, positions, settings and collections. Until then no Cinema code calls the service.

## 13. What the API is missing for a client

These are the gaps found while writing this spec. None blocks the Launcher; each has a workaround stated.

1. **No device name.** `GET /v1/sessions` shows device ids only, so "Another PC" is all the UI can say. Ask: an optional
   `name` (up to 64 characters) on sign-in and on sessions.
2. **No quota.** A client cannot see how many items or bytes an account holds, so it can't warn before `account-full`.
   Ask: `GET /v1/account` to return `items` and `bytes` against the limits.
3. **No tombstone pruning.** Deletions stay forever, and nothing removes old ones. A long-lived account only grows.
   Ask: a tombstone expiry, or a client-driven purge of tombstones older than a version.
4. **No filter on pulls.** `GET /v1/sync` returns every app's items. Cinema and Launcher on one account pull each other's
   data. Ask: `?app=` and `?type=` filters.
5. **No way to read one item.** A client that loses a conflict answer must re-pull the page. Ask: `GET /v1/sync/item`.
6. **Value cap is small for pictures.** 64 KiB per value means a custom cover over about 48 KiB stays on the PC. Ask: a
   blob route for images, or a higher cap for `cover-image` only.
7. **No email in `GET /v1/account`.** The client keeps the email it typed; a second PC sees only the id. Acceptable, but
   stated: a second PC shows the account id, not the email, until it types one.
8. **The account response shape is undocumented in code.** `GET /v1/account` returns `{account:{id,created}}`, and the
   client reads only that. Fine; listed so a future field is an addition, not a surprise.
9. **README says "not live".** It still does, while the service answers. The README and PRIVACY.md must say what the
   live service holds before any app contacts it.
10. **No sign-in state for "has a recovery code".** The client can't show whether a code exists, so the screen after
    sign-in can't say "you have no recovery code". Ask: a boolean on `GET /v1/account`.
11. **Sync routes have no rate limit.** Only password routes are limited. A buggy client could push fast. The client's
    2-second debounce is the only limit today. Ask: a per-account write limit with a `429 slow-down` answer.
12. **Export is not filtered and not versioned.** The file has no format version. Ask: a `format` field so a future
    importer can tell an old export from a new one.

## 14. Open decisions, for the owner

Nothing here is needed to start; each is recorded so it is not lost.

- Whether the Steam store and CDN terms allow the Exo cache (section 9.2). Blocking for the cache, not for sync.
- Whether Exo Notes sync in a later version (section 8.1).
- The deploy of the account service and of the catalog cache. Both need the owner's OK.

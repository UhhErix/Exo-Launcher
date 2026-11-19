# Exo mailbox

Where Exo's own devices leave each other end-to-end encrypted messages (settings sync, new sync keys, the recovery
kit), and where a new PC meets one that's already linked. It is the cloud half of Exo identity
(`ExoLauncher/Services/Identity/`): no account, no email, no password. A box is named by the hash of a P-256 public key,
and only whoever holds the private key gets in.

It is a Cloudflare Worker with two Durable Objects (`src/worker.js`, plain JavaScript, no build step): `Box`, one per
box, keeping its rows in SQLite; and `Link`, one per nameplate, keeping nothing. It is separate from the call relay
(`relay/`), so the relay's "nothing is stored" stays true.

**Live.** It is deployed as `exo-mailbox` on workers.dev (`exo-mailbox.exo-erix.workers.dev`), with the `BOXES` and `LINKS`
Durable Objects and the `LINK_GUESSES` limiter (checked 2026-11-06: latest version 69bb2c2a). The `MEDIA` R2 binding in
`wrangler.toml` is not on the live Worker; once this code is deployed, `/v1/media` answers 503 there until the bucket is bound. Redeploying needs the owner's OK; `wrangler dev` on this PC needs nothing.

## What it can and can't see

| The mailbox and Cloudflare see | They never see |
| --- | --- |
| The IP address of each connection, while it's open | Who anyone is, or which PCs belong together |
| A box id: 16 bytes of a hash of a key, new for every sync key | Any key that opens a message, or anything that leads to one |
| How many connections a box has, when messages come, how many | What's in a message: settings, wishlists, device names |
| Each message's size, padded to classes (256 bytes up to 64 KiB) | The recovery code, or that one exists |
| An 8-digit nameplate while two PCs link | The link key or the 6-digit match code |

It stores ciphertext only. A row goes 30 days behind the box's newest row, never behind the clock, so a box nobody
writes to keeps its last month whole (its newest snapshot and recovery kit among it): a PC away for months, or a new PC
after the only one was lost, finds the account as it was, as iCloud keeps one. A box nobody has logged in to or written
to for two years is deleted whole. It writes no logs (`observability.enabled = false`, no `console`
calls) and stores no IP address. Nameplate guesses are counted per address by Cloudflare's rate limiter at the edge
for a minute (`LINK_GUESSES`); the Worker keeps no count of its own. It can drop, delay or replay messages, but not
read or change them: Exo checks every message's AES-GCM tag and its sender's signature.

## Protocol

### A box: `GET /v1/box/<32 hex>`, WebSocket

`<32 hex>` is the first 16 bytes of SHA-256 of the box's SEC1 public key (65 bytes). Every frame is binary; its first
byte says what it is.

| Frame | Direction | Meaning |
| --- | --- | --- |
| `0x10` challenge(32) | to client | Sent at once |
| `0x20` pub(65) sig(64) | from client | Log in: P1363 ECDSA P-256 SHA-256 over `"exo-box-auth/1"` + box id (16 raw bytes) + challenge, s at most n / 2 (a high-s signature is refused). Nothing else is taken first, and only within 10 s |
| `0x11` first:u32 last:u32 | to client | Logged in. The lowest seq kept and the highest given; an empty box says first 1, last 0 |
| `0x21` blob | from client | Store a message (1 byte to 64 KiB) |
| `0x13` seq:u32 | to client | Stored as seq |
| `0x12` seq:u32 at:u32 blob | to client | A message: live from another connection, or replayed; `at` in Unix seconds |
| `0x22` seq:u32 | from client | Send what came after seq |
| `0x14` last:u32 more:u8 | to client | End of a replay page (256 rows or 1 MiB); `more` 1 when there's more to ask for |
| `0x23` seq:u32 | from client | Delete rows up to seq, never the newest, so seq numbers carry on (after a full snapshot) |

A phone client (Swift CryptoKit, Kotlin) builds to the shared vectors in `Exo.Identity/Vectors/exo-identity-vectors.json`
(`mailboxAuth`, `highS`, `media`), which `test/vectors.test.mjs` holds the Worker to. In CryptoKit the public key is
`P256.Signing.PublicKey.x963Representation` (the 65-byte SEC1 point) and the signature `rawRepresentation` (P1363 r || s),
but CryptoKit does not make s low: a client turns s into n - s when it is above n / 2, or the Worker refuses about half
its logins and media requests.

A client waits for `0x11` before sending anything else. A writer gets `0x13`, not its own message back. A reader whose
last seq is below `first` missed rows (they expired or a cap took them) and resyncs from a snapshot; one whose last seq
is above `last` is looking at a box that emptied and started again, and reads it from the start.

Boxes Exo uses (plan §2.6, §2.11, §2.12): the shared sync box (key derived from the sync key, so a new box at every
device removal), a box of an app's own (from its own sub-key), and the recovery box (key from the recovery code).

### Linking: `GET /v1/link` and `GET /v1/link/<8 digits>`, WebSocket

The new PC opens `/v1/link` and gets `0x30` and its 8-digit nameplate (ASCII). The linked PC opens
`/v1/link/<nameplate>`; both get `0x31`. From then on each binary frame from one PC goes to the other as it is (the
handshake in `LinkHandshake.cs`: commitment, key, reveal, sealed envelopes). The Worker's own frames are `0x30` and up,
and a PC sending one ends the session.

- A second PC coming for the same nameplate crowds it: both PCs get `0x33` and the session ends, and the new PC starts
  again with a new nameplate (Magic Wormhole's rule).
- When one PC leaves, the other gets `0x32` and is closed.
- A nameplate lasts 10 minutes and is never handed out twice at once.
- Joins are limited to 10 a minute per address.

## Limits

Sized for Cloudflare's free plan (plan §1.2): 100,000 Durable Object requests and 100,000 SQLite rows written a day
for the whole account, 5 GB stored. All of them are in `src/limits.js`.

| Limit | Value | Why |
| --- | --- | --- |
| Message | 64 KiB | The largest envelope; Exo splits bigger batches |
| Box | 10,000 rows, 50 MB; the oldest go first | One box can't take more than 1% of the account's storage |
| Kept | 30 days behind the box's newest row (checked daily while rows come in); two years with no login and no write and the box is deleted whole | A quiet box loses nothing to time, and a busy one stays the size it was; a login is noted at most once a day |
| Connections per box | 16; one that hasn't logged in after 10 s gives its place up | A person's PCs times Exo's apps |
| Frames per connection | 60, then 1 every 2 s | Incoming messages bill as 1/20 request |
| Puts per connection | 20, then 1 every 30 s | A put is a row written, the scarcest thing on the free plan: one looping device uses at most about 3% of a day's |
| Replay page | 256 rows or 1 MiB | |
| Nameplate | 10 minutes; 10 joins a minute per address | |

Each put costs one row written (and two row reads); a replay reads only the rows it sends; expiry reads only the rows
it deletes. A box's size is two row reads, not a scan, because each row carries the box's running byte total.

## Run it on this PC

Node 22.13 or later, no packages:

```bash
node cloud/mailbox/local/server.mjs --port 8788
```

`local/runtime.mjs` stands in for the parts of Cloudflare's runtime the Worker uses (Durable Objects with SQLite
through `node:sqlite`, alarms, the rate limiter, WebSocket hibernation), so the same `src/worker.js` runs. It grew out
of `relay/local/runtime.mjs`.

Or under Cloudflare's own runtime (workerd), still only on this PC, with Wrangler's metrics off (`send_metrics`):

```bash
cd cloud/mailbox
npx wrangler@4.165.0 dev --port 8787 --ip 127.0.0.1
```

## Tests

```bash
node --test cloud/mailbox/test
```

Each test runs twice, the second time with every Durable Object thrown away after each event, as hibernation does.
With `MAILBOX_URL=ws://127.0.0.1:8787` the ones that need no clock or limit changes run against `wrangler dev` instead.
Exo's own end-to-end test (`ExoLauncher.Tests/MailboxEndToEndTests.cs`) logs in, stores and reads with Launcher's C#
keys and links two C# PCs through `local/server.mjs`.

Under `wrangler dev`, a close sent to the socket on the far side of an event can leave its TCP connection open for a
while even though the WebSocket close is done; that's why the PC left behind is told with `0x32` first.

## Deploy (only with the owner's OK)

1. From this folder, with Wrangler signed in to Exo's Cloudflare account: `npx wrangler@4.165.0 deploy`. The first
   deploy creates the `Box` and `Link` classes (`[[migrations]]`) and the `LINK_GUESSES` limiter.
2. Check it answers 426 to a plain request: `curl -i https://exo-mailbox.<subdomain>.workers.dev/v1/link`.
3. Add the host to `ExoLauncher/NetworkDestinations.cs` with its PRIVACY.md row, before any app
   talks to it (plan §2.8).

A deploy closes every open connection; Exo reconnects on its own. Deploy rarely, never under load. In the dashboard,
leave Workers Logs and Logpush off.

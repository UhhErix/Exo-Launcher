# Exo relay

The one server piece Exo Launcher has. It passes sealed messages between Exo apps, so Exo doesn't have to tell Discord:

- **In a call:** Exo apps in the same Discord call show each other what they play in Exo's Music room, for Listen along
  and the Jam box.
- **Between friends (the friend link):** two friends' Exo apps tell each other what's only for other Exo users, starting
  with the game opened from Exo.

It is a Cloudflare Worker with one Durable Object per topic (`src/worker.js`, plain JavaScript, no build step). It
forwards opaque, end-to-end encrypted messages between the connections on a topic and keeps nothing else.

## What it can and can't see

### In a call

Exo derives each topic and the key that seals every message from the call's own DAVE end-to-end encryption group
(32 bytes of its MLS exporter under the label `Exo call relay v1`, expanded with HKDF-SHA256). Only the people in the
call can compute them, and each join or leave starts a new MLS epoch with a new topic and key. Discord's servers
can't: they never hold an epoch secret. Neither can the relay.

| The relay and Cloudflare see | They never see |
| --- | --- |
| The IP address of each Exo app that connects | Who anyone is on Discord, or which call |
| A random 128-bit topic, new at every join or leave | What anyone plays (title, artists, service, position) |
| How many connections share a topic, and when they come and go | The key, or anything that leads to it |
| When a message is sent and its size (padded to 256, 512, 1024 or 1536 bytes) | |

It stores nothing on disk and writes no logs (`observability.enabled = false`, no `console` calls). Each connection's
last message is kept only in that connection's own attachment, to hand to whoever joins later, and goes when it
closes. It can drop, delay or replay messages, but not read or change them: a changed message fails its check in Exo.

### Between friends

Each Discord account on a PC has a P-256 key; its public half is in the account's own Discord presence, which Discord
authenticates. ECDH and HKDF-SHA256, with both Discord ids and both public keys in the info, give two friends one topic
and one key. The relay never gets a Discord token, id or key. What's inside is in
`ExoLauncher/Services/DiscordClient/FriendLinkFormat.cs`.

| The relay and Cloudflare see | They never see |
| --- | --- |
| The IP address of each Exo app that connects | Who anyone is on Discord |
| A random 128-bit topic per pair of friends, **the same every day**, so that the same two IP addresses keep meeting on it | What anyone plays |
| When each side is connected, and when a message is sent (every message is 1565 bytes) | The key, or anything that leads to it |

## Protocol

`GET /v1/<topic>`, `<topic>` 32 lowercase hex characters, as a WebSocket. Every binary message (1 to 2048 bytes)
replaces the sender's last one and goes at once to the other connections. To clients:

| Frame | Meaning |
| --- | --- |
| `0x01` conn `u32` age `u32` payload | A connection's message; `age` is how long the relay held it, in ms (0 when live) |
| `0x02` conn `u32` | That connection closed |

`conn` is random per connection. Limits: 32 connections per topic (more get HTTP 429), 2048-byte messages (1009),
20 messages per 10 seconds per connection (1008), binary only (1003). A connection the relay closes is told to the
others like one that left.

The worker knows nothing of either use. What Exo puts inside a message is in
`ExoLauncher/Services/DiscordClient/CallRelayFormat.cs` (a call) and `FriendLinkFormat.cs` (a friend link). Against the
limits, a friend link has 2 connections on a topic, sends 1565 bytes, and sends on a game change or a connect.

## Cost

Cloudflare's free plan covers it: 100,000 Worker and Durable Object requests a day. A connection is one request,
an incoming message a twentieth of one; outgoing messages, protocol pings and idle hibernated connections cost
nothing. An hour in a call with 20 songs and a few people coming and going is about 5 requests per person. A friend link
is a request for each connect and a twentieth for each message, so an evening is under a request per friend online; its
sockets ping and hibernate like the rest. The free plan's 100,000 a day is about 20,000 people with 5 Exo friends each,
connecting once a day.

## Run it on this PC

Node 22 or later, no packages:

```bash
node relay/local/server.mjs --port 8787
```

`local/runtime.mjs` stands in for the parts of Cloudflare's runtime the Worker uses, so the same `src/worker.js` runs
locally. Exo connects to it when started with `EXO_CALL_RELAY=ws://127.0.0.1:8787`.

## Tests

```bash
node --test relay/test
```

Each test runs twice, the second time with the Durable Object thrown away after every event, as hibernation does.
Exo's own end-to-end tests (`ExoLauncher.Tests/CallRelayEndToEndTests.cs`, `FriendLinkEndToEndTests.cs`) run real Exo
relay clients through `local/server.mjs`.

## Deploy

1. Sign up at dash.cloudflare.com (the free plan is enough). Pick a workers.dev subdomain when asked.
2. From this folder, sign Wrangler in to that account and deploy:

   ```bash
   npx wrangler@4.158.0 login
   ```

   ```bash
   npx wrangler@4.158.0 deploy
   ```

   The first deploy creates the `Topic` Durable Object class (`[[migrations]]` in `wrangler.toml`). It prints the
   address, `https://exo-relay.<subdomain>.workers.dev`.
3. Check it from this PC against the deployed relay (it should answer 426 to a plain request):

   ```bash
   curl -i https://exo-relay.<subdomain>.workers.dev/v1/00000000000000000000000000000000
   ```

4. Put `wss://exo-relay.<subdomain>.workers.dev` in `CallRelayClient.Endpoint` and its host in
   `ExoLauncher/NetworkDestinations.cs`, then build Exo.

A new deploy closes every open connection; Exo reconnects on its own. In the dashboard, leave Workers Logs and Logpush
off.

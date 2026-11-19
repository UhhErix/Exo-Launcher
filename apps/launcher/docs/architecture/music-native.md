# Music: native engines, Jam, and the plan

Research for "the best music player that shares one native client". Written 2026-11-03; web pages were read that day.
Every claim carries a grade: **solid** (read in the source named, or in this repo's code), **inferred** (follows from
solid facts, not stated by a source), **unknown** (not found; do not build on it). Nothing here was tried against a
real account. Web content was read as data. Search-engine summaries are marked where a claim rests only on one.

## 1. What Exo does today

| Fact | Grade | Source |
|---|---|---|
| Exo's player (`ui/src/components/music/`) is one UI over one hidden web engine at a time: open.spotify.com, music.apple.com or music.youtube.com in the shared WebView2 profile, each driven through the page's own APIs (`Helpers/SpotifyNative.cs`, `AppleMusicNative.cs`, `YouTubeMusicNative.cs`) | solid | `docs/architecture/music-engines.md`, the helpers |
| One adapter per service answers the same shapes (`ui/src/lib/musicAdapters.ts`, `player.ts`); one `MusicPlayback` (now playing), one queue, library views, lyrics lines and the bar's mini player sit above all three. So the "one Exo-owned now-playing/queue/lyrics layer" already exists; what differs per service is a feature set (`MusicFeature`) | solid | `musicAdapters.ts`, `music.ts` |
| Idle policy: a paused hidden engine gets a final idle wait, then `TrySuspendAsync`; memory target goes to Low when suspended or silent; closed after 15 minutes asleep; woken when the pointer nears a music control | solid | `MainWindow.Music.cs` (`SuspendIdleMusicAsync`, `SetWebViewMemoryTarget`) |
| YouTube Music has no synced lyrics on the web: Exo asks LRCLIB (title, artists, album, length; no cookies) | solid | `Helpers/SyncedLyrics.cs` |
| Spotify Jam: Exo starts, joins (link/QR/token), shows members, removes, leaves and ends a Jam through the web player's own `SocialConnectAPI` | solid (code) / unknown (works today; needs a real account) | `SpotifyNative.cs` (`jamStart`, `jamJoin`, `jamState`), CHANGELOG |
| `music-engines.md` said Jam "exists only in Spotify's apps, not its web player". Spotify's support page lists desktop, mobile, TV and cars and does not mention the web player; the code reaches Jam in the page anyway. Reworded there to what is known | solid | Spotify support "Start or join a Jam" (support.spotify.com/us/article/jam/); `SpotifyNative.cs` |
| Exo Jam (Discord call listen-along): a call member's Exo sends its song through Exo's relay, sealed with a key from the call's DAVE secret; others see it in the Jam box and Listen plays it on their own service and account, direct by track id on the same service, else by search and `MusicMatch`. Without Exo, a friend's Discord Listening activity is followed instead | solid | `ui/src/lib/listenAlong.ts`, `JamBox.tsx`, `Services/DiscordClient/CallSongs.cs` |
| **Cost per web player (RAM, CPU)** | **unknown** | No number is in the repo's docs, and none was measured here (no e2e runs, no audio). `tools/e2e/suites/music.mjs` is where a number belongs |

Reference point for the native claim: a press test reported Spotifast peaking at 168 MB against 1,124 MB for the
official Spotify client (PC Games Hardware, via search summary; the page itself was not read). That compares a native
client with Spotify's own Electron-style app, not with a suspended Exo engine. **Inferred**: Exo's idle engine is
already far below the official client, and a native client mainly helps while playing.

## 2. What a native, browserless client could use, per service

### Spotify

- **librespot** (Rust): MIT, "only works with Spotify Premium", acts as a Spotify Connect receiver, and its README says
  "using this code to connect to Spotify's API is probably forbidden by them. Use at your own risk." **Solid**
  (github.com/librespot-org/librespot README). 0.8.0 added `OAuthClient`/`OAuthClientBuilder` and login5 token retrieval
  (release notes, search summary). Spotify's audio here is not Widevine: librespot speaks Spotify's own protocol and
  decrypts with a key Spotify's servers hand out to a logged-in Premium session. **Inferred** from its feature list; the
  wiki pages on authentication and reverse engineering did not load, so the key mechanics are **unknown** to this note.
- **Clients built on it**: ncspot (BSD-2-Clause, Premium only, Windows via Scoop/WinGet; README) **solid**; Spotifast
  (MIT, egui/Rust, no browser engine, librespot player, Connect device and controller, 168 MB peak per press, Premium
  only, no video podcasts or Canvas; spotifast.rocks and its README) **solid**. Its Windows Media Player `.wmz` and
  Winamp `.wsz` skins and a MilkDrop visualizer with 10,000+ presets (downloaded on first use, not on Windows ARM) are
  on its page; whether skins run scripts, and which visualizer engine it uses, are **unknown**. It does not mention Jam
  **solid**. spotify-player and spotatui were not read; **unknown**.
- **How Spotifast signs in** is the part worth copying: the user registers their own Spotify developer app (redirect
  `http://127.0.0.1:8989/login`, Web API ticked), pastes the Client ID, and approves in their browser; the registering
  account needs Premium. **Solid** (README). Spotify's February 2026 change reportedly caps Development Mode apps at
  5 users and ties them to the owner's Premium, with Extended Quota reserved for established apps. **Inferred / weak**:
  community posts and secondary news, not Spotify's own page (a developer.spotify.com blog from April 2025 confirms
  extended access is criteria-gated). So a shipped Exo could not use one Exo-owned client id for everyone.
- **Web Playback SDK** (official): Premium only, desktop/mobile browsers, needs encrypted-media in the page, and "must
  not be used in commercial projects without Spotify's prior written approval" (developer.spotify.com/documentation/web-playback-sdk).
  **Solid.** It is the sanctioned way to play in a web view; Exo's current engine is the player Spotify ships itself, not
  this SDK.
- **DRM limits**: Spotify's own web player plays through the browser's Widevine; Exo never touches the audio
  (`music-engines.md`) **solid**. A native client replaces Widevine with librespot's key flow, i.e. a path Spotify did
  not intend for third parties. **Inferred.**
- **ToS and ban risk**: librespot's authors call it probably forbidden **solid**. The Spotify User Agreement (read via a
  summarizer) allows personal, non-commercial use, says third-party apps are the user's own risk, and lets Spotify
  suspend access at any time if it believes the terms are breached **solid**; whether it names reverse engineering or
  DRM circumvention could not be confirmed (that is likely in the Platform Rules / Developer Terms) **unknown**. Reports
  of real bans for librespot-class clients were not found **unknown**; "no reports found" is not "safe". The owner has
  accepted account-ban risk for himself; a public default for everyone is a different decision.
- **What a native path would lose**: Exo gets library, home, search, lyrics, friend activity and Jam from the web
  player's internals today. A librespot-based engine plays audio and is a Connect device but has no lyrics, library or
  Jam API; those would come from the Web API, now gated by Development/Extended quota rules. **Inferred.**

### Apple Music

- The Windows Apple Music app cannot be driven (no library/queue/quality API, link protocols did not start it, no media
  session until playback) **solid** (`music-engines.md`, checked at app 1.1540).
- MusicKit JS plays through the page's DRM. A community Rust crate (bramley-engine) runs MusicKit JS in an embedded
  runtime with native Widevine host objects, so non-browser playback has been done, unofficially **solid that it
  exists / unknown whether it is permitted or stable**. No Apple document was found on FairPlay versus Widevine for web
  playback or on developer-token terms **unknown**. What plays in Exo's WebView2 is AAC 256 kbps (High) or 64 kbps
  (Standard), no lossless or Atmos (`music-engines.md`) **solid**.
- Open-source Apple clients (Cider and others) wrap the same web player; no license or ToS comparison was made
  **unknown**.
- Verdict: no native path exists that is both legal to ship and stable. The web engine stays.

### YouTube Music

- **ytmusicapi** (Python, MIT) emulates the web client with the user's cookies for library, playlists, search and
  lyrics; it does not claim to provide streams **solid** (README). **pear-desktop** (formerly th-ch youtube-music;
  Electron, MIT) wraps the web page with plugins (ad blocking, lyrics, SponsorBlock) **solid** (listings). Exo's engine
  already does the same thing in a hidden WebView2 with its own InnerTube client (`YouTubeMusicNative.cs`) **solid**.
- Stream extraction for a browserless player (yt-dlp-class) means working around YouTube's client checks (PO tokens,
  SABR) and, for ad-free streams, the ad system. Not researched in depth **unknown**; **inferred** to be both ToS-hostile
  and a moving target. Not worth building when the web player is free, licensed and has no DRM.
- Verdict: nothing to gain. Keep the web engine.

### Spotube and the "other sources" family

Spotube (AGPL-3.0-or-later) is plugin-based; its README does not say which audio sources it uses **solid**; that it
streams from non-Spotify sources is widely reported but was not confirmed here **unknown**. Taking Spotify's catalog
and playing another source's audio is the pattern Exo should not copy.

## 3. Licensing: what is legally shippable

- Exo's `LICENSE` is source-available (read and check only); Exo Mouse is a separate AGPL-3.0-or-later program
  (`AGENTS.md`) **solid**.
- **MIT/BSD code** (librespot, ncspot, Spotifast, ytmusicapi, pear-desktop) may be bundled or ported with its notice in
  `THIRD_PARTY_NOTICES.md` **solid** (license texts). Legal to ship is not the same as safe to ship: see ToS above.
- **AGPL code** (Spotube): do not copy into Exo. Anything derived would have to be offered under AGPL, which the
  source-available Exo license does not allow. Exo Mouse shows the safe shape: a separate program, talking by postMessage
  **inferred** (not legal advice).
- **LGPL** (projectM, the MilkDrop engine; LGPL-2.1, ships no presets, needs OpenGL, Windows frontends exist; its
  README) **solid**. As a replaceable DLL it could ship with the notice. Presets are separate packs with their own
  licenses **unknown** per pack.
- **Keys and ids**: never bundle librespot's built-in Spotify client ids, any Widevine or FairPlay material, or any
  token. A BYO client id (Spotifast's way) is the only honest form. **Solid** as a rule (`AGENTS.md`: secrets stay in
  native code, no keys we have no right to).

## 4. Spotify Jam and Exo Jam

What is public about Spotify Jam (support.spotify.com/us/article/jam/) **solid**:

- Premium is needed to host; free users join in person only, Premium to join remotely.
- Invites by link, QR, nearby phones (about 1 m, Bluetooth), same Wi-Fi, or Spotify Messages for friends.
- Host controls: remove guests, let guests change what plays (off: add songs only), let guests change volume (Cast only).
- If the host leaves, the Jam ends. No participant limit is stated; third-party figures (10, 32) disagree **unknown**.
- Web player: not listed. A free-hosted Jam rumour (Android Authority, August 2026) is unconfirmed.
- There is no public API for a Jam. Everything Exo does with Jam is through the web player's internal module
  (`SocialConnectAPI`), which Spotify can change without notice **solid** (code) and **inferred** (fragility).

How the two can interoperate honestly:

| | Spotify Jam | Exo Jam |
|---|---|---|
| What it is | One shared queue and one playback, in Spotify's cloud, played on each member's own device | Each person follows someone's current song on their own service and account |
| Who needs what | Premium to host and to join remotely, Spotify only | Any of the three services, Exo, a Discord call (or a friend's Discord activity) |
| Shared state | Queue, controls | None: nobody changes anyone else's queue |

Honest bridges, in order of value:

1. **Same service, exact song**: Exo Jam on Spotify follows by track id (already built).
2. **Do not collide**: following someone while you are in a Spotify Jam would play a song into the Jam (host) or fail
   (guest without control). Listen along now refuses and says so (built; section 6).
3. **Invite the call to your Spotify Jam**: after hosting, a button that puts the Jam link in the call's text chat. Needs a
   real Spotify and Discord account to verify (not built; open decision).
4. **Follow a Jam's playing song from another service**: possible by reading the Jam's current track and treating the
   host like any friend. Not built: it is Exo Jam with a different source, and the web player does not expose a Jam's
   host track as an activity. **Unknown.**
5. **Cannot do**: join a Spotify Jam from Apple Music or YouTube Music, or mirror its queue there. Spotify's session is
   Spotify-only; no public protocol.

## 5. Decisions

**Build (this pass)**

- Listen-along sync accuracy: the listener's actual player position is now checked against the friend's clock and moved
  when off (tolerance 1.5 s for a call song, 5 s for Discord activity, cooldowns 8 s and 20 s); a measured seek time
  is added to each seek. Pure logic in `ui/src/lib/listenSync.ts`, tested with Node's own runner.
- Listen along on Spotify refuses while you are in a Spotify Jam.
- Correct the docs about Jam in the web player.

**Next, on a real account (the owner)**: the measurements and checks in section 7.

**Do not build**

- A bundled librespot/Spotify native engine in this pass. Reasons: Spotify calls it probably forbidden; Premium only; a
  BYO client id plus Development Mode limits puts a developer-dashboard step in front of every user; it would lose the
  lyrics, library and Jam Exo gets from the page today; it adds a Rust sidecar to build, sign and update; and the memory
  win over an already suspended engine is unmeasured. Revisit only as an explicit opt-in "experimental" engine, after the
  numbers in section 7 say the web engine is too heavy while playing.
- Native Apple Music or YouTube Music engines: no legal, stable path (above), and YouTube's web page has no DRM to
  escape from.
- A MilkDrop-class visualizer now. Audio plays inside each service's DRM-protected page; Exo does not receive PCM, and
  a web page cannot route protected media into Web Audio (**inferred**). The one route is a loopback of Exo's own
  audio session, plus an OpenGL host for projectM (LGPL) and a preset licensing review. That is a project, not a cheap
  add. A simple own-shader visualizer on loopback could be a later experiment.
- Spotube-style cross-source playback, anything AGPL in the main program, and any token or key Exo does not own.

## 6. Built in this pass

- `ui/src/lib/listenSync.ts` and `ui/test/listenSync.test.ts` (`npm test`, part of `npm run check`).
- `ui/src/lib/listenAlong.ts`: drift check on each `music.playback` update, seek latency estimate, Spotify Jam guard.
- The "who is listening", join flow and cross-service behaviour already exist (`JamBox.tsx`); not changed.

## 7. Needs a real account to verify

- Drift correction: listen along from a call member on each pair of services; watch that it does not stutter (seek
  storm) and that a stalled player catches up. If it stutters, widen `SYNC` in `listenSync.ts`.
- The Spotify Jam guard: be in a Jam (host and guest), press Listen on a call member's song.
- Memory and CPU of each hidden engine, playing and suspended (e2e `music` suite): the number that decides whether a
  native Spotify engine is worth a spike.
- Whether Jam still works through the web player today (`music-engines.md` previously said it did not).

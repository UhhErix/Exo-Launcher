# Exo Shield

Exo's blocker for the music players (YouTube Music, Spotify and Apple Music in the embedded browser). It is Exo's own
code under the launcher's licence, written for these three pages and nothing else: a few dozen network rules, a little
CSS, and one script on YouTube Music. It has no background page or service worker, downloads nothing and has no settings.

| What | Where it runs | Source |
| --- | --- | --- |
| Network rules (declarativeNetRequest, block only) | Requests made by the three provider pages, matched by the browser's network service | `rules/network.json` |
| Ad fields out of YouTube Music's player responses | music.youtube.com, in the page, before YouTube's code (`world: MAIN`, `document_start`) | `rules/youtube-music.js` |
| Cosmetic CSS | music.youtube.com and open.spotify.com only | `rules/youtube-music.css`, `rules/spotify.css` |

`node shield/build.mjs` (from `apps/launcher`) writes the extension to `ExoLauncher/Assets/Extensions/exo-shield` and pins
its new hash in `Helpers/WebExtensions.cs`; `node shield/build.mjs --check` fails when the shipped copy or the pin is behind
`rules/`. `ExoLauncher.Tests/ExoShieldTests` checks every rule (block only, provider pages only, no query rewrites, valid
filters, at most 100) and that the shipped copy matches the build. Commit `rules/`, the built folder and the pin together.

## When YouTube Music shows ads again

YouTube schedules a video ad from fields of the player response (`/youtubei/v1/player`): today `adPlacements`, `adSlots`,
`playerAds` and `adBreakHeartbeatParams`.

1. In Edge with no blocker, open DevTools on `https://music.youtube.com/watch?v=dQw4w9WgXcQ` in a fresh InPrivate window
   (signed out, the first video of a session usually gets an ad), filter the Network panel by `player`, and look at the
   response for top-level fields that name ads (search `ad`).
2. Add the new name to `AD_FIELDS` in `rules/youtube-music.js`. If the response moved (the page now reads it some other
   way than `JSON.parse` or `Response.json`), say so in the script and hook that instead.
3. If a new ad or tracking address shows up in the Network panel, add it to a group in `rules/network.json` (or a new group
   with its own `why`), limited by `from` to the page that makes the request. Never add a rule for `jnn-pa.googleapis.com`,
   `/youtubei/v1/player`, `/api/stats/watchtime` or `/api/stats/playback`: playback and history need them.
4. Bump `VERSION` in `build.mjs`, run `node shield/build.mjs`, then `dotnet test` in `apps/launcher`.
5. Check: a signed-out first video plays the song, not an ad (the music player's `.ad-showing` never appears), on a fresh
   profile.

## Spotify and Apple Music

Spotify's ads come only to signed-in Free accounts: the web player asks `spclient` for ad slots (`/ads/`) and ad decisions
(`/ad-logic/`). If an audio ad plays or a new upsell appears, find its request in the Network panel the same way and add
it to the Spotify groups. Keep away from what playback and Spotify Connect use: `/connect-state/`, `/track-playback/`,
`/melody/`, `apresolve.spotify.com`, `dealer` and `/storage-resolve/`. Apple Music has no ads; its one rule drops its
usage reports to `xp.apple.com`. Never rewrite a query on apple.com: Apple's sign-in hands its answer to the page as one.

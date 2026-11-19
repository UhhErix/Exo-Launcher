# Cinema art and metadata: a Cloudflare cache? (decision, 2026-11-05)

## Decision

**No.** Do not build a Worker, R2 bucket or Cache API layer for Cinema's posters or metadata. No provider clearly allows a shared copy, and the measured gain from this PC is about zero. Keep the direct fetches, and take the client-side changes below instead. Nothing was deployed, and no code was added under `cloud/art`.

| Source | Shared cache in R2 or the Worker? | Why |
| --- | --- | --- |
| Jellyfin / Emby images | **Never.** | They come from the person's own server. Routing them through Exo breaks PRIVACY.md and `Egress` (Server purpose). |
| TMDB (`image.tmdb.org`, and TMDB-sourced data) | **No.** | API terms list "Cache, for longer than 6 months, any information obtained through or from TMDB" among prohibited uses (line 354 of the terms text). They also prohibit "Make derivatives of the TMDB APIs or TMDB Content", and a resized copy served to others is a derivative. Attribution is required, and commercial use needs a written agreement. |
| Cinemeta (`v3-cinemeta.strem.io`, `cinemeta-catalogs.strem.io`) | **No.** | Stremio's terms (section 5.4.1) prohibit "copying or extracting any information from Stremio, including its contents". Cinemeta publishes no separate licence that allows redistribution. |
| MetaHub (`images.metahub.space`, `episodes.metahub.space`) | **No, until written permission.** | No published terms found on a search or on the site. Absence of terms is not permission. |
| Wikidata (`query.wikidata.org`) | **Allowed, but not worth it.** | Data is CC0 with no attribution required (attribution is requested). Its policy asks for a User-Agent and for backing off on 429 and Retry-After. Cinema already looks up each TMDB-only title once, keeps the answer sealed for a month, and sends a User-Agent (`ImdbIds.cs`). A shared Worker in front would add a privacy hop for almost no gain. |

## What Cinema does today (read from source)

- `src/ExoCinema/Library/ArtCache.cs`: every picture goes to `%LOCALAPPDATA%\Exo\art` (shared with the other apps), keyed by SHA-256 of the source URL. Repeat views come from disk. Concurrent requests for one picture share one download. Pictures are made to the page's width steps (`&w=`) on the PC, so a card never decodes a 1920 px original. HTTP/2 with pooling, 32 connections per host, 45 s timeout.
- `src/ExoCinema/Stremio/AddonClient.cs`: Cinemeta catalogs, meta and streams are fetched per request. There is no catalog cache, so Home refetches on each launch.
- `src/ExoCinema/Net/Egress.cs`: the Metadata purpose allows only the fixed hosts above, over https. A Worker would need a new fixed host and a PRIVACY.md change before it could be used.
- `PRIVACY.md` (Cinemeta section): says picture hosts "see your IP address and what you look at, as any website does; there's no account with them and nothing else is sent". A shared proxy would be a new observer of that same information, so the text would have to change.

## Measured from this PC (2026-11-05, curl, one connection per request)

Times are seconds. Medians are of 5 to 6 runs. The spread is large, so read these as ranges.

| Endpoint | Size | TTFB | Total |
| --- | --- | --- | --- |
| `images.metahub.space/poster/small/tt0111161/img` | 26.9 KB | median ~0.09 (range 0.06 to 0.27) | ~0.09 |
| `images.metahub.space/poster/medium/...` | 65.5 KB | 0.087 | 0.103 |
| `images.metahub.space/poster/large/...` | 100.3 KB | 0.068 | 0.084 |
| `v3-cinemeta.strem.io/meta/movie/tt0111161.json` | 2.3 KB | 0.087 | 0.087 |
| `v3-cinemeta.strem.io/catalog/movie/top.json` | 307 redirect (96 B), then 65 KB JSON | ~0.09 to 0.18 across runs | |
| `query.wikidata.org/sparql` (small query) | 171 B | 0.12 | 0.12 |
| Cloudflare edge (`cdn-cgi/trace`, served from colo ATL) | 236 B | 0.066 | 0.066 |

Not measured: `image.tmdb.org`. The probe URL I built was invalid and the request timed out, so there is no reliable number.

## Expected gain of a Cloudflare layer

A Worker cache only helps a cold picture, meaning the first time this PC sees it. Warm pictures already come from disk. The nearest Cloudflare colo to this PC is ATL, at about 66 ms, and MetaHub already answers in about 65 to 90 ms. The expected gain is roughly 0 to 30 ms per cold picture, which is inside the measurement noise. I'm not claiming a gain I didn't measure.

## Cost

- Workers requests and R2 operations for every cold picture, and an R2 bucket that would hold copies the terms don't allow.
- Work to add a fixed Egress host, change PRIVACY.md and add tests (the EgressTests pattern).
- Operating a shared proxy against Exo's "no logs" and "no analytics" rules (`observability.enabled = false` elsewhere in `cloud/`).

## Privacy effect

A shared proxy would see, per IP, which titles people browse, since IMDb IDs are in the picture URLs. Today only MetaHub and TMDB see that, as with any website. Adding Exo as a second observer is the main cost, and it is not worth a gain of roughly zero.

## Client-side improvements that do win (not implemented here)

1. **Smaller MetaHub size for small cards.** The Cinemeta meta already gives `poster/small` URLs. `playerplus.js` hard-codes `medium`. For cards up to 240 px wide, `small` is 27 KB against 65 to 100 KB, which saves 40 to 70 KB per cold poster. The time gain on this link is modest, because the cost is mostly round-trip latency. Needs an in-app measurement before the claim is made.
2. **Persist the Cinemeta catalog JSON on disk with a TTL, and paint from it first** (stale-while-revalidate). Home would paint from disk on relaunch instead of waiting on the network for each row. This saves per-row latency on cold start and needs no third party. Caveat: it keeps a local copy of Stremio's data, so the Stremio section 5.4.1 point needs an owner or legal read before shipping. The art cache already does the same for pictures.
3. **Keep the existing art cache** (already in place): dedupe, width fitting and disk reuse cover most of what a shared cache would do.

## Not verified

- MetaHub's terms: none found, not confirmed with the operator.
- The TMDB 6-month clause is read from the live terms text (line 354 of the page text, inside the prohibited-uses list). Confirm the context with the owner or a lawyer before relying on it.
- Stremio section 5.4.1 is read from the GTC text, not interpreted by counsel.
- Whether Exo's use is "commercial" under TMDB's terms: unknown, depends on Exo's business model.
- Client-side gains (items 1 and 2) are estimates, not in-app measurements.
- One probe was a mistake: a GET to `query.wikidata.org/sparql` with no query returned about 68 MB. It was one request and was not repeated. No data was kept.

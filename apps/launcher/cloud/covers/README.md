# Exo cover cache (cloud/covers)

One lookup for everyone. `GET https://covers.getexo.app/v1/cover?title=&id=&store=steam|gog` (a store only together with its id) answers
`{"ok":true,"url":"<store picture>","source":"steam|gog"}`, or 404 `{"ok":false,"miss":true}` when no store had it (never final: the Launcher still searches itself; the Worker remembers a clean miss for an hour only to spare the stores; `/health` says whether KV works), or 503 with `Retry-After: 900` when every store asked is backed off or failing. A 404 with `partial:true` means a store could not be asked: it is not remembered and the Launcher may look elsewhere. A 403 from a store counts as that store being unavailable, never as "no art". The Launcher asks it before its own title
lookups (`ExoLauncher/Services/CoverCacheClient.cs`); a miss or an unreachable cache falls through to the Launcher's own
lookups.

## Sources (keyless)

- Steam: a store search by title (exact match only), or a Steam app id; the poster is found on Steam's CDN (`library_600x900`).
- GOG: the catalog search by exact title (`catalog.gog.com`), or a GOG product id (`api.gog.com/v2/games`); the vertical cover.
- Epic (title only): the store-content product whose slug is the normalized title, and its first 1200x1600 portrait on Epic's own CDN (the Launcher's EpicCatalogArt path).

Title matching is `src/match.js`, the same rules as `CoverArtService.ScoreTitleMatch` in the Launcher. A sequel
(`Dying Light 2`) never covers its predecessor; an edition (`... Gold Edition`) does.

## Run the tests

    npm install   (once; the test tools are devDependencies)
    npm test

`node --test` checks the title rules (match.js). `vitest run` runs the Worker itself under workerd: SELF.fetch calls the real Worker, KV and the rate limit are miniflare's local stand-ins from wrangler.toml, and the Worker's outbound fetch is routed through rules in test/workerd/cover.test.mjs. No store is contacted.

## Deploy

See `DEPLOY.md` (exact steps; the KV id in `wrangler.toml` is a placeholder until step 3).

## Privacy

Only the title, the store and the id are read. The Launcher's row is in PRIVACY.md ("Exo's cover cache"). The Worker
does not log (`observability` off) and keeps no address; the rate limit counts per address at the edge.

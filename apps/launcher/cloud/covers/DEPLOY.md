# Deploy the cover cache (exo-covers)

Run from `cloud\covers` by the orchestrator, after review. Nothing here needs a key beyond the Cloudflare login the
other Workers already use. The Worker has never been deployed: `covers.getexo.app` does not exist yet.

1. Install and test (must pass first):

       npm ci
       npm test

2. Create the cache namespace and note the id it prints:

       npx wrangler kv namespace create COVERS

   If wrangler offers to add the KV binding to `wrangler.toml` itself, answer **No**: step 3 does it by hand, and the
   file's other settings (observability off, no workers.dev) must not be rewritten.

3. Put that id in `wrangler.toml`, replacing the whole placeholder value
   `REPLACE_WITH_COVERS_KV_ID_FROM_DEPLOY_MD` under `[[kv_namespaces]]`. While the placeholder is there, a deploy
   fails on purpose.

4. Check the four rate-limit namespaces are free on the account: `2101` (120 asks per address per minute), `2102`
   (20 uncached asks per address per minute), `2103` (300 store requests per minute, the ceiling of one Cloudflare
   location: rate limits count per location, not worldwide) and `2104` (50 store requests per address per minute, its
   share of that ceiling). Other Workers use 1001, 1101, 2001-2003, 3001, 3002, 3101, 3102, 4001, 4101, 4201 and 4301. Pick other
   numbers if the account already holds any of them.

5. Note the version now live, for the rollback below (there is none on a first deploy):

       npx wrangler deployments list

6. Deploy:

       npx wrangler deploy

   This creates Worker `exo-covers`, the custom domain `covers.getexo.app` on Exo's zone (with its proxied DNS record),
   observability off, `workers_dev` off. It must be a custom domain: the Cache API, where misses and backoffs live, does
   nothing on workers.dev.

7. Verify (a game name only, nothing about a player):

       curl -i "https://covers.getexo.app/v1/cover?title=Hades"
       curl -i "https://covers.getexo.app/v1/cover?store=steam&id=1145360"
       curl -i "https://covers.getexo.app/v1/cover?title=%21%21%21"
       curl -i "https://covers.getexo.app/v1/cover?store=gog&title=Hades"

   Expect JSON with `"ok":true` and a `url` for the first two (the second served from KV when repeated), and 400 for the
   third and fourth (a store needs its id). A title no store has answers 404 with `"miss":true` (remembered for an hour,
   only to spare the stores; the Launcher still searches for itself). If a store was unreachable, or this location's store
   ceiling was reached, it says `"partial":true` too and is not remembered. When every store is unreachable the answer is
   503 with `Retry-After: 900`. A 429 carries `Retry-After: 60`.

8. Post-deploy checks, the same day and again after a week:

   - **Health.** `curl -i https://covers.getexo.app/health` must answer 200 with
     `{"ok":true,"kvFailuresLastHour":0,"storesBackedOffCount":0}` (no personal data; it is rate-limited like the rest, and its KV check is kept for 60 seconds, so do not expect a change faster than that). `ok:false` with a 503 means KV does not
     answer. A `kvFailuresLastHour` above zero, a day later, means KV reads or writes are failing (the quota, most likely):
     answers still work, but nothing new is being kept. A `storesBackedOffCount` that stays above zero for hours means a store is refusing
     Cloudflare's addresses (Epic is the likely one): its titles are then asked of the Launcher itself, which is fine, but
     look at `npx wrangler tail exo-covers` for a minute to see why. The counts are per Cloudflare location, so check from
     a few places if it matters.
   - **KV keys.** `npx wrangler kv key list --binding COVERS --remote` shows only `cover:v1:...` keys, which are
     hits. Backoffs and misses are in the Cache API and never appear here; a `backoff:` key in KV means the wrong build is
     live.
   - **KV write quota.** Confirm the account's KV plan and its daily write limit in the Cloudflare dashboard
     (Workers & Pages, KV). The Worker writes KV only for a hit, one write per game found, once per 30 days.
   - **No CORS header.** `curl -i "https://covers.getexo.app/v1/cover?title=Hades"` must show no
     `Access-Control-Allow-Origin`: the cache is for the Launcher, not for web pages.

9. Commit the real KV id in `wrangler.toml`. Nothing else is stored: art addresses keyed by store id or normalized title
   in KV (30 days); clean misses (1 hour), store backoffs (at most 15 minutes) and the KV failure count in the Cache API.

## Rollback

If the cache misbehaves, the Launcher falls back to its own lookups on its own (an unreachable or failing cache is left
alone for two minutes at a time), so there is time to act calmly:

    npx wrangler rollback            # back to the previous version (the one noted in step 5)
    npx wrangler delete exo-covers   # or remove the Worker; the Launcher then simply never gets an answer

Removing the Worker leaves the custom domain's DNS record: delete `covers` in the zone's DNS list as well if it must not
resolve. Hits already in KV can stay; they are only art addresses.

## Timing the Launcher relies on

The Worker gives up on its stores after the total in `src/limits.json` (9 seconds; 3 seconds for each store), and the
Launcher waits 12 seconds (`RequestTimeout` in `ExoLauncher/Services/CoverCacheClient.cs`). A test in
`ExoLauncher.Tests` reads `limits.json` and fails if the Launcher's wait is not longer.

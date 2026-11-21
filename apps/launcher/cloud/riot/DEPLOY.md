# Deploy Exo Riot hours (exo-riot)

Run from `apps\launcher\cloud\riot` after review.

1. Install and run tests (must pass first):

       npm ci
       npm test

2. Create the KV namespace and note its ID:

       npx wrangler kv namespace create RIOT_CACHE

   Answer **No** if Wrangler offers to edit `wrangler.toml` automatically.

3. Put the KV namespace ID into `wrangler.toml`, replacing `REPLACE_WITH_RESOURCE_ID`, and add the custom domain route:

       routes = [{ pattern = "riot.getexo.app", custom_domain = true }]

4. Check rate-limit namespaces `5101`, `5102`, and `5103` are unused on the Cloudflare account.

5. Add provider secrets (at least one):

       npx wrangler secret put RIOT_API_KEY
       npx wrangler secret put HENRIK_API_KEY

6. Deploy:

       npx wrangler deploy

7. Verify:

       curl -i "https://riot.getexo.app/health"
       curl -i "https://riot.getexo.app/v1/hours?riotId=YourName%23TAG"

## Budget contract

`src/limits.json` sets `totalBudgetMs` (18s). `ExoLauncher/Adapters/Owned/RiotHoursClient.cs` waits longer (`RequestTimeout` = 24s), enforced by a test in `ExoLauncher.Tests`.

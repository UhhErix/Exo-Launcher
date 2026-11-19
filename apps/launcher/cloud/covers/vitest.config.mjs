import { cloudflareTest } from '@cloudflare/vitest-plugin'
import { defineConfig } from 'vitest/config'

// The Worker runs under workerd (the runtime Cloudflare deploys), with its KV namespace and rate limit from wrangler.toml
// simulated locally by miniflare. Upstream fetches are intercepted by fetchMock; no store is contacted.
export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: './wrangler.toml' },
    }),
  ],
  test: {
    include: ['test/workerd/**/*.test.mjs'],
  },
})

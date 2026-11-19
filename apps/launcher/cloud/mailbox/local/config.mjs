/** The bindings wrangler.toml declares, for the local runtime (test/mailbox.test.mjs checks the two agree). */
export const bindings = { BOXES: 'Box', LINKS: 'Link' }
export const ratelimits = { LINK_GUESSES: { limit: 10, period: 60 } }
export const buckets = ['MEDIA']

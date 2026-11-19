#!/usr/bin/env node
/**
 * Checks that keys can leave ACCOUNT_SECRETS (README.md › Keys). Usage:
 *   node scripts/retire-key.mjs <current key id> <retired key id>... < rows.json
 * rows.json is the account rows, from (the Worker's D1, read only):
 *   npx wrangler@4.158.0 d1 execute exo-account --remote --json --command "SELECT id, email_mac, recovery_mac, hex(item_key) AS item_key FROM accounts"
 * Exit 0 when no row names a retired key. Exit 1, with the count per column and no account ids, when one does, or when the
 * current key is among those retired (the first key of the ring is current and never retires).
 */
import { namedRetired } from '../src/rotation.js'

const [current, ...retired] = process.argv.slice(2)
if (!current || retired.length === 0) {
  console.error('usage: node scripts/retire-key.mjs <current key id> <retired key id>... < rows.json')
  process.exit(2)
}
if (retired.includes(current)) {
  console.error(`refused: ${current} is the current key`)
  process.exit(1)
}

let text = ''
for await (const chunk of process.stdin) text += chunk
const parsed = JSON.parse(text)
const rows = Array.isArray(parsed) && parsed[0]?.results ? parsed[0].results : parsed

const named = namedRetired(rows, retired)
if (named.length) {
  const counts = {}
  for (const [column] of named) counts[column] = (counts[column] ?? 0) + 1
  console.error(`refused: rows still name ${retired.join(', ')} (${JSON.stringify(counts)})`)
  process.exit(1)
}
console.log(`ok: no row names ${retired.join(', ')} (${rows.length} rows)`)

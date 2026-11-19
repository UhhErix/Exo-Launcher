/**
 * The deploy steps in README.md must never replace the live keyring. A `wrangler secret put ACCOUNT_SECRETS` on the live
 * Worker makes every account's items and recovery codes unreadable for good (Cloudflare never gives a secret back). How
 * this could fail: a "first deploy" step that generates and puts a fresh key is copied back in, or a redeploy step tells
 * the reader to put the secret, or the warning at the top goes.
 *
 *   node --test test/readme.test.mjs
 */
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const README = readFileSync(new URL('../README.md', import.meta.url), 'utf8').replaceAll('\r\n', '\n')

/** The README split into its "## " sections, each with its heading. */
function sections(text) {
  const out = []
  let current = { heading: '(top)', lines: [] }
  for (const line of text.split('\n')) {
    if (line.startsWith('## ')) {
      out.push(current)
      current = { heading: line.slice(3).trim(), lines: [] }
    } else current.lines.push(line)
  }
  out.push(current)
  return out
}

const PUT = /secret put ACCOUNT_SECRETS/

/** A mention that forbids the command, not one that tells the reader to run it. */
const forbids = (line) => /\bnever\b/i.test(line)

/** Every instruction to put the secret outside the Keys section, as "section: line". */
export function strayPuts(text) {
  const found = []
  for (const { heading, lines } of sections(text)) {
    if (heading === 'Keys') continue
    for (const line of lines) if (PUT.test(line) && !forbids(line)) found.push(`${heading}: ${line.trim()}`)
  }
  return found
}

test('the README puts ACCOUNT_SECRETS only in the Keys section; elsewhere it only says never to', () => {
  assert.deepEqual(strayPuts(README), [])
})

test('no step generates a fresh key straight into the secret', () => {
  for (const line of README.split('\n')) {
    assert.ok(!(/randomBytes/.test(line) && PUT.test(line)), line)
  }
})

test('the warning is at the top and the redeploy steps exist', () => {
  const top = README.split('\n## ')[0]
  assert.match(top, /Never run `wrangler secret put ACCOUNT_SECRETS`/)
  assert.match(README, /^## Redeploy$/m)
  assert.doesNotMatch(README, /nothing exists yet/)
})

test('the guard finds a put outside Keys, the way the old Before deploy step had it', () => {
  const old = "# Exo account\n## Before deploy\n2. **ACCOUNT_SECRETS**: `node -e \"...\" | npx wrangler@4.158.0 secret put ACCOUNT_SECRETS`.\n## Keys\n1. `npx wrangler secret put ACCOUNT_SECRETS`\n"
  assert.equal(strayPuts(old).length, 1)
  assert.match(strayPuts(old)[0], /^Before deploy: /)
})

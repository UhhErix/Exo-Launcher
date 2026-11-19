// The release card (../README.md, At each release): when a release is live, the Worker posts its card to the app's Discord
// releases channel, once per tag. The webhook is a Worker secret, DISCORD_WEBHOOK_<APP>, and the role ping DISCORD_ROLE_<APP>,
// so no publishing PC holds them.
//
// Nothing here can fail a release: every refusal is a status the caller prints, and a missing secret is logged and skipped.
// The claim is an R2 object, announce/<app>/<tag>.json, written with a precondition so only one request can post a tag.
//
// One card per channel: once a tag's card is up, the cards of earlier tags are deleted (removeOlderCards). The cards still up
// are listed in announce/<app>/cards.json. A second call for a tag that already has its card posts nothing and only finishes
// that clean-up, so a delete Discord refused is tried again.
const PRODUCTS = { launcher: 'Exo Launcher', cinema: 'Exo Cinema' }
// Discord draws colour 0 as none, so Void black is 0x010101.
const VOID_BLACK = 0x010101
const MARK_URL = 'https://getexo.app/brand/discord/exo-logo-512.png'
// The card's silent flag: a role mention fires without a notification sound.
const SUPPRESS_NOTIFICATIONS = 4096
const SNOWFLAKE = /^\d{17,20}$/
const MAX_HIGHLIGHTS = 5
const MAX_HIGHLIGHT_LENGTH = 200
// Older cards deleted in one call, and claims read when cards.json does not exist yet: both keep a call's requests few.
const MAX_REMOVALS = 8
const MAX_CLAIMS_READ = 30

/** True only for an https Discord webhook on the default port, with no user info; a query string (thread_id) is allowed. */
export function isDiscordWebhook(url) {
  if (typeof url !== 'string') return false
  let parsed
  try {
    parsed = new URL(url)
  } catch {
    return false
  }
  return parsed.protocol === 'https:' && parsed.port === '' && !parsed.username && !parsed.password &&
    ['discord.com', 'discordapp.com'].includes(parsed.hostname) && /^\/api\/webhooks\/\d+\/[\w-]+\/?$/.test(parsed.pathname)
}

/** The card's JSON: the product and version, the change highlights, the download link and an optional role ping. */
export function cardPayload({ app, version, highlights, roleId, when }) {
  const product = PRODUCTS[app]
  const changelog = `https://getexo.app/${app}/changelog`
  // What changed first, then how to get it. No download link (owner, 2026-11-07): the app updates itself.
  const lines = highlights.map((line) => `• ${line}`)
  lines.push(...(lines.length ? [''] : []), `Open ${product} to update. [Full changelog](${changelog})`)
  const description = lines.join('\n')
  const payload = {
    username: `${product} Releases`,
    avatar_url: `https://getexo.app/brand/discord/exo-${app}-avatar.png`,
    allowed_mentions: roleId ? { parse: [], roles: [roleId] } : { parse: [] },
    flags: SUPPRESS_NOTIFICATIONS,
    embeds: [{
      color: VOID_BLACK,
      title: `${product} ${version}`,
      url: changelog,
      description,
      footer: { text: 'Exo', icon_url: MARK_URL },
      timestamp: when.toISOString().replace(/\.\d{3}Z$/, 'Z'),
    }],
  }
  if (roleId) payload.content = `<@&${roleId}>`
  return payload
}

/**
 * Posts the card for a live release, once. Returns { status }: posted, already (this tag has a claim), skipped (-NoAnnounce),
 * no-webhook, refused (the secret is not a Discord webhook), not-live, wrong-version, or failed (Discord refused it; the claim
 * is released so a retry can post). Never returns the webhook URL.
 */
export async function announce({ app, tag, announce = true, env, mirror, fetcher = (...args) => globalThis.fetch(...args), now = new Date() }) {
  // announce:false is a no-op: it writes no claim, so an unauthenticated call can never stop a release's card.
  if (!announce) return { status: 'skipped' }
  const claim = `announce/${app}/${tag}.json`
  const at = now.toISOString()
  const latest = await readJson(mirror, `${app}/latest.json`)
  if (latest?.tag !== tag) return { status: 'not-live' }
  const whatsNew = await readJson(mirror, `${app}/${tag}/whats-new.json`)
  if (whatsNew?.version !== tag.slice(1)) return { status: 'wrong-version' }

  const name = app.toUpperCase()
  const webhook = env[`DISCORD_WEBHOOK_${name}`]
  if (!webhook) {
    console.warn(`DISCORD_WEBHOOK_${name} is not set; the ${tag} release card was skipped.`)
    return { status: 'no-webhook' }
  }
  if (!isDiscordWebhook(webhook)) {
    console.warn(`DISCORD_WEBHOOK_${name} is not a Discord webhook URL; the ${tag} release card was skipped.`)
    return { status: 'refused' }
  }
  const role = (env[`DISCORD_ROLE_${name}`] ?? '').replace(/\s/g, '')
  if (role && !SNOWFLAKE.test(role)) console.warn(`DISCORD_ROLE_${name} is not a Discord id; the card posts without a mention.`)
  const roleId = SNOWFLAKE.test(role) ? role : ''

  // The webhook's query (thread_id) goes on every call; wait=true returns the posted message's id.
  const hook = new URL(webhook)
  const search = hook.search
  hook.search = ''
  const root = hook.toString().replace(/\/$/, '')

  const claimed = await mirror.put(claim, JSON.stringify({ state: 'posting', at }), { onlyIf: { etagDoesNotMatch: '*' } })
  if (!claimed) {
    const existing = await readJson(mirror, claim)
    const isPosted = existing?.state === 'posted'
    if (isPosted && SNOWFLAKE.test(String(existing.id ?? ''))) {
      await removeOlderCards({ app, tag, id: String(existing.id), mirror, fetcher, root, search })
    }
    return { status: 'already', posted: isPosted }
  }

  const highlights = (Array.isArray(whatsNew.items) ? whatsNew.items : [])
    .map((item) => (typeof item?.title === 'string' ? item.title.slice(0, MAX_HIGHLIGHT_LENGTH) : ''))
    .filter(Boolean)
    .slice(0, MAX_HIGHLIGHTS)
  const payload = cardPayload({ app, version: tag.slice(1), highlights, roleId, when: now })

  const posted = await send(fetcher, `${root}?wait=true${search ? '&' + search.slice(1) : ''}`, 'POST', payload)
  const id = String(posted?.id ?? '')
  if (!SNOWFLAKE.test(id)) {
    await mirror.delete(claim)
    return { status: 'failed' }
  }
  // The role mention fires once; the card keeps its text without it.
  if (roleId) await send(fetcher, `${root}/messages/${id}${search}`, 'PATCH', { content: '' })
  await mirror.put(claim, JSON.stringify({ state: 'posted', id, at }))
  await removeOlderCards({ app, tag, id, mirror, fetcher, root, search })
  return { status: 'posted' }
}

const isCard = (card) => typeof card?.tag === 'string' && SNOWFLAKE.test(String(card?.id ?? ''))

/**
 * Deletes the cards of earlier tags, so the channel holds this tag's card only. cards.json lists the cards still up; before it
 * exists, every posted claim is read once. A card Discord would not delete (anything but 204, or 404 for one already gone)
 * stays in the list for the next call. Never throws: the card is up and the release is live whatever happens here.
 */
async function removeOlderCards({ app, tag, id, mirror, fetcher, root, search }) {
  try {
    const key = `announce/${app}/cards.json`
    const known = await readJson(mirror, key)
    const listed = Array.isArray(known?.cards) ? known.cards.filter(isCard) : null
    const older = (listed ?? await claimedCards(mirror, app)).filter((card) => String(card.id) !== id)
    if (listed && !older.length && listed.length === 1) return
    const left = older.slice(MAX_REMOVALS)
    for (const card of older.slice(0, MAX_REMOVALS)) {
      const status = await remove(fetcher, `${root}/messages/${card.id}${search}`)
      if (status !== 204 && status !== 404) left.push(card)
    }
    await mirror.put(key, JSON.stringify({ cards: [...left.map((card) => ({ tag: card.tag, id: String(card.id) })), { tag, id }] }))
  } catch {
    // Left for the next call.
  }
}

/** Every tag's posted card, read from the claims (announce/<app>/v*.json). Only used until cards.json exists. */
async function claimedCards(mirror, app) {
  const prefix = `announce/${app}/v`
  const listing = await mirror.list({ prefix, limit: MAX_CLAIMS_READ })
  const cards = []
  for (const object of listing?.objects ?? []) {
    const claim = await readJson(mirror, object.key)
    const card = { tag: object.key.slice(prefix.length - 1, -'.json'.length), id: String(claim?.id ?? '') }
    if (claim?.state === 'posted' && isCard(card)) cards.push(card)
  }
  return cards
}

/** One DELETE to Discord; the status, or 0 when the request itself failed. */
async function remove(fetcher, url) {
  try {
    return (await fetcher(url, { method: 'DELETE' })).status
  } catch {
    return 0
  }
}

async function readJson(mirror, key) {
  try {
    const object = await mirror.get(key)
    return object ? await object.json() : null
  } catch {
    return null
  }
}

/** One request to Discord; the parsed JSON on success, null on any failure. Errors are not returned: they can carry the URL. */
async function send(fetcher, url, method, body) {
  try {
    const response = await fetcher(url, {
      method,
      headers: { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
    if (!response.ok) return null
    return await response.json().catch(() => ({}))
  } catch {
    return null
  }
}

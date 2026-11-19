/** A held item's key is "type/id"; the reason is a code the host sends (AccountSyncEngine.HeldTooLarge, HeldRefused). */
export type HeldItem = { key: string; reason: string }

/** The words for each synced type, singular and plural. Internal keys never reach the page. */
const LABELS: Record<string, [string, string]> = {
  'cover-image': ['cover image', 'cover images'],
  cover: ['cover choice', 'cover choices'],
  settings: ['setting', 'settings'],
  playtime: ['playtime entry', 'playtime entries'],
  'playtime-seed': ['starting playtime', 'starting playtimes'],
  'riot-ledger': ['Riot hours entry', 'Riot hours entries'],
  collection: ['collection', 'collections'],
}

/** One plain sentence per kind of held item, grouped: "1 cover image is too large to sync. It stays on this PC." */
export function heldSentences(held: HeldItem[]): string[] {
  const groups = new Map<string, { type: string; reason: string; count: number }>()
  for (const item of held) {
    const type = item.key.split('/')[0] ?? item.key
    const group = `${type}|${item.reason}`
    const seen = groups.get(group)
    if (seen) seen.count += 1
    else groups.set(group, { type, reason: item.reason, count: 1 })
  }
  return [...groups.values()].map(({ type, reason, count }) => {
    const [one, many] = LABELS[type] ?? ['item', 'items']
    const noun = count === 1 ? one : many
    if (reason === 'too-large') {
      return count === 1
        ? `1 ${noun} is too large to sync. It stays on this PC.`
        : `${count} ${noun} are too large to sync. They stay on this PC.`
    }
    return count === 1
      ? `1 ${noun} was not accepted by the service. It stays on this PC.`
      : `${count} ${noun} were not accepted by the service. They stay on this PC.`
  })
}

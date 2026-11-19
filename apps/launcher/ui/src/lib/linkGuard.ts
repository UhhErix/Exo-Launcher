import { host } from './host'

/**
 * Opens a link from a message or a note in the browser. The host checks it against look-alikes of well-known addresses
 * (offline, by its spelling alone): one that imitates a brand isn't opened but reported to `onLookalike` with the brand
 * ('' for an address that hides where it goes), and the page asks in place before opening it with `openAnyway`.
 */
export function openGuarded(url: string, onLookalike: (brand: string) => void) {
  void host.openUrl(url, { guard: true }).then(result => { if (result.lookalike !== undefined) onLookalike(result.lookalike) }).catch(() => { })
}

export function openAnyway(url: string) {
  void host.openUrl(url, { confirmed: true }).catch(() => { })
}

export const lookalikeQuestion = (brand: string) => (brand ? `Fake ${brand} link? Open anyway` : 'Hides its address. Open anyway')

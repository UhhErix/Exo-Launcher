import { prefetchMessages } from './messageCache'

/** Now, where the room lands: who is in voice, who is playing, what is unread. */
export const NOW = 'now'
/** Friends, the second row of the list (a saved place of 'friends' from an older build opens it). */
export const FRIENDS = 'friends'
/** Messages with no conversation open, and the Servers page: places in the room, as Shop and Message requests are. */
export const MESSAGES = 'messages'
export const SERVERS = 'servers'
export const SHOP = 'shop'
export const REQUESTS = 'requests'

// A conversation the pointer rests on is read before the click, as Discord's own client does.
export const peek = (event: React.PointerEvent) => prefetchMessages((event.target as HTMLElement).closest<HTMLElement>('[data-dc-channel]')?.dataset.dcChannel)
export const unpeek = () => prefetchMessages(null)


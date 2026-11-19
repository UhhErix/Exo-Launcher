import { createContext, useCallback, useEffect, useState } from 'react'
import type { DiscordFriend, DiscordPresence } from './discord'
import { host, onHostEvent, type Game } from './host'
import { onPageActivityChanged, pageIsVisible } from './pageActivity'

/**
 * The Library as Home sees it: the games, the one starting, and Play or Install (the Library's own
 * shortcut) or the Store's search. Provided by LauncherApp around the room, so a game change redraws only what reads it.
 */
export type GameLink = { games: Game[]; startingId: string | null; onPlay: (game: Game) => void; onFind: (title: string) => void }
export const GameLinkContext = createContext<GameLink | null>(null)

/** A presence that changes what the list shows: someone on it whose status or activity is not what it shows. */
function changes(friend: DiscordFriend, event: DiscordPresence) {
  return friend.status !== event.status || (friend.activity ?? null) !== (event.activity ?? null) || (friend.activityType ?? null) !== (event.activityType ?? null)
    || JSON.stringify(friend.rich ?? null) !== JSON.stringify(event.rich ?? null)
    || JSON.stringify(friend.prep ?? null) !== JSON.stringify(event.prep ?? null)
}

/**
 * The friends list from Exo's Discord client (friends and requests), kept live by its presence events. A burst of
 * changed events (a sign-in sync) becomes one read; one while Exo is out of sight waits until it's back. Presence comes
 * for every server member on screen in Discord: only a friend's that changes what shows redraws, and a burst of them
 * (a big server coming online) lands as one redraw. reload: read again now (after an accept or a remove).
 */
export function useFriends(): { friends: DiscordFriend[]; loaded: boolean; reload: () => void } {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [loaded, setLoaded] = useState(false)
  const reload = useCallback(() => { host.discordFriends().then(result => setFriends(result.friends)).catch(() => { }).finally(() => setLoaded(true)) }, [])
  useEffect(() => {
    let stale = false
    const load = reload
    load()
    let timer = 0
    const soon = () => {
      window.clearTimeout(timer)
      if (!pageIsVisible()) { stale = true; return }
      timer = window.setTimeout(load, 400)
    }
    const offActivity = onPageActivityChanged(() => { if (stale && pageIsVisible()) { stale = false; load() } })
    // A server's own changes (its channels, roles, events) never touch your friends; yours, a DM's and the account's may.
    const offChanged = onHostEvent('discord.changed', event => { if (!event.voice && !event.guildId) soon() })
    const waiting = new Map<string, DiscordPresence>()
    let flush = 0
    const offPresence = onHostEvent('discord.presence', event => {
      waiting.set(event.userId, event)
      if (flush) return
      flush = window.setTimeout(() => {
        flush = 0
        const batch = new Map(waiting)
        waiting.clear()
        setFriends(list => list.some(f => { const e = batch.get(f.id); return !!e && changes(f, e) })
          ? list.map(f => { const e = batch.get(f.id); return e && changes(f, e) ? { ...f, status: e.status, activity: e.activity, activityType: e.activityType, rich: e.rich, prep: e.prep } : f })
          : list)
      }, 120)
    })
    return () => { window.clearTimeout(timer); window.clearTimeout(flush); offActivity(); offChanged(); offPresence() }
  }, [reload])
  return { friends, loaded, reload }
}

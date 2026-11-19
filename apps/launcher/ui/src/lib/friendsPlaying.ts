import { createContext, useCallback, useEffect, useState } from 'react'
import type { DiscordFriend, DiscordPresence } from './discord'
import { host, onHostEvent, type Game } from './host'
import { onPageActivityChanged, pageIsVisible } from './pageActivity'
import { titleIdentity } from './utils'

/** A game friends are in: one you have (its card), or one you don't (only its name, from their presence). */
export type PlayingGroup = { key: string; title: string; game: Game | null; friends: DiscordFriend[] }

/**
 * The Library as Home sees it: the games, the one starting, and Play or Install (the Library's own
 * shortcut) or the Store's search. Provided by LauncherApp around the room, so a game change redraws only what reads it.
 */
export type GameLink = { games: Game[]; startingId: string | null; onPlay: (game: Game) => void; onFind: (title: string) => void }
export const GameLinkContext = createContext<GameLink | null>(null)

/**
 * The game in a presence's name. A clip recorder or overlay that reports the game for its user adds itself to the name
 * ("VALORANT with Medal", "Apex Legends via Overwolf"); the game is what comes before it.
 */
function gameName(presence: string): string {
  return presence.trim().replace(/\s+(?:with|via|on|\+|-|\|)\s+(?:Medal(?:\.tv)?|Overwolf|Outplayed|Insights Capture|SteelSeries Moments|Blitz|Mobalytics|Porofessor|U\.GG)\s*$/i, '').trim()
}

/** What a friend is playing, by its rich presence's name, else the activity Discord says is Playing; null when nothing. */
export function playingName(friend: DiscordFriend): string | null {
  if (friend.kind !== 'friend' || friend.status === 'offline') return null
  const name = friend.rich?.type === 0 ? friend.rich.name : friend.activityType === 0 ? friend.activity : null
  return name ? gameName(name) || null : null
}

// Friends in a game, the most players first: those you have by their card, the rest by the name they show in Discord.
export function playingGroups(friends: DiscordFriend[], games: Game[]): PlayingGroup[] {
  if (friends.length === 0) return []
  // A card goes by each game it holds too: a friend's Discord says "Call of Duty®" of the card named Modern Warfare 4.
  const byTitle = new Map(games.flatMap(game => [game.title, ...(game.includes ?? []).map(part => part.title)].map(title => [titleIdentity(title), game] as const)))
  const groups = new Map<string, PlayingGroup>()
  for (const friend of friends) {
    const name = playingName(friend)
    const identity = name ? titleIdentity(name) : ''
    if (!name || !identity) continue
    const game = byTitle.get(identity) ?? null
    const key = game ? game.id : `title:${identity}`
    const group = groups.get(key) ?? { key, title: game ? game.title : name, game, friends: [] }
    group.friends.push(friend)
    groups.set(key, group)
  }
  return [...groups.values()].sort((a, b) => b.friends.length - a.friends.length || a.title.localeCompare(b.title))
}

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

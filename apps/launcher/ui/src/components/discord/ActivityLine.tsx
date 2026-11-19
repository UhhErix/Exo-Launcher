import type { DiscordFriend } from '../../lib/discord'
import { Icons } from './icons'

const firstArtist = (state: string | null | undefined) => state?.split(';')[0].trim() ?? null

export function statusWord(status: string | null | undefined) {
  return status === 'idle' ? 'Idle' : status === 'dnd' ? 'Do not disturb' : status === 'offline' || status === 'invisible' || !status ? 'Offline' : 'Online'
}

const DOING: Record<number, string | undefined> = { 0: 'Playing', 1: 'Streaming', 2: 'Listening to', 3: 'Watching', 5: 'Competing in' }

/** A conversation's muted line under its person: what they are doing ("Playing VALORANT"), else their status. */
export function doingWords(friend: DiscordFriend | undefined, status: string | null | undefined, activity: string | null | undefined): string {
  const offline = (friend?.status ?? status) === 'offline'
  if (friend?.activity && !offline) {
    if (friend.activityType === 4) return friend.activity
    const what = friend.activityType === 2 && friend.rich?.details ? friend.rich.details : friend.activity
    const verb = DOING[friend.activityType ?? 0]
    return verb ? `${verb} ${what}` : what
  }
  if (activity && !offline) return activity
  return statusWord(friend?.status ?? status)
}

const KIND_ICONS: Record<number, typeof Icons.Playing | undefined> = { 0: Icons.Playing, 1: Icons.Streaming, 2: Icons.Listening, 3: Icons.Watching, 5: Icons.Competing }

/** The line under a friend: their custom status, else the activity with its icon, else their status. */
export function ActivityLine({ friend }: { friend: DiscordFriend }) {
  if (!friend.activity || friend.status === 'offline') return <>{statusWord(friend.status)}</>
  const Icon = KIND_ICONS[friend.activityType ?? -1]
  return <span className="exo-dc-activity">{Icon ? <Icon size={14} className="exo-dc-activity-icon" /> : null}{activityWords(friend)}</span>
}

/** The words of the line, in full (an Active Now card's name for screen readers and its tip): a song says its title and first artist. */
export function activityWords(friend: DiscordFriend): string {
  if (!friend.activity || friend.status === 'offline') return statusWord(friend.status)
  return friend.activityType === 2 && friend.rich?.details ? [friend.rich.details, firstArtist(friend.rich.state)].filter(Boolean).join(' · ') : friend.activity
}

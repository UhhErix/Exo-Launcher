import { useEffect, useState, type ReactNode } from 'react'
import { Stop } from '../../brand/icons'
import { host, onHostEvent } from '../../lib/host'
import type { MusicPlayback } from '../../lib/music'
import { listenAlong, sameSong, stopListening, useListenAlong, useListeningFriends, type ListenAlong } from '../../lib/listenAlong'
import { Avatar } from './presence'

/** What Exo's music has on: a song playing or paused, or null. */
function usePlayback() {
  const [now, setNow] = useState<MusicPlayback | null>(null)
  useEffect(() => {
    let live = true
    const take = (playback: MusicPlayback | null | undefined) =>
      setNow(playback && (playback.status === 'playing' || playback.status === 'paused') && playback.title ? playback : null)
    host.musicPlayback().then(result => { if (live) take(result.playback) }).catch(() => { if (live) setNow(null) })
    const off = onHostEvent('music.playback', take)
    return () => { live = false; off() }
  }, [])
  return now
}

/**
 * The Jam box, above Voice connected: one row, only for someone else's music you can join, the song first (its cover,
 * title and artist) and who is on it after the title (their pictures). Listening along with someone (Stop); else
 * someone in your call playing a song you aren't (Listen plays it here, on your service). Your own song shows only in
 * the Bar's player. In your call a song is what their Exo plays, through Exo's call relay (end-to-end encrypted with
 * the call's own keys); otherwise a friend's Discord activity (lib/listenAlong.ts). Yours goes only to the Exo apps in
 * your call that way: Exo never shares it with Discord. callIds: who is in your call.
 */
export function JamBox({ callIds }: { callIds: readonly string[] }) {
  const along = useListenAlong()
  const now = usePlayback()
  const playing = now?.status === 'playing' ? now.title : null
  // Friends are only read while there could be a Jam to join: in a call, or listening along.
  const { listening } = useListeningFriends(along.status !== 'off' || callIds.length > 0)

  if (along.status !== 'off') return <AlongRow along={along} now={now} listening={listening} />
  const inCall = listening.find(({ friend, rich }) => callIds.includes(friend.id) && !sameSong(rich.details, playing))
  if (inCall) {
    const { friend, rich } = inCall
    const onIt = listening.filter(other => sameSong(other.rich.details, rich.details)).map(other => other.friend)
    return (
      <JamRow label="Jam" art={rich.image} stand={friend} title={rich.details ?? ''} line={rich.state} people={onIt}>
        <button type="button" className="exo-set-btn is-compact"
          onClick={() => listenAlong({ id: friend.id, name: friend.name, avatar: friend.avatar }, rich)}>Listen</button>
      </JamRow>
    )
  }
  return null
}

type Listener = { id: string; name: string; avatar: string | null }

/** Listening along: their song (its cover here once it plays, else theirs), the friend and anyone else on it, and Stop. */
function AlongRow({ along, now, listening }: {
  along: Exclude<ListenAlong, { status: 'off' }>; now: MusicPlayback | null; listening: ReturnType<typeof useListeningFriends>['listening']
}) {
  const line = along.status === 'finding' ? `Finding it on ${along.on}` : along.status === 'missing' ? `Not on ${along.on}`
    : along.status === 'failed' ? `Couldn't play it on ${along.on}` : along.status === 'paused' ? 'Paused' : along.artist
  const theirs = listening.find(({ friend }) => friend.id === along.friend.id)?.rich
  const others = listening.filter(({ friend, rich }) => friend.id !== along.friend.id && sameSong(rich.details, along.title)).map(({ friend }) => friend)
  return (
    <JamRow label="Jam" art={sameSong(now?.title, along.title) ? now?.artworkDataUrl ?? theirs?.image : theirs?.image}
      stand={along.friend} title={along.title} line={line} people={[along.friend, ...others]}>
      <button type="button" className="exo-dc-icon-btn" aria-label="Stop listening along" onClick={() => stopListening()}><Stop size={14} /></button>
    </JamRow>
  )
}

/**
 * One row of the Jam box: the song's cover (else the picture of whoever plays it, <paramref name="stand"/>), its title
 * and line, the pictures of who is on it (three, then a count), and its button. Its controls are the Bar's player's.
 */
function JamRow({ label, art, stand, title, line, people = [], children }: {
  label: string; art?: string | null; stand?: Listener; title: string; line?: string | null; people?: Listener[]; children?: ReactNode
}) {
  const shown = people.slice(0, 3)
  return (
    <section className="exo-dc-jam" aria-label={label}>
      {art ? <img className="exo-dc-jam-art" src={art} alt="" width={32} height={32} draggable={false} decoding="async" />
        : stand ? <Avatar src={stand.avatar} name={stand.name} status={null} size={32} /> : null}
      <span className="exo-dc-me-names"><strong>{title}</strong>{line ? <span>{line}</span> : null}</span>
      {shown.length ? (
        <span className="exo-dc-jam-people" role="img" aria-label={`Listening: ${people.map(person => person.name).join(', ')}`}>
          {shown.map(person => <Avatar key={person.id} src={person.avatar} name={person.name} status={null} size={20} />)}
          {people.length > shown.length ? <span className="exo-dc-jam-more">+{people.length - shown.length}</span> : null}
        </span>
      ) : null}
      {children}
    </section>
  )
}

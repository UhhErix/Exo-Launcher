import { useCallback, useContext, useMemo, useRef, useState } from 'react'
import { host } from '../../../lib/host'
import { report } from '../../../lib/report'
import { useGlide } from '../../../lib/glide'
import type { DiscordDm, DiscordFriend, DiscordHome } from '../../../lib/discord'
import { useVoiceNow } from '../../../lib/discordNow'
import { GameLinkContext, playingGroups, playingName, useFriends } from '../../../lib/friendsPlaying'
import { buildParties } from '../../../lib/playRooms'
import { titleIdentity } from '../../../lib/utils'
import { ErrorNote } from '../../ui/ErrorNote'
import { GamesRoom } from './GamesRoom'
import { GameRoom } from './GameRoom'
import { PartyRoom } from './PartyRoom'
import { ClipsRoom } from './ClipsRoom'
import { PlayWait } from './parts'

const NO_DMS: DiscordDm[] = []
type Room = 'games' | 'party' | 'clips'
const ROOMS: { id: Room; label: string }[] = [{ id: 'games', label: 'Games' }, { id: 'party', label: 'Party' }, { id: 'clips', label: 'Clips' }]

/**
 * Play, the Discord room's fourth place once turned on (Discord's settings › Chat): Exo's own rooms for playing with friends
 * (docs/architecture/chat-rooms.md). Games (a game's players, and your PC's Optimize and shaders for it, and its clips), Party
 * (who is ready to play one together) and Clips (your captures, shared in one press). Home, Messages and Servers are untouched.
 */
export function PlayPage({ home, callId, onOpenDm, onJoinVoice }: {
  home: DiscordHome | null
  /** The conversation or voice channel your call is in, if any. */
  callId: string | null
  onOpenDm: (channelId: string) => void
  /** Joins a voice channel where you are, without leaving the page. */
  onJoinVoice: (guildId: string, channelId: string) => void
}) {
  const [room, setRoom] = useState<Room>('games')
  const [gameId, setGameId] = useState<string | null>(null)
  const [partyKey, setPartyKey] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const tabs = useGlide<HTMLSpanElement>()
  const link = useContext(GameLinkContext)
  const games = link?.games
  const { friends, loaded } = useFriends()
  const rooms = useVoiceNow()
  const groups = useMemo(() => playingGroups(friends, games ?? []), [friends, games])
  const parties = useMemo(() => buildParties({ friends, groups, rooms, games: games ?? [], identity: titleIdentity, playingNow: playingName }), [friends, groups, rooms, games])

  const opening = useRef(onOpenDm)
  opening.current = onOpenDm
  const message = useCallback((friend: DiscordFriend) => {
    setError(null)
    if (friend.dmId) { opening.current(friend.dmId); return }
    host.discordFriend('dm', { userId: friend.id }).then(result => {
      if (result.ok && result.channelId) opening.current(result.channelId)
      else setError(result.message ?? 'Discord did not take that.')
    }, (err: unknown) => { report(err, 'PlayPage.dm'); setError('Discord did not take that.') })
  }, [])

  const ready = !!link && loaded
  const open = gameId ? games?.find(game => game.id === gameId) ?? null : null
  const goParty = (key: string | null) => { setPartyKey(key); setGameId(null); setRoom('party') }
  return (
    <section className="exo-dc-chat exo-dc-friends exo-dc-play" aria-label="Play">
      <header className="exo-dc-chat-head">
        <span ref={tabs} className="exo-dc-tabs exo-glide" role="group" aria-label="Play">
          {ROOMS.map(({ id, label }) => (
            <button key={id} type="button" className="exo-dc-tab" aria-pressed={room === id} onClick={() => { setRoom(id); setGameId(null) }}>{label}</button>
          ))}
        </span>
      </header>
      <div className="exo-dc-friends-body">
        <div className="exo-dc-lobby-scroll exo-play-scroll">
          {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
          {!ready ? <PlayWait room={room} /> : room === 'games' ? (
            open ? <GameRoom key={open.id} game={open} party={parties.find(party => party.game?.id === open.id) ?? null} dms={home?.dms ?? NO_DMS} onBack={() => setGameId(null)} onParty={goParty} onMessage={message} />
              : <GamesRoom games={games ?? []} parties={parties} onOpen={game => setGameId(game.id)} />
          ) : room === 'party' ? (
            <PartyRoom parties={parties} chosen={partyKey} onChoose={setPartyKey} me={home?.me ?? null} callId={callId} onJoinVoice={onJoinVoice} onMessage={message} onGames={() => setRoom('games')} />
          ) : <ClipsRoom games={games ?? []} parties={parties} home={home} />}
        </div>
      </div>
    </section>
  )
}

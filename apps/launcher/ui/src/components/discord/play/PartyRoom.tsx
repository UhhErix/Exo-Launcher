import { useContext, useEffect, useState } from 'react'
import type { DiscordFriend, DiscordHome } from '../../../lib/discord'
import { GameLinkContext } from '../../../lib/friendsPlaying'
import { host, shelfAction } from '../../../lib/host'
import { usePageClock } from '../../../lib/pageActivity'
import { launchPlan, myState, readiness, type Party } from '../../../lib/playRooms'
import { Avatar } from '../presence'
import { Icons } from '../icons'
import { PlayerRow, StateWord } from './parts'

/** How many parties get a chip: the most people first, so a long list never crowds the page. */
const MOST_PARTIES = 6

type Board = {
  party: Party
  me: DiscordHome['me']
  callId: string | null
  onJoinVoice: (guildId: string, channelId: string) => void
  onMessage: (friend: DiscordFriend) => void
}

/**
 * Party: who is ready to play one game together. The people are the friends on it (playing, or getting ready as their own Exo
 * says, when they chose to share that), you first, and the voice room most of them are in. Launch together joins that room, if
 * there is one you are not in, and starts the game through the Library's own Play or Install; it sends nobody a message.
 * Someone who didn't turn on Share when I'm getting ready shows only what Discord says: on the game, or in voice.
 */
export function PartyRoom({ parties, chosen, onChoose, onGames, ...board }: Omit<Board, 'party'> & {
  parties: Party[]
  chosen: string | null
  onChoose: (key: string) => void
  onGames: () => void
}) {
  const party = parties.find(p => p.key === chosen) ?? parties[0] ?? null
  if (!party) return (
    <div className="exo-play-empty">
      <p className="exo-dc-lobby-none">No one is getting ready to play.</p>
      <button type="button" className="exo-set-btn is-compact" onClick={onGames}>Games</button>
    </div>
  )
  return (
    <div className="exo-play-room exo-motion-arrive">
      {parties.length > 1 ? (
        <div className="exo-shelf-chips" role="group" aria-label="Parties">
          {parties.slice(0, MOST_PARTIES).map(p => <button key={p.key} type="button" className="exo-shelf-chip" aria-pressed={p.key === party.key} onClick={() => onChoose(p.key)}>{p.title}</button>)}
        </div>
      ) : null}
      <PartyBoard key={party.key} party={party} {...board} />
    </div>
  )
}

function PartyBoard({ party, me, callId, onJoinVoice, onMessage }: Board) {
  const now = usePageClock(60_000, Date.now)
  const mine = myState(party.game)
  const { ready, total } = readiness(party.members, mine)
  const shaders = useShaderWords(party.game?.id ?? null)
  const { room } = party
  return (
    <>
      <header className="exo-play-head">
        <span className="exo-play-head-title">
          <h2>{party.title}</h2>
          <p>{ready} of {total} ready</p>
        </span>
        <Launch party={party} callId={callId} onJoinVoice={onJoinVoice} />
      </header>
      <ul className="exo-play-people" aria-label={`${party.title} party`}>
        <li className="exo-play-person" data-state={mine} data-me="">
          <span className="exo-play-person-main">
            <Avatar src={me?.avatar} name={me?.name ?? 'You'} status={null} size={36} />
            <span className="exo-play-person-names"><strong>You</strong>{mine === 'ready' && shaders ? <span>{shaders}</span> : null}</span>
          </span>
          <span className="exo-play-state"><StateWord state={mine} percent={null} /></span>
        </li>
        {party.members.map(member => <PlayerRow key={member.id} member={member} now={now} onMessage={onMessage} />)}
      </ul>
      {room ? (
        <p className="exo-play-voice">
          <Icons.Voice size={16} />
          <span>{room.name} in {room.guild}</span>
          <span className="exo-play-voice-count">{room.count} in voice</span>
        </p>
      ) : null}
    </>
  )
}

// Join voice alone, and the one press that does both: the voice room, then the game through the Library's own Play or Install.
function Launch({ party, callId, onJoinVoice }: Pick<Board, 'callId' | 'onJoinVoice'> & { party: Party }) {
  const link = useContext(GameLinkContext)
  const { game } = party
  const { joinVoice } = launchPlan({ party, inVoiceChannelId: callId })
  const action = game ? shelfAction(game) : null
  const starting = !!game && game.id === link?.startingId
  const launch = () => {
    if (joinVoice) onJoinVoice(joinVoice.guildId, joinVoice.channelId)
    if (game && link) link.onPlay(game)
  }
  return (
    <span className="exo-play-head-end">
      {joinVoice ? <button type="button" className="exo-set-btn" onClick={() => onJoinVoice(joinVoice.guildId, joinVoice.channelId)}>Join voice</button> : null}
      {game && action && link ? (
        <button type="button" className="exo-set-btn is-primary" disabled={starting} onClick={launch}>
          {starting ? 'Starting' : action.action === 'play' ? 'Launch together' : action.label}
        </button>
      ) : null}
    </span>
  )
}

/** Your own shaders for the party's game, in a few words, once the engine has answered; nothing while it hasn't or has nothing to say. */
function useShaderWords(gameId: string | null): string | null {
  const [read, setRead] = useState<{ id: string; words: string | null } | null>(null)
  useEffect(() => {
    if (!gameId) return
    let live = true
    host.shadersStatus(gameId).then(next => {
      if (!live || !next.ok) return
      const words = next.state === 'warmed' ? 'Shaders compiled' : next.state === 'ready' || next.state === 'record' || next.state === 'stale' ? 'Shaders not compiled' : null
      setRead({ id: gameId, words })
    }, () => { /* the line is a convenience; the board stands without it */ })
    return () => { live = false }
  }, [gameId])
  return gameId && read?.id === gameId ? read.words : null
}

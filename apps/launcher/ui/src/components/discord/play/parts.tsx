import { memo } from 'react'
import { sinceWords } from '../../../lib/discordNow'
import { stateWords, type Member, type PartyState } from '../../../lib/playRooms'
import { ExoMark } from '../../../brand/ExoMark'
import { Wait, sample, times } from '../../ui/Wait'
import { WorkingWord } from '../../ui/WorkingWord'
import { Avatar } from '../presence'

/** What a person is doing about the game, in a word: green for playing and for ready, the moving dots for work under way, muted for the rest. */
export function StateWord({ state, percent }: { state: PartyState; percent: number | null }) {
  if (state === 'compiling') return <WorkingWord word="Compiling shaders" />
  if (state === 'installing') return <WorkingWord word="Installing" value={percent === null ? null : percent / 100} />
  return <span className={state === 'playing' || state === 'ready' ? 'is-good' : undefined}>{stateWords(state, percent)}</span>
}

/** Their line under a name: how long they have played, and what they do in the game. */
function lineOf(member: Member, now: number) {
  if (member.state !== 'playing') return member.state === 'voice' ? 'Not on the game yet' : ''
  return [member.since ? sinceWords(member.since, now) : null, member.detail].filter(Boolean).join(' · ')
}

/** A person on a game: their picture and name (a press is their DM, right-click the person menu), what they are doing, and how ready they are. */
export const PlayerRow = memo(function PlayerRow({ member, now, onMessage }: { member: Member; now: number; onMessage: (friend: NonNullable<Member['friend']>) => void }) {
  const line = lineOf(member, now)
  const friend = member.friend
  return (
    <li className="exo-play-person" data-state={member.state}>
      <button type="button" className="exo-play-person-main" aria-label={`Message ${member.name}`} data-dc-user={member.id} data-dc-name={member.name} data-dc-username={friend?.username ?? undefined}
        onClick={() => { if (friend) onMessage(friend) }}>
        <Avatar src={member.avatar} name={member.name} status={friend?.status ?? null} size={36} />
        <span className="exo-play-person-names">
          <strong>{member.name}{member.state === 'playing' && member.throughExo ? <span className="exo-dc-home-through" role="img" aria-label="Through Exo"><ExoMark size={12} /></span> : null}</strong>
          {line ? <span>{line}</span> : null}
        </span>
      </button>
      <span className="exo-play-state"><StateWord state={member.state} percent={member.percent} /></span>
    </li>
  )
})

/** A few faces, overlapped, for a game's card; the rest are counted. */
export function FaceStack({ people, max = 3 }: { people: readonly { id: string; name: string; avatar: string | null }[]; max?: number }) {
  const shown = people.slice(0, max)
  return (
    <span className="exo-play-faces" aria-hidden="true">
      {shown.map(person => <Avatar key={person.id} src={person.avatar} name={person.name} status={null} size={24} />)}
      {people.length > shown.length ? <span className="exo-play-faces-rest">+{people.length - shown.length}</span> : null}
    </span>
  )
}

/** A room on its way, in its own markup (DESIGN.md › Waits): game cards, a party's people, or clip tiles. */
export function PlayWait({ room }: { room: 'games' | 'party' | 'clips' }) {
  const label = room === 'games' ? 'Loading games' : room === 'party' ? 'Loading the party' : 'Loading clips'
  return (
    <Wait label={label}>
      {room === 'party' ? (
        <div className="exo-play-room">
          <header className="exo-play-head"><h2>{sample(0)}</h2></header>
          <ul className="exo-play-people">
            {times(4, i => (
              <li className="exo-play-person">
                <span className="exo-play-person-main">
                  <span className="exo-dc-avatar-wrap" style={{ width: 36, height: 36, borderRadius: '50%' }} skullet-block="" />
                  <span className="exo-play-person-names"><strong>{sample(i)}</strong><span>{sample(i + 2)}</span></span>
                </span>
                <span className="exo-play-state"><span>Ready</span></span>
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <div className={room === 'games' ? 'exo-play-grid' : 'exo-play-grid is-clips'}>
          {times(room === 'games' ? 8 : 6, i => (
            <div className="exo-play-card">
              <span className="exo-play-art" skullet-block="" />
              <span className="exo-play-card-text"><strong>{sample(i)}</strong><span>{sample(i + 2)}</span></span>
            </div>
          ))}
        </div>
      )}
    </Wait>
  )
}

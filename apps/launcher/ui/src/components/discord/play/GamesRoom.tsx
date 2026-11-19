import { memo, useMemo } from 'react'
import type { Game } from '../../../lib/host'
import { orderGames, type Party } from '../../../lib/playRooms'
import { formatPlayed } from '../../../lib/utils'
import { HeroWash } from '../../CoverArt'
import { FaceStack } from './parts'

/** The drawn width the card's art asks for: about 220 wide, and a narrower 16:9 one is under what counts as wide art (coverFit). */
const ART_THUMB = 280

/** What a game's card says under its name: who is on it, else how long you have played it, else that you don't have it. */
export function cardLine(game: Game, party: Party | null): string {
  const playing = party?.members.filter(m => m.state === 'playing').length ?? 0
  const getting = (party?.members.length ?? 0) - playing
  if (playing || getting) return [playing ? `${playing} playing` : null, getting ? `${getting} getting ready` : null].filter(Boolean).join(' · ')
  if (!game.installed) return 'Not installed'
  const played = formatPlayed(game.playtimeMinutes)
  return played === '—' ? '' : `${played} played`
}

/**
 * Games: your Library as rooms, the games friends are on first. A card opens that game's room. Nothing counts unread; a live
 * dot and the faces are all that moves a game up the page.
 */
export function GamesRoom({ games, parties, onOpen }: { games: Game[]; parties: Party[]; onOpen: (game: Game) => void }) {
  const byGame = useMemo(() => new Map(parties.flatMap(party => party.game ? [[party.game.id, party] as const] : [])), [parties])
  const ordered = useMemo(() => orderGames(games, new Map([...byGame].map(([id, party]) => [id, party.members.length]))), [games, byGame])
  if (!ordered.length) return <p className="exo-dc-lobby-none">No games in your Library.</p>
  return (
    <div className="exo-play-grid exo-motion-arrive" role="list" aria-label="Games">
      {ordered.map(game => <GameCard key={game.id} game={game} party={byGame.get(game.id) ?? null} onOpen={onOpen} />)}
    </div>
  )
}

const GameCard = memo(function GameCard({ game, party, onOpen }: { game: Game; party: Party | null; onOpen: (game: Game) => void }) {
  const line = cardLine(game, party)
  const live = !!party?.members.some(m => m.state === 'playing')
  return (
    <div role="listitem" className="exo-play-cell">
      <button type="button" className="exo-play-card" aria-label={[game.title, line].filter(Boolean).join(', ')} onClick={() => onOpen(game)}>
        <span className="exo-play-art">
          <HeroWash game={game} className="exo-play-art-img" thumb={ART_THUMB} />
          {party ? <FaceStack people={party.members} /> : null}
        </span>
        <span className="exo-play-card-text">
          <strong>{game.title}</strong>
          <span className="exo-play-card-line">{live ? <i className="exo-play-live" aria-hidden="true" /> : null}{line}</span>
        </span>
      </button>
    </div>
  )
})

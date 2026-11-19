import { useContext, useMemo } from 'react'
import type { DiscordDm, DiscordFriend } from '../../../lib/discord'
import { GameLinkContext } from '../../../lib/friendsPlaying'
import { shelfAction, type Game } from '../../../lib/host'
import { usePageClock } from '../../../lib/pageActivity'
import { shareTargets, type Party } from '../../../lib/playRooms'
import { formatPlayed, storeLabel } from '../../../lib/utils'
import { ChevronLeft } from '../../../brand/icons'
import { HeroWash } from '../../CoverArt'
import { OptimizeLine } from '../../game/OptimizeLine'
import { ShaderPrecompile } from '../../game/ShaderPrecompile'
import { gameSelectionKey, useAchievements } from '../../game/useGameDetails'
import { ErrorNote } from '../../ui/ErrorNote'
import { ClipTile } from './ClipTile'
import { PlayerRow } from './parts'
import { useClips } from './useClips'

/** The wide art the room's top asks for. */
const HERO_THUMB = 640
/** How many of its clips a game's room shows: the rest are in Clips. */
const MOST_CLIPS = 4

/** The facts you come back for, each a value with its name under it (the game page's own), and none that Exo doesn't know. */
function Facts({ game, achievements }: { game: Game; achievements: { unlocked: number; total: number } | null }) {
  const played = formatPlayed(game.playtimeMinutes)
  const facts = [
    played === '—' ? null : { label: 'Played', value: played },
    achievements ? { label: 'Achievements', value: `${achievements.unlocked} of ${achievements.total}` } : null,
    { label: 'Store', value: storeLabel(game.store) },
  ].filter((fact): fact is { label: string; value: string } => !!fact)
  return (
    <dl className="exo-game-facts">
      {facts.map(fact => <div key={fact.label} className="exo-game-fact"><dd className="tabular-nums">{fact.value}</dd><dt>{fact.label}</dt></div>)}
    </dl>
  )
}

/**
 * A game's room: its players (friends on it now, and how ready each is), what your own PC has done for it (Optimize and
 * shaders, the game page's own lines, read and pressed here) and its latest clips. Play or Install is the Library's own press.
 */
export function GameRoom({ game, party, dms, onBack, onParty, onMessage }: {
  game: Game
  party: Party | null
  dms: DiscordDm[]
  onBack: () => void
  onParty: (key: string) => void
  onMessage: (friend: DiscordFriend) => void
}) {
  const link = useContext(GameLinkContext)
  const now = usePageClock(60_000, Date.now)
  const { achievements } = useAchievements(game.id, gameSelectionKey(game), game.installed)
  const clips = useClips(game.id, MOST_CLIPS)
  const action = shelfAction(game)
  const starting = game.id === link?.startingId
  const targets = useMemo(() => shareTargets({ gameId: game.id, parties: party ? [party] : [], dms }), [game.id, party, dms])
  return (
    <div className="exo-play-room exo-play-game exo-motion-arrive" onKeyDown={event => { if (event.key === 'Escape') onBack() }}>
      <header className="exo-play-head">
        <button type="button" className="exo-set-btn is-compact" onClick={onBack} aria-label="Back to games"><ChevronLeft size={16} />Games</button>
        <span className="exo-play-head-title"><h2>{game.title}</h2></span>
        <span className="exo-play-head-end">
          {action && link ? <button type="button" className="exo-set-btn is-primary" disabled={starting} onClick={() => link.onPlay(game)}>{starting ? 'Starting' : action.label}</button> : null}
        </span>
      </header>
      <div className="exo-play-columns">
        <section className="exo-play-side" aria-label="Your PC">
          <span className="exo-play-hero"><HeroWash game={game} className="exo-play-art-img" thumb={HERO_THUMB} /></span>
          <Facts game={game} achievements={achievements} />
          {game.installed ? (
            <div className="exo-game-lines">
              <OptimizeLine gameId={game.id} />
              <ShaderPrecompile gameId={game.id} onOpenShaders={() => window.dispatchEvent(new CustomEvent('exo-open-pc', { detail: 'shaders' }))} />
            </div>
          ) : null}
        </section>
        <div className="exo-play-main">
          <section aria-label="Players">
            <header className="exo-play-sub">
              <h3>Players</h3>
              {party ? <button type="button" className="exo-set-btn is-compact" onClick={() => onParty(party.key)}>Party</button> : null}
            </header>
            {party ? (
              <ul className="exo-play-people">
                {party.members.map(member => <PlayerRow key={member.id} member={member} now={now} onMessage={onMessage} />)}
              </ul>
            ) : <p className="exo-play-none">No one is on {game.title}.</p>}
          </section>
          {clips.failed ? (
            <section aria-label="Clips">
              <header className="exo-play-sub"><h3>Clips</h3></header>
              <ErrorNote message="Your clips couldn't be read." />
              <button type="button" className="exo-set-btn is-compact" onClick={clips.reload}>Try again</button>
            </section>
          ) : clips.items?.length ? (
            <section aria-label="Clips">
              <header className="exo-play-sub"><h3>Clips</h3></header>
              <ul className="exo-play-grid is-clips">
                {clips.items.map(item => <ClipTile key={item.id} item={item} targets={targets} />)}
              </ul>
            </section>
          ) : null}
        </div>
      </div>
    </div>
  )
}

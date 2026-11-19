import { memo, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import { bannerThumbWidth } from '../../lib/coverThumb'
import { shelfAction, type Game, type InstallProgress } from '../../lib/host'
import { useLens } from '../../lib/glass'
import { Play } from '../../brand/icons'
import { formatPlayed, formatRelativeLastPlayed, installVerb, transferShare } from '../../lib/utils'
import { HeroWash } from '../CoverArt'
import { Progress } from '../ui/Progress'
import { WorkingWord } from '../ui/WorkingWord'
import { menuAt, type GameMenuAt } from './GameMenu'
import { artInitial } from '../ui/artInitial'

/**
 * Continue (DESIGN.md › Library): the games you are actually playing, as big
 * landscape art with Play one click away: three equal banners, or two across in a narrow window (library.css). The live state rides on the card:
 * the green glow while it runs, and while it downloads or updates, the work and how far it is ("Updating · 54%").
 */
export const ContinueRow = memo(function ContinueRow({ games, transferFor, startingId, onOpen, onPlay, onMenu }: {
  games: Game[]
  transferFor: (game: Game) => InstallProgress | null
  startingId: string | null
  onOpen: (game: Game) => void
  onPlay: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}) {
  const row = useRef<HTMLDivElement>(null)
  const thumb = useBannerThumb(row, games.length)
  if (games.length === 0) return null
  return (
    <section className="exo-continue" aria-label="Continue">
      <div ref={row} className="exo-continue-row" data-count={games.length}>
        {games.map(game => <ContinueCard key={game.id} game={game} thumb={thumb} transfer={transferFor(game)} starting={game.id === startingId} onOpen={onOpen} onPlay={onPlay} onMenu={onMenu} />)}
      </div>
    </section>
  )
})

/** The largest banner thumbnail this run has needed: it only grows, so a smaller window never swaps in softer art. */
let widestThumb: number | null | undefined

/**
 * The thumbnail width that covers the row's largest banner at device pixels (null: the original), measured before the
 * first paint and again as the row changes size; undefined until measured, so no full-size hero starts loading first.
 */
function useBannerThumb(row: RefObject<HTMLDivElement | null>, count: number): number | null | undefined {
  const [thumb, setThumb] = useState(widestThumb)
  useLayoutEffect(() => {
    const element = row.current
    if (!element) return
    const measure = () => {
      let next: number | null | undefined = widestThumb
      for (const card of element.children) {
        const box = card.getBoundingClientRect()
        if (box.width < 1 || box.height < 1 || next === null) continue
        const asked = bannerThumbWidth(box.width, box.height, window.devicePixelRatio || 1)
        if (asked === null || next === undefined || asked > next) next = asked
      }
      if (next === undefined) return
      widestThumb = next
      setThumb(next)
    }
    measure()
    const watch = new ResizeObserver(measure)
    watch.observe(element)
    return () => watch.disconnect()
  }, [row, count])
  return thumb
}

function ContinueCard({ game, thumb, transfer, starting, onOpen, onPlay, onMenu }: {
  game: Game
  thumb: number | null | undefined
  transfer: InstallProgress | null
  starting: boolean
  onOpen: (game: Game) => void
  onPlay: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}) {
  const working = !!transfer?.isActive
  const plate = useRef<HTMLDivElement>(null)
  // The art is shown whole; the title reads off it on dark glass, over any art.
  useLens(plate)
  const shortcut = shelfAction(game)
  return (
    <article className={`exo-continue-card${game.isRunning ? ' is-playing' : ''}`}>
      <button type="button" className="exo-continue-open" data-vt-art aria-label={`Open ${game.title}`} onClick={() => onOpen(game)}
        onContextMenu={(event) => { event.preventDefault(); onMenu(menuAt(game, event)) }}>
        <span className="exo-continue-scrim"><span className="exo-art-initial" aria-hidden="true">{artInitial(game.title)}</span>{thumb === undefined ? null : <HeroWash game={game} className="exo-continue-art" poster thumb={thumb} />}</span>
      </button>
      <div className="exo-continue-info">
        <div ref={plate} className="exo-glass exo-continue-text">
          <strong>{game.title}</strong>
          {working ? <span><WorkingWord word={installVerb(transfer, game.installed ? 'update' : 'download')} value={transferShare(transfer)} /></span>
            : game.isRunning ? <span className="is-good">Playing</span>
            : <ContinueFacts game={game} />}
        </div>
        {!working && shortcut ? (
          <button type="button" className="exo-continue-play" disabled={starting} aria-label={`${starting ? 'Starting' : shortcut.label} ${game.title}`} onClick={() => onPlay(game)}>
            {starting ? <WorkingWord word="Preparing" /> : <><Play size={14} />{shortcut.label}</>}
          </button>
        ) : null}
      </div>
      {working ? <Progress edge size="small" value={transferShare(transfer)} label={`${installVerb(transfer, game.installed ? 'update' : 'download')} ${game.title}`} /> : null}
    </article>
  )
}

/** Why this game is here: its hours and when you last played, and an update when one waits. */
function ContinueFacts({ game }: { game: Game }) {
  const facts = [
    game.playtimeMinutes ? formatPlayed(game.playtimeMinutes) : null,
    game.lastPlayedUtc ? formatRelativeLastPlayed(game.lastPlayedUtc) : null,
    game.updateAvailable ? 'Update ready' : null,
  ].filter(Boolean)
  return facts.length ? <span className="tabular-nums">{facts.join(' · ')}</span> : null
}

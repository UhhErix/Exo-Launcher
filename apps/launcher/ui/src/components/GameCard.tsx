import { isPreloaded, resolvePrimaryAction, type Game, type InstallProgress } from '../lib/host'
import { allowHover } from '../lib/hoverGate'
import { cn, formatPlayed, formatSize, transferShare } from '../lib/utils'
import { useJustFinished } from '../lib/useJustFinished'
import { CoverArt } from './CoverArt'
import { Progress } from './ui/Progress'
import { WorkingWord } from './ui/WorkingWord'
import { memo, useState, useSyncExternalStore, type CSSProperties, type MouseEvent } from 'react'
import { menuAt, type GameMenuAt } from './shell/GameMenu'
import { CHECK_PATH, Check } from '../brand/icons'

type Transfer = Partial<InstallProgress> | null

type CardState = {
  installed: boolean
  /** Downloaded for its release day: on this PC, with its own badge. */
  preloaded: boolean
  /** Not on this PC: neither installed nor preloaded. */
  dim: boolean
  hasUpdate: boolean
  isPlaying: boolean
  transferring: boolean
  phase: string
  /** How far the transfer is, 0 to 1, or null while it isn't measured. */
  share: number | null
}

function cardState(game: Game, transfer: Transfer): CardState {
  const variants = game.variants ?? []
  return {
    installed: !!game.installed,
    preloaded: isPreloaded(game),
    dim: !game.installed && !isPreloaded(game),
    hasUpdate: resolvePrimaryAction(game) === 'update' || !!game.updateAvailable || variants.some((v) => v.updateAvailable),
    isPlaying: !!(game.canStop || game.isRunning || variants.some((v) => v.canStop || v.isRunning)),
    transferring: !!transfer,
    phase: transfer?.phase === "installing" ? "Installing" : transfer?.phase === "finalizing" ? "Finalizing" : game.installed ? "Updating" : "Downloading",
    share: transferShare(transfer),
  }
}

/**
 * One badge at most. Playing beats Downloading beats Update beats Queued beats Preloaded. A download's word says how far
 * it is, live ("Downloading · 54%"), or has moving dots while that isn't measured.
 */
function badgeFor(state: CardState, queued: boolean): { text: string; tone: string; working?: boolean } | null {
  if (state.isPlaying) return { text: 'Playing', tone: 'is-good' }
  if (state.transferring) return { text: state.phase, tone: 'is-update', working: true }
  if (state.hasUpdate && state.installed) return { text: 'Update', tone: 'is-update' }
  if (queued) return { text: 'Queued', tone: '' }
  if (state.preloaded) return { text: 'Preloaded', tone: '' }
  return null
}

/** The badge's word, in the badge's order, so the name says what the card shows. */
function ariaLabelFor(title: string, state: CardState, queued: boolean): string {
  if (state.isPlaying) return `${title} (playing)`
  if (state.transferring) return `${title} (${state.phase.toLowerCase()})`
  if (state.hasUpdate && state.installed) return `${title} (update)`
  if (queued) return `${title} (queued)`
  if (state.preloaded) return `${title} (preloaded)`
  return title
}


// Library › Refresh (LibraryPane › useRefresh): every cover shines where it is for the whole refresh, as the Store's
// tiles do (DESIGN.md › Waits); the art that follows swaps in quietly as each lands.
let coversShining = false
/** The one card naming itself: the pointer on one card and focus on another still draw one plate, the latest's. */
let plated: ((on: boolean) => void) | null = null
const plateOn = (point: (on: boolean) => void) => {
  if (plated !== point) plated?.(false)
  plated = point
  point(true)
}

const shineListeners = new Set<() => void>()
export function setCoversShining(on: boolean) {
  if (coversShining === on) return
  coversShining = on
  for (const listener of shineListeners) listener()
}
const watchShine = (listener: () => void) => { shineListeners.add(listener); return () => { shineListeners.delete(listener) } }
const shineNow = () => coversShining

export const GameCard = memo(function GameCard({
  game,
  selected,
  onSelect,
  preload = false,
  transfer = null,
  queued = false,
  tabIndex,
  onActiveGameChange,
  position,
  setSize,
  arriveIndex,
  onMenu,
  picked,
}: {
  game: Game
  selected: boolean
  /** A press: Select mode passes the click on, for Shift's range. */
  onSelect: (game: Game, event?: MouseEvent) => void
  /** Right-click: the card's menu (Play, Open folder). Without it, right-click opens the sheet. */
  onMenu?: (at: GameMenuAt) => void
  preload?: boolean
  transfer?: Transfer
  queued?: boolean
  tabIndex?: number
  onActiveGameChange?: (gameId: string) => void
  /** 1-based place in a windowed list, so assistive tech counts the whole shelf. */
  position?: number
  setSize?: number
  /** Set only while the grid first shows: this card's place in the arrival. */
  arriveIndex?: number
  /** Select mode: whether this card is picked (a press toggles it); null outside it. */
  picked?: boolean | null
}) {
  const state = cardState(game, transfer)
  const badge = badgeFor(state, queued)
  // Done (DESIGN.md › Motion): a download or update that lands on an installed, current game.
  const finished = useJustFinished(state.transferring, state.installed && !state.hasUpdate)
  const shining = useSyncExternalStore(watchShine, shineNow)
  // A card under the pointer or focus names itself (TilePlate), and a dim one shows its plain art (CoverArt's undim). React's
  // own handlers, one listener at its root for every card, not four on each.
  const [pointed, point] = useState(false)

  return (
    <article
      role={position != null ? 'listitem' : undefined}
      aria-posinset={position}
      aria-setsize={setSize}
      className={cn(
        'exo-tile group relative w-full',
        arriveIndex != null && 'exo-motion-arrive',
        selected && 'is-selected',
        picked && 'is-picked',
        state.dim && 'is-dim',
        state.hasUpdate && !state.isPlaying && 'is-update',
        state.isPlaying && 'is-playing',
      )}
      style={arriveIndex != null ? { '--i': arriveIndex } as CSSProperties : undefined}
    >
      <button
        type="button"
        data-game-id={game.id}
        tabIndex={tabIndex}
        onFocus={() => { onActiveGameChange?.(game.id); plateOn(point) }}
        onBlur={() => point(false)}
        onPointerEnter={event => { if (allowHover(event)) plateOn(point) }}
        onPointerLeave={() => point(false)}
        onClick={(event) => {
          if (event.detail > 1 && picked == null) return
          onSelect(game, event)
        }}
        onContextMenu={(event) => {
          event.preventDefault()
          if (game.isAddPortable || game.id === 'local:add') return
          if (onMenu) onMenu(menuAt(game, event)); else onSelect(game)
        }}
        className="exo-tile-hit"
        aria-label={ariaLabelFor(game.title, state, queued)}
        {...pickAria(picked)}
      >
        <div className="exo-tile-frame" data-vt-art>
          <span className={cn('exo-tile-media', shining && 'exo-shine')} aria-hidden="true">
            <CoverArt game={game} preload={preload} dim={state.dim} undim={pointed} className="absolute inset-0 h-full w-full" />
          </span>
          <TileDone finished={finished} />
          {badge && (
            <div className="absolute left-2 top-2 z-[5]">
              <span className={cn('exo-badge', badge.tone)}>{badge.working ? <WorkingWord word={badge.text} value={state.share} /> : badge.text}</span>
            </div>
          )}
          {state.transferring ? <Progress edge size="small" value={state.share} label={`${state.phase} ${game.title}`} /> : null}
          {pointed && !state.transferring ? <TilePlate game={game} /> : null}
          <TileCheck picked={picked} />
        </div>
      </button>
    </article>
  )
})

/**
 * The cover's words, there only while it's pointed at or focused, so one small glass plate is ever drawn: the name, then
 * its hours and storage, which the Game already carries.
 */
function TilePlate({ game }: { game: Game }) {
  const shared = game.familyShared ? (game.sharedBy ? `Shared by ${game.sharedBy}` : 'Steam Family') : null
  const facts = [shared, formatPlayed(game.playtimeMinutes), formatSize(game.sizeBytes)].filter((fact): fact is string => !!fact && fact !== '—')
  return (
    <span className="exo-tile-plate" aria-hidden="true">
      <strong>{game.title}</strong>
      {facts.length ? <span className="tabular-nums">{facts.join(' · ')}</span> : null}
    </span>
  )
}

/** Opens the game sheet; in Select mode it picks the game instead, a toggle. */
const pickAria = (picked: boolean | null | undefined) => picked == null ? { 'aria-haspopup': 'dialog' as const } : { 'aria-pressed': picked }

/** Select mode's ring on the cover, checked once picked. */
function TileCheck({ picked }: { picked: boolean | null | undefined }) {
  if (picked == null) return null
  return <span className="exo-tile-check" aria-hidden="true">{picked && <Check size={14} />}</span>
}

/** Done's own layer (the frame's ::after is its hover and selection ring): the check when a download lands. */
function TileDone({ finished }: { finished: boolean }) {
  if (!finished) return null
  return (
    <span className="exo-tile-motion exo-motion-done" aria-hidden="true">
      <svg className="exo-motion-check" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>
    </span>
  )
}

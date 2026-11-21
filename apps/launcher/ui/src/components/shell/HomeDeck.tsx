import { memo, useCallback, useEffect, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, More, Play } from '../../brand/icons'
import { bannerThumbWidth } from '../../lib/coverThumb'
import { resolvePrimaryAction, shelfAction, type Game, type InstallProgress } from '../../lib/host'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { formatPlayed, formatRelativeLastPlayed, formatSize, installVerb, storeLabel, transferShare } from '../../lib/utils'
import { CoverArt, HeroWash } from '../CoverArt'
import { Progress } from '../ui/Progress'
import { WorkingWord } from '../ui/WorkingWord'
import { artInitial } from '../ui/artInitial'
import { menuAt, type GameMenuAt } from './GameMenu'

export type HomeDeckProps = {
  games: Game[]
  active: boolean
  blocked: boolean
  transferFor: (game: Game) => InstallProgress | null
  startingId: string | null
  onOpen: (game: Game) => void
  onPlay: (game: Game) => void
  onProperties: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}

function primaryVerb(game: Game): string {
  if (game.isRunning) return 'Resume'
  const shortcut = shelfAction(game)
  if (shortcut) return shortcut.label
  const action = resolvePrimaryAction(game)
  if (action === 'update') return 'Update'
  if (action === 'install') return 'Install'
  return 'Play'
}

function gameStatusTone(game: Game, working: boolean): { text: string; tone: 'good' | 'warn' | 'muted' } {
  if (working) return { text: 'Downloading', tone: 'warn' }
  if (game.isRunning) return { text: 'Playing now', tone: 'good' }
  if (game.updateAvailable) return { text: 'Update ready', tone: 'warn' }
  if (game.installed) return { text: 'Ready to play', tone: 'good' }
  return { text: 'Not installed', tone: 'muted' }
}

function DeckBadges({ game, transfer, working }: { game: Game; transfer: InstallProgress | null; working: boolean }) {
  return (
    <span className="exo-deck-badges">
      {game.isRunning ? <span className="exo-deck-pill is-good">Playing</span> : null}
      {!game.isRunning && game.updateAvailable ? <span className="exo-deck-pill is-warn">Update</span> : null}
      {working && transfer ? (
        <span className="exo-deck-pill is-warn">
          <WorkingWord word={installVerb(transfer, game.installed ? 'update' : 'download')} value={transferShare(transfer)} />
        </span>
      ) : null}
    </span>
  )
}

function DeckCard({
  game,
  isActive,
  transfer,
  onFocus,
  onOpen,
  onMenu,
}: {
  game: Game
  isActive: boolean
  transfer: InstallProgress | null
  onFocus: () => void
  onOpen: () => void
  onMenu: (at: GameMenuAt) => void
}) {
  const working = !!transfer?.isActive
  const played = formatPlayed(game.playtimeMinutes)
  const last = formatRelativeLastPlayed(game.lastPlayedUtc)
  const sub = [last !== '—' ? last : null, played !== '—' ? played : null].filter(Boolean).join(' · ')
  const deckThumb = typeof window === 'undefined' ? null : bannerThumbWidth(540, 304, window.devicePixelRatio || 1)
  return (
    <article
      className={`exo-deck-card${isActive ? ' is-active' : ''}${game.isRunning ? ' is-playing' : ''}`}
      data-deck-id={game.id}
      onMouseEnter={onFocus}
    >
      <button
        type="button"
        className="exo-deck-card-btn"
        data-vt-art
        aria-pressed={isActive}
        aria-label={isActive ? `Open ${game.title}` : `Select ${game.title}`}
        onClick={() => (isActive ? onOpen() : onFocus())}
        onContextMenu={(event) => {
          event.preventDefault()
          onMenu(menuAt(game, event))
        }}
      >
        <span className="exo-deck-media">
          <span className="exo-art-initial" aria-hidden="true">{artInitial(game.title)}</span>
          {isActive ? (
            <HeroWash game={game} className="exo-deck-art" poster thumb={deckThumb} />
          ) : (
            <CoverArt game={game} preload className="exo-deck-poster" />
          )}
        </span>
        <span className="exo-deck-scrim" aria-hidden="true" />
        <span className="exo-deck-top">
          <DeckBadges game={game} transfer={transfer} working={working} />
          {isActive && !game.isAddPortable ? (
            <span className="exo-glass exo-deck-store">{storeLabel(game.store)}</span>
          ) : null}
        </span>
        <span className="exo-deck-caption">
          {isActive && sub ? <span className="exo-deck-sub tabular-nums">{sub}</span> : null}
          <strong className="exo-deck-name">{game.title}</strong>
        </span>
      </button>
      {working ? (
        <Progress edge size="small" value={transferShare(transfer)} label={`${installVerb(transfer, game.installed ? 'update' : 'download')} ${game.title}`} />
      ) : null}
    </article>
  )
}

function DeckFooter({
  game,
  transfer,
  starting,
  onPlay,
  onOpen,
  onProperties,
  onMenu,
}: {
  game: Game
  transfer: InstallProgress | null
  starting: boolean
  onPlay: () => void
  onOpen: () => void
  onProperties: () => void
  onMenu: (at: GameMenuAt) => void
}) {
  const working = !!transfer?.isActive
  const status = gameStatusTone(game, working)
  const played = formatPlayed(game.playtimeMinutes)
  const size = formatSize(game.sizeBytes)
  const last = formatRelativeLastPlayed(game.lastPlayedUtc)
  const meta = [
    storeLabel(game.store),
    played !== '—' ? `${played} played` : null,
    size !== '—' ? size : null,
    last !== '—' ? last : null,
  ].filter(Boolean)
  const verb = primaryVerb(game)

  return (
    <footer className="exo-home-foot">
      <div className="exo-home-foot-copy">
        <div className="exo-home-meta tabular-nums">
          <span className={`exo-home-status is-${status.tone}`}>{status.text}</span>
          {meta.map((item) => (
            <span key={item} className="exo-home-meta-item">{item}</span>
          ))}
        </div>
        <h2 className="exo-home-title">{game.title}</h2>
      </div>
      <div className="exo-home-actions">
        <button
          type="button"
          className="exo-play exo-home-play"
          disabled={starting || working}
          onClick={onPlay}
        >
          {working ? (
            <WorkingWord word={installVerb(transfer, game.installed ? 'update' : 'download')} value={transferShare(transfer)} />
          ) : starting ? (
            <WorkingWord word="Preparing" />
          ) : (
            <>
              <Play size={14} />
              {verb}
            </>
          )}
        </button>
        <button type="button" className="exo-glass exo-home-room-btn" onClick={onOpen}>
          <span>Game Room</span>
          <ChevronRight size={15} />
        </button>
        <button
          type="button"
          className="exo-glass exo-home-more-btn"
          aria-label={`Options for ${game.title}`}
          onClick={(event) => {
            if (game.installed) onProperties()
            else onMenu(menuAt(game, event))
          }}
        >
          <More size={16} />
        </button>
      </div>
    </footer>
  )
}

export const HomeDeck = memo(function HomeDeck({
  games,
  active,
  blocked,
  transferFor,
  startingId,
  onOpen,
  onPlay,
  onProperties,
  onMenu,
}: HomeDeckProps) {
  const [focusedId, setFocusedId] = useState<string | null>(() => games[0]?.id ?? null)
  const trackRef = useRef<HTMLDivElement>(null)
  const activeIdx = Math.max(0, games.findIndex((item) => item.id === focusedId))
  const focused = games[activeIdx] ?? games[0] ?? null

  const step = useCallback((delta: number) => {
    if (games.length < 2) return
    const next = games[(activeIdx + delta + games.length) % games.length]
    setFocusedId(next.id)
    requestAnimationFrame(() => {
      const card = trackRef.current?.querySelector<HTMLElement>(`[data-deck-id="${CSS.escape(next.id)}"]`)
      card?.scrollIntoView({ behavior: shouldReduceMotion() ? 'instant' : 'smooth', inline: 'center', block: 'nearest' })
    })
  }, [activeIdx, games])

  useEffect(() => {
    if (!active || blocked || games.length === 0) return
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return
      const target = event.target instanceof HTMLElement ? event.target : null
      if (target?.closest('input, textarea, select, [contenteditable="true"], [role="menu"], [role="dialog"], [popover]:popover-open')) return
      if (event.key === 'ArrowRight') {
        event.preventDefault()
        step(1)
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault()
        step(-1)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [active, blocked, games.length, step])

  if (!focused) return null

  return (
    <div className="exo-home-deck">
      <div className="exo-home-backdrop" aria-hidden="true">
        <HeroWash key={focused.id} game={focused} fill />
      </div>

      <section className="exo-home-stage" aria-label="Recent games">
        <div className="exo-home-stage-head">
          <div className="exo-home-stage-label">
            <span>Recent Games</span>
            <span className="exo-home-stage-count tabular-nums">{activeIdx + 1} of {games.length}</span>
          </div>
          {games.length > 1 ? (
            <div className="exo-home-nav">
              <button type="button" className="exo-glass exo-home-step" aria-label="Previous game" onClick={() => step(-1)}>
                <ChevronLeft size={15} />
              </button>
              <button type="button" className="exo-glass exo-home-step" aria-label="Next game" onClick={() => step(1)}>
                <ChevronRight size={15} />
              </button>
            </div>
          ) : null}
        </div>

        <div ref={trackRef} className="exo-deck-track">
          {games.map((item) => (
            <DeckCard
              key={item.id}
              game={item}
              isActive={item.id === focused.id}
              transfer={transferFor(item)}
              onFocus={() => setFocusedId(item.id)}
              onOpen={() => onOpen(item)}
              onMenu={onMenu}
            />
          ))}
        </div>
      </section>

      <DeckFooter
        game={focused}
        transfer={transferFor(focused)}
        starting={focused.id === startingId}
        onPlay={() => onPlay(focused)}
        onOpen={() => onOpen(focused)}
        onProperties={() => onProperties(focused)}
        onMenu={onMenu}
      />
    </div>
  )
})

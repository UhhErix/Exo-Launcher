import { memo, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Apps, ChevronLeft, ChevronRight, Close, More, Play, Search } from '../../brand/icons'
import { host, resolvePrimaryAction, shelfAction, type Game, type InstallProgress } from '../../lib/host'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { useTrackArrows } from '../../lib/useTrackArrows'
import { installVerb, transferShare } from '../../lib/utils'
import { CoverArt, HeroWash } from '../CoverArt'
import { GameLogo } from '../GameLogo'
import { Progress } from '../ui/Progress'
import { WorkingWord } from '../ui/WorkingWord'
import { menuAt, type GameMenuAt } from './GameMenu'
import { GiftsPill } from './GiftsShelf'
import type { LibraryTab } from './LibraryHead'

export type HomeDeckProps = {
  onTab: (next: LibraryTab) => void
  query: string
  onQuery: (next: string) => void
  searchOpen: boolean
  onSearchOpenChange: (open: boolean) => void
  games: Game[]
  active: boolean
  blocked: boolean
  transferFor: (game: Game) => InstallProgress | null
  startingId: string | null
  onOpen: (game: Game) => void
  onPlay: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
  tools: ReactNode
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

function HeroLockup({
  game,
  transfer,
  starting,
  onPlay,
  onOpen,
}: {
  game: Game
  transfer: InstallProgress | null
  starting: boolean
  onPlay: () => void
  onOpen: () => void
}) {
  const working = !!transfer?.isActive
  const verb = primaryVerb(game)
  return (
    <section className="exo-home-hero" aria-label={game.title}>
      <GameLogo
        game={game}
        wrapClass="exo-home-identity"
        imgClass="exo-home-logo"
        titleClass="exo-home-title"
      />
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
        <button
          type="button"
          className="exo-glass exo-home-more-btn"
          aria-label={`Details for ${game.title}`}
          onClick={onOpen}
        >
          <More size={16} />
        </button>
      </div>
    </section>
  )
}

function PosterCard({
  game,
  isActive,
  transfer,
  onHover,
  onSelect,
  onOpen,
  onMenu,
}: {
  game: Game
  isActive: boolean
  transfer: InstallProgress | null
  onHover: () => void
  onSelect: () => void
  onOpen: () => void
  onMenu: (at: GameMenuAt) => void
}) {
  const working = !!transfer?.isActive
  return (
    <article
      className={`exo-deck-card${isActive ? ' is-active' : ''}${game.isRunning ? ' is-playing' : ''}`}
      data-deck-id={game.id}
      onMouseEnter={onHover}
    >
      <button
        type="button"
        className="exo-deck-card-btn"
        data-vt-art
        aria-pressed={isActive}
        aria-label={isActive ? `Open ${game.title}` : `Select ${game.title}`}
        onFocus={onHover}
        onClick={() => (isActive ? onOpen() : onSelect())}
        onContextMenu={(event) => {
          event.preventDefault()
          onMenu(menuAt(game, event))
        }}
      >
        <CoverArt game={game} large preload className="exo-deck-poster" />
        {game.isRunning ? <span className="exo-deck-pill is-good">Playing</span> : null}
        {!game.isRunning && game.updateAvailable ? <span className="exo-deck-pill is-warn">Update</span> : null}
      </button>
      {working ? (
        <Progress edge size="small" value={transferShare(transfer)} label={`${installVerb(transfer, game.installed ? 'update' : 'download')} ${game.title}`} />
      ) : null}
    </article>
  )
}

function InlineSearch({
  query,
  open,
  onQuery,
  onOpenChange,
}: {
  query: string
  open: boolean
  onQuery: (next: string) => void
  onOpenChange: (open: boolean) => void
}) {
  const inputRef = useRef<HTMLInputElement>(null)
  const expanded = open || query.length > 0

  useEffect(() => {
    if (open) requestAnimationFrame(() => inputRef.current?.focus())
  }, [open])

  if (!expanded) {
    return (
      <button
        type="button"
        className="exo-glass exo-home-icon-btn"
        aria-label="Search library"
        onClick={() => onOpenChange(true)}
      >
        <Search size={15} />
      </button>
    )
  }

  return (
    <label className="exo-glass exo-home-search">
      <Search size={14} className="exo-home-search-ico" aria-hidden="true" />
      <input
        ref={inputRef}
        type="search"
        value={query}
        placeholder="Filter games…"
        aria-label="Filter library"
        onChange={(event) => onQuery(event.target.value)}
        onKeyDown={(event) => {
          if (event.key !== 'Escape') return
          event.preventDefault()
          event.stopPropagation()
          onQuery('')
          onOpenChange(false)
        }}
        onBlur={() => {
          if (!query.trim()) onOpenChange(false)
        }}
      />
      <button
        type="button"
        className="exo-home-search-clear"
        aria-label="Close search"
        onMouseDown={(event) => event.preventDefault()}
        onClick={() => {
          onQuery('')
          onOpenChange(false)
        }}
      >
        <Close size={13} />
      </button>
    </label>
  )
}

export const HomeDeck = memo(function HomeDeck({
  onTab,
  query,
  onQuery,
  searchOpen,
  onSearchOpenChange,
  games,
  active,
  blocked,
  transferFor,
  startingId,
  onOpen,
  onPlay,
  onMenu,
  tools,
}: HomeDeckProps) {
  const [focusedId, setFocusedId] = useState<string | null>(() => games[0]?.id ?? null)
  const { trackRef, edges, measure, page } = useTrackArrows<HTMLDivElement>(games.length, { wheel: true })
  const activeIdx = Math.max(0, games.findIndex((item) => item.id === focusedId))
  const focused = games[activeIdx] ?? games[0] ?? null

  useEffect(() => {
    if (!active || !focused || focused.isAddPortable) return
    const timer = window.setTimeout(() => {
      void host.gameExtras(focused.id).catch(() => { })
    }, 240)
    return () => window.clearTimeout(timer)
  }, [active, focused])

  const focusAndReveal = useCallback((id: string) => {
    setFocusedId(id)
    requestAnimationFrame(() => {
      const card = trackRef.current?.querySelector<HTMLElement>(`[data-deck-id="${CSS.escape(id)}"]`)
      card?.scrollIntoView({ behavior: shouldReduceMotion() ? 'instant' : 'smooth', inline: 'nearest', block: 'nearest' })
    })
  }, [trackRef])

  const step = useCallback((delta: number) => {
    if (games.length < 2) return
    const next = games[(activeIdx + delta + games.length) % games.length]
    focusAndReveal(next.id)
  }, [activeIdx, games, focusAndReveal])

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

  const trimmedQuery = query.trim()

  return (
    <div className="exo-home-deck">
      <div className="exo-home-stage">
        <div className="exo-home-stage-art" aria-hidden="true">
          {focused ? <HeroWash key={focused.id} game={focused} fill /> : null}
        </div>
        <div className="exo-home-stage-foot">
          {focused ? (
            <HeroLockup
              game={focused}
              transfer={transferFor(focused)}
              starting={focused.id === startingId}
              onPlay={() => onPlay(focused)}
              onOpen={() => onOpen(focused)}
            />
          ) : (
            <div className="exo-home-hero" />
          )}
          <div className="exo-home-bar-tools">
            <GiftsPill />
            <InlineSearch query={query} open={searchOpen} onQuery={onQuery} onOpenChange={onSearchOpenChange} />
            <button type="button" className="exo-glass exo-home-icon-btn" aria-label="All games grid" onClick={() => onTab('all')}>
              <Apps size={15} />
            </button>
            {tools}
          </div>
        </div>
      </div>

      <div className="exo-home-lower">
        {games.length > 0 ? (
          <div
            className="exo-deck-rail"
            data-can-prev={!edges.start || undefined}
            data-can-next={!edges.end || undefined}
          >
            {!edges.start ? (
              <button type="button" className="exo-glass exo-deck-arrow is-prev" aria-label="Previous games" onClick={() => page(-1, 0.8)}>
                <ChevronLeft size={16} />
              </button>
            ) : null}
            <div ref={trackRef} className="exo-deck-track" data-h-track="" role="region" aria-label="Games" onScroll={measure}>
              {games.map((item) => (
                <PosterCard
                  key={item.id}
                  game={item}
                  isActive={focused?.id === item.id}
                  transfer={transferFor(item)}
                  onHover={() => setFocusedId(item.id)}
                  onSelect={() => focusAndReveal(item.id)}
                  onOpen={() => onOpen(item)}
                  onMenu={onMenu}
                />
              ))}
            </div>
            {!edges.end ? (
              <button type="button" className="exo-glass exo-deck-arrow is-next" aria-label="Next games" onClick={() => page(1, 0.8)}>
                <ChevronRight size={16} />
              </button>
            ) : null}
          </div>
        ) : (
          <div className="exo-home-empty">
            <p>{trimmedQuery ? `No games match “${trimmedQuery}”.` : 'No games yet.'}</p>
            {trimmedQuery ? (
              <button type="button" className="exo-set-btn" onClick={() => { onQuery(''); onSearchOpenChange(false) }}>
                Clear search
              </button>
            ) : null}
          </div>
        )}
      </div>
    </div>
  )
})

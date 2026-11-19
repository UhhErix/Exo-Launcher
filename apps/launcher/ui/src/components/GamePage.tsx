import { useEffect, useId, useState } from 'react'
import { CHECK_PATH, Info } from '../brand/icons'
import { hostedBuyUrl, resolvePrimaryAction, type Game, type GameVariant, type InstallProgress } from '../lib/host'
import { transferForGame } from '../lib/library'
import { formatPlayed, formatRelativeLastPlayed, installProgressLabel, transferPercent } from '../lib/utils'
import { CoverArt, HeroWash } from './CoverArt'
import { GameHeader, GameStats, GameStore } from './game/GameSections'
import { HdrRow } from './game/HdrRow'
import { FrameGenModRow, FrameGenRow } from './game/FrameGenRow'
import { OptimizeLine } from './game/OptimizeLine'
import { ArtworkStrip, GameActions } from './game/ManageStrip'
import { PrimaryAction } from './game/PrimaryAction'
import { ShaderPrecompile } from './game/ShaderPrecompile'
import { useGameDetails } from './game/useGameDetails'
import { useJustFinished } from '../lib/useJustFinished'
import { UpscalerFiles } from './UpscalerFiles'

function isNoiseStatus(message: string): boolean {
  return /^(canceled\.?|uninstalled\.?|removed\.?|installed\.?|up to date\.?|running|(removing|uninstalling) .+…)$/i.test(message.trim())
}

/** Which sections a card gets. A portable "Add folder" tile gets none of them. */
function pageFlags(selected: Game) {
  const portable = !!selected.isAddPortable
  const storePage = !selected.owned && !selected.installed && !portable
  const installedActions = selected.installed && !portable
  const artworkEnabled = !storePage && !portable && (!!selected.owned || selected.installed)
  return { storePage, installedActions, artworkEnabled }
}

/**
 * A just-mutated cover painted over the card until the host's own revision
 * catches up, then dropped so the library copy is the single source again.
 * Dropped during render (another source picked, or the revision caught up), so
 * no frame pairs the page with a cover that is no longer its own.
 */
function useArtworkOverride(selected: Game, selectionKey: string): [Game, (game: Game) => void] {
  const [art, setArt] = useState<{ key: string; game: Game } | null>(null)
  const live = art && art.key === selectionKey && (selected.artRevision ?? 0) < (art.game.artRevision ?? 0) ? art.game : null
  if (art && !live) setArt(null)
  const view = live
    ? { ...selected, coverUrl: live.coverUrl ?? null, coverSource: live.coverSource ?? null, artRevision: live.artRevision }
    : selected
  return [view, (game: Game) => setArt({ key: selectionKey, game })]
}

/** Installed with nothing left to update: where a finished download or update lands. */
function isCurrent(selected: Game): boolean {
  return !!selected.installed && resolvePrimaryAction(selected) !== 'update'
}

function sourceVariants(selected: Game): GameVariant[] | null {
  const sources = selected.variants ?? []
  return sources.length > 1 ? sources : null
}

export type GamePageProps = {
  selected: Game
  busy: boolean
  statusMsg: string | null
  progress: InstallProgress | null
  /** Waiting in Exo's download queue. */
  queued?: boolean
  onPrimary: () => void
  onStop: () => void
  onCancel: () => void
  onDequeue?: () => void
  /** Opens the PC room's Graphics page: every game's upscalers, or its Shaders. */
  onOpenSystem?: (page: 'graphics' | 'shaders') => void
  /** Switches the exact store entry behind a grouped library card. */
  onSelectSource?: (id: string) => void
  onStatus: (msg: string | null, sticky?: boolean) => void
  onUninstalled: () => void
  /** System > HDR > HDR for HDR games, and turning it on from the HDR row. */
  hdrForGames: boolean
  onHdrForGames: () => Promise<boolean>
  /** Properties over the page (its tool, or the card menu's Properties). */
  propertiesOpen: boolean
  onPropertiesOpen: (open: boolean) => void
}

/**
 * The game's lines, one row each, their names, readings and controls on shared edges, every row the same height: Upscalers
 * and Shaders (each opening its part of PC › Graphics), Frame generation, HDR and Optimize. The set is the same for every
 * game: a line the game doesn't support stays and says so, so the page never grows or shrinks with what a game has. Last,
 * the driver's own frame generation, on a PC whose AMD or Intel driver has it (the same for every game on that PC).
 */
function GameLines({ selected, details, onOpenSystem, hdrForGames, onHdrForGames }: {
  selected: Game
  details: ReturnType<typeof useGameDetails>
} & Pick<GamePageProps, 'onOpenSystem' | 'hdrForGames' | 'onHdrForGames'>) {
  return (
    <div className="exo-game-lines">
      {onOpenSystem && (
        <>
          <UpscalerFiles
            key={'upscalers' + details.selectionKey}
            status={details.dlss}
            scanState={!details.dlss ? 'loading' : details.dlss.ok === false ? 'failed' : 'ready'}
            onOpen={() => onOpenSystem('graphics')}
          />
          <ShaderPrecompile key={'shaders' + details.selectionKey} gameId={selected.id} onOpenShaders={() => onOpenSystem('shaders')} />
        </>
      )}
      <FrameGenModRow key={'framegenmod' + details.selectionKey} gameId={selected.id} />
      <HdrRow key={'hdr' + details.selectionKey} gameId={selected.id} hdrForGames={hdrForGames} onHdrForGames={onHdrForGames} />
      <OptimizeLine key={'optimize' + details.selectionKey} gameId={selected.id} />
      <FrameGenRow key={'framegen' + details.selectionKey} gameId={selected.id} />
    </div>
  )
}

/** Live status for screen readers while something is happening to this title. */
function LiveStatus({
  selected,
  busy,
  progress,
  statusMsg,
}: Pick<GamePageProps, 'selected' | 'busy' | 'statusMsg'> & { progress: InstallProgress | null }) {
  if (progress) {
    // Phase and quarter steps only: every byte tick would be read aloud. The
    // pill's progressbar carries the exact figure for anyone who asks.
    const percent = transferPercent(progress)
    return (
      <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
        {[
          installProgressLabel({ phase: progress.phase, status: progress.status }, 'download'),
          percent == null ? null : `${Math.floor(percent / 25) * 25}%`,
        ].filter(Boolean).join(' ')}
      </span>
    )
  }
  if (busy) {
    return (
      <span className="sr-only" role="status" aria-live="polite">
        {selected.canStop ? 'Closing' : 'Preparing'} {selected.title}
      </span>
    )
  }
  if (!statusMsg || isNoiseStatus(statusMsg)) return null
  // A pill floating at the foot of the page: it fades in over the page and
  // clears on its own, so a message never pushes the page around.
  return (
    <p key={statusMsg} className="exo-game-status exo-motion-switch" aria-live="polite">
      <span>{statusMsg}</span>
    </p>
  )
}

/**
 * Info (the pane beside the hero) stays as it was left for the rest of the session, game to game, as Photos' does.
 * Not saved: Exo opens a game on its hero.
 */
let infoWanted = false

/** The hero's one line under the title: hours and when last played, the two facts people open a game for. */
function glanceLine(selected: Game): string | null {
  const parts = [
    selected.playtimeMinutes && selected.playtimeMinutes > 0 ? `${formatPlayed(selected.playtimeMinutes)} played` : null,
    selected.lastPlayedUtc ? `Last played ${formatRelativeLastPlayed(selected.lastPlayedUtc).toLocaleLowerCase()}` : null,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : null
}

/**
 * Info's key and its Escape: I opens or closes it, and Escape closes it before the sheet (unless Properties, a menu or a
 * field has the key). Capture, so it runs ahead of the sheet's own Escape.
 */
function useInfoKeys(open: boolean, setOpen: (open: boolean) => void, propertiesOpen: boolean) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.isComposing || event.ctrlKey || event.metaKey || event.altKey || propertiesOpen) return
      const target = event.target instanceof HTMLElement ? event.target : null
      if (target?.closest('input, textarea, [contenteditable="true"], [role="menu"], [popover]')) return
      if (event.key === 'Escape' && open) {
        event.preventDefault()
        setOpen(false)
      } else if (event.key.toLowerCase() === 'i' && !event.shiftKey) {
        event.preventDefault()
        setOpen(!open)
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [open, setOpen, propertiesOpen])
}

export function GamePage({
  selected,
  busy,
  statusMsg,
  progress,
  queued = false,
  onPrimary,
  onStop,
  onCancel,
  onDequeue,
  onOpenSystem,
  onSelectSource,
  onStatus,
  onUninstalled,
  hdrForGames,
  onHdrForGames,
  propertiesOpen,
  onPropertiesOpen,
}: GamePageProps) {
  const { installedActions, artworkEnabled } = pageFlags(selected)
  const details = useGameDetails(selected)
  const [manageBusy, setManageBusy] = useState(false)
  const [artworkView, setArtworkGame] = useArtworkOverride(selected, details.selectionKey)

  const action = resolvePrimaryAction(selected)
  const buyUrl = hostedBuyUrl(selected)
  const selectedProgress = transferForGame(progress, selected)
  const hostLocked = busy || !!selectedProgress?.isActive
  const sources = sourceVariants(selected)
  // Done (DESIGN.md › Motion) on the cover when a download lands installed; while it runs, the pill carries the bar.
  const downloading = !!selectedProgress?.isActive
  const finished = useJustFinished(downloading, isCurrent(selected))
  // Content first, details on demand (DESIGN.md › Game sheet): the hero holds the cover, the name, Play and Properties;
  // Info opens the pane beside it with the facts, the game's lines and the cover tools. The sheet is the same size for
  // every game, whatever the PC supports for it; the pane scrolls what it holds.
  const [info, setInfoState] = useState(infoWanted)
  const setInfo = (open: boolean) => { infoWanted = open; setInfoState(open) }
  // What the pane holds mounts the first time it opens for this game (each line reads its own state), then stays.
  const [shownFor, setShownFor] = useState<string | null>(info ? details.selectionKey : null)
  if (info && shownFor !== details.selectionKey) setShownFor(details.selectionKey)
  useInfoKeys(info, setInfo, propertiesOpen)
  const paneId = useId()
  const glance = glanceLine(selected)
  const note = !!details.driveHeat && details.driveHeat.slowedSeconds > 0

  return (
    <article className={`exo-game-page exo-enter${info ? ' is-info' : ''}`}>
      <div className="exo-game-page-wash" aria-hidden>
        <HeroWash game={artworkView} fill={false} />
      </div>
      <div className="exo-game-page-inner">
        <div className="exo-game-page-layout">
          <div className="exo-game-cover-col">
            <div className="exo-game-detail-cover">
              <CoverArt game={artworkView} large preload />
              {finished && (
                <span className="exo-game-cover-motion exo-motion-done" aria-hidden="true">
                  <svg className="exo-motion-check" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>
                </span>
              )}
            </div>
          </div>
          <div className="exo-game-page-body">
            <div className="exo-game-info">
              <GameHeader selected={selected}>
                <GameStore
                  selected={selected}
                  sources={sources}
                  locked={hostLocked || manageBusy}
                  onSelectSource={onSelectSource}
                />
              </GameHeader>
              {glance ? <p className="exo-game-glance tabular-nums">{glance}</p> : null}
              <div className="exo-game-actions">
                <PrimaryAction
                  selected={selected}
                  action={action}
                  buyUrl={buyUrl}
                  busy={busy}
                  statusMsg={statusMsg}
                  progress={selectedProgress}
                  queued={queued}
                  onPrimary={onPrimary}
                  onStop={onStop}
                  onCancel={onCancel}
                  onDequeue={onDequeue}
                />
                {installedActions && (
                  <GameActions
                    selected={selected}
                    coverSource={artworkView.coverSource}
                    locked={hostLocked}
                    upscalers={details.dlss}
                    onUpscalersChanged={() => void details.refreshDlss(false)}
                    onStatus={onStatus}
                    onArtwork={setArtworkGame}
                    onUninstalled={onUninstalled}
                    onBusyChange={setManageBusy}
                    propertiesOpen={propertiesOpen}
                    onPropertiesOpen={onPropertiesOpen}
                  />
                )}
                <button
                  type="button"
                  className={`exo-game-tool exo-game-side is-icon exo-game-info-toggle${note ? ' has-note' : ''}`}
                  aria-label={note ? 'Info, something to know' : 'Info'}
                  aria-pressed={info}
                  aria-controls={paneId}
                  onClick={() => setInfo(!info)}
                >
                  <Info size={16} />
                </button>
              </div>
            </div>
          </div>
        </div>
        <aside id={paneId} className="exo-game-info-pane" aria-label="Info" hidden={!info}>
          {shownFor === details.selectionKey && (
            <>
              <GameStats selected={selected} sources={sources} achievements={details.achievements} driveHeat={details.driveHeat} loading={details.loading} />
              {artworkEnabled && !installedActions && (
                <ArtworkStrip selected={selected} coverSource={artworkView.coverSource} locked={hostLocked} onArtwork={setArtworkGame} onStatus={onStatus} />
              )}
              {installedActions && (
                <GameLines selected={selected} details={details} onOpenSystem={onOpenSystem} hdrForGames={hdrForGames} onHdrForGames={onHdrForGames} />
              )}
            </>
          )}
        </aside>
      </div>
      <LiveStatus selected={selected} busy={busy} progress={selectedProgress} statusMsg={statusMsg} />
    </article>
  )
}

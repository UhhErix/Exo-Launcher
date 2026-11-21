import { useState } from 'react'
import { CHECK_PATH, ChevronLeft } from '../brand/icons'
import { hostedBuyUrl, resolvePrimaryAction, type Game, type GameVariant, type InstallProgress } from '../lib/host'
import { transferForGame } from '../lib/library'
import { installProgressLabel, transferPercent } from '../lib/utils'
import { CoverArt, HeroWash } from './CoverArt'
import { GameHeader, GameShared, GameStats, GameStore } from './game/GameSections'
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
  /** Opens the PC room's Graphics page, Upscalers sheet, or Shaders. */
  onOpenSystem?: (page: 'graphics' | 'shaders' | 'upscalers') => void
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
  onBack?: () => void
}

function GameStatusPill({ selected, downloading }: { selected: Game; downloading: boolean }) {
  if (downloading) return <span className="exo-game-eyebrow is-warn"><span className="exo-game-eyebrow-dot" aria-hidden="true" />Downloading</span>
  if (selected.isRunning) return <span className="exo-game-eyebrow is-good"><span className="exo-game-eyebrow-dot" aria-hidden="true" />Playing now</span>
  if (selected.updateAvailable) return <span className="exo-game-eyebrow is-warn"><span className="exo-game-eyebrow-dot" aria-hidden="true" />Update ready</span>
  if (selected.installed) return <span className="exo-game-eyebrow is-good"><span className="exo-game-eyebrow-dot" aria-hidden="true" />Installed</span>
  return <span className="exo-game-eyebrow"><span className="exo-game-eyebrow-dot" aria-hidden="true" />Not installed</span>
}

function GameLines({ selected, details, onOpenSystem, hdrForGames, onHdrForGames }: {
  selected: Game
  details: ReturnType<typeof useGameDetails>
} & Pick<GamePageProps, 'onOpenSystem' | 'hdrForGames' | 'onHdrForGames'>) {
  return (
    <section className="exo-game-tuning" aria-label="Game tuning">
      <div className="exo-game-tuning-head">
        <h2>Game Tuning</h2>
        <span className="exo-game-tuning-rule" aria-hidden="true" />
      </div>
      <div className="exo-game-lines">
        {onOpenSystem && (
          <>
            <UpscalerFiles
              key={'upscalers' + details.selectionKey}
              status={details.dlss}
              scanState={!details.dlss ? 'loading' : details.dlss.ok === false ? 'failed' : 'ready'}
              onOpen={() => onOpenSystem('upscalers')}
            />
            <ShaderPrecompile key={'shaders' + details.selectionKey} gameId={selected.id} onOpenShaders={() => onOpenSystem('shaders')} />
          </>
        )}
        <FrameGenModRow key={'framegenmod' + details.selectionKey} gameId={selected.id} />
        <HdrRow key={'hdr' + details.selectionKey} gameId={selected.id} hdrForGames={hdrForGames} onHdrForGames={onHdrForGames} />
        <OptimizeLine key={'optimize' + details.selectionKey} gameId={selected.id} />
        <FrameGenRow key={'framegen' + details.selectionKey} gameId={selected.id} />
      </div>
    </section>
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
  onBack,
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
  const downloading = !!selectedProgress?.isActive
  const finished = useJustFinished(downloading, isCurrent(selected))

  return (
    <article className="exo-game-page exo-enter">
      <div className="exo-game-page-wash" aria-hidden>
        <HeroWash game={artworkView} fill />
      </div>
      <header className="exo-game-room-bar">
        {onBack ? (
          <button type="button" className="exo-glass exo-game-back" onClick={onBack}>
            <ChevronLeft size={16} />
            Library
          </button>
        ) : <span />}
        <div className="exo-game-room-meta">
          <GameShared selected={selected} />
          <div className="exo-glass exo-game-store-pill">
            <GameStore
              selected={selected}
              sources={sources}
              locked={hostLocked || manageBusy}
              onSelectSource={onSelectSource}
            />
          </div>
        </div>
      </header>
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
            {artworkEnabled && !installedActions && (
              <ArtworkStrip selected={selected} coverSource={artworkView.coverSource} locked={hostLocked} onArtwork={setArtworkGame} onStatus={onStatus} />
            )}
          </div>
          <div className="exo-game-page-body">
            <div className="exo-game-info">
              <GameStatusPill selected={selected} downloading={downloading} />
              <GameHeader selected={selected} />
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
              </div>
              <GameStats selected={selected} sources={sources} achievements={details.achievements} driveHeat={details.driveHeat} loading={details.loading} />
            </div>
          </div>
        </div>
        {installedActions && (
          <GameLines selected={selected} details={details} onOpenSystem={onOpenSystem} hdrForGames={hdrForGames} onHdrForGames={onHdrForGames} />
        )}
      </div>
      <LiveStatus selected={selected} busy={busy} progress={selectedProgress} statusMsg={statusMsg} />
    </article>
  )
}

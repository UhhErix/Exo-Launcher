import { CompanyLogo } from '../../brand/CompanyLogo'
import { Members } from '../../brand/icons'
import type { DriveHeat, Game, GameVariant } from '../../lib/host'
import { formatPlayed, formatRelativeLastPlayed, formatSize, storeLabel } from '../../lib/utils'
import { GameLogo } from '../GameLogo'
import type { Achievements } from './useGameDetails'

export function GameShared({ selected }: { selected: Game }) {
  if (!selected.familyShared) return null
  return (
    <span className="exo-game-shared">
      <Members size={12} />
      {selected.sharedBy ? `Shared by ${selected.sharedBy}` : 'Steam Family'}
    </span>
  )
}

export function GameHeader({ selected, children }: { selected: Game; children?: React.ReactNode }) {
  return (
    <div className="exo-game-title-row">
      <GameLogo
        game={selected}
        wrapClass="exo-game-identity"
        imgClass="exo-game-logo"
        titleClass="exo-game-title"
      />
      {children}
    </div>
  )
}

export function GameStore({
  selected,
  sources,
  locked,
  onSelectSource,
}: {
  selected: Game
  sources: GameVariant[] | null
  locked: boolean
  onSelectSource?: (id: string) => void
}) {
  if (sources && onSelectSource) {
    return (
      <div className="exo-game-store">
        <span className="exo-game-sources" role="group" aria-label="Choose game source">
          {sources.map((variant) => (
            <button
              key={variant.id}
              type="button"
              disabled={locked}
              onClick={() => onSelectSource(variant.id)}
              aria-pressed={variant.id === selected.id}
              aria-label={variant.updateAvailable ? `${storeLabel(variant.store)}, update available` : storeLabel(variant.store)}
              className={`exo-game-source${variant.id === selected.id ? ' is-on' : ''}${variant.updateAvailable ? ' has-update' : ''}`}
            >
              <CompanyLogo company={variant.store.toLowerCase()} size={14} />
              {storeLabel(variant.store)}
            </button>
          ))}
        </span>
      </div>
    )
  }
  if (selected.isAddPortable) return null
  return (
    <div className="exo-game-store">
      <span className="exo-game-store-name">
        <CompanyLogo company={selected.store.toLowerCase()} size={18} />
        {storeLabel(selected.store)}
      </span>
    </div>
  )
}

const driveSlowed = (heat: DriveHeat | null) => !!heat && heat.slowedSeconds > 0

function Fact({ label, value, pct }: { label: string; value: string; pct?: number }) {
  return (
    <div className="exo-game-fact">
      <dd className="tabular-nums">{value}</dd>
      <dt>{label}</dt>
      {pct != null ? (
        <span className="exo-game-fact-bar" aria-hidden="true">
          <span style={{ width: `${Math.min(100, Math.max(0, pct))}%` }} />
        </span>
      ) : null}
    </div>
  )
}

function shownAny(known: number, achievements: Achievements | null, driveHeat: DriveHeat | null) {
  return known > 0 || !!achievements || driveSlowed(driveHeat)
}

export function GameStats({
  selected,
  sources,
  achievements,
  driveHeat,
  loading = false,
}: {
  selected: Game
  sources: GameVariant[] | null
  achievements: Achievements | null
  driveHeat: DriveHeat | null
  loading?: boolean
}) {
  const source = sources?.find((variant) => variant.id === selected.id)
  const played = formatPlayed(source?.playtimeMinutes ?? selected.playtimeMinutes)
  const last = formatRelativeLastPlayed(source?.lastPlayedUtc ?? selected.lastPlayedUtc)
  const size = formatSize(selected.sizeBytes)
  const known = (value: string) => value !== '—'
  if (!shownAny([played, last, size].filter(known).length, achievements, driveHeat)) return null
  const achPct = achievements && achievements.total > 0 ? Math.round((achievements.unlocked / achievements.total) * 100) : undefined
  return (
    <div className="exo-game-stats" aria-busy={loading || undefined}>
      <dl className="exo-glass exo-game-facts">
        {known(played) && <Fact label="Played" value={played} />}
        {known(last) && <Fact label="Last played" value={last} />}
        {known(size) && <Fact label={selected.installed ? 'Storage' : 'Size'} value={size} />}
        {achievements && <Fact label="Achievements" value={`${achievements.unlocked} / ${achievements.total}`} pct={achPct} />}
      </dl>
      {driveSlowed(driveHeat) && <p className="exo-game-drive">Your drive ran hot and slowed this game.</p>}
    </div>
  )
}

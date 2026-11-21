
import { useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { Members } from '../../brand/icons'
import type { DriveHeat, Game, GameVariant } from '../../lib/host'
import { artSteamAppId } from '../../lib/library'
import { formatRelativeLastPlayed, formatSize, storeLabel, formatPlayed } from '../../lib/utils'
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
  const appId = artSteamAppId(selected)
  const [failed, setFailed] = useState(false)
  const [logoKey, setLogoKey] = useState(selected.id)
  if (logoKey !== selected.id) {
    setLogoKey(selected.id)
    setFailed(false)
  }
  const logoSrc = appId && !failed
    ? `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/logo.png`
    : null
  return (
    <div className="exo-game-title-row">
      {logoSrc ? (
        <h1 className="exo-game-logo-wrap">
          <img
            src={logoSrc}
            alt={selected.title}
            className="exo-game-logo"
            draggable={false}
            decoding="async"
            onError={() => setFailed(true)}
          />
        </h1>
      ) : (
        <h1 className="exo-game-title">{selected.title}</h1>
      )}
      {children}
    </div>
  )
}

/**
 * The store the game belongs to, beside its name: the store's mark and name, or one pill
 * to switch between the stores you own it on.
 */
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
        <CompanyLogo company={selected.store.toLowerCase()} size={20} />
        {storeLabel(selected.store)}
      </span>
    </div>
  )
}

/** The drive in the last session, said only when its heat slowed the game: no readings, the one thing that happened. */
const driveSlowed = (heat: DriveHeat | null) => !!heat && heat.slowedSeconds > 0

/** One fact: its value large enough to read from your chair, its name quiet under it. */
function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="exo-game-fact">
      <dd className="tabular-nums">{value}</dd>
      <dt>{label}</dt>
    </div>
  )
}

/** Whether the stats hold anything to show: a known number, an achievement or a drive reading. */
function shownAny(known: number, achievements: Achievements | null, driveHeat: DriveHeat | null) {
  return known > 0 || !!achievements || driveSlowed(driveHeat)
}

/**
 * What people open a game page for: time played, last played, storage and achievements, each its own fact with room
 * around it. A fact Exo doesn't know is left out. A Riot game shows its lifetime hours as Played, as a Steam game does
 * (the Riot ID's ledger: starting hours plus Exo's tracked sessions), and nothing more. The drive is said under
 * them, only when its heat slowed the game.
 */
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
  // Source chips name one store. Use that store's hours, not the grouped
  // card total — the default chip matches the card id, so reading
  // selected.playtimeMinutes showed Epic+Steam while Epic looked selected.
  const source = sources?.find((variant) => variant.id === selected.id)
  const played = formatPlayed(source?.playtimeMinutes ?? selected.playtimeMinutes)
  const last = formatRelativeLastPlayed(source?.lastPlayedUtc ?? selected.lastPlayedUtc)
  const size = formatSize(selected.sizeBytes)
  const known = (value: string) => value !== '—'
  // Stale first: what is known shows at once, and nothing stands in for the extras while they load.
  if (!shownAny([played, last, size].filter(known).length, achievements, driveHeat)) return null
  return (
    <div className="exo-game-stats" aria-busy={loading || undefined}>
      <dl className="exo-game-facts">
        {known(played) && <Fact label="Played" value={played} />}
        {known(last) && <Fact label="Last played" value={last} />}
        {known(size) && <Fact label={selected.installed ? 'Storage' : 'Size'} value={size} />}
        {achievements && <Fact label="Achievements" value={`${achievements.unlocked} of ${achievements.total}`} />}
      </dl>
      {driveSlowed(driveHeat) && <p className="exo-game-drive">Your drive ran hot and slowed this game.</p>}
    </div>
  )
}

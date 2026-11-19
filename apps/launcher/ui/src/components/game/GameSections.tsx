
import { useEffect, useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { ActionButton } from '../ui/ConfirmButton'
import { host, type DriveHeat, type Game, type GameVariant, type RiotLifetime } from '../../lib/host'
import { formatRelativeLastPlayed, formatSize, storeLabel, formatPlayed } from '../../lib/utils'
import type { Achievements } from './useGameDetails'

/** The title with the store it belongs to beside it. */
export function GameHeader({ selected, children }: { selected: Game; children?: React.ReactNode }) {
  return (
    <div className="exo-game-title-row">
      <h1 className="exo-game-title">{selected.title}</h1>
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

/** The Riot facts for a game page: each mode's hours, then the profile stats tracker.gg gave (a stat it did not give is left out). */
function riotLifetimeFacts(lifetime: RiotLifetime): { label: string; value: string }[] {
  const modes = lifetime.modes.map(({ mode, minutes }) => ({
    label: mode === 'unknown' ? 'Unknown mode' : mode.charAt(0).toUpperCase() + mode.slice(1),
    value: formatPlayed(minutes),
  }))
  const stats = lifetime.stats
  if (!stats) return modes
  const extra: { label: string; value: string | null }[] = [
    { label: 'Matches', value: stats.matches != null ? String(stats.matches) : null },
    { label: 'Wins', value: stats.wins != null ? String(stats.wins) : null },
    { label: 'K/D', value: stats.killDeath != null ? stats.killDeath.toFixed(2) : null },
    { label: 'Rank', value: stats.currentRank ?? null },
    { label: 'Peak rank', value: stats.peakRank ?? null },
    { label: 'Top picks', value: stats.topPicks?.length ? stats.topPicks.join(', ') : null },
  ]
  return [...modes, ...extra.flatMap(({ label, value }) => (value ? [{ label, value }] : []))]
}

/** One fact: its value large enough to read from your chair, its name quiet under it. */
function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="exo-game-fact">
      <dd className="tabular-nums">{value}</dd>
      <dt>{label}</dt>
    </div>
  )
}

/**
 * A Riot game's hours by mode and tracker.gg's profile stats, from the Riot ID's ledger. `saved` is true when a Riot ID is
 * saved (the ledger answered); `reload` reads the ledger again after a manual re-import. Nothing for other stores.
 */
function useRiotLifetime(game: Game): { facts: { label: string; value: string }[]; saved: boolean; reload: () => void } {
  const riotGame = game.store === 'riot' ? game.id : null
  const [read, setRead] = useState<{ game: string; lifetime: RiotLifetime } | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    if (!riotGame) return
    let current = true
    void host.riotLifetime(riotGame).then(
      next => { if (current) setRead({ game: riotGame, lifetime: next }) },
      () => { /* no ledger to show: the page keeps Played and the rest */ },
    )
    return () => { current = false }
  }, [riotGame, round])
  const lifetime = read?.game === riotGame ? read.lifetime : null
  return {
    facts: lifetime?.ok ? riotLifetimeFacts(lifetime) : [],
    saved: lifetime?.ok === true,
    reload: () => setRound(n => n + 1),
  }
}

/** Re-reads tracker.gg for the saved Riot ID by hand; the ledger keeps counting Exo's own sessions after it. */
function RiotReimport({ onDone }: { onDone: () => void }) {
  const [note, setNote] = useState<string | null>(null)
  // The button shows the import (ActionButton: its ring, then its check, or Bad with why beside it); what tracker.gg said stays under it.
  return (
    <div className="exo-game-riot">
      <ActionButton className="exo-set-btn is-compact" context="RiotReimport" fallback="Hours could not be imported. Try again."
        onPress={() => { setNote(null); return host.riotHoursRefresh() }}
        onDone={result => { setNote((result as { message?: string | null }).message ?? 'Hours imported.'); onDone() }}>Re-import from tracker.gg</ActionButton>
      {note ? <p className="exo-game-drive">{note}</p> : null}
    </div>
  )
}

/**
 * What people open a game page for: time played, last played, storage and achievements, each its own fact with room
 * around it. A fact Exo doesn't know is left out. The drive is said under them, only when its heat slowed the game.
 */
/** Whether the stats hold anything to show: a known number, an achievement, a drive reading, or a Riot fact. */
function shownAny(known: number, achievements: Achievements | null, driveHeat: DriveHeat | null, riotFacts: number, riotSaved: boolean) {
  return known > 0 || !!achievements || driveSlowed(driveHeat) || riotFacts > 0 || riotSaved
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
  // Source chips name one store. Use that store's hours, not the grouped
  // card total — the default chip matches the card id, so reading
  // selected.playtimeMinutes showed Epic+Steam while Epic looked selected.
  const source = sources?.find((variant) => variant.id === selected.id)
  const played = formatPlayed(source?.playtimeMinutes ?? selected.playtimeMinutes)
  const last = formatRelativeLastPlayed(source?.lastPlayedUtc ?? selected.lastPlayedUtc)
  const size = formatSize(selected.sizeBytes)
  const known = (value: string) => value !== '—'
  const riot = useRiotLifetime(selected)
  // Stale first: what is known shows at once, and nothing stands in for the extras while they load.
  if (!shownAny([played, last, size].filter(known).length, achievements, driveHeat, riot.facts.length, riot.saved)) return null
  return (
    <div className="exo-game-stats" aria-busy={loading || undefined}>
      <dl className="exo-game-facts">
        {known(played) && <Fact label="Played" value={played} />}
        {known(last) && <Fact label="Last played" value={last} />}
        {known(size) && <Fact label={selected.installed ? 'Storage' : 'Size'} value={size} />}
        {achievements && <Fact label="Achievements" value={`${achievements.unlocked} of ${achievements.total}`} />}
        {riot.facts.map(fact => <Fact key={fact.label} label={fact.label} value={fact.value} />)}
      </dl>
      {riot.saved && <RiotReimport onDone={riot.reload} />}
      {driveSlowed(driveHeat) && <p className="exo-game-drive">Your drive ran hot and slowed this game.</p>}
    </div>
  )
}

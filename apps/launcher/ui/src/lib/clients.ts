import type { ClientsStatus } from './host'

/** The Game clients card's heading, in Exo's words: what this PC is missing. */
export function clientsHeading(status: ClientsStatus): string {
  if (status.problem && !status.needsFix) return 'Exo couldn’t check your game clients'
  const epic = status.epic.layout === 'old'
  const steam = status.steam.channel === 'stable'
  if (epic && steam) return 'Exo needs the new Epic launcher and Steam Beta'
  if (epic) return 'Exo needs the new Epic launcher'
  if (steam) return 'Exo needs Steam Beta'
  return 'Your game clients are ready'
}

/**
 * The setup step is offered when a store is installed: a PC with neither has nothing to fix. A check that failed is offered
 * too, so the step shows its reason and Try again instead of being skipped.
 */
export function clientsOffered(status: ClientsStatus | null, checkFailed = false): boolean {
  if (checkFailed) return true
  return !!status && (status.epic.layout !== 'none' || status.steam.channel !== 'none')
}

/**
 * Continue waits on the clients step until its check passes, or until Continue for now (offered once a Fix has failed)
 * has deferred it to the next start. A check that cannot answer holds the step too (it shows the reason).
 */
export function clientsBlocks(step: string, status: ClientsStatus | null, deferred = false, checkFailed = false): boolean {
  return step === 'clients' && (checkFailed || !!status?.needsFix || !!status?.problem) && !deferred
}

/**
 * The card's Continue for now: shown after a failed Fix or after Epic's installer has run its wait (useInstallerLong), and only
 * where there is something to defer (onLater). One rule, so the card's control and the footer's never disagree.
 */
export function offersLater(failed: boolean, installerLong: boolean, onLater: unknown): boolean {
  return (failed || installerLong) && !!onLater
}

/** The footer is not shown on welcome, nor on a restore step that is held (its restart was refused: no Start fresh, no Continue). */
export function footerHidden(id: string, restoreHeld: boolean): boolean {
  return id === 'welcome' || (id === 'restore' && restoreHeld)
}

/** The start-up card holds the room until its check passes, unless Continue for now deferred it for this run. */
export function clientsGateShows(status: ClientsStatus | null, deferred: boolean): boolean {
  return !!status?.needsFix && !deferred
}

/** What Fix will do on this PC, with only the parts that apply, in the words the card shows. */
export function clientsFixCopy(status: ClientsStatus): string {
  const parts: string[] = []
  if (status.steam.channel === 'stable') parts.push('closes Steam and restarts it on Steam Beta')
  if (status.epic.layout === 'old') parts.push("downloads Epic's new launcher and opens its installer")
  return parts.length === 0
    ? 'Nothing changes while a game plays.'
    : `Fix ${parts.join(', and ')}. Nothing changes while a game plays.`
}

/**
 * Whether the library waits: setup not done, or the Game clients check says a Fix is still due (and not deferred).
 * While the check cannot answer, the library waits only on an answer, never on a failed check.
 * Onboarding's finish does not read the library itself: this gate lets the first read happen once, when it lifts.
 */
export function libraryHeld(onboardingComplete: boolean, status: ClientsStatus | null, deferred: boolean, checkFailed: boolean): boolean {
  return onboardingComplete && (status ? clientsGateShows(status, deferred) : !checkFailed)
}

/**
 * The footer's one forward button on a step: whether it waits, its words when they differ from the step's default (the restore
 * offer's is Start fresh), whether it is quiet (the step's own primary is elsewhere on it), and whether pressing it defers the
 * clients check. A check that cannot answer (unreadable, or its read failed) is deferred by this button, not by a second one in
 * the card; a Fix that failed keeps its own Continue for now in the card.
 */
export function footerNext(id: string, status: ClientsStatus | null, deferred: boolean, failed: boolean, restoring: boolean, installerLong = false): { blocked: boolean; label?: string; quiet: boolean; defers: boolean } {
  // installerLong: Epic's installer has run 30 s (useInstallerLong), so setup offers Continue for now rather than waiting on a window it cannot see.
  const defers = id === 'clients' && !deferred && (failed || !!status?.problem || installerLong)
  if (defers) return { blocked: false, label: 'Continue for now', quiet: true, defers }
  const blocked = clientsBlocks(id, status, deferred, failed) || (id === 'restore' && restoring)
  return { blocked, label: id === 'restore' ? 'Start fresh' : undefined, quiet: id === 'restore', defers: false }
}

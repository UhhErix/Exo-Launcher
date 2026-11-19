// Order per AGENTS.md: Welcome, then the Exo account (required, no skip while the service answers), then Game clients.
export const STEPS = [
  { id: 'welcome', title: 'Welcome to Exo' },
  { id: 'account', title: 'Exo account' },
  { id: 'restore', title: 'Restore your data' },
  { id: 'clients', title: 'Game clients' },
  { id: 'stores', title: 'Your game stores' },
  { id: 'apps', title: 'Music' },
  { id: 'discord', title: 'Discord' },
  { id: 'capture', title: 'Captures' },
  { id: 'cleanup', title: 'Clean up' },
  { id: 'pc', title: 'Best settings for this PC' },
  { id: 'done', title: "You're all set" },
] as const

export type StepId = (typeof STEPS)[number]['id']
export type StoreCheckState = 'checking' | 'ready' | 'error'
export type Feedback = { message: string; error?: boolean }
export type MissingRuntime = { id: string; name: string }

const JUNK_SCAN_STEPS: ReadonlySet<StepId> = new Set<StepId>(['discord', 'capture', 'cleanup', 'pc'])

/**
 * Setup's junk scan (seconds on a busy PC) runs once, when setup reaches Discord: the step before Clean up's, the only
 * one that can lead into it. It does not run on mount, so a PC that never gets there never pays for it.
 */
export function junkScanDue(step: StepId, alreadyStarted: boolean): boolean {
  return !alreadyStarted && JUNK_SCAN_STEPS.has(step)
}

/**
 * Continue waits while the scan runs, only on the step that leads straight into Clean up (Captures, or Discord when Captures
 * is left out), so Clean up is never skipped by a race and no earlier step waits.
 */
export function waitsForJunkScan(step: StepId, captureShown: boolean): boolean {
  return step === 'capture' || (step === 'discord' && !captureShown)
}

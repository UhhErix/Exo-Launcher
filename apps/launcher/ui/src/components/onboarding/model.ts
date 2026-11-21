export const STEPS = [
  { id: 'welcome', title: 'Welcome to Exo' },
  { id: 'account', title: 'Exo account' },
  { id: 'restore', title: 'Restore your data' },
  { id: 'clients', title: 'Game clients' },
  { id: 'stores', title: 'Your game stores' },
  { id: 'apps', title: 'Music' },
  { id: 'discord', title: 'Discord' },
  { id: 'cleanup', title: 'Clean up' },
  { id: 'pc', title: 'Best settings for this PC' },
] as const

export type StepId = (typeof STEPS)[number]['id']
export type StoreCheckState = 'checking' | 'ready' | 'error'
export type Feedback = { message: string; error?: boolean }
export type MissingRuntime = { id: string; name: string }

const JUNK_SCAN_STEPS: ReadonlySet<StepId> = new Set<StepId>(['discord', 'cleanup', 'pc'])

export function junkScanDue(step: StepId, alreadyStarted: boolean): boolean {
  return !alreadyStarted && JUNK_SCAN_STEPS.has(step)
}

export function waitsForJunkScan(step: StepId): boolean {
  return step === 'discord'
}

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

const SLOW_PROBE_RE = /did not answer in time|didn(?:'|’)t answer|timed? ?out|still starting|canceled|cancelled/i

export function isSlowProbeError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : String(error ?? '')
  return SLOW_PROBE_RE.test(message)
}

export function onboardingProbeReportLevel(error: unknown): 'warn' | 'error' {
  return isSlowProbeError(error) ? 'warn' : 'error'
}

export function shouldRetryOnboardingProbe(error: unknown, attempt: number): boolean {
  return attempt < 1 && isSlowProbeError(error)
}

export function isPartialGpuProfile(profile: { ok: boolean; status: string; message?: string | null }): boolean {
  return !profile.ok && profile.status === 'none' && isSlowProbeError(profile.message ?? '')
}

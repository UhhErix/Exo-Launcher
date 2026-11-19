/**
 * The life of one press (DESIGN.md › Feel, components/ui/ConfirmButton.tsx): idle, then pending while the work runs
 * (the control's ring), then success (its check, held a beat before the control settles into its next real state), or
 * error (it turns Bad and says why beside itself, and its next press tries again). Pure, so the rules are tested
 * (test/actionState.test.ts): a second press while pending is ignored, an answer that comes after a newer press (or
 * after the wait gave up) is dropped, and nothing can leave a control pending forever (`settled` with a time limit).
 */
import { plainError, type PlainError } from './errorWords.ts'

export type ActionPhase = 'idle' | 'pending' | 'success' | 'error'

export type ActionState = {
  phase: ActionPhase
  /** Counts presses: an answer carries the run it belongs to, and only the current run's answer moves the state. */
  run: number
  error: PlainError | null
}

export type ActionEvent =
  | { type: 'start' }
  | { type: 'resolve'; run: number }
  | { type: 'reject'; run: number; error: PlainError }
  | { type: 'settle'; run: number }
  | { type: 'reset' }

export const idleAction: ActionState = { phase: 'idle', run: 0, error: null }

/** How long the check holds before the control settles into its next state. */
export const SUCCESS_HOLD_MS = 1200
/** How long a press may wait before it says so, unless the action names its own limit (an install names none). */
export const ACTION_TIMEOUT_MS = 60_000

export function actionReducer(state: ActionState, event: ActionEvent): ActionState {
  switch (event.type) {
    case 'start':
      // A press while it works does nothing; from rest, a check still held or an error (Retry), it starts a new run.
      return state.phase === 'pending' ? state : { phase: 'pending', run: state.run + 1, error: null }
    case 'resolve':
      return state.phase === 'pending' && event.run === state.run ? { ...state, phase: 'success' } : state
    case 'reject':
      return state.phase === 'pending' && event.run === state.run ? { ...state, phase: 'error', error: event.error } : state
    case 'settle':
      return state.phase === 'success' && event.run === state.run ? { ...state, phase: 'idle' } : state
    case 'reset':
      return state.phase === 'idle' && !state.error ? state : { ...state, phase: 'idle', error: null }
  }
}

/** The sentence a timed-out wait says. */
export const TIMED_OUT = 'That is taking too long. Try again.'

export class ActionTimeout extends Error {
  constructor() { super(TIMED_OUT); this.name = 'ActionTimeout' }
}

/**
 * Whether an answer is a refusal. Host calls mostly answer `{ ok: false, message }` instead of throwing; `false` is a
 * plain no. Anything else (void, a value, `{ ok: true }`) is done.
 */
export function refusal(value: unknown): { message: string | null } | null {
  if (value === false) return { message: null }
  if (value && typeof value === 'object' && 'ok' in value && (value as { ok: unknown }).ok === false) {
    const { message, error } = value as { message?: unknown; error?: unknown }
    const text = typeof message === 'string' && message ? message : typeof error === 'string' && error ? error : null
    return { message: text }
  }
  return null
}

/**
 * Runs the work to its answer: done with its value, or failed with the plain words for why (errorWords). With a limit,
 * a wait past it fails as timed out (the work itself goes on; its late answer is dropped by the reducer).
 */
export async function settled<T>(work: () => T | Promise<T>, options: { timeoutMs?: number | null; fallback?: string } = {}):
  Promise<{ ok: true; value: T } | { ok: false; error: PlainError; cause: unknown }> {
  const limit = options.timeoutMs === undefined ? ACTION_TIMEOUT_MS : options.timeoutMs
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    const running = Promise.resolve().then(work)
    const value = limit === null ? await running : await Promise.race([
      running,
      new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new ActionTimeout()), limit) }),
    ])
    const no = refusal(value)
    if (no) return { ok: false, error: plainError(no.message, options.fallback), cause: no.message ?? 'refused' }
    return { ok: true, value }
  } catch (cause) {
    return { ok: false, error: plainError(cause, options.fallback), cause }
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

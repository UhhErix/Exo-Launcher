/** The host's update states (UpdateFlow.cs, camelCase): the one source the update sheet, notice and Settings draw from. */
export type UpdateState = 'idle' | 'checking' | 'downloading' | 'waitingForGame' | 'ready' | 'applying' | 'restarting' | 'failed'

export type UpdatePhase = 'checking' | 'idle' | 'available' | 'installing' | 'restarting' | 'failed' | 'waiting'

const STATES: readonly UpdateState[] = ['idle', 'checking', 'downloading', 'waitingForGame', 'ready', 'applying', 'restarting', 'failed']

export function isUpdateState(value: unknown): value is UpdateState {
  return typeof value === 'string' && (STATES as readonly string[]).includes(value)
}

/**
 * What the update looks like for a host state. `offered`: a newer release is known; `asking`: this page's Update now is
 * still waiting for its answer (Ready on the way to Applying is then part of the install, not an update kept for later).
 * A check that could not answer leaves the host Idle, so it shows nothing: only a download or install fails.
 */
export function updatePhase(state: UpdateState, offered: boolean, asking: boolean): UpdatePhase {
  switch (state) {
    case 'checking': return 'checking'
    case 'downloading':
    case 'applying': return 'installing'
    case 'restarting': return 'restarting'
    case 'waitingForGame': return 'waiting'
    case 'ready': return asking ? 'installing' : 'waiting'
    case 'failed': return 'failed'
    case 'idle': return offered ? 'available' : 'idle'
  }
}

/** The update sheet after a check's answer: it stays only while that answer still offers an update. */
export function sheetAfterCheck(sheet: boolean, answer: { ok: boolean; updateAvailable?: boolean }): boolean {
  return sheet && answer.ok && !!answer.updateAvailable
}

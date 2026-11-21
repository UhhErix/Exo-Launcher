import type { Snapshot } from './shaderCall'

export const stale = (prev: Snapshot | null, next: Snapshot) =>
  !!prev && (prev.rev > next.rev || (prev.rev === 0 && !!next.app?.scanning && next.games.length === 0 && prev.games.length > 0))

export const rebase = (prev: Snapshot | null) => prev && prev.rev > 0 ? { ...prev, rev: 0 } : prev

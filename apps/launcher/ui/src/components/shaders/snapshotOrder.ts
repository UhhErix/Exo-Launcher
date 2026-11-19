/** Which engine state the Shaders board keeps: pure (no host), so the order is tested in test/shaderSnapshot.test.ts. */
import type { Snapshot } from './shaderCall'

/**
 * The newest state wins. The last state the engine left when it exited has rev 0; a starting engine's first states
 * (scanning, no games listed yet) don't replace it, as it lists them already.
 */
export const stale = (prev: Snapshot | null, next: Snapshot) =>
  !!prev && (prev.rev > next.rev || (prev.rev === 0 && !!next.app?.scanning && next.games.length === 0 && prev.games.length > 0))

/** Each engine counts its revs from 1: what an earlier one sent reads as rev 0, so the running one's states win. */
export const rebase = (prev: Snapshot | null) => prev && prev.rev > 0 ? { ...prev, rev: 0 } : prev

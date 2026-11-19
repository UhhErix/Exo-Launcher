import type { FrameKey } from './frameStore'

/**
 * What Exo Mouse keeps besides its profiles: the HITS presets and the bunny hop last written to each mouse. Exo keeps
 * them in its own data folder (as it keeps the profiles), so they survive anything that clears this frame and go into
 * Exo's backups. Read once; what this frame kept in its own storage before moves there once, and leaves the frame only
 * after Exo has saved it. When Exo's copy could not be read, nothing is written: a save then would replace the whole file
 * with only this run's values.
 */
export type SavedKey = 'hits-presets' | 'host-bunny-hop'

const BEFORE: Record<SavedKey, FrameKey> = { 'hits-presets': 'exo-mouse-hits-presets', 'host-bunny-hop': 'exo-mouse.host-bunny-hop' }

export type Reply = { ok?: boolean; data?: unknown } | null
export type SavedHost = { read: () => Promise<Reply>; write: (data: Record<string, unknown>) => Promise<Reply> }
export type FrameStorage = { get: (key: FrameKey) => string | null; remove: (key: FrameKey) => void }

/** <paramref>say</paramref>: one plain sentence for the player, said once when a save cannot be kept. */
export function makeSaved(host: SavedHost, frame: FrameStorage, say: (text: string) => void = () => { }) {
  let saved: Partial<Record<SavedKey, unknown>> = {}
  let readable = false
  let toldUnread = false
  let loading: Promise<void> | null = null

  const write = async () => readable && (await host.write(saved).catch(() => null))?.ok === true

  /** Reads what Exo kept, once; later calls wait for the same read. */
  const load = () => loading ??= (async () => {
    const reply = await host.read().catch(() => null)
    if (reply?.ok !== true) return
    readable = true
    saved = reply.data && typeof reply.data === 'object' && !Array.isArray(reply.data) ? { ...(reply.data as Record<string, unknown>) } : {}
    let moved = false
    for (const key of Object.keys(BEFORE) as SavedKey[]) {
      const raw = frame.get(BEFORE[key])
      if (raw === null || saved[key] !== undefined) continue
      try { saved[key] = JSON.parse(raw) as unknown; moved = true } catch { /* unreadable: nothing to move */ }
    }
    if (moved && !await write()) return
    for (const key of Object.keys(BEFORE) as SavedKey[]) if (saved[key] !== undefined) frame.remove(BEFORE[key])
  })()

  return {
    load,
    value: (key: SavedKey): unknown => saved[key],
    /** Saves one value; false when Exo could not keep it (it then lasts until the frame closes). */
    save: (key: SavedKey, value: unknown): Promise<boolean> => {
      saved = { ...saved, [key]: value }
      if (!readable && loading !== null && !toldUnread) {
        toldUnread = true
        say("Exo could not read your saved presets, so changes to them last until Exo closes.")
      }
      return write()
    },
  }
}

import { onUiPrefs, setUiPref, uiPref } from './uiPrefs.ts'

export const MAX_PINNED_DMS = 50
const PINNED_DMS_EVENT = 'exo-dc-pins-changed'

export function sanitizePinnedDms(raw: unknown): string[] {
  if (!Array.isArray(raw)) return []
  const seen = new Set<string>()
  const out: string[] = []
  for (const item of raw) {
    if (typeof item !== 'string') continue
    const id = item.trim()
    if (!id || seen.has(id)) continue
    seen.add(id)
    out.push(id)
    if (out.length >= MAX_PINNED_DMS) break
  }
  return out
}

export function readPinnedDms(): string[] {
  return sanitizePinnedDms(uiPref('discordPinnedDms'))
}

export function isDmPinned(channelId: string): boolean {
  return readPinnedDms().includes(channelId)
}

export function togglePinDm(channelId: string): boolean {
  const id = channelId.trim()
  if (!id) return false
  const current = readPinnedDms()
  const wasPinned = current.includes(id)
  const next = wasPinned
    ? current.filter(entry => entry !== id)
    : [id, ...current].slice(0, MAX_PINNED_DMS)
  setUiPref('discordPinnedDms', next)
  if (typeof window !== 'undefined' && typeof window.dispatchEvent === 'function' && typeof Event === 'function') {
    window.dispatchEvent(new Event(PINNED_DMS_EVENT))
  }
  return !wasPinned
}

export function onPinnedDms(listener: () => void): () => void {
  const offPrefs = onUiPrefs(listener)
  if (typeof window === 'undefined' || typeof window.addEventListener !== 'function') return offPrefs
  window.addEventListener(PINNED_DMS_EVENT, listener)
  return () => {
    offPrefs()
    window.removeEventListener(PINNED_DMS_EVENT, listener)
  }
}

export function partitionPinnedDms<T extends { id: string }>(dms: readonly T[], pinnedIds: readonly string[]): {
  pinned: T[]
  unpinned: T[]
} {
  if (pinnedIds.length === 0) return { pinned: [], unpinned: [...dms] }
  const byId = new Map(dms.map(dm => [dm.id, dm]))
  const pinned: T[] = []
  const pinnedSet = new Set<string>()
  for (const id of pinnedIds) {
    const dm = byId.get(id)
    if (!dm || pinnedSet.has(id)) continue
    pinnedSet.add(id)
    pinned.push(dm)
  }
  const unpinned = dms.filter(dm => !pinnedSet.has(dm.id))
  return { pinned, unpinned }
}

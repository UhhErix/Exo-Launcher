import { pageGet, pageRemove, pageSet, type PageKey } from './pageStore.ts'

/**
 * The page's own choices, kept by Exo in its settings (settings.json's uiPrefs), so a backup carries them and a restore
 * brings them back. Only these keys exist (the host refuses any other, PlayerData.UiPrefs, where each says whether it syncs
 * to the player's other PCs or stays on this one); this page's own storage holds only a copy for the first frame.
 */
export const UI_PREF_KEYS = ['discord', 'discordCamera', 'discordPerformance', 'driverBranch', 'wearing', 'shadersOrder', 'goLiveQuality', 'goLivePreview', 'voiceChat'] as const
export type UiPrefKey = (typeof UI_PREF_KEYS)[number]

/** Where an older Exo kept each choice in this page's own storage, and how it was written there. */
const BEFORE: Partial<Record<UiPrefKey, { key: PageKey; read: (raw: string) => unknown }>> = {
  discord: { key: 'exo.discordPrefs', read: raw => withoutCamera(JSON.parse(raw) as unknown) },
  discordCamera: { key: 'exo.discordPrefs', read: raw => (JSON.parse(raw) as { cameraId?: unknown }).cameraId ?? undefined },
  discordPerformance: { key: 'exo.discordPerformance', read: raw => raw === '1' },
  driverBranch: { key: 'exo.driverBranch', read: raw => (raw === 'studio' ? 'studio' : 'game') },
  wearing: { key: 'exo.devices.headphones', read: raw => JSON.parse(raw) as unknown },
  shadersOrder: { key: 'exo.shadersOrder.2', read: raw => JSON.parse(raw) as unknown },
  goLiveQuality: { key: 'exo.discord.goLive', read: raw => JSON.parse(raw) as unknown },
  goLivePreview: { key: 'exo.discord.goLivePreview', read: raw => raw !== 'hidden' },
  voiceChat: { key: 'exo.discord.voiceChat', read: raw => raw !== 'off' },
}

function withoutCamera(value: unknown): unknown {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return value
  const { cameraId: _camera, ...rest } = value as Record<string, unknown>
  return rest
}

type Prefs = Partial<Record<UiPrefKey, unknown>>
/** How the page reaches Exo's settings: a change (settings.set's uiPrefs), and a move from old storage (uiPrefsMoved). Each answers Exo's choices after it. */
export type UiPrefsHost = {
  save: (patch: Prefs) => Promise<{ uiPrefs?: Record<string, unknown> }>
  move: (patch: Prefs) => Promise<{ uiPrefs?: Record<string, unknown> }>
}

const listeners = new Set<() => void>()
let host: UiPrefsHost | null = null
let waiting: Prefs = {}

function readCopy(): Prefs {
  try {
    const saved: unknown = JSON.parse(pageGet('exo.uiPrefs') ?? '{}')
    return saved && typeof saved === 'object' && !Array.isArray(saved) ? pick(saved as Record<string, unknown>) : {}
  } catch { return {} }
}

function pick(from: Record<string, unknown>): Prefs {
  const kept: Prefs = {}
  for (const key of UI_PREF_KEYS) if (from[key] !== undefined && from[key] !== null) kept[key] = from[key]
  return kept
}

let prefs: Prefs = readCopy()

function keepCopy() {
  pageSet('exo.uiPrefs', JSON.stringify(prefs))
}

/** A choice as last saved; undefined when the player never made it (the caller's default stands). */
export function uiPref(key: UiPrefKey): unknown {
  return prefs[key]
}

/** Saves a choice: at once on this page, then in Exo's settings, one key at a time. A refused save keeps this run's choice. */
export function setUiPref(key: UiPrefKey, value: unknown) {
  prefs = { ...prefs, [key]: value }
  keepCopy()
  listeners.forEach(listener => listener())
  if (host) host.save({ [key]: value }).catch(() => { /* kept for this run; the next save tries again */ })
  else waiting = { ...waiting, [key]: value }
}

export function onUiPrefs(listener: () => void): () => void {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b)

/**
 * Exo's settings arrived: what they hold wins. A choice this page still keeps in its old storage from an Exo that kept it
 * there moves into Exo once, one key at a time, and only where Exo has no value: Exo marks it moved, so it never counts as
 * a newer change than the account's. Its old copy goes only once Exo holds exactly that value; a key Exo already had keeps
 * its old copy untouched. A change made before Exo's settings arrived is sent as the change it is.
 */
export async function loadUiPrefs(saved: Record<string, unknown> | null | undefined, to: UiPrefsHost): Promise<void> {
  host = to
  const fromHost = pick(saved ?? {})
  const moving: Prefs = {}
  for (const key of UI_PREF_KEYS) {
    const before = BEFORE[key]
    if (!before || fromHost[key] !== undefined || waiting[key] !== undefined) continue
    const raw = pageGet(before.key)
    if (raw === null) continue
    try {
      const value = before.read(raw)
      if (value !== undefined) moving[key] = value
    } catch { /* unreadable: the default stands */ }
  }
  const changed = waiting
  waiting = {}
  prefs = { ...fromHost, ...moving, ...changed }
  keepCopy()
  listeners.forEach(listener => listener())
  const moved = new Set<UiPrefKey>()
  await Promise.all([
    ...Object.entries(moving).map(async ([key, value]) => {
      const reply = await to.move({ [key]: value }).catch(() => null)
      if (reply && same(reply.uiPrefs?.[key], value)) moved.add(key as UiPrefKey)
    }),
    ...Object.entries(changed).map(([key, value]) => to.save({ [key]: value }).catch(() => null)),
  ])
  forgetMoved(moved)
}

/** Removes an old copy only once every choice kept in it has moved (Discord's look and its camera share one). */
function forgetMoved(moved: Set<UiPrefKey>) {
  const byCopy = new Map<PageKey, UiPrefKey[]>()
  for (const key of UI_PREF_KEYS) {
    const before = BEFORE[key]
    if (before) byCopy.set(before.key, [...(byCopy.get(before.key) ?? []), key])
  }
  for (const [copy, keys] of byCopy)
    if (keys.some(key => moved.has(key)) && keys.every(key => moved.has(key) || !wasIn(copy, key))) pageRemove(copy)
}

/** Whether the old copy held a value for this choice at all. */
function wasIn(copy: PageKey, key: UiPrefKey): boolean {
  const raw = pageGet(copy)
  if (raw === null) return false
  try { return BEFORE[key]!.read(raw) !== undefined } catch { return false }
}

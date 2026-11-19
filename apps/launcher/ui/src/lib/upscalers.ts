import type { DlssStatus, DlssStatusItem } from './host'

/** Whole-game conditions that switch every action off. */
export type UpscalerGate = {
  gameRunning?: boolean
  installing?: boolean
}

/** One upscaler type, whatever folders or APIs its files sit in. */
export type UpscalerRow = {
  key: string
  label: string
  /** mod: every copy is an upscaler mod's file, which Exo leaves alone. */
  state: 'current' | 'outdated' | 'newer' | 'unknown' | 'mod'
  /** The version the row's state is about: the newest release when current, the old file when outdated. */
  version: string | null
  /** The mod whose file it is, when the row is a mod's. */
  mod?: string | null
  /** Every copy of the file in the game. */
  keys: string[]
}

export function gateReason(gate?: UpscalerGate): string | null {
  if (gate?.gameRunning) return 'Close the game first'
  if (gate?.installing) return 'Wait for the download to finish'
  return null
}

// Worst first: one outdated copy makes the whole row outdated; a row is a mod's only when every copy is.
const SEVERITY: UpscalerRow['state'][] = ['outdated', 'unknown', 'newer', 'current', 'mod']

/**
 * One row per label, in the order the host lists them: a game that ships a
 * file in two folders, or XeSS for DirectX 11 and 12, is one row whose state
 * is the worst of its files. Where the files are never reaches the sheet.
 */
function upscalerRows(items: DlssStatusItem[] | null | undefined): UpscalerRow[] {
  const rows = new Map<string, UpscalerRow>()
  for (const item of items ?? []) {
    const label = item.displayName || item.fileName
    const state = item.state ?? 'unknown'
    const version = rowVersion(item, state)
    const mod = state === 'mod' ? item.mod || 'Mod' : null
    const row = rows.get(label.toLowerCase())
    if (!row) {
      rows.set(label.toLowerCase(), { key: label.toLowerCase(), label, state, version, mod, keys: [item.key] })
      continue
    }
    row.keys.push(item.key)
    if (SEVERITY.indexOf(state) < SEVERITY.indexOf(row.state)) Object.assign(row, { state, version, mod })
  }
  return [...rows.values()]
}

/**
 * Current files read as the release they match (310.9.1); others as the file
 * itself, cut to as many parts as the release's number has (310.3.0.0 reads
 * 310.3.0 next to DLSS's three-part numbers, XeSS keeps its four).
 */
function rowVersion(item: DlssStatusItem, state: UpscalerRow['state']): string | null {
  const latest = item.latestVersion || null
  if (state === 'current' && latest) return latest
  const file = item.currentVersion || item.fileVersion
  if (!file) return null
  const parts = file.split('.')
  const keep = latest ? latest.split('.').length : parts.length
  return parts.slice(0, Math.max(keep, 1)).join('.')
}

export type UpscalerView = {
  rows: UpscalerRow[]
  /** The anti-cheat's name, for the confirmation. */
  antiCheatName: string | null
  /** never (Riot, Vanguard), available (the user may allow it), given; null without anti-cheat. */
  consent: 'never' | 'available' | 'given' | null
  /** Anti-cheat without consent: every action but putting originals back is off. */
  locked: boolean
  /** Files Update and Restore act on: every copy, not one per row. */
  updatable: number
  restorable: number
  /** The upscaler mods in the game, by name: their files are left alone. */
  mods: string[]
}

export function upscalerView(status: DlssStatus | null | undefined): UpscalerView {
  const items = status?.items ?? []
  const antiCheatName = status?.antiCheat || null
  // A host that names anti-cheat without a consent state offers no opt-in.
  const consent = antiCheatName ? status?.consent ?? 'never' : null
  return {
    rows: upscalerRows(items),
    antiCheatName,
    consent,
    locked: !!antiCheatName && consent !== 'given',
    updatable: items.filter((item) => item.canUpdate === true).length,
    restorable: items.filter((item) => item.canRestore === true).length,
    mods: [...new Set(items.filter(item => item.state === 'mod').map(item => item.mod || 'Mod'))],
  }
}

const FAMILIES = ['DLSS', 'FSR', 'XeSS'] as const

export function compareVersions(a: string, b: string): number {
  const left = a.split('.').map(Number)
  const right = b.split('.').map(Number)
  for (let i = 0; i < Math.max(left.length, right.length); i++) {
    const diff = (left[i] || 0) - (right[i] || 0)
    if (diff) return diff
  }
  return 0
}

/**
 * Upscalers by family, DLSS, FSR, XeSS (the game page counts its updates this way):
 * its worst state, and the version that state is about (the oldest outdated
 * file, else the newest current one).
 */
export function upscalerFamilies(rows: UpscalerRow[]): UpscalerRow[] {
  const families = new Map<string, UpscalerRow>()
  for (const row of rows) {
    const name = FAMILIES.find(family => row.label.startsWith(family)) ?? row.label
    const family = families.get(name)
    if (!family) {
      families.set(name, { key: name.toLowerCase(), label: name, state: row.state, version: row.version, keys: [...row.keys] })
      continue
    }
    family.keys.push(...row.keys)
    const worse = SEVERITY.indexOf(row.state) < SEVERITY.indexOf(family.state)
    const same = row.state === family.state && row.version && family.version
    if (worse) Object.assign(family, { state: row.state, version: row.version })
    else if (same) {
      const order = compareVersions(row.version!, family.version!)
      if (row.state === 'outdated' ? order < 0 : order > 0) family.version = row.version
    }
  }
  return [...families.values()]
}

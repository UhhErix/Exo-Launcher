import { useSyncExternalStore } from 'react'
import { host, onHostEvent } from './host'
import { notify } from './notify'
import type { NotesRequest } from './notes'
import { report } from './report'
import { errorText } from './errorWords'

/** What Exo Capture is doing (capture.state). */
export type CaptureState = {
  /** The add-on is installed. */
  present: boolean
  running: boolean
  /** Instant replay is holding the last seconds in memory. */
  buffering: boolean
  /** Measured playable replay, not the requested length. Omitted by older add-ons. */
  replayRetainedMs?: number | null
  replayLimitSeconds?: number | null
  recording: boolean
  /** The replay or recording holds the call's voices now (only with Record the call's voices on). */
  holdsCall?: boolean
  recordingMs: number
  codec?: string | null
  encoder?: string | null
  width?: number | null
  height?: number | null
  fps?: number | null
  hdr: boolean
  error?: string | null
  /** False when another app already holds those keys. */
  hotkeys: { saveReplay: boolean; record: boolean; snip: boolean; printScreen: boolean }
}

export type CaptureSettings = {
  quality: number
  replaySeconds: number
  /** 60, or 120 where the screen runs that fast. */
  maxFps: number
  replayMode: 'games' | 'always' | 'off'
  cursor: boolean
  saveReplayKeys: string
  recordKeys: string
  snipKeys: string
  printScreen: boolean
  /** edit: a new snip opens in the editor (not one taken over a game); copy: it is only copied and saved. */
  afterSnip: 'edit' | 'copy'
  folder: string
  keepGb: number
  keepDays: number
  /** New recordings are re-encoded to smaller files that look the same, while no game runs. */
  autoShrink: boolean
  /** The call's voices on a track of their own and in the mix, and the call told it's recorded; off, they're left out. */
  callVoices: boolean
}

/** One file in the captures folder. */
export type CaptureItem = {
  id: string
  kind: 'video' | 'image'
  /** The game's folder name (its title), or Desktop. */
  game: string
  gameId?: string | null
  name: string
  created: string
  size: number
  durationMs?: number | null
  favourite: boolean
  url: string
  thumb?: string | null
  /** A trim, an export or a reel made from other clips; a highlight reel leaves it out. */
  derived?: boolean
}

export type CaptureExport = 'trim' | 'discord' | 'youtube' | 'vertical' | 'gif' | 'webm'

/** capture.reel: a highlight reel waiting for a game to end, working (percent), done (item), stopped by the user, or failed (message; wait: a game is why). */
export type ReelEvent = { state: 'waiting' | 'working' | 'done' | 'stopped' | 'failed'; percent: number; message?: string | null; wait: boolean; item?: CaptureItem | null }

type Snapshot = { state: CaptureState | null; settings: CaptureSettings | null; at: number }
let snapshot: Snapshot = { state: null, settings: null, at: 0 }
const listeners = new Set<() => void>()
let started = false

function publish(next: Partial<Snapshot>) {
  snapshot = { ...snapshot, ...next, at: performance.now() }
  for (const listener of listeners) listener()
}

function start() {
  if (started) return
  started = true
  onHostEvent('capture.state', state => publish({ state }))
  onHostEvent('capture.reel', onReelEvent)
  void host.captureStatus()
    .then(result => { if (result.ok && result.state) publish({ state: result.state, settings: result.settings ?? null }) })
    .catch((error: unknown) => report(error, 'capture.status', { level: 'warn' }))
}

function subscribe(listener: () => void) {
  start()
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

/** Exo Capture's state and settings, kept current by its events. */
export function useCapture(): Snapshot {
  return useSyncExternalStore(subscribe, () => snapshot)
}

/** Saves a settings patch; the reply carries every setting as saved and the add-on's state. */
export async function saveCaptureSettings(patch: Partial<CaptureSettings>): Promise<string | null> {
  try {
    const result = await host.captureSettings(patch)
    if (result.settings) publish({ settings: result.settings, state: result.state ?? snapshot.state })
    return result.ok ? null : errorText(result.message, 'Exo couldn’t save that. Try again.')
  } catch (error) {
    return errorText(error, 'Exo couldn’t save that. Try again.')
  }
}

/** "Ctrl+Alt+F10" as the keys read, for menus. */
export function keysLabel(keys: string | undefined): string {
  return (keys ?? '').split('+').join(' + ')
}

/** 0:42 or 1:02:03. */
export function clock(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60), s = total % 60
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`
}

// The Captures sheet opens over any room; the title bar, a game page and a new
// snip (straight into its editor) all open it here, the Capture menu straight
// into its settings, and Notes (lib/notes.ts) on its notes.
type SheetRequest = { open: boolean; gameId?: string | null; itemId?: string | null; edit?: boolean; settings?: boolean; notes?: NotesRequest; seq: number }
let sheet: SheetRequest = { open: false, seq: 0 }
const sheetListeners = new Set<() => void>()

export function openCaptures(options: { gameId?: string | null; itemId?: string | null; edit?: boolean; settings?: boolean; notes?: NotesRequest } = {}) {
  sheet = { open: true, ...options, seq: sheet.seq + 1 }
  for (const listener of sheetListeners) listener()
}

/** Closes the sheet as it is (same seq), so it doesn't blank and read the folder again while it slides away. */
export function closeCaptures() {
  if (!sheet.open) return
  sheet = { ...sheet, open: false }
  for (const listener of sheetListeners) listener()
}

export function useCapturesSheet(): SheetRequest {
  return useSyncExternalStore(listener => { sheetListeners.add(listener); return () => { sheetListeners.delete(listener) } }, () => sheet)
}

/**
 * A highlight reel being made (capture.reel): clips of a game's session, or the ones picked, joined into one new clip.
 * `ids` are the clips it was asked for (so Wait can ask again); `message` is why one did not start or stop, `canWait` that a game is
 * why. It lives here, not in the sheet, so the reel goes on and is heard when the sheet is closed.
 */
export type ReelState = {
  phase: 'idle' | 'waiting' | 'working'
  /** 0 to 100 while working. */
  percent: number
  ids: string[]
  message: string | null
  canWait: boolean
}

let reel: ReelState = { phase: 'idle', percent: 0, ids: [], message: null, canWait: false }
const reelListeners = new Set<() => void>()
const reelDoneListeners = new Set<(item: CaptureItem) => void>()

function setReel(next: Partial<ReelState>) {
  reel = { ...reel, ...next }
  for (const listener of reelListeners) listener()
}

export function useReel(): ReelState {
  start()
  return useSyncExternalStore(listener => { reelListeners.add(listener); return () => { reelListeners.delete(listener) } }, () => reel)
}

/** The sheet's: a reel that finishes while it shows opens in its viewer, else a notice offers it. */
export function onReelDone(listener: (item: CaptureItem) => void): () => void {
  reelDoneListeners.add(listener)
  return () => { reelDoneListeners.delete(listener) }
}

/** What the last press said is forgotten when the sheet opens. */
export function clearReelMessage() {
  if (reel.message !== null || reel.canWait) setReel({ message: null, canWait: false })
}

/** Makes one clip of these (any order; the host puts them in time order). wait: hold it until the game that is running ends. */
export async function makeReel(ids: string[], wait = false): Promise<void> {
  setReel({ ids, message: null, canWait: false })
  const failed = 'Exo couldn’t make the reel. Try again.'
  try {
    const result = await host.captureReel(ids, wait)
    if (!result.ok) setReel({ message: errorText(result.message, failed), canWait: result.wait === true })
    else if (reel.phase === 'idle') setReel({ phase: wait ? 'waiting' : 'working', percent: 0 })
  } catch (error) {
    setReel({ message: errorText(error, failed), canWait: false })
  }
}

export function cancelReel() {
  void host.captureReelCancel().catch((error: unknown) => report(error, 'capture.reel.cancel', { level: 'warn' }))
}

function onReelEvent(event: ReelEvent) {
  switch (event.state) {
    case 'waiting': setReel({ phase: 'waiting', percent: 0, message: null, canWait: false }); break
    case 'working': setReel({ phase: 'working', percent: event.percent, message: null, canWait: false }); break
    case 'stopped': setReel({ phase: 'idle', percent: 0 }); break
    case 'failed': setReel({ phase: 'idle', percent: 0, message: errorText(event.message, 'Exo couldn’t make the reel. Try again.'), canWait: event.wait }); break
    case 'done': {
      setReel({ phase: 'idle', percent: 0 })
      const item = event.item
      if (!item) break
      if (sheet.open && reelDoneListeners.size > 0) for (const listener of reelDoneListeners) listener(item)
      else notify({ id: 'capture-reel', text: `${item.name} is ready.`, tone: 'good', action: { label: 'Open', onPress: () => openCaptures({ itemId: item.id }) } })
      break
    }
  }
}

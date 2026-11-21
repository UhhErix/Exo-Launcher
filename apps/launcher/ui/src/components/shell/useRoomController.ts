import { startTransition, useCallback, useEffect, useRef, useState } from 'react'
import { host, hostQuiet, type LauncherSettings } from '../../lib/host'
import { suppressHover } from '../../lib/hoverGate'
import type { MusicService } from '../../lib/music'
import { pageGet, pageSet } from '../../lib/pageStore'
import { transition, visible, type Morph } from '../../lib/viewTransition'
import { clearDialogOpener } from '../ui/Dialog'
import { EJECTABLE_ROOMS } from './BootGate'
import { ROOM_ORDER, type View } from './TitleBar'

const MUSIC_USED_KEY = 'exo.music.usedAt'
const ROOM_IDLE_MS = 3 * 60 * 1000
const PREWARM_MS = 8000
const BOOT_PRELOAD_AFTER_MS = 450
const BOOT_PRELOAD_CAP_MS = 2500
const BOOT_WARM_AFTER_MS = 8000
const ROOM_OPEN_CAP_MS = 2500
const MUSIC_OPEN_CAP_MS = 4500
const ROOM_QUIET_MS = 150

const LAZY_ROOMS: ReadonlyMap<View, (() => Promise<unknown>) | null> = new Map<View, (() => Promise<unknown>) | null>([
  ['store', () => import('../store/StoreRoom')],
  ['friends', () => import('../FriendsRoomRoute')],
  ['music', () => import('../music/NativeMusicRoom')],
  ['exo-ai', () => import('../gpu/ExoAiRoom')],
  ['apps', () => import('../apps/AppsRoom')],
  ['system', () => import('../gpu/PcRoom')],
])

const nextFrame = () => new Promise<void>((resolve) => { requestAnimationFrame(() => resolve()); window.setTimeout(resolve, 50) })
const wait = (ms: number) => new Promise<void>((resolve) => window.setTimeout(resolve, Math.max(0, ms)))

function musicUsedLately() {
  try { return Date.now() - Number(pageGet(MUSIC_USED_KEY) ?? 0) < 7 * 24 * 3600_000 } catch { return false }
}

/**
 * A room opened the first time is built out of sight: its code, its host reads, and its first-screen
 * decoded bitmaps, so it appears whole at once rather than filling in incrementally.
 */
async function roomReady(room: View) {
  const since = performance.now()
  const until = since + (room === 'music' ? MUSIC_OPEN_CAP_MS : ROOM_OPEN_CAP_MS)
  const code = LAZY_ROOMS.get(room)
  if (code) await Promise.race([code().catch(() => { }), wait(until - performance.now())])
  await nextFrame()
  await nextFrame()
  await hostQuiet(ROOM_QUIET_MS, since, until)
  await nextFrame()
  const box = document.querySelector(`[data-room="${room}"]`)
  const first = [...(box?.querySelectorAll('img') ?? [])].filter((img) => {
    const at = img.getBoundingClientRect()
    return at.width > 0 && at.bottom > 0 && at.top < window.innerHeight && at.right > 0 && at.left < window.innerWidth
  }).slice(0, 48)
  for (const img of first) if (img.loading === 'lazy') img.loading = 'eager'
  await Promise.race([Promise.allSettled(first.map((img) => img.decode())), wait(until - performance.now())])
}

/**
 * Preloads light rooms behind the boot splash and records active warming window.
 */
function useBootPreload(
  booting: boolean,
  settings: LauncherSettings | null,
  musicService: MusicService,
  setOpenedRooms: (update: (rooms: ReadonlySet<View>) => ReadonlySet<View>) => void,
) {
  const preloaded = useRef<Promise<unknown> | null>(null)
  const [warming, setWarming] = useState(false)
  useEffect(() => {
    if (booting || !warming) return
    const timer = window.setTimeout(() => setWarming(false), BOOT_WARM_AFTER_MS)
    return () => clearTimeout(timer)
  }, [booting, warming])
  useEffect(() => {
    if (!booting || settings?.onboardingComplete !== true || preloaded.current) return
    preloaded.current = new Promise((resolve) => window.setTimeout(() => {
      setWarming(true)
      setOpenedRooms((current) => new Set<View>([...current, 'devices', 'settings', ...(musicUsedLately() ? ['music' as const] : [])]))
      resolve(Promise.race([
        Promise.allSettled([
          import('../DevicesRoom'),
          import('../SettingsPanel').then((module) => module.preloadSettingsRoom()),
          import('./GameSheet'),
          import('../gpu/ExoAiRoom'),
          import('../gpu/PcRoom'),
          import('../blocks/command-menu-1'),
        ]),
        new Promise<void>((done) => window.setTimeout(done, BOOT_PRELOAD_CAP_MS)),
      ]))
    }, BOOT_PRELOAD_AFTER_MS))
  }, [booting, settings?.onboardingComplete, musicService, setOpenedRooms])
  return { preloaded, warming }
}

/** Pre-mounts deferred rooms during idle moments after startup so first opens don't stutter. */
function useIdleRooms(
  booting: boolean,
  setOpenedRooms: (update: (rooms: ReadonlySet<View>) => ReadonlySet<View>) => void,
) {
  useEffect(() => {
    if (booting) return
    const order: View[] = ['store', 'friends', 'exo-ai', 'apps', 'system']
    let idle = 0
    const next = () => {
      const room = order.shift()
      if (!room) return
      setOpenedRooms((current) => (current.has(room) ? current : new Set<View>([...current, room])))
      idle = requestIdleCallback(next, { timeout: 5000 })
    }
    const start = window.setTimeout(() => { idle = requestIdleCallback(next, { timeout: 5000 }) }, 2500)
    return () => { window.clearTimeout(start); cancelIdleCallback(idle) }
  }, [booting, setOpenedRooms])
}

/** Logs blank covers if coming back to Library left any visible cover undrawn, plus Settings paint timing. */
function logBlankCoversAfterRoom(from: View) {
  window.setTimeout(() => {
    if (document.hidden) return
    const pane = document.querySelector<HTMLElement>('main.exo-library-pane')
    if (!pane) return
    const viewport = pane.getBoundingClientRect()
    const tiles = [...pane.querySelectorAll<HTMLElement>('.exo-tile-hit[data-game-id]')].filter((tile) => {
      const rect = tile.getBoundingClientRect()
      return rect.width > 0 && rect.bottom > viewport.top && rect.top < viewport.bottom
    })
    const blank = tiles.flatMap((tile) => {
      const image = tile.querySelector<HTMLImageElement>('img.exo-cover-front:not([data-under])')
      if (!image || (image.complete && image.naturalWidth > 0 && image.style.opacity === '1')) return []
      const origin = image.src.startsWith('https://thumbs.') ? 'thumb' : image.src.startsWith('https://covers.') ? 'file' : 'remote'
      return [`${tile.dataset.gameId}:${origin}:${image.complete ? `w${image.naturalWidth}` : 'loading'}`]
    })
    if (!blank.length) return
    void host.log({
      level: 'warn',
      message: `Library covers blank 1 s after leaving ${from}: ${blank.length} of ${tiles.length} (${blank.slice(0, 8).join(', ')})`,
    }).catch(() => { })
  }, 1000)
}

/**
 * Owns shell room routing, prewarming, ViewTransition morphing, idle room ejection, and top-level shortcuts.
 */
export function useRoomController({
  booting,
  settings,
  musicService,
  libraryPane,
  commandOpen,
  activateKeyOpen,
  hasDepPrompt,
  closeGamePage,
  closeGameSheet,
  onToggleCommandMenu,
}: {
  booting: boolean
  settings: LauncherSettings | null
  musicService: MusicService
  libraryPane: 'shelf' | 'game'
  commandOpen: boolean
  activateKeyOpen: boolean
  hasDepPrompt: boolean
  closeGamePage: () => void
  closeGameSheet: () => void
  onToggleCommandMenu: () => void
}) {
  const [view, setView] = useState<View>('library')
  const settingsReturnView = useRef<View>('library')
  const [openedRooms, setOpenedRooms] = useState<ReadonlySet<View>>(() => new Set<View>(['library']))
  const openedRef = useRef(openedRooms)
  openedRef.current = openedRooms
  const [opening, setOpening] = useState<View | null>(null)
  const openingRef = useRef<View | null>(null)
  const goToViewRef = useRef<(next: View, morph?: Morph, alongside?: () => void) => Promise<void>>(() => Promise.resolve())
  const viewRequestedAt = useRef(0)

  useEffect(() => {
    suppressHover()
    clearDialogOpener()
  }, [view, libraryPane])

  const goToView = useCallback((next: View, morph?: Morph, alongside?: () => void): Promise<void> => {
    viewRequestedAt.current = performance.now()
    suppressHover()
    clearDialogOpener()
    closeGamePage()
    if (next === view) {
      if (openingRef.current !== null) { openingRef.current = null; setOpening(null) }
      return Promise.resolve()
    }
    if (LAZY_ROOMS.has(next) && !openedRef.current.has(next)) {
      openingRef.current = next
      setOpening(next)
      setOpenedRooms((current) => new Set<View>([...current, next]))
      return roomReady(next).then(() => {
        if (openingRef.current !== next) return
        openingRef.current = null
        setOpening(null)
        return goToViewRef.current(next, morph, alongside)
      })
    }
    openingRef.current = null
    setOpening(null)
    if (next === 'settings') settingsReturnView.current = view
    const room = next !== 'settings' && view !== 'settings'
    const stream = () => visible('.exo-mini-stream') ?? visible('.exo-dc-stream-video')
    const from = stream()
    return transition(
      () => { setView(next); alongside?.() },
      {
        kind: room ? 'room' : next === 'settings' ? 'none' : 'fade',
        back: room && ROOM_ORDER.indexOf(next) < ROOM_ORDER.indexOf(view),
        morph: [morph ?? [], { name: from?.classList.contains('exo-mini-stream') ? 'exo-dc-mini' : 'exo-dc-stream', from, to: stream }].flat(),
      },
    )
  }, [closeGamePage, view])
  goToViewRef.current = goToView

  const { preloaded, warming } = useBootPreload(booting, settings, musicService, setOpenedRooms)
  useIdleRooms(booting, setOpenedRooms)

  useEffect(() => {
    if (view !== 'music') return
    try { pageSet(MUSIC_USED_KEY, String(Date.now())) } catch { /* convenience only */ }
  }, [view])

  const under = view === 'settings' ? settingsReturnView.current : view
  const underRef = useRef(under)
  underRef.current = under
  const viewRef = useRef(view)
  viewRef.current = view
  const roomHiddenAt = useRef(new Map<View, number>())

  useEffect(() => {
    const shown = new Set<View>([underRef.current, viewRef.current])
    for (const room of EJECTABLE_ROOMS) {
      if (shown.has(room)) roomHiddenAt.current.delete(room)
      else if (openedRef.current.has(room)) roomHiddenAt.current.set(room, roomHiddenAt.current.get(room) ?? Date.now())
    }
  }, [under, view])

  useEffect(() => {
    const sweep = setInterval(() => {
      const now = Date.now()
      setOpenedRooms((current) => {
        let next: Set<View> | null = null
        for (const room of EJECTABLE_ROOMS) {
          const since = roomHiddenAt.current.get(room)
          if (since == null || now - since < ROOM_IDLE_MS || !current.has(room)) continue
          if (room === underRef.current || room === viewRef.current) continue
          next ??= new Set(current)
          next.delete(room)
          roomHiddenAt.current.delete(room)
        }
        return next ?? current
      })
    }, 30_000)
    return () => clearInterval(sweep)
  }, [])

  const [prewarm, setPrewarm] = useState<View | null>(null)
  const prewarmTimer = useRef(0)
  const warmRoom = useCallback((room: View) => {
    if (room === viewRef.current || room === underRef.current || openingRef.current !== null) return
    if (!openedRef.current.has(room)) {
      if (!LAZY_ROOMS.has(room)) return
      setOpenedRooms((current) => new Set<View>([...current, room]))
    }
    if (roomHiddenAt.current.has(room) || !openedRef.current.has(room)) roomHiddenAt.current.set(room, Date.now())
    window.clearTimeout(prewarmTimer.current)
    prewarmTimer.current = window.setTimeout(() => startTransition(() => setPrewarm(null)), PREWARM_MS)
    startTransition(() => setPrewarm(room))
  }, [])
  useEffect(() => () => window.clearTimeout(prewarmTimer.current), [])

  const toggleSettings = useCallback(() => {
    if (view === 'settings') void transition(() => setView(settingsReturnView.current))
    else void goToView('settings')
  }, [goToView, view])

  const previousView = useRef<View>(view)
  const settingsOpens = useRef(0)
  useEffect(() => {
    const from = previousView.current
    previousView.current = view
    if (view === 'library' && from !== 'library') logBlankCoversAfterRoom(from)
    if (view === 'settings' && from !== 'settings') {
      const requestedAt = viewRequestedAt.current
      const open = ++settingsOpens.current
      requestAnimationFrame(() => window.setTimeout(() => void host.log({
        level: 'info',
        message: `PERF settings-open paintMs=${Math.round(performance.now() - requestedAt)} open=${open} from=${from}`,
      }).catch(() => { })))
    }
  }, [view])

  useEffect(() => {
    setOpenedRooms((current) => {
      if (current.has(view)) return current
      const next = new Set(current)
      next.add(view)
      return next
    })
  }, [view])

  // Keyboard navigation shortcuts (Ctrl+K, Ctrl+1..9/,, Escape).
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        onToggleCommandMenu()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onToggleCommandMenu])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!e.ctrlKey || e.altKey || e.shiftKey || e.metaKey || e.defaultPrevented || e.isComposing) return
      if (commandOpen || activateKeyOpen) return
      if (!/^[1-9,]$/.test(e.key)) return
      const next = e.key === ',' ? 'settings' : ROOM_ORDER[Number(e.key) - 1]
      if (!next) return
      e.preventDefault()
      if (next !== view) void goToView(next)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [commandOpen, activateKeyOpen, goToView, view])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || e.isComposing || commandOpen || activateKeyOpen || hasDepPrompt || e.key !== 'Escape') return
      if (view === 'settings') {
        e.preventDefault()
        void transition(() => setView(settingsReturnView.current))
        document.querySelector<HTMLElement>('.exo-settings-btn')?.focus({ preventScroll: true })
        return
      }
      if (libraryPane === 'game') closeGameSheet()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [view, libraryPane, commandOpen, activateKeyOpen, hasDepPrompt, closeGameSheet])

  return {
    view,
    under,
    openedRooms,
    opening,
    prewarm,
    warming,
    preloaded,
    goToView,
    warmRoom,
    toggleSettings,
  }
}

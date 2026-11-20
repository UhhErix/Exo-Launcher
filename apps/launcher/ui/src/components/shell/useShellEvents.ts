import { useEffect, type MutableRefObject } from 'react'
import { openCaptures } from '../../lib/capture'
import { setPendingOpen } from '../../lib/discord'
import { host, onHostEvent, type Game, type InstallProgress, type PausedDownload, type StoreStatus } from '../../lib/host'
import {
  beginGameRead,
  findLibraryGame,
  mergeExactGame,
  mergeExactGameKeepingRunState,
  mergeLibraryUpdate,
  setExactRunState,
} from '../../lib/library'
import { startMouseRelay } from '../../lib/mouseRelay'
import { onMouseNotice } from '../../lib/mouseState'
import { MUSIC_LINK } from '../../lib/music'
import { watchNotesOpen } from '../../lib/notes'
import { dismissNotice, notify as notice } from '../../lib/notify'
import { pageGet, pageSet } from '../../lib/pageStore'
import { report } from '../../lib/report'
import type { SettingsSectionId } from '../../lib/settingsSections'
import type { PcLink } from '../gpu/pcSections'
import type { View } from './TitleBar'
import type { ActionStatus } from './useActionStatus'

const PROGRESS_FLUSH_MS = 250

type LaunchStatusEvent = { message?: string; phase?: string; ok?: boolean; gameId?: string }

function announceBackupStop(sentence: string, since: string) {
  if (pageGet('exo.backupStopTold') === since) return
  pageSet('exo.backupStopTold', since)
  notice({ tone: 'bad', text: sentence })
}

export async function tellBackupStop() {
  const note = (await host.backupStatus().catch(() => null))?.note
  if (note?.stopped && note.since) announceBackupStop(note.sentence, note.since)
}

function notifyAway(p: InstallProgress, games: Game[]) {
  const away = document.hidden || document.documentElement.dataset.exoInactive === 'true'
  if (!away || games.some((g) => g.isRunning)) return
  const title = findLibraryGame(games, p.gameId)?.title ?? 'Exo'
  const line = p.status || (p.phase === 'completed' ? 'Ready to play.' : p.phase === 'failed' ? 'The download failed.' : 'The download was canceled.')
  void host.appNotify(title, line).catch(() => { })
}

/** Keeps the latest value and applies it at most every `ms`; `now` applies it at once. */
function latestEvery<T>(apply: (value: T) => void, ms: number) {
  let latest: T | undefined
  let timer = 0
  let flushedAt = 0
  const flush = () => {
    timer = 0
    flushedAt = performance.now()
    apply(latest as T)
  }
  return {
    push(value: T, now: boolean) {
      latest = value
      if (now) {
        window.clearTimeout(timer)
        flush()
      } else if (!timer) {
        timer = window.setTimeout(flush, Math.max(0, ms - (performance.now() - flushedAt)))
      }
    },
    cancel: () => {
      window.clearTimeout(timer)
      timer = 0
    },
  }
}

/**
 * Startup milestone: every card on the first screen shows its art (or has no art to show).
 */
export function logFirstCoversPainted(bootAt: number) {
  const deadline = performance.now() + 10_000
  const check = () => {
    const tiles = [...document.querySelectorAll<HTMLElement>('.exo-tile-media')].filter((tile) => {
      const rect = tile.getBoundingClientRect()
      return rect.width > 0 && rect.bottom > 0 && rect.top < window.innerHeight
    })
    const waiting = tiles.some((tile) => {
      const image = tile.querySelector('img')
      return !!image && !(image.complete && image.naturalWidth > 0 && image.style.opacity === '1')
    })
    if (tiles.length > 0 && !waiting) {
      requestAnimationFrame(() => void host.log({
        level: 'info',
        message: `PERF library-covers-painted after ${Date.now() - bootAt} ms covers=${tiles.length}`,
      }).catch(() => { }))
    } else if (performance.now() < deadline && !document.hidden) {
      window.setTimeout(check, 50)
    }
  }
  check()
}

/**
 * Registers all shell-level host events, cross-room window events, and initial settings/store loading.
 */
export function useShellHostEvents({
  gamesRef,
  selectedIdRef,
  libraryPushes,
  progressVersion,
  progressFlushRef,
  stoppedTransfers,
  setGames,
  setRecent,
  setStores,
  setStoreMatrixReady,
  setQueuedIds,
  setProgress,
  setTransferFailures,
  clearTransferFailure,
  setActivateKeyOpen,
  setGpuJump,
  setSettingsSection,
  setStatus,
  notify,
  loadLibrary,
  loadSettings,
  goToView,
  openGamePage,
}: {
  gamesRef: MutableRefObject<Game[]>
  selectedIdRef: MutableRefObject<string | null>
  libraryPushes: MutableRefObject<number>
  progressVersion: MutableRefObject<number>
  progressFlushRef: MutableRefObject<{ cancel: () => void } | null>
  stoppedTransfers: MutableRefObject<Set<string>>
  setGames: (update: Game[] | ((items: Game[]) => Game[])) => void
  setRecent: (update: (current: string[] | undefined) => string[] | undefined) => void
  setStores: (stores: StoreStatus[]) => void
  setStoreMatrixReady: (ready: boolean) => void
  setQueuedIds: (ids: string[]) => void
  setProgress: (value: InstallProgress | null) => void
  setTransferFailures: (update: (current: Record<string, string>) => Record<string, string>) => void
  clearTransferFailure: (gameId: string) => void
  setActivateKeyOpen: (open: boolean) => void
  setGpuJump: (jump: { tab: PcLink } | null) => void
  setSettingsSection: (section: { id: SettingsSectionId } | null) => void
  setStatus: (message: string | null, gameId?: string | null, sticky?: boolean, tone?: ActionStatus['tone']) => void
  notify: (message: string) => void
  loadLibrary: (force?: boolean, silent?: boolean, full?: boolean, quietErrors?: boolean) => Promise<void>
  loadSettings: () => Promise<void>
  goToView: (next: View) => Promise<void>
  openGamePage: (id: string, motion?: 'pointer' | 'instant') => void
}) {
  useEffect(() => {
    void loadSettings()
    void host
      .storesMatrix()
      .then(setStores)
      .catch((e: unknown) => report(e, 'LauncherApp.storesMatrix'))
      .finally(() => setStoreMatrixReady(true))

    const offLaunch = onHostEvent('launch.status', (data) => {
      const d = (data ?? {}) as LaunchStatusEvent
      if (d.message) setStatus(d.message, d.gameId ?? null)
      if (!d.gameId || (d.phase !== 'running' && d.phase !== 'stopped')) return
      const gameId = d.gameId
      if (d.phase === 'running') {
        setRecent((current) => current?.[0]?.toLowerCase() === gameId.toLowerCase() ? current
          : [gameId, ...(current ?? []).filter((id) => id.toLowerCase() !== gameId.toLowerCase())])
        const launched = gameId.toLowerCase()
        const now = new Date().toISOString()
        setGames((items) => items.map((g) => g.id.toLowerCase() === launched || g.variants?.some((v) => v.id.toLowerCase() === launched)
          ? { ...g, lastPlayedUtc: now } : g))
      }
      if (d.phase === 'stopped') {
        beginGameRead(gameId)()
        setGames((items) => setExactRunState(items, gameId, false, false))
      }
      const current = beginGameRead(gameId)
      void host.getGame(gameId).then((result) => {
        if (!result.ok || !result.game) return
        const game = result.game
        setGames((items) => current() ? mergeExactGame(items, game) : mergeExactGameKeepingRunState(items, game))
      }).catch((e: unknown) => report(e, 'LauncherApp.launchStatus.getGame'))
    })

    startMouseRelay()
    const offMouse = onMouseNotice(notify)
    const offSale = onHostEvent('store.sale', (sale) => {
      const price = sale.price ?? (sale.final != null && (sale.currency ?? 'USD') === 'USD' ? `$${(sale.final / 100).toFixed(2)}` : 'a lower price')
      notify(`${sale.name} is on sale on Steam: ${price} (-${sale.discount}%).`)
    })
    const offParts = onHostEvent('components.failed', ({ feature }) => notice({ tone: 'bad', text: `${feature} couldn't be set up` }))
    const offOptimized = onHostEvent('game.optimizedAgain', ({ title }) => notice({
      id: `optimized-again-${title}`, tone: 'good', text: `${title}'s update reset its settings. Exo optimized it again.`,
    }))
    const offQueue = onHostEvent('install.queue', (data) => {
      const queue = data as { queuedGameIds?: string[]; pausedDownloads?: PausedDownload[]; held?: string | null }
      if (Array.isArray(queue?.queuedGameIds)) setQueuedIds(queue.queuedGameIds)
    })
    const progressFlush = latestEvery(setProgress, PROGRESS_FLUSH_MS)
    progressFlushRef.current = progressFlush
    const transferEnded = (p: InstallProgress) => {
      if (p.status) setStatus(p.status, p.gameId)
      notifyAway(p, gamesRef.current)
      if (p.phase === 'completed') void loadLibrary(true, true)
    }
    const offProgress = onHostEvent('install.progress', (data) => {
      progressVersion.current++
      const p = data as InstallProgress
      progressFlush.push(p, !p?.isActive)
      if (p?.phase === 'cancelled' && p.gameId) stoppedTransfers.current.add(p.gameId)
      if (p?.phase === 'failed') {
        setTransferFailures((current) => ({ ...current, [p.gameId]: p.status || 'Install failed.' }))
      } else if (p?.gameId && (p.isActive || p.phase === 'completed' || p.phase === 'cancelled')) {
        clearTransferFailure(p.gameId)
      }
      if (!p?.isActive && (p?.phase === 'completed' || p?.phase === 'failed' || p?.phase === 'cancelled')) transferEnded(p)
    })
    const offCovers = onHostEvent('library.updated', (data) => {
      const d = data as { games?: Game[] }
      if (!Array.isArray(d?.games)) return
      libraryPushes.current++
      if (d.games.length === 0) {
        gamesRef.current = []
        setGames([])
        return
      }
      setGames((prev) => mergeLibraryUpdate(prev, d.games!, selectedIdRef.current))
    })
    return () => {
      progressFlush.cancel()
      offLaunch()
      offProgress()
      offQueue()
      offSale()
      offParts()
      offOptimized()
      offMouse()
      offCovers()
    }
  }, [
    clearTransferFailure, gamesRef, libraryPushes, loadLibrary, loadSettings, notify,
    progressFlushRef, progressVersion, selectedIdRef, setGames, setProgress,
    setQueuedIds, setRecent, setStatus, setStoreMatrixReady, setStores, setTransferFailures, stoppedTransfers,
  ])

  useEffect(() => onHostEvent('versions.new', (found) => notice({
    id: `versions-${found.id}`,
    title: `${found.name} ${found.version}`,
    text: found.installed ? `Out now. You have ${found.installed}.` : 'Out now.',
    action: {
      label: 'Open',
      primary: true,
      onPress: () => {
        dismissNotice(`versions-${found.id}`)
        setGpuJump({ tab: found.page })
        void goToView('system')
      },
    },
  })), [goToView, setGpuJump])

  useEffect(() => onHostEvent('backup.stopped', (stop) => {
    if (stop?.sentence && stop.since) announceBackupStop(stop.sentence, stop.since)
  }), [])

  useEffect(() => onHostEvent('discord.open', (open) => { setPendingOpen(open); void goToView('friends') }), [goToView])

  useEffect(() => {
    const open = ({ room, part }: { room: 'pc' | 'library' | null; part: string | null }) => {
      if (room === 'pc') { void goToView('system'); setGpuJump({ tab: (part ?? 'overview') as PcLink }) }
      else if (room === 'library') void goToView('library')
    }
    host.appPendingOpen().then(open, (error: unknown) => report(error, 'LauncherApp.pendingOpen', { level: 'warn' }))
    return onHostEvent('app.open', open)
  }, [goToView, setGpuJump])

  useEffect(() => {
    const open = (event: Event) => { setGpuJump({ tab: (event as CustomEvent<PcLink>).detail }); void goToView('system') }
    window.addEventListener('exo-open-pc', open)
    return () => window.removeEventListener('exo-open-pc', open)
  }, [goToView, setGpuJump])

  useEffect(() => onHostEvent('capture.saved', ({ item, edit }) => {
    if (edit) openCaptures({ itemId: item.id, edit: true })
  }), [])

  useEffect(() => watchNotesOpen((message) => setStatus(message, null, false, 'bad')), [setStatus])

  useEffect(() => onHostEvent('keys.found', (data) => {
    const payload = (data ?? {}) as { ok?: boolean; gameId?: string; message?: string }
    if (payload.ok && payload.gameId) {
      const gameId = payload.gameId
      setActivateKeyOpen(false)
      setStatus(payload.message ?? 'Key activated.', null)
      void loadLibrary(true, true).then(() => openGamePage(gameId, 'instant'))
      return
    }
    setStatus(payload.message ?? 'No new game was confirmed.', null, true)
  }), [loadLibrary, openGamePage, setActivateKeyOpen, setStatus])

  useEffect(() => {
    const open = () => void goToView('friends')
    window.addEventListener('exo-discord-settings', open)
    return () => window.removeEventListener('exo-discord-settings', open)
  }, [goToView])

  useEffect(() => {
    const open = () => {
      setSettingsSection({ id: 'music' })
      void goToView('settings')
    }
    window.addEventListener('exo-music-settings', open)
    return () => window.removeEventListener('exo-music-settings', open)
  }, [goToView, setSettingsSection])

  useEffect(() => {
    const open = () => void goToView('music')
    window.addEventListener(MUSIC_LINK, open)
    return () => window.removeEventListener(MUSIC_LINK, open)
  }, [goToView])
}

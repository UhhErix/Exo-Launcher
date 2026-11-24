/**
 * Exo Launcher shell — the Library, rooms, and the game sheet.
 * CTA strings (Play | Install | Update) and cancelInstall live via GamePage + host.
 */
import { lazy, startTransition, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  host,
  type Game,
  type InstallProgress,
  type LauncherSettings,
  type LibraryFolder,
  type StoreStatus,
} from '../lib/host'
import { libraryAnswerParts } from '../lib/libraryAnswer'
import { loadSettingsOrFail, saveSettingsPatch, setSettingsSink } from '../lib/settingsSave'
import { ClientsStart, useClientsStatus } from './clients/ClientsCard'
import { clientsGateShows, libraryHeld } from '../lib/clients'
import {
  cardForExactId,
  findLibraryGame,
  findLibraryGameByTitle,
  isInstallingGame,
  librarySortMode,
  materializeVariant,
  mergedCoverUrl,
  mergeHostGames,
  preferredVariantId,
  reuseGame,
  reuseList,
  transferForGame,
} from '../lib/library'
import { addPortableFolder, addPortableProgram } from '../lib/portable'
import { notify as notice } from '../lib/notify'
import { newerNotice } from '../lib/upscalerNotice'
import { report } from '../lib/report'
import { errorText } from '../lib/errorWords'
import { applyTextTheme, readCachedTextTheme } from '../lib/exoTheme'
import { preloadUpscalerStatuses } from '../lib/upscalerCache'
import { preloadInitialCoverArt, preloadLibraryArtwork } from './CoverArt'
import type { Command } from './blocks/command-menu-1'
import { usePaletteExtras } from './shell/paletteExtras'
import { ExoIsland } from './shell/ExoIsland'
import { useWhatsNew } from './shell/useWhatsNew'
import { AccountOverlays, useAccountShell } from './account/AccountShell'
import { updateSheetShown } from '../lib/accountPrompt'
import { bootGate, BootSplash, Room } from './shell/BootGate'
import { ErrorBoundary } from './shell/ErrorBoundary'
import { pcLinkRoom, type PcLink } from './gpu/pcSections'
import { LibraryPane } from './shell/LibraryPane'
import { ROOM_ORDER, TitleBar, type View } from './shell/TitleBar'
import { GamepadBar } from './shell/GamepadBar'
import { installGamepadNav } from '../lib/gamepadNav'
import { savedMusicService, saveMusicService, type MusicService } from '../lib/music'
import { loadUiPrefs } from '../lib/uiPrefs'
import { ShellNotices } from './shell/ShellNotices'
import { NoticeStack } from './shell/NoticeStack'
import { DriverInstallOverlay } from './shell/DriverInstallOverlay'
import { DiscordNotices } from './shell/DiscordNotices'
import { StoreOutageNotices } from './shell/StoreOutageNotices'
import { AntiCheatNotices } from './shell/AntiCheatNotices'
import { onReportedFailure, useActionStatus, type ActionStatus } from './shell/useActionStatus'
import { holdsBoot, useAppUpdate } from './shell/useAppUpdate'
import { useRunStatePoll } from './shell/useRunStatePoll'
import { useLiveLibraryRefresh } from './shell/useLiveLibraryRefresh'
import { useShellLayout } from './shell/useShellLayout'
import { useShelves } from './shell/useShelves'
import type { SettingsSectionId } from '../lib/settingsSections'
import { useCapturesSheet } from '../lib/capture'
import { useNoteCommands } from '../lib/notes'
import { openDiscord, type DiscordNotice } from '../lib/discord'
import { requestStoreSearch } from '../lib/storeSearch'
import { GameLinkContext } from '../lib/friendsPlaying'
import { onScreen, pressedArt, transition, visible } from '../lib/viewTransition'
import { buildShellCommands, executeShellCommand } from './shell/launcherCommands'
import { useRoomController } from './shell/useRoomController'
import { useGameActions } from './shell/useGameActions'
import { useExoTour } from './shell/useExoTour'
import { logFirstCoversPainted, tellBackupStop, useShellHostEvents } from './shell/useShellEvents'

const SettingsPanel = lazy(() => import('./SettingsPanel').then((m) => ({ default: m.SettingsPanel })))
const FriendsRoom = lazy(() => import('./FriendsRoomRoute').then((m) => ({ default: m.FriendsRoom })))
const NativeMusicRoom = lazy(() => import('./music/NativeMusicRoom').then((m) => ({ default: m.NativeMusicRoom })))
const DevicesRoom = lazy(() => import('./DevicesRoom').then((m) => ({ default: m.DevicesRoom })))
const StoreRoom = lazy(() => import('./store/StoreRoom').then((m) => ({ default: m.StoreRoom })))
const CommandMenu = lazy(() => import('./blocks/command-menu-1'))
const GameSheet = lazy(() => import('./shell/GameSheet').then((m) => ({ default: m.GameSheet })))
const ExoAiRoom = lazy(() => import('./gpu/ExoAiRoom').then((m) => ({ default: m.ExoAiRoom })))
const AppsRoom = lazy(() => import('./apps/AppsRoom').then((m) => ({ default: m.AppsRoom })))
const ActivateKeySheet = lazy(() => import('./ActivateKeySheet').then((m) => ({ default: m.ActivateKeySheet })))
const WhatsNewSheet = lazy(() => import('./shell/WhatsNewSheet').then((m) => ({ default: m.WhatsNewSheet })))
const UpdateSheet = lazy(() => import('./shell/WhatsNewSheet').then((m) => ({ default: m.UpdateSheet })))
const CapturesOverlay = lazy(() => import('./captures/CapturesSheet').then((m) => ({ default: m.CapturesOverlay })))
const ExoTour = lazy(() => import('./shell/ExoTour').then((m) => ({ default: m.ExoTour })))

const NO_FOLDERS: LibraryFolder[] = []
const NO_IDS: string[] = []
const NO_COMMANDS: Command[] = []
const MIN_BOOT_SPLASH_MS = 900
const BOOT_COVERS_CAP_MS = 1500
const BOOT_COVERS = 40

applyTextTheme(readCachedTextTheme())

function useLibraryStart(settings: LauncherSettings | null, loadLibrary: () => Promise<unknown>) {
  const onboardingComplete = settings?.onboardingComplete === true
  const { status: clients, setStatus: setClients, failed: clientsFailed, failures: clientsFailures, checking: clientsChecking, recheck: recheckClients } = useClientsStatus(onboardingComplete)
  const [later, setLater] = useState(false)
  const held = libraryHeld(onboardingComplete, clients, later, clientsFailed)
  const [libraryOn, setLibraryOn] = useState(false)
  useEffect(() => {
    const timer = window.setTimeout(() => {
      host.dlssNewer().then((answer) => {
        const offer = newerNotice(answer?.items ?? [], () => {
          host.dlssDownloadNewer().then((done) => notice(done?.ok
            ? { id: 'upscalers-newer-done', text: 'Downloaded and checked.', tone: 'good' }
            : { id: 'upscalers-newer-done', text: errorText(done?.message, "Exo couldn't download it."), tone: 'bad' }))
            .catch(() => notice({ id: 'upscalers-newer-done', text: "Exo couldn't download it.", tone: 'bad' }))
        })
        if (offer) notice(offer)
      }).catch(() => undefined)
    }, 20_000)
    return () => window.clearTimeout(timer)
  }, [])
  useEffect(() => {
    if (libraryOn || !onboardingComplete || held) return
    setLibraryOn(true)
    void loadLibrary()
  }, [libraryOn, onboardingComplete, held, loadLibrary])
  const holdPage = !held ? null
    : clients && clientsGateShows(clients, later)
      ? <ClientsStart status={clients} onStatus={setClients} onLater={() => setLater(true)} />
      : <div className="exo-app"><BootSplash /></div>
  const clientsProblem = clients?.problem || clientsFailed
    ? { text: clients?.problem ?? (clientsFailures > 1 ? 'Exo still could not check your game clients.' : 'Exo couldn’t check your game clients.'), busy: clientsChecking, onRetry: recheckClients }
    : null
  return { holdPage, libraryOn, clientsProblem }
}

function useLibraryFolders(
  settings: LauncherSettings | null,
  setSettings: (update: (current: LauncherSettings | null) => LauncherSettings | null) => void,
  persist: (patch: Partial<LauncherSettings>) => void,
) {
  const folders = settings?.libraryFolders ?? NO_FOLDERS
  const hidden = settings?.hiddenGames ?? NO_IDS
  const save = useCallback((next: LibraryFolder[]) => {
    setSettings((current) => current ? { ...current, libraryFolders: next } : current)
    persist({ libraryFolders: next })
  }, [setSettings, persist])
  const saveHidden = useCallback((next: string[]) => {
    setSettings((current) => current ? { ...current, hiddenGames: next } : current)
    persist({ hiddenGames: next })
  }, [setSettings, persist])
  return [folders, save, hidden, saveHidden] as const
}

const isSurfaceActive = (view: View, room: View, blockers: readonly boolean[]) => view === room && !blockers.some(Boolean)
const activeTransferId = (progress: InstallProgress | null) => progress?.isActive ? progress.gameId : null
const shellNotice = (status: ActionStatus) => (status.gameId === null && status.message ? { message: status.message, tone: status.tone, sticky: status.sticky } : null)
const isBanner = (art: Element | null | undefined) => !!art?.closest('.exo-deck-card')
function setAway(art: Element | null | undefined, away: boolean) {
  ;(art?.closest('.exo-deck-card') ?? art)?.toggleAttribute('data-exo-away', away)
}

export function LauncherApp() {
  const [games, setGames] = useState<Game[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [prefsLoaded, setPrefsLoaded] = useState(false)
  const [libraryError, setLibraryError] = useState<string | null>(null)
  const [booting, setBooting] = useState(true)
  const [bootRevealing, setBootRevealing] = useState(false)
  const coldStart = useRef(true)
  const bootAt = useRef(Date.now())
  const bootPreparationStarted = useRef(false)
  const usableLogged = useRef(false)

  const [musicService, setMusicService] = useState<MusicService>(savedMusicService)
  const [nowPlaying, setNowPlaying] = useState(0)
  useEffect(() => {
    let live = true
    const changed = (event: Event) => { setMusicService((event as CustomEvent<MusicService>).detail); setNowPlaying(0) }
    window.addEventListener('exo-music-service', changed)
    void host.webClientPreferences().then(async (options) => {
      if (!live) return
      if (options.musicService) saveMusicService(options.musicService)
      else {
        const service = savedMusicService()
        await host.setWebClientPreferences({ musicService: service })
        if (live) saveMusicService(service)
      }
    }).catch((e: unknown) => report(e, 'LauncherApp.webClientPreferences'))
    return () => { live = false; window.removeEventListener('exo-music-service', changed) }
  }, [])

  const [activateKeyOpen, setActivateKeyOpen] = useState(false)
  const addingFolder = useRef(false)
  const [settings, setSettings] = useState<LauncherSettings | null>(null)
  const [recent, setRecent] = useState<string[] | undefined>()
  const [settingsSection, setSettingsSection] = useState<{ id: SettingsSectionId } | null>(null)
  const [settingsError, setSettingsError] = useState<string | null>(null)
  const [stores, setStores] = useState<StoreStatus[]>([])
  const [storeMatrixReady, setStoreMatrixReady] = useState(false)
  const [commandOpen, setCommandOpen] = useState(false)
  const [activeGameId, setActiveGameId] = useState<string | null>(null)
  const [libraryPane, setLibraryPane] = useState<'shelf' | 'game'>('shelf')
  const [gpuJump, setGpuJump] = useState<{ tab: PcLink } | null>(null)
  const [overlayMotion, setOverlayMotion] = useState<'pointer' | 'instant'>('pointer')
  const overlayOpenedAtRef = useRef(0)

  const selectedIdRef = useRef<string | null>(null)
  const gamesRef = useRef<Game[]>([])
  const libraryMainRef = useRef<HTMLElement>(null)
  selectedIdRef.current = selectedId
  gamesRef.current = games

  const layout = useShellLayout(settings)
  const update = useAppUpdate()
  const checkUpdate = update.check

  const selectedCard = useMemo(() => (
    selectedId ? cardForExactId(games, selectedId) ?? findLibraryGame(games, selectedId) ?? null : null
  ), [games, selectedId])
  const selected = useMemo(
    () => selectedCard ? materializeVariant(selectedCard, selectedVariantId ?? preferredVariantId(selectedCard, settings?.preferredGameSources)) : null,
    [selectedCard, selectedVariantId, settings],
  )
  const overlayOpen = libraryPane === 'game' && !!selected

  const selectCard = useCallback((id: string | null) => {
    selectedIdRef.current = id
    setSelectedId(id)
    setSelectedVariantId(null)
  }, [])

  const gameArtFrom = useRef<Element | null>(null)
  const closeGamePage = useCallback(() => {
    setLibraryPane('shelf')
    selectCard(null)
    setAway(gameArtFrom.current, false)
  }, [selectCard])

  const closeGameSheet = useCallback(() => {
    const id = selectedIdRef.current
    const card = gameArtFrom.current
    void transition(closeGamePage, {
      kind: 'morph',
      back: true,
      morph: {
        name: 'exo-game-art',
        from: visible('.exo-game-detail-cover'),
        to: () => onScreen(card) ? card : id ? visible(`[data-game-id="${CSS.escape(id)}"] [data-vt-art]`) : null,
      },
    })
  }, [closeGamePage])

  const libraryRequest = useRef(0)
  const libraryPushes = useRef(0)

  const statusSinkRef = useRef<ActionStatus['set']>(() => { })
  const setStatus = useCallback<ActionStatus['set']>(
    (message, gameId, sticky, tone) => statusSinkRef.current(message, gameId, sticky, tone),
    [],
  )
  const libraryActionsSinkRef = useRef<{
    setQueuedIds: (ids: string[]) => void
    writeProgress: (next: InstallProgress | null) => void
  }>({
    setQueuedIds: () => { },
    writeProgress: () => { },
  })

  const loadLibrary = useCallback(async (force = false, silent = false, full = false, quietErrors = false) => {
    const request = ++libraryRequest.current
    const pushesAtStart = libraryPushes.current
    if (coldStart.current) { bootAt.current = Date.now(); setLoading(true) }
    if (!silent) { setStatus(null, null); setLibraryError(null) }
    try {
      const res = await host.getLibrary(force, full)
      if (request !== libraryRequest.current) return
      const parts = libraryAnswerParts(res, libraryPushes.current !== pushesAtStart)
      if (parts.games) {
        const previous = gamesRef.current
        const prevById = new Map(previous.map((g) => [g.id, g]))
        const incoming = parts.games.map((g) => {
          const old = prevById.get(g.id)
          return reuseGame(old, { ...g, coverUrl: mergedCoverUrl(g, old) })
        })
        const merged = reuseList(previous, mergeHostGames(previous, incoming, selectedIdRef.current))
        gamesRef.current = merged
        if (previous.length === 0) setGames(merged)
        else startTransition(() => setGames(merged))
      }
      if (parts.recent) {
        const next = parts.recent
        setRecent((current) => current?.join('\u001f') === next.join('\u001f') ? current : next)
      }
      if (parts.stores) setStores(parts.stores)
      if (parts.queuedGameIds) libraryActionsSinkRef.current.setQueuedIds(parts.queuedGameIds)
      if (parts.progress) libraryActionsSinkRef.current.writeProgress(parts.progress)
    } catch (e) {
      if (quietErrors || request !== libraryRequest.current) return
      report(e, 'LauncherApp.loadLibrary')
      setLibraryError(errorText(e, 'Exo couldn’t load your library. Try again.'))
    } finally {
      setLoading(false)
    }
  }, [setStatus])

  const actions = useGameActions({
    gamesRef,
    selected,
    selectedCard,
    selectedIdRef,
    overlayOpen,
    settings,
    setGames,
    setSettings,
    setSelectedVariantId,
    loadLibrary,
    setStatus,
  })
  libraryActionsSinkRef.current = { setQueuedIds: actions.setQueuedIds, writeProgress: actions.writeProgress }

  const status = useActionStatus(activeTransferId(actions.progress), selectedIdRef)
  statusSinkRef.current = status.set
  const notify = status.notify
  useEffect(() => onReportedFailure((message) => setStatus(message, null, false, 'bad')), [setStatus])

  const liveShelves = useShelves(games, actions.progress, settings, recent ?? settings?.recent)
  const { holdPage, libraryOn, clientsProblem } = useLibraryStart(settings, loadLibrary)
  const rescanLiveLibrary = useCallback(() => loadLibrary(true, true, true, true), [loadLibrary])
  const refreshEverything = useCallback(() => {
    const all = host.libraryRefreshAll()
    return { stores: rescanLiveLibrary(), done: all.then(() => rescanLiveLibrary()) }
  }, [rescanLiveLibrary])
  useLiveLibraryRefresh(rescanLiveLibrary, libraryOn)

  const loadSettings = useCallback(async () => {
    try {
      const s = await loadSettingsOrFail()
      setSettings(s)
      if (s.onboardingComplete) void tellBackupStop()
      void loadUiPrefs(s.uiPrefs, { save: (uiPrefs) => saveSettingsPatch({ uiPrefs }), move: (uiPrefsMoved) => saveSettingsPatch({ uiPrefsMoved }) })
        .finally(() => setPrefsLoaded(true))
      setSettingsError(null)
    } catch (error) {
      setSettingsError(errorText(error, 'Exo couldn’t load your settings. Try again.'))
    }
  }, [])

  useEffect(() => {
    setSettingsSink(setSettings)
    return () => setSettingsSink(null)
  }, [])
  const persistSettings = useCallback((patch: Partial<LauncherSettings>) => {
    saveSettingsPatch(patch).catch((e: unknown) => {
      report(e, 'LauncherApp.persistSettings')
      setStatus('That change did not save.', null, false, 'bad')
      void loadSettings()
    })
  }, [loadSettings, setStatus])
  const saveSettings = useCallback(async (patch: Partial<LauncherSettings>) => {
    try { await saveSettingsPatch(patch); return true }
    catch (e) { report(e, 'LauncherApp.saveSettings'); return false }
  }, [])

  const openGamePage = useCallback((id: string, motion: 'pointer' | 'instant' = 'pointer', options?: { game?: Game }) => {
    const library = gamesRef.current
    const existing = findLibraryGame(library, id) ?? (options?.game ? findLibraryGameByTitle(library, options.game.title) : undefined)
    selectCard(existing?.id ?? id)
    if (existing && existing.id !== id && existing.variants?.some((variant) => variant.id === id)) setSelectedVariantId(id)
    setOverlayMotion(motion)
    overlayOpenedAtRef.current = performance.now()
    setLibraryPane('game')
    setStatus(null, null)
  }, [selectCard, setStatus])

  const toggleCommandMenu = useCallback(() => setCommandOpen((open) => !open), [])
  const { view, under, openedRooms, opening, prewarm, warming, preloaded, goToView, warmRoom, toggleSettings } = useRoomController({
    booting,
    settings,
    musicService,
    libraryPane,
    commandOpen,
    activateKeyOpen,
    hasDepPrompt: !!actions.depPrompt,
    closeGamePage,
    closeGameSheet,
    onToggleCommandMenu: toggleCommandMenu,
  })

  useShellHostEvents({
    gamesRef,
    selectedIdRef,
    libraryPushes,
    progressVersion: actions.progressVersion,
    progressFlushRef: actions.progressFlushRef,
    stoppedTransfers: actions.stoppedTransfers,
    setGames,
    setRecent,
    setStores,
    setStoreMatrixReady,
    setQueuedIds: actions.setQueuedIds,
    setProgress: actions.setProgress,
    setTransferFailures: actions.setTransferFailures,
    clearTransferFailure: actions.clearTransferFailure,
    setActivateKeyOpen,
    setGpuJump,
    setSettingsSection,
    setStatus,
    notify,
    loadLibrary,
    loadSettings,
    goToView,
    openGamePage,
  })

  const openNowPlaying = useCallback(() => void goToView('music',
    { name: 'exo-music-now', from: visible('.exo-mini-cover'), to: () => visible('.exo-music-now > .exo-music-art') },
    () => setNowPlaying((n) => n + 1)), [goToView])

  const capturesSheet = useCapturesSheet()
  useEffect(() => { if (capturesSheet.open) closeGamePage() }, [capturesSheet.open, capturesSheet.seq, closeGamePage])

  const openNotice = useCallback((discordNotice: DiscordNotice) => {
    openDiscord({
      guildId: discordNotice.guildId ?? null,
      channelId: discordNotice.channelId,
      parentId: discordNotice.parentId,
      name: discordNotice.parentId ? discordNotice.place?.replace(/^#/, '') : undefined,
    })
    void goToView('friends')
  }, [goToView])

  const findInStore = useCallback((title: string) => { requestStoreSearch(title); void goToView('store') }, [goToView])
  const openLibraryGameFromStore = useCallback((title: string, gameId?: string) => {
    const library = gamesRef.current
    const game = (gameId ? findLibraryGame(library, gameId) : undefined) ?? findLibraryGameByTitle(library, title)
    void goToView('library')
    if (game) openGamePage(game.id, 'instant')
  }, [goToView, openGamePage])

  useEffect(() => {
    if (!booting || bootPreparationStarted.current || settings?.onboardingComplete !== true || loading) return
    bootPreparationStarted.current = true
    const prepare = async () => {
      const wait = Math.max(0, MIN_BOOT_SPLASH_MS - (Date.now() - bootAt.current))
      const firstScreen = gamesRef.current.slice(0, BOOT_COVERS)
      await Promise.all([
        preloaded.current,
        new Promise<void>((resolve) => window.setTimeout(resolve, wait)),
        Promise.race([
          preloadInitialCoverArt(firstScreen, BOOT_COVERS).catch(() => { }),
          new Promise<void>((resolve) => window.setTimeout(resolve, BOOT_COVERS_CAP_MS)),
        ]),
      ])
      coldStart.current = false
      setBootRevealing(true)
    }
    void prepare()
  }, [booting, loading, settings?.onboardingComplete, preloaded])

  useEffect(() => {
    if (booting) return
    if (!usableLogged.current) {
      usableLogged.current = true
      performance.mark('exo.library.usable')
      void host.log({ level: 'info', message: `Library usable after ${Date.now() - bootAt.current} ms` }).catch(() => { })
      logFirstCoversPainted(bootAt.current)
    }
    let cancelled = false
    const artworkCancellation = new AbortController()
    const timer = window.setTimeout(() => {
      const visibleGames = gamesRef.current.slice(0, 24)
      const tasks = [
        () => preloadLibraryArtwork(visibleGames, 6, artworkCancellation.signal),
        () => preloadUpscalerStatuses(visibleGames.slice(0, 8), 2, 2000),
      ]
      void (async () => {
        for (const task of tasks) {
          if (cancelled || document.hidden) break
          try { await task() } catch { /* optional warming */ }
        }
      })()
    }, 1000)
    return () => { cancelled = true; artworkCancellation.abort(); clearTimeout(timer) }
  }, [booting])

  useEffect(() => {
    if (booting) return
    const rooms = window.setTimeout(() => { if (!document.hidden) void host.prepareRooms().catch(() => { }) }, 100)
    return () => clearTimeout(rooms)
  }, [booting])

  useEffect(() => {
    if (!selectedId) return
    const card = cardForExactId(games, selectedId)
    if (!card || card.id === selectedId) return
    const retainedVariant = selectedVariantId && card.variants?.some((variant) => variant.id === selectedVariantId) ? selectedVariantId : selectedId
    setSelectedId(card.id)
    setSelectedVariantId(retainedVariant)
  }, [games, selectedId, selectedVariantId])

  useRunStatePoll(games, selectedId, selectedVariantId, actions.busy, setGames)

  const addFromLibrary = useCallback(async (add: typeof addPortableFolder) => {
    if (addingFolder.current) return
    addingFolder.current = true
    try {
      const result = await add()
      if (result.cancelled) return
      setStatus(result.message ?? (result.ok ? 'Game added.' : 'Could not add the game.'), null, false, result.ok ? 'info' : 'bad')
      if (result.ok) await loadLibrary(true, true)
    } catch (error) {
      setStatus(errorText(error, 'The picker didn’t open. Try again.'), null, false, 'bad')
    } finally { addingFolder.current = false }
  }, [loadLibrary, setStatus])
  const addGameFromLibrary = useCallback(() => addFromLibrary(addPortableProgram), [addFromLibrary])

  const paletteExtras = usePaletteExtras(commandOpen)
  const noteCommands = useNoteCommands(commandOpen)
  const commands = useMemo<Command[]>(() => commandOpen ? buildShellCommands({
    library: liveShelves.library,
    hidden: liveShelves.hidden,
    selected,
    noteCommands,
    paletteExtras,
  }) : NO_COMMANDS, [commandOpen, liveShelves.library, liveShelves.hidden, selected, noteCommands, paletteExtras])

  const selectCommand = useCallback((command: Command) => executeShellCommand(command, {
    selected,
    goToView,
    openGamePage,
    setGpuJump,
    setSettingsSection,
    setActivateKeyOpen,
    addGame: () => void addGameFromLibrary(),
    refreshLibrary: () => { void loadLibrary(true) },
    checkUpdate: () => void checkUpdate().then((result) => setStatus(result.message, null, false, result.ok ? 'info' : 'bad')),
    minimize: () => void host.minimize(),
    close: () => void host.close(),
    runPrimary: (game, action) => void actions.runPrimary(false, game, action),
    setErrorStatus: (message) => setStatus(message, null, false, 'bad'),
  }), [selected, goToView, openGamePage, addGameFromLibrary, loadLibrary, checkUpdate, setStatus, actions])

  const openCard = useCallback((game: Game) => {
    setActiveGameId(game.id)
    const art = pressedArt()
    const from = isBanner(art) ? art?.closest('.exo-deck-card') ?? art : art
    setAway(gameArtFrom.current, false)
    gameArtFrom.current = from
    void transition(() => { openGamePage(game.id, 'pointer', { game }); setAway(from, true) }, {
      kind: 'morph',
      morph: { name: 'exo-game-art', from, to: () => visible('.exo-game-detail-cover') },
    })
  }, [openGamePage])

  const [propertiesId, setPropertiesId] = useState<string | null>(null)
  const openProperties = useCallback((game: Game) => { openCard(game); setPropertiesId(game.id) }, [openCard])
  useEffect(() => { if (!overlayOpen) setPropertiesId(null) }, [overlayOpen])

  const activeProgress = actions.progress?.isActive ? actions.progress : null
  const transferFor = useCallback((game: Game) => transferForGame(activeProgress, game), [activeProgress])

  async function finishOnboarding() {
    const next = await saveSettingsPatch({ onboardingComplete: true })
    if (!next?.onboardingComplete) throw new Error('Could not save settings. Try again.')
  }

  async function addFolderDuringOnboarding() {
    const result = await addPortableFolder()
    if (result.cancelled) return false
    if (!result.ok) throw new Error(result.message || 'The folder could not be added.')
    return true
  }

  const [libraryFolders, saveFolders, hiddenGames, saveHiddenGames] = useLibraryFolders(settings, setSettings, persistSettings)
  const sortLibrary = useCallback((next: Parameters<typeof librarySortMode>[0]) => persistSettings({ sortMode: librarySortMode(next) }), [persistSettings])
  const activateKey = useCallback(() => setActivateKeyOpen(true), [])
  const closeActivateKey = useCallback(() => setActivateKeyOpen(false), [])
  const retryLibrary = useCallback(() => { void loadLibrary(true) }, [loadLibrary])
  const openStoreSettings = useCallback(() => { setSettingsSection({ id: 'accounts' }); void goToView('settings') }, [goToView])
  const turnOnHdrForGames = useCallback(() => saveSettings({ hdrForGames: true }), [saveSettings])
  const updateStores = useCallback(async (next: StoreStatus[]) => { setStores(next); await loadLibrary(true, true) }, [loadLibrary])
  const keyStatus = useCallback((message: string, fail?: boolean) => setStatus(message, null, !!fail), [setStatus])
  const emptyLibrary = !booting && !loading && liveShelves.library.length + liveShelves.hidden.length === 0
  const displayedGame = selected

  const [whatsNew, closeWhatsNew] = useWhatsNew(settings, [booting, view !== 'library', overlayOpen, commandOpen, activateKeyOpen, !!actions.depPrompt, update.pending, update.sheet], persistSettings)
  const accountShell = useAccountShell(!!whatsNew)
  const tour = useExoTour(
    prefsLoaded,
    settings?.onboardingComplete,
    [booting, loading, view !== 'library', overlayOpen, commandOpen, activateKeyOpen, !!actions.depPrompt, update.sheet, !!whatsNew, accountShell.sheetOpen, capturesSheet.open],
  )

  const dialogBlockers = [overlayOpen, commandOpen, activateKeyOpen, !!actions.depPrompt, booting, capturesSheet.open, tour.open]
  const friendsActive = isSurfaceActive(view, 'friends', dialogBlockers)
  const musicActive = isSurfaceActive(view, 'music', dialogBlockers)
  const devicesActive = isSurfaceActive(view, 'devices', dialogBlockers)
  const sortMode = settings ? librarySortMode(settings.sortMode) : null
  const openDiscordRoom = useCallback(() => goToView('friends'), [goToView])
  const openSearch = useCallback(() => setCommandOpen(true), [])
  const gamepadState = useRef({ view, under, overlayOpen, commandOpen, goToView, closeGamePage, toggleSettings })
  gamepadState.current = { view, under, overlayOpen, commandOpen, goToView, closeGamePage, toggleSettings }
  useEffect(() => installGamepadNav({
    onRoomStep: (delta) => {
      const s = gamepadState.current
      if (s.commandOpen) setCommandOpen(false)
      if (s.overlayOpen) s.closeGamePage()
      const idx = ROOM_ORDER.indexOf(s.under)
      const next = ROOM_ORDER[(idx < 0 ? 0 : idx + delta + ROOM_ORDER.length) % ROOM_ORDER.length]
      setGpuJump(null)
      void s.goToView(next)
    },
    onSearchToggle: () => setCommandOpen(open => !open),
    onSettingsToggle: () => gamepadState.current.toggleSettings(),
    onBackFallback: () => {
      const s = gamepadState.current
      if (s.commandOpen) setCommandOpen(false)
      else if (s.overlayOpen) s.closeGamePage()
      else if (s.view === 'settings') s.toggleSettings()
    },
  }), [])

  const openRoomFromBar = useCallback((next: View) => {
    setGpuJump(null)
    return goToView(next)
  }, [goToView])

  const island = useMemo(() => <ExoIsland inDiscord={view === 'friends'} onOpenDiscord={openDiscordRoom} />, [view, openDiscordRoom])
  const titleBar = useMemo(() => (
    <TitleBar
      view={view}
      opening={opening}
      onView={openRoomFromBar}
      onWarm={warmRoom}
      onNowPlaying={openNowPlaying}
      onToggleSettings={toggleSettings}
      musicService={musicService}
      island={island}
      onSearch={openSearch}
    />
  ), [view, opening, openRoomFromBar, warmRoom, openNowPlaying, toggleSettings, musicService, island, openSearch])

  const settingsRoom = useMemo(() => settings && (
    <Room mounted={openedRooms.has('settings')} warm={warming} active={view === 'settings'} label="Settings" className="exo-set-room">
      <SettingsPanel
        active={view === 'settings'}
        onClose={toggleSettings}
        section={settingsSection}
        room={under}
        settings={settings}
        stores={stores}
        updateBusy={update.busy}
        updateError={update.error}
        updatePercent={update.percent}
        updateStatus={update.status}
        updateAvailable={update.available}
        onCheckUpdate={checkUpdate}
        onInstallUpdate={update.install}
        onSettings={setSettings}
        onStores={updateStores}
      />
    </Room>
  ), [openedRooms, warming, view, settingsSection, under, settings, stores, update.busy, update.error, update.percent, update.status, update.available, update.install, checkUpdate, updateStores, toggleSettings])

  const gameLink = useMemo(() => ({ games, startingId: actions.startingId, onPlay: actions.playFromLibrary, onFind: findInStore }), [games, actions.startingId, actions.playFromLibrary, findInStore])
  const friendsRoomBody = useMemo(() => <FriendsRoom active={friendsActive} />, [friendsActive])
  const friendsRoom = useMemo(() => (
    <Room name="friends" mounted={openedRooms.has('friends')} warm={warming || opening === 'friends' || prewarm === 'friends'} active={under === 'friends'} covered={view === 'settings'} label="Discord" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <GameLinkContext.Provider value={gameLink}>{friendsRoomBody}</GameLinkContext.Provider>
    </Room>
  ), [under, view, openedRooms, warming, opening, prewarm, gameLink, friendsRoomBody])

  const musicRoom = useMemo(() => (
    <Room name="music" mounted={openedRooms.has('music')} warm={warming || opening === 'music' || prewarm === 'music'} active={under === 'music'} covered={view === 'settings'} label="Music" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <NativeMusicRoom key={musicService} service={musicService} active={musicActive} shown={view === 'music' && !booting} nowPlaying={nowPlaying} />
    </Room>
  ), [under, openedRooms, warming, opening, prewarm, view, musicService, musicActive, booting, nowPlaying])

  const storeRoom = useMemo(() => (
    <Room name="store" mounted={openedRooms.has('store')} warm={warming || opening === 'store' || prewarm === 'store'} active={under === 'store'} covered={view === 'settings'} label="Store" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <StoreRoom active={view === 'store'} games={games} onOpenGame={openLibraryGameFromStore} />
    </Room>
  ), [under, openedRooms, warming, opening, prewarm, view, games, openLibraryGameFromStore])

  const devicesRoom = useMemo(() => (
    <Room mounted={openedRooms.has('devices')} warm={warming || prewarm === 'devices'} active={under === 'devices'} covered={view === 'settings'} label="Devices" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <DevicesRoom active={devicesActive} />
    </Room>
  ), [under, view, openedRooms, warming, prewarm, devicesActive])

  const exoAiRoom = useMemo(() => (
    <Room name="exo-ai" mounted={openedRooms.has('exo-ai')} warm={warming || opening === 'exo-ai' || prewarm === 'exo-ai'} active={under === 'exo-ai'} covered={view === 'settings'} label="Exo AI" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <Suspense fallback={null}><ExoAiRoom active={view === 'exo-ai'} jump={gpuJump} games={games} settings={settings} onSave={saveSettings} /></Suspense>
    </Room>
  ), [under, openedRooms, warming, opening, prewarm, view, gpuJump, games, settings, saveSettings])

  const appsRoom = useMemo(() => (
    <Room name="apps" mounted={openedRooms.has('apps')} warm={warming || opening === 'apps' || prewarm === 'apps'} active={under === 'apps'} covered={view === 'settings'} label="Apps" className="relative z-10 flex min-h-0 flex-1 flex-col">
      <Suspense fallback={null}><AppsRoom active={view === 'apps'} /></Suspense>
    </Room>
  ), [under, openedRooms, warming, opening, prewarm, view])

  const libraryRoom = useMemo(() => sortMode && (
    <Room mounted={openedRooms.has('library')} active={under === 'library'} covered={view === 'settings'} label="Library" className="exo-under-bar relative z-10 flex min-h-0 flex-1">
      <LibraryPane
        mainRef={libraryMainRef}
        active={under === 'library'}
        overlayOpen={overlayOpen}
        loading={loading}
        empty={emptyLibrary}
        error={libraryError}
        games={liveShelves.library}
        hidden={liveShelves.hidden}
        hiddenIds={hiddenGames}
        onHidden={saveHiddenGames}
        playing={liveShelves.playing}
        selectedId={selectedId}
        activeGameId={activeGameId}
        queuedIds={actions.queuedIds}
        sort={sortMode}
        reduceMotion={layout.reduceMotion}
        transferFor={transferFor}
        onActiveGameChange={setActiveGameId}
        onOpen={openCard}
        onPlay={actions.playFromLibrary}
        onPlayAs={actions.playAs}
        startingId={actions.startingId}
        onOpenFolder={actions.openGameFolder}
        onProperties={openProperties}
        onSort={sortLibrary}
        folders={libraryFolders}
        onFolders={saveFolders}
        onAddGame={addGameFromLibrary}
        onActivateKey={activateKey}
        onRefresh={refreshEverything}
        onRetry={retryLibrary}
        onStoreSettings={openStoreSettings}
        onUninstall={actions.uninstallGames}
      />
    </Room>
  ), [under, view, openedRooms, overlayOpen, loading, emptyLibrary, libraryError, liveShelves, selectedId, activeGameId, actions, sortMode, layout.reduceMotion,
    transferFor, openCard, openProperties, sortLibrary, libraryFolders, saveFolders, hiddenGames, saveHiddenGames, addGameFromLibrary,
    activateKey, refreshEverything, retryLibrary, openStoreSettings])

  const activateKeySheet = useMemo(() => (
    <Suspense fallback={null}><ActivateKeySheet open={activateKeyOpen} onClose={closeActivateKey} onStatus={keyStatus} /></Suspense>
  ), [activateKeyOpen, closeActivateKey, keyStatus])

  const commandMenu = useMemo(() => (
    <Suspense fallback={null}><CommandMenu
      open={commandOpen}
      onOpenChange={setCommandOpen}
      commands={commands}
      groupOrder={['Go to', 'Library', 'Actions', 'Notes', 'Music', 'Discord']}
      onSelect={selectCommand}
      inputPlaceholder="Search"
      className="pointer-events-none absolute inset-0 z-[60] h-full w-full bg-transparent"
    /></Suspense>
  ), [commandOpen, commands, selectCommand])

  if (holdsBoot(update)) return <div className="exo-app"><BootSplash checkingUpdate /></div>

  const gate = bootGate({
    settings,
    settingsError,
    storeMatrixReady,
    stores,
    onRetrySettings: () => void loadSettings(),
    onSettings: setSettings,
    onStores: setStores,
    onComplete: finishOnboarding,
    onAddFolder: addFolderDuringOnboarding,
    over: <UpdateSheet update={update} shown />,
    clientsHold: holdPage,
  })
  if (gate || !settings) return gate

  return (
    <div className="exo-app">
      {booting && <BootSplash revealing={bootRevealing} reduceMotion={layout.reduceMotion} onComplete={() => setBooting(false)} />}
      {titleBar}

      <ShellNotices
        transferFailures={Object.entries(actions.transferFailures).filter(([id, message]) => !overlayOpen || !selected || !cardForExactId([selected], id) || status.gameId !== id || status.message !== message)
          .map(([gameId, message]) => ({ gameId, message, title: findLibraryGame(games, gameId)?.title ?? gameId }))}
        onDismissTransferFailure={actions.clearTransferFailure}
        clientsProblem={clientsProblem}
        depPrompt={actions.depPrompt}
        onInstallDeps={() => void actions.onOfferMissingDeps()}
        onContinueAfterDeps={() => void actions.onContinueAfterDeps()}
        onDismissDeps={() => { actions.declinedDeps.current = actions.depPrompt?.gameId ?? null; actions.setDepPrompt(null) }}
        update={update}
        showUpdate={view !== 'settings'}
        notice={shellNotice(status)}
        onDismissNotice={() => setStatus(null, null)}
        passing={status.notice}
      />

      <DiscordNotices onOpen={openNotice} />
      <StoreOutageNotices />
      <AntiCheatNotices />
      <NoticeStack />
      <DriverInstallOverlay />

      {settingsRoom}
      {friendsRoom}
      {musicRoom}
      {storeRoom}
      {exoAiRoom}
      {appsRoom}
      {devicesRoom}
      {libraryRoom}
      <ErrorBoundary
        label="Game sheet"
        onReset={closeGamePage}
        className="fixed inset-0 z-[30] bg-bg"
      >
        <Suspense fallback={null}><GameSheet
          game={displayedGame}
          open={overlayOpen}
          instant={overlayMotion === 'instant'}
          openedAt={overlayOpenedAtRef.current}
          statusMsg={status.message}
          statusGameId={status.gameId}
          busy={actions.holdsGame(displayedGame) || actions.pendingTransfers.includes(displayedGame?.id ?? '')}
          progress={actions.progress}
          onPrimary={() => void actions.onPrimary()}
          onStop={() => void actions.onStopGame()}
          onCancel={() => void actions.onCancel()}
          queued={!!displayedGame && actions.queuedIds.some((id) => isInstallingGame(displayedGame, id))}
          onOpenSystem={(page) => {
            if (actions.holdsGame(displayedGame) || !displayedGame) return
            closeGamePage()
            const target = pcLinkRoom(page)
            setGpuJump(target === 'exo-ai' ? { tab: page } : null)
            void goToView(target)
          }}
          onDequeue={() => void actions.onDequeue()}
          hdrForGames={settings.hdrForGames === true}
          onHdrForGames={turnOnHdrForGames}
          propertiesOpen={!!displayedGame && propertiesId === displayedGame.id}
          onPropertiesOpen={(open) => setPropertiesId(open && displayedGame ? displayedGame.id : null)}
          onClose={closeGameSheet}
          onSelectSource={actions.selectSource}
          onStatusFor={(gameId, message, sticky) => setStatus(message, gameId, !!sticky)}
          onUninstalled={() => {
            closeGamePage()
            void loadLibrary(true, true)
          }}
        /></Suspense>
      </ErrorBoundary>
      <Suspense fallback={null}><CapturesOverlay games={games} /></Suspense>
      {activateKeySheet}
      <RoomReport room={view} />
      {commandMenu}
      <Suspense fallback={null}><WhatsNewSheet releases={whatsNew} update={update} onClose={closeWhatsNew} /></Suspense>
      <TourSlot tour={tour} currentView={view} onNavigate={goToView} onBeforeNavigate={closeGamePage} />
      <AccountOverlays shell={accountShell} />
      <Suspense fallback={null}><UpdateSheet update={update} shown={updateSheetShown(booting, accountShell.sheetOpen)} /></Suspense>
      <GamepadBar view={view} overlayOpen={overlayOpen} />
    </div>
  )
}

function RoomReport({ room }: { room: string }) {
  useEffect(() => { void host.telemetryRoom(room).catch(() => { }) }, [room])
  return null
}

function TourSlot({
  tour,
  currentView,
  onNavigate,
  onBeforeNavigate,
}: {
  tour: ReturnType<typeof useExoTour>
  currentView: View
  onNavigate: (view: View) => Promise<void>
  onBeforeNavigate: () => void
}) {
  if (!tour.open) return null
  return (
    <Suspense fallback={null}>
      <ExoTour
        open={tour.open}
        step={tour.step}
        currentView={currentView}
        onStep={tour.setStep}
        onNavigate={onNavigate}
        onBeforeNavigate={onBeforeNavigate}
        onClose={tour.close}
      />
    </Suspense>
  )
}

import { useCallback, useRef, useState, type MutableRefObject } from 'react'
import { errorText } from '../../lib/errorWords'
import { pressOutcome } from '../../lib/gameHold'
import {
  host,
  resolvePrimaryAction,
  shelfAction,
  type Game,
  type InstallProgress,
  type InstallResponse,
  type LaunchResponse,
  type LauncherSettings,
} from '../../lib/host'
import {
  beginGameRead,
  cardForExactId,
  findLibraryGame,
  isInstallingGame,
  materializeVariant,
  mergeExactGame,
  mergeExactGameKeepingRunState,
  setExactRunState,
  statusBelongsToSelection,
} from '../../lib/library'
import { report } from '../../lib/report'
import { saveSettingsPatch } from '../../lib/settingsSave'
import type { DepPrompt } from './ShellNotices'
import { offerOffline } from './StoreOutageNotices'
import type { ActionStatus } from './useActionStatus'
import { useGameHolds } from './useGameHolds'

function commandActionUnavailable(game: Game, requested?: 'play' | 'install'): string | null {
  if (requested === 'play' && (game.isRunning || game.canStop || !game.installed)) {
    return game.installed ? 'The selected game is already running.' : 'Install this game first.'
  }
  if (requested === 'install' && (game.installed || resolvePrimaryAction(game) !== 'install')) {
    return game.installed ? 'This game is already installed.' : 'Exo cannot install this game yet. Install it from its store app.'
  }
  return null
}

function pendingTransfer(gameId: string, kind: 'install' | 'update'): InstallProgress {
  const status = kind === 'update' ? 'Starting update…' : 'Starting install…'
  return { gameId, phase: 'preparing', percent: null, status, canCancel: true, isActive: true }
}

/**
 * Owns game execution, transfer lifecycle, queued jobs, runtime prompts, and preferred source saves.
 */
export function useGameActions({
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
}: {
  gamesRef: MutableRefObject<Game[]>
  selected: Game | null
  selectedCard: Game | null
  selectedIdRef: MutableRefObject<string | null>
  overlayOpen: boolean
  settings: LauncherSettings | null
  setGames: (update: (items: Game[]) => Game[]) => void
  setSettings: (update: (current: LauncherSettings | null) => LauncherSettings | null) => void
  setSelectedVariantId: (id: string | null) => void
  loadLibrary: (force?: boolean, silent?: boolean, full?: boolean, quietErrors?: boolean) => Promise<void>
  setStatus: (message: string | null, gameId?: string | null, sticky?: boolean, tone?: ActionStatus['tone']) => void
}) {
  const { holds, holdsGame, heldNow, hold: holdGame, releaseAfter, cancelRelease } = useGameHolds()
  const busy = holds.size > 0
  const dependencyPending = useRef(false)
  const transferRequests = useRef(new Set<string>())
  const [pendingTransfers, setPendingTransfers] = useState<string[]>([])
  const [startingId, setStartingId] = useState<string | null>(null)
  const progressVersion = useRef(0)
  const [progress, setProgress] = useState<InstallProgress | null>(null)
  const progressFlushRef = useRef<{ cancel: () => void } | null>(null)
  const writeProgress = useCallback(
    (next: InstallProgress | null | ((p: InstallProgress | null) => InstallProgress | null)) => {
      progressFlushRef.current?.cancel()
      setProgress(next)
    },
    [],
  )
  const [transferFailures, setTransferFailures] = useState<Record<string, string>>({})
  const stoppedTransfers = useRef(new Set<string>())
  const [queuedIds, setQueuedIds] = useState<string[]>([])
  const [depPrompt, setDepPrompt] = useState<DepPrompt | null>(null)
  const declinedDeps = useRef<string | null>(null)
  const sourceSaveVersions = useRef(new Map<string, number>())
  const confirmedSources = useRef(new Map<string, string | undefined>())

  const clearTransferFailure = useCallback((gameId: string) => {
    setTransferFailures((current) => {
      if (!(gameId in current)) return current
      const next = { ...current }
      delete next[gameId]
      return next
    })
  }, [])

  const settleTransfer = useCallback((res: InstallResponse, ok: boolean, gameId: string, version: number) => {
    if (version !== progressVersion.current) return
    if (res.progress) { writeProgress(res.progress); return }
    writeProgress((p) => (p?.gameId === gameId ? {
      ...p,
      isActive: false,
      canCancel: false,
      percent: res.handoffOnly ? null : p.percent,
      phase: res.handoffOnly ? 'idle' : ok ? 'completed' : 'failed',
    } : p))
  }, [writeProgress])

  const promptForDeps = useCallback((res: LaunchResponse | InstallResponse, action: 'play' | 'install' | 'update', gameId: string): boolean => {
    if (!res.needsDependencies || !res.missingDependencies?.length) return false
    setDepPrompt({ action, gameId, deps: res.missingDependencies })
    setStatus(res.message || 'Install required', gameId)
    if (action === 'play') holdGame(gameId, null)
    else writeProgress((p) => (p?.gameId === gameId ? null : p))
    return true
  }, [holdGame, setStatus, writeProgress])

  const runPlay = useCallback(async (game: Game, skipDeps: boolean, scope: string | null) => {
    setStatus('Preparing launch…', scope)
    const res = await host.launch(game.id, { skipDeps })
    if (promptForDeps(res, 'play', game.id)) return
    if (res.offline) {
      setStatus(res.message, scope, true)
      offerOffline(
        res.offline,
        { id: `outage:${res.offline}`, title: game.title, text: res.message || 'Its store is offline.' },
        () => void runPlay(game, skipDeps, scope),
      )
      holdGame(game.id, null)
      return
    }
    setStatus(res.message || (res.ok ? null : 'Could not start the game.'), scope, !res.ok)
    if (!res.ok) { holdGame(game.id, null); return }
    releaseAfter(game.id, 1500)
  }, [holdGame, promptForDeps, releaseAfter, setStatus])

  const runTransfer = useCallback(async (game: Game, kind: 'install' | 'update', skipDeps: boolean, scope: string | null = game.id) => {
    clearTransferFailure(game.id)
    stoppedTransfers.current.delete(game.id)
    setStatus(kind === 'update' ? 'Starting update…' : 'Starting install…', scope)
    const version = progressVersion.current
    writeProgress((p) => (p?.isActive ? p : pendingTransfer(game.id, kind)))
    const res = kind === 'update'
      ? await host.update(game.id, { skipDeps })
      : await host.install(game.id, undefined, game.title, { skipDeps })
    if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
    if (promptForDeps(res, kind, game.id)) return res
    if (res.queued) {
      setStatus(res.message || null, scope)
      writeProgress((p) => (p?.gameId === game.id ? null : p))
      return res
    }
    const done = kind === 'update' ? 'Update' : 'Install'
    const stopped = stoppedTransfers.current.delete(game.id)
    if (!res.ok && !stopped) setTransferFailures((current) => ({ ...current, [game.id]: res.message || `${done} failed` }))
    setStatus(res.message || (res.handoffOnly ? 'Finish this in the store app.' : res.ok ? null : `${done} failed`), scope)
    settleTransfer(res, res.ok, game.id, version)
    return res
  }, [clearTransferFailure, promptForDeps, setStatus, settleTransfer, writeProgress])

  const statusScope = useCallback((game: Game): string | null => (
    overlayOpen && statusBelongsToSelection(game.id, selected) ? game.id : null
  ), [overlayOpen, selected])

  const runPrimary = useCallback(async (skipDeps = false, target?: Game, requestedAction?: 'play' | 'install') => {
    const game = target ?? selected
    if (!game || transferRequests.current.has(game.id)) return
    if (declinedDeps.current === game.id) skipDeps = true
    declinedDeps.current = null
    const scope = statusScope(game)
    const resolvedAction = resolvePrimaryAction(game)
    const unavailable = commandActionUnavailable(game, requestedAction)
    if (unavailable) { setStatus(unavailable, scope); return }
    const nextAction = resolvedAction === 'none' ? resolvedAction : requestedAction ?? resolvedAction
    if (nextAction === 'none') {
      setStatus(
        game.installed
          ? 'Exo cannot launch this copy. Open it from its store app.'
          : 'Exo cannot install this game yet. Install it from its store app.',
        scope,
      )
      return
    }
    if (nextAction === 'play') {
      const press = pressOutcome(heldNow(game.id), 'play', game.title)
      if (!press.go) { setStatus(press.say, scope); return }
      holdGame(game.id, 'starting')
    } else {
      transferRequests.current.add(game.id)
      setPendingTransfers([...transferRequests.current])
    }
    cancelRelease(game.id)
    setStatus(null, game.id)
    setDepPrompt(null)
    setStartingId(game.id)
    try {
      if (nextAction === 'play') await runPlay(game, skipDeps, scope)
      else await runTransfer(game, nextAction, skipDeps, scope)
    } catch (e) {
      report(e, `LauncherApp.${nextAction}`)
      const failed = nextAction === 'play' ? 'The game didn’t start. Try again.' : nextAction === 'update' ? 'The update didn’t start. Try again.' : 'The install didn’t start. Try again.'
      setStatus(errorText(e, failed), scope, scope === null, 'bad')
      if (nextAction !== 'play') setTransferFailures((current) => ({ ...current, [game.id]: errorText(e, failed) }))
      if (nextAction === 'play') holdGame(game.id, null)
      writeProgress((p) => (p?.gameId === game.id && p.isActive ? { ...p, isActive: false, canCancel: false, phase: 'failed' } : p))
    } finally {
      transferRequests.current.delete(game.id)
      setPendingTransfers([...transferRequests.current])
      setStartingId((current) => (current === game.id ? null : current))
    }
  }, [cancelRelease, heldNow, holdGame, runPlay, runTransfer, selected, setStatus, statusScope, writeProgress])

  const runPrimaryRef = useRef(runPrimary)
  runPrimaryRef.current = runPrimary
  const playFromLibrary = useCallback((game: Game) => {
    const shortcut = shelfAction(game)
    if (shortcut) void runPrimaryRef.current(false, game, shortcut.action)
  }, [])

  const playAs = useCallback(async (game: Game, account: { key: string; name: string }) => {
    setStatus(`Signing in as ${account.name}…`, null)
    try {
      const result = await host.accountsSwitch('riot', account.key)
      if (!result.ok) {
        setStatus(errorText(result.message, 'Exo couldn’t switch the Riot account. Try again.'), null, false, 'bad')
        return
      }
    } catch {
      setStatus('Exo couldn’t switch the Riot account. Try again.', null, false, 'bad')
      return
    }
    playFromLibrary(game)
  }, [playFromLibrary, setStatus])

  const onStopGame = useCallback(async (target?: Game) => {
    const game = target ?? selected
    if (!game || !game.canStop || (progress?.isActive && progress.gameId === game.id)) return
    const press = pressOutcome(heldNow(game.id), 'stop', game.title)
    if (!press.go) { setStatus(press.say, game.id); return }
    holdGame(game.id, 'closing')
    cancelRelease(game.id)
    setStatus(`Closing ${game.title}…`, game.id, true, 'info')
    try {
      const result = await host.stop(game.id)
      setStatus(result.message ?? (result.ok ? null : 'Could not close the game.'), game.id, !result.ok)
      if (result.ok) {
        beginGameRead(game.id)()
        setGames((items) => setExactRunState(items, game.id, false, false))
        const current = beginGameRead(game.id)
        void host.getGame(game.id).then((refreshed) => {
          if (!refreshed.ok || !refreshed.game) return
          const next = refreshed.game
          setGames((items) => current() ? mergeExactGame(items, next) : mergeExactGameKeepingRunState(items, next))
        }).catch((e: unknown) => report(e, 'LauncherApp.stop.getGame'))
      }
    } catch (error) {
      setStatus(errorText(error, 'Exo couldn’t close the game. Try again.'), game.id, true)
    } finally {
      holdGame(game.id, null)
    }
  }, [cancelRelease, heldNow, holdGame, progress?.gameId, progress?.isActive, selected, setGames, setStatus])

  const onPrimary = useCallback(async () => {
    if (selected?.canStop) { await onStopGame(selected); return }
    await runPrimary(false)
  }, [onStopGame, runPrimary, selected])

  const onContinueAfterDeps = useCallback(async (skipChecks = false) => {
    if (!depPrompt) return
    const gameId = depPrompt.gameId
    const game = findLibraryGame(gamesRef.current, gameId)
    setDepPrompt(null)
    if (!game) { setStatus('The original game is no longer in your library.', gameId); return }
    await runPrimary(skipChecks, materializeVariant(game, gameId))
  }, [depPrompt, gamesRef, runPrimary, setStatus])

  const onOfferMissingDeps = useCallback(async () => {
    if (!depPrompt || dependencyPending.current) return
    const prompt = depPrompt
    dependencyPending.current = true
    try {
      const result = await host.runtimesInstall('missing', prompt.deps.map((dep) => dep.id))
      const failed = result.results.find((item) => item.outcome === 'failed')
      if (failed) throw new Error(`${failed.name}: ${failed.message}`)
      if (!result.ok) throw new Error(result.message || 'The runtimes could not be installed. Try again.')
      if (result.results.some((item) => item.outcome === 'restart')) {
        setDepPrompt((prev) => (prev === prompt ? { ...prev, awaitingContinue: true } : prev))
        setStatus('Restart Windows to finish installing, then Check and retry.', prompt.gameId)
        return
      }
      await onContinueAfterDeps()
    } catch (error) {
      setStatus(errorText(error, 'The runtime didn’t install. Try again.'), prompt.gameId, true)
    } finally {
      dependencyPending.current = false
    }
  }, [depPrompt, onContinueAfterDeps, setStatus])

  const onCancel = useCallback(async () => {
    try {
      const id = progress?.isActive ? progress.gameId : null
      if (!id) return
      const res = await host.cancelInstall(id)
      if (!res.ok) throw new Error(res.message || 'Exo couldn’t cancel. Try again.')
      setStatus(res.message ?? null, progress?.gameId ?? selectedIdRef.current)
    } catch (e) {
      setStatus(errorText(e, 'Exo couldn’t cancel. Try again.'), progress?.gameId ?? selectedIdRef.current)
    }
  }, [progress?.gameId, progress?.isActive, selectedIdRef, setStatus])

  const onDequeue = useCallback(async () => {
    const id = selected ? queuedIds.find((queued) => isInstallingGame(selected, queued)) ?? selected.id : null
    if (!id) return
    try {
      const res = await host.dequeue(id)
      if (res.queuedGameIds) setQueuedIds(res.queuedGameIds)
      setStatus(res.message ?? (res.ok ? null : 'Could not remove this game from the queue.'), id, !res.ok)
    } catch (e) {
      setStatus(errorText(e, 'Exo couldn’t take it off the queue. Try again.'), id, true)
    }
  }, [queuedIds, selected, setStatus])

  const selectSource = useCallback((id: string) => {
    const card = selectedCard
    if (!card?.variants?.some((variant) => variant.id === id)) return
    setSelectedVariantId(id)
    setStatus(null, id)
    const key = card.canonicalTitleKey
    if (!key) return
    if (!confirmedSources.current.has(key)) confirmedSources.current.set(key, settings?.preferredGameSources?.[key])
    const version = (sourceSaveVersions.current.get(key) ?? 0) + 1
    sourceSaveVersions.current.set(key, version)
    setSettings((current) => current ? { ...current, preferredGameSources: { ...current.preferredGameSources, [key]: id } } : current)
    saveSettingsPatch({ preferredGameSources: { [key]: id } })
      .then((saved) => { confirmedSources.current.set(key, saved.preferredGameSources?.[key] ?? id) })
      .catch((error: unknown) => {
        if (sourceSaveVersions.current.get(key) !== version) return
        const previous = confirmedSources.current.get(key)
        setSettings((current) => {
          if (!current) return current
          const preferences = { ...current.preferredGameSources }
          if (previous) preferences[key] = previous
          else delete preferences[key]
          return { ...current, preferredGameSources: preferences }
        })
        const openCard = selectedIdRef.current ? cardForExactId(gamesRef.current, selectedIdRef.current) : undefined
        if (openCard?.canonicalTitleKey === key) setSelectedVariantId(previous ?? card.selectedVariantId ?? card.id)
        setStatus(`Could not save the game source: ${error instanceof Error ? error.message : 'Try again.'}`, previous ?? card.id, true)
      })
  }, [gamesRef, selectedCard, selectedIdRef, setSelectedVariantId, setSettings, setStatus, settings?.preferredGameSources])

  const openGameFolder = useCallback(async (game: Game) => {
    try {
      const result = await host.openFolder(game.id)
      if (!result.ok) setStatus(errorText(result.message, 'Game folder not found.'), null, false, 'bad')
    } catch (error) {
      setStatus(errorText(error, 'Exo couldn’t open the game folder. Try again.'), null, false, 'bad')
    }
  }, [setStatus])

  const uninstallGames = useCallback(async (picked: Game[]) => {
    const [doing, done] = picked.every((game) => game.store === 'local') ? ['Removing', 'Removed.'] : ['Uninstalling', 'Uninstalled.']
    setStatus(`${doing} ${picked.length === 1 ? picked[0].title : `${picked.length} games`}…`, null)
    let failed: string | null = null
    let handoff = false
    let queued = false
    for (const game of picked) {
      const r = await host.uninstall(game.id).catch((error: unknown) => ({ ok: false, message: error instanceof Error ? error.message : undefined, queued: false, handoffOnly: false }))
      if (!r.ok) failed ??= errorText(r.message, `Exo couldn’t uninstall ${game.title}. Try again.`)
      handoff ||= !!r.handoffOnly
      queued ||= !!r.queued
    }
    setStatus(failed ?? (handoff ? 'Finish uninstalling in the store app.' : queued ? 'Queued.' : done), null, !!failed)
    void loadLibrary(true, true)
  }, [loadLibrary, setStatus])

  return {
    busy,
    holdsGame,
    pendingTransfers,
    startingId,
    progressVersion,
    progress,
    setProgress,
    progressFlushRef,
    writeProgress,
    transferFailures,
    setTransferFailures,
    clearTransferFailure,
    stoppedTransfers,
    queuedIds,
    setQueuedIds,
    depPrompt,
    setDepPrompt,
    declinedDeps,
    runPrimary,
    playFromLibrary,
    playAs,
    onStopGame,
    onPrimary,
    onOfferMissingDeps,
    onContinueAfterDeps,
    onCancel,
    onDequeue,
    selectSource,
    openGameFolder,
    uninstallGames,
  }
}

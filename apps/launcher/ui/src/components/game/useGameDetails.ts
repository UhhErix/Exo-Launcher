import { useEffect, useRef, useState } from 'react'
import {
  host,
  onHostEvent,
  type DlssStatus,
  type DriveHeat,
  type Game,
} from '../../lib/host'
import { report } from '../../lib/report'
import { loadUpscalerStatus, peekUpscalerStatus } from '../../lib/upscalerCache'

export type Achievements = { unlocked: number; total: number }

/**
 * What the game sheet last read for a game, so a sheet opened again draws it at
 * once and reads again behind it (stale while it revalidates). Bounded: past
 * `max` games the one read longest ago goes.
 */
export function sheetMemory<T>(max = 64) {
  const reads = new Map<string, T>()
  return {
    get: (id: string): T | null => reads.get(id) ?? null,
    set: (id: string, value: T) => {
      reads.delete(id)
      reads.set(id, value)
      if (reads.size > max) reads.delete(reads.keys().next().value as string)
    },
  }
}

const achievementsRead = sheetMemory<Achievements>()

/** One exact store entry. Two cards for the same title never share a fetch. */
function selectionKeyFor(game: Pick<Game, 'id' | 'store'>): string {
  return `${game.id}\u001f${game.store}`
}

/**
 * A game's achievements and its drive's heat in the last session (only when it came near its limit or slowed itself), for
 * the game page and Play's game room. The last read shows at once; one read answers the cached counts; the host pushes a
 * newer read. `selectionKey` is the exact store entry's (selectionKeyFor).
 */
export function useAchievements(id: string, selectionKey: string, installed: boolean) {
  const [achievements, setAchievements] = useState<Achievements | null>(() => achievementsRead.get(selectionKey))
  const [driveHeat, setDriveHeat] = useState<DriveHeat | null>(null)
  // True until the game's extras answer (or fail): the sheet shows a skeleton where its numbers will go.
  const [loading, setLoading] = useState(true)
  useEffect(() => {
    let active = true
    let pushed = false
    setLoading(true)
    const show = (value: Achievements) => {
      achievementsRead.set(selectionKey, value)
      setAchievements(value)
    }
    setAchievements(achievementsRead.get(selectionKey))
    setDriveHeat(null)
    void host.gameExtras(id).then((extras) => {
      if (!active) return
      setDriveHeat(extras.driveHeat ?? null)
      if (!pushed && extras.achievementsTotal && extras.achievementsUnlocked != null)
        show({ unlocked: extras.achievementsUnlocked, total: extras.achievementsTotal })
      setLoading(false)
    }).catch((e: unknown) => {
      if (active) setLoading(false)
      report(e, 'useGameDetails.extras', { level: 'warn' })
    })
    const off = onHostEvent('game.achievements', (data) => {
      if (!active || data.id !== id || data.total <= 0) return
      pushed = true
      show({ unlocked: data.unlocked, total: data.total })
    })
    return () => { active = false; off() }
  }, [id, installed, selectionKey])
  return { achievements, driveHeat, loading }
}

/**
 * Optional controls load after the sheet renders, keyed by the exact store
 * entry. Library details never request catalog text or block the Play action.
 */
export function useGameDetails(selected: Game) {
  const selectionKey = selectionKeyFor(selected)
  // A new symbol for every selection and every read: an answer for anything older is dropped.
  const upscalerGeneration = useRef(Symbol())
  const { id, installed } = selected
  const { achievements, driveHeat, loading } = useAchievements(id, selectionKey, installed)
  const [dlss, setDlss] = useState<DlssStatus | null>(() => (installed ? peekUpscalerStatus(id) : null))

  const refreshDlss = async (force = false) => {
    const generation = Symbol()
    upscalerGeneration.current = generation
    // Keep the current rows while verified versions are read back from disk.
    await loadUpscalerStatus(id, force)
      .then((status) => {
        if (upscalerGeneration.current === generation)
          setDlss(previous => status.ok === false
            ? {...status, message:status.message || 'Versions could not be refreshed. Showing the last checked files.', items:previous?.items ?? status.items}
            : status)
      })
      .catch((e: unknown) => {
        report(e, 'useGameDetails.refreshDlss')
        if (upscalerGeneration.current === generation)
          setDlss(previous => ({...previous, ok:false, message:'Versions could not be refreshed. Showing the last checked files.', items:previous?.items ?? []}))
      })
  }

  useEffect(() => {
    const generation = Symbol()
    upscalerGeneration.current = generation
    if (!installed) {
      setDlss(null)
      return
    }
    let active = true
    setDlss(peekUpscalerStatus(id))
    const current = () => active && upscalerGeneration.current === generation
    // Newest versions ship with Exo, so one read answers fully.
    void loadUpscalerStatus(id).then(status => {
      if (current()) setDlss(status)
    }).catch((e: unknown) => {
      report(e, 'useGameDetails.upscalerStatus')
      // The line opens PC › Graphics › Upscalers, where the files are checked again.
      if (current()) setDlss({ok:false, message:'Could not check the files.', items:[]})
    })
    return () => { active = false; upscalerGeneration.current = Symbol() }
  }, [id, installed, selectionKey])

  return { selectionKey, achievements, driveHeat, loading, dlss, refreshDlss }
}

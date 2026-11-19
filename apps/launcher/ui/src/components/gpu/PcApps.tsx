import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type InstalledApp, type RuntimeItem } from '../../lib/host'
import { report } from '../../lib/report'
import { GameOverlay } from '../../motion'
import { AppsPanel } from '../apps/AppsPanel'

/** What the room says about this PC's apps, without the list: how many, the bloatware among them, the runtimes. */
type AppsSummary = { apps: number; bloat: number; runtimes: RuntimeItem[] | null }

/** The last summary, so the room shows it at once the next time it opens and reads again behind it. */
let last: AppsSummary | null = null

/**
 * The apps and runtimes, read the first time the room shows (the list and the runtimes each cost
 * the host a walk of the disk or registry, so not on every return) and again after the Apps sheet closes, where an app
 * may have gone. `read` is a new number each time the room should read again.
 */
export function useAppsSummary(active: boolean, read: number): AppsSummary | null {
  const [summary, setSummary] = useState<AppsSummary | null>(last)
  const seen = useRef(-1)
  useEffect(() => {
    if (!active || seen.current === read) return
    seen.current = read
    let live = true
    Promise.allSettled([host.appsList(), host.runtimesList()]).then(([list, runtimes]) => {
      if (!live) return
      for (const failed of [list, runtimes]) if (failed.status === 'rejected') report(failed.reason, 'PcApps.read', { level: 'warn' })
      const programs = list.status === 'fulfilled' ? list.value.programs : null
      const next: AppsSummary = {
        apps: programs?.length ?? last?.apps ?? 0,
        bloat: programs ? bloatOf(programs) : last?.bloat ?? 0,
        runtimes: runtimes.status === 'fulfilled' ? runtimes.value.items : last?.runtimes ?? null,
      }
      last = next
      setSummary(next)
    })
    return () => { live = false }
  }, [active, read])
  // The host's read behind its last list found another: the counts follow it.
  useEffect(() => onHostEvent('apps.changed', () => {
    host.appsList().then(({ programs }) => {
      if (!last) return
      last = { ...last, apps: programs.length, bloat: bloatOf(programs) }
      setSummary(last)
    }, (error: unknown) => report(error, 'PcApps.changed', { level: 'warn' }))
  }), [])
  return summary
}

const bloatOf = (programs: InstalledApp[]) => programs.filter(app => app.bloat && app.removable && !app.replaces).length

/**
 * PC › Apps, a chip on This PC's line: it opens the Apps sheet, Exo's uninstaller; it counts the bloatware while there is some.
 */
export function AppsChip({ summary, onOpen }: { summary: AppsSummary | null; onOpen: () => void }) {
  return (
    <button type="button" className="exo-pc-chip" data-pc-menu="apps" aria-haspopup="dialog" onClick={onOpen}>
      Apps{summary?.bloat ? ` · ${summary.bloat}` : null}
    </button>
  )
}

/**
 * The Apps sheet (DESIGN.md › PC › Apps): Exo's uninstaller, as large glass over the dimmed room. Escape or a press
 * outside closes it. The list scrolls under its head. The game runtimes are PC › Updates'.
 */
export function AppsSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <GameOverlay open={open} label="Apps" scrim={<button type="button" className="exo-game-overlay-scrim" aria-label="Close" tabIndex={-1} onClick={onClose} />}>
      <div className="exo-pc-apps-sheet">
        <section className="exo-pc-apps-part" aria-labelledby="pc-apps-title">
          <AppsPanel active={open} />
        </section>
      </div>
    </GameOverlay>
  )
}

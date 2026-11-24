import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type InstalledApp, type RuntimeItem } from '../../lib/host'
import { report } from '../../lib/report'

type AppsSummary = { apps: number; bloat: number; runtimes: RuntimeItem[] | null; runtimesDeferred: boolean }

let last: AppsSummary | null = null

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
        runtimesDeferred: runtimes.status === 'fulfilled' ? Boolean(runtimes.value.deferred) : last?.runtimesDeferred ?? false,
      }
      last = next
      setSummary(next)
    })
    return () => { live = false }
  }, [active, read])
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

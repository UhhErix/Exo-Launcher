import { useCallback, useEffect, useRef, useState } from 'react'
import type { LauncherSettings } from '../../lib/host'
import { fullChangelog, missedReleases, releasesThrough, WHATS_NEW, type WhatsNewRelease } from '../../lib/whatsNew'

/**
 * The Changelog sheet's releases while it is open, newest first. What changed since the version last seen opens
 * once, while unseen and nothing in `busy` holds (booting, away from the Library, a game's page, a menu or dialog
 * open, the update sheet up or still to come); Settings › About opens every release up to the running one, all the way back. Closing
 * marks the version seen.
 */
export function useWhatsNew(settings: LauncherSettings | null, busy: readonly boolean[], persist: (patch: Partial<LauncherSettings>) => void) {
  const [open, setOpen] = useState<'missed' | 'all' | null>(null)
  const shown = useRef(false)
  const [full, setFull] = useState<WhatsNewRelease[] | null>(null)
  const version = settings?.appVersion
  const seen = settings?.whatsNewSeen
  const due = !!version && missedReleases(version, seen).length > 0
  const free = !busy.includes(true)

  useEffect(() => {
    if (!due || !free) return
    // A beat after the Library settles, and not over a dialog of a room's own (a folder, a Discord dialog).
    const timer = window.setTimeout(() => {
      if (shown.current || document.querySelector('[aria-modal="true"]:not([hidden], [hidden] *, [inert] *)')) return
      shown.current = true
      setOpen('missed')
    }, 600)
    return () => window.clearTimeout(timer)
  }, [due, free])

  useEffect(() => {
    const show = () => {
      shown.current = true
      setOpen('all')
    }
    window.addEventListener(WHATS_NEW, show)
    return () => window.removeEventListener(WHATS_NEW, show)
  }, [])

  // The whole history is read when it is first asked for; the recent releases show meanwhile, and stay if it can't be read.
  useEffect(() => {
    if (open !== 'all' || !version || full) return
    let live = true
    fullChangelog(version).then(all => { if (live) setFull(all) }, () => { })
    return () => { live = false }
  }, [open, version, full])

  const close = useCallback(() => {
    setOpen(null)
    if (version && seen !== version) persist({ whatsNewSeen: version })
  }, [version, seen, persist])

  const releases: WhatsNewRelease[] | null = !open || !version ? null : open === 'missed' ? missedReleases(version, seen) : full ?? releasesThrough(version)
  return [releases, close] as const
}

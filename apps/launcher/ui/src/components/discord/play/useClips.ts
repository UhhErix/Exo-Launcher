import { useCallback, useEffect, useState } from 'react'
import type { CaptureItem } from '../../../lib/capture'
import { host, onHostEvent } from '../../../lib/host'
import { onPageActivityChanged, pageIsVisible } from '../../../lib/pageActivity'
import { report } from '../../../lib/report'

/**
 * Your captures, newest first (host.captureList), for Play's Clips and a game room's strip: `items` is null while they are read,
 * `failed` when they could not be (not an empty folder), and a new capture or a shrunk one lands in place. Read again when Exo
 * comes back to the front, since captures are saved while it is out of sight.
 */
export function useClips(gameId: string | null, limit: number) {
  const [items, setItems] = useState<CaptureItem[] | null>(null)
  const [failed, setFailed] = useState(false)
  const load = useCallback(() => {
    setFailed(false)
    host.captureList(gameId, limit).then(result => {
      if (result.ok && result.items) setItems(result.items)
      else setFailed(true)
    }, (error: unknown) => { report(error, 'useClips.list', { level: 'warn' }); setFailed(true) })
  }, [gameId, limit])
  useEffect(() => {
    setItems(null)
    load()
    let missed = false
    const offSaved = onHostEvent('capture.saved', ({ item }) => {
      if (gameId && item.gameId !== gameId) return
      setItems(list => list ? [item, ...list.filter(other => other.id !== item.id)].slice(0, limit || undefined) : list)
    })
    const offShrunk = onHostEvent('capture.shrunk', ({ id, item }) => { if (item) setItems(list => list?.map(other => other.id === id ? item : other) ?? list) })
    const offPage = onPageActivityChanged(() => {
      if (!pageIsVisible()) { missed = true; return }
      if (missed) { missed = false; load() }
    })
    return () => { offSaved(); offShrunk(); offPage() }
  }, [gameId, limit, load])
  return { items, failed, reload: load }
}

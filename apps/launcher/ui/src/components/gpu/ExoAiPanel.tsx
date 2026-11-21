import { useCallback, useEffect, useState } from 'react'
import { host, type ExoAiState } from '../../lib/host'
import { usePageVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'

export function useExoAi(active: boolean, open: boolean) {
  const [state, setState] = useState<ExoAiState | null>(null)
  const visible = usePageVisible()
  const refresh = useCallback(() => {
    host.exoAiState().then(setState, (e: unknown) => report(e, 'ExoAi.state', { level: 'warn' }))
  }, [])
  useEffect(() => {
    if (!active && !open) return
    refresh()
    const intervalMs = visible ? (open ? 5000 : 15000) : 30000
    const timer = window.setInterval(refresh, intervalMs)
    return () => window.clearInterval(timer)
  }, [active, open, visible, refresh])
  return { state, setState, refresh }
}

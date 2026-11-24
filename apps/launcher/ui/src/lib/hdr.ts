import { useEffect, useState } from 'react'
import { host, onHostEvent, type HdrState } from './host'
import { report } from './report'

/** The main screen's HDR state: read on mount, when shown again, and when Exo's window comes back while shown. */
export function useHdrState(active = true) {
  const [state, setState] = useState<HdrState | null>(null)
  useEffect(() => {
    let live = true
    const load = () => { host.hdrState().then(next => { if (live) setState(next) }).catch(e => report(e, 'hdr.state')) }
    load()
    if (!active) return () => { live = false }
    window.addEventListener('focus', load)
    const offWarn = onHostEvent('hdr.warning', () => load())
    return () => { live = false; window.removeEventListener('focus', load); offWarn() }
  }, [active])
  return state
}

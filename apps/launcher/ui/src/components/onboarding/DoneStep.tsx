import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { CHECK_PATH } from '../../brand/icons'

/** The Done step (SPEC-v2): a check drawn on in 400 ms, "You're all set", a summary in three tiles, then Open library. */
export function DoneStep() {
  const [signedIn, setSignedIn] = useState<boolean | null>(null)
  useEffect(() => {
    void host.exoAccountState().then(
      (state) => setSignedIn(state.signedIn),
      (error: unknown) => report(error, 'DoneStep.state', { level: 'warn' }),
    )
  }, [])
  return (
    <div className="exo-ob-done">
      <svg className="exo-ob-check" viewBox="0 0 24 24" width={48} height={48} aria-hidden="true">
        <path className="exo-ob-check-path" d={CHECK_PATH} pathLength={1} fill="none" />
      </svg>
      <ul className="exo-ob-summary" aria-label="Summary">
        <li><span className="exo-ob-tile-title">Account</span><span className="exo-ob-tile-detail">{signedIn === null ? '' : signedIn ? 'Signed in' : 'Sign in later'}</span></li>
        <li><span className="exo-ob-tile-title">Library</span><span className="exo-ob-tile-detail">Ready</span></li>
        <li><span className="exo-ob-tile-title">Settings</span><span className="exo-ob-tile-detail">Saved</span></li>
      </ul>
    </div>
  )
}

import type { ReactNode } from 'react'
import { CopyLogs } from './ErrorNote'

/**
 * Exo's one error block for a place that did not load (DESIGN.md › Controls): what failed as a heading, one plain sentence,
 * Try again (the one wording for the action, never Retry or Open again), and Copy logs beside it. `log` is the detail
 * Copy logs reports; it is never printed (the host's own words stay out of the page, in app.log through report()).
 * `children` are extra actions under the row (Music's Change service).
 */
export function ErrorState({ title, message, log, onRetry, busy = false, children }: {
  title: string
  message: string
  log?: string
  onRetry: () => void
  busy?: boolean
  children?: ReactNode
}) {
  return (
    <div className="exo-empty exo-error-state" role="alert">
      <h2>{title}</h2>
      <p>{message}</p>
      <div className="exo-empty-actions">
        <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={onRetry}>Try again</button>
        <CopyLogs error={log ?? message} />
      </div>
      {children}
    </div>
  )
}

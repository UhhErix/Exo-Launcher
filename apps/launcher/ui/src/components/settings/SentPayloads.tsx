import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'

/**
 * View what's sent, in the app: the last payloads exactly as they left this PC, oldest first (diagnostics SPEC 5.2). Used
 * by Settings > Privacy, so the person can read what was sent without leaving Exo.
 */
export function SentPayloads() {
  const [payloads, setPayloads] = useState<string[] | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    host.telemetrySent().then(done => setPayloads(done.payloads), error => {
      report(error, 'SentPayloads.read')
      setFailed(true)
    })
  }, [])

  if (failed) return <p className="text-[length:calc(12px*var(--exo-scale,1))] text-muted" role="alert">What was sent could not be read.</p>
  if (payloads === null) return <p className="text-[length:calc(12px*var(--exo-scale,1))] text-muted" role="status">Reading what was sent…</p>
  if (payloads.length === 0) return <p className="text-[length:calc(12px*var(--exo-scale,1))] text-muted">Nothing has been sent from this PC.</p>
  return (
    <ol className="flex flex-col gap-2">
      {payloads.map((payload, index) => (
        <li key={index}>
          <pre className="overflow-x-auto whitespace-pre-wrap break-all text-[length:calc(12px*var(--exo-scale,1))] text-muted">{payload}</pre>
        </li>
      ))}
    </ol>
  )
}

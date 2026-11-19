import { useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { Button } from '../ui/Button'
import { Row, Section } from './parts'
import { SentPayloads } from './SentPayloads'
import { errorText } from '../../lib/errorWords'

/** Settings › Privacy: what was sent, and Delete my data. App data and crash reports are always sent (owner, 2026-11-09): no switches. */
export function PrivacySection() {
  const [viewing, setViewing] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [message, setMessage] = useState<string | null>(null)


  async function remove() {
    setConfirming(false)
    try {
      const done = await host.telemetryDelete()
      setMessage(done.ok ? 'Your data was deleted. Exo made a new install ID for this PC.' : errorText(done.message, 'Your data was not deleted. Try again.'))
      if (done.ok) setViewing(false)
    } catch (error) {
      report(error, 'PrivacySection.delete')
      setMessage('Your data was not deleted. Try again.')
    }
  }

  return (
    <Section id="privacy">

      <Row label="What's sent">
        <Button variant="ghost" onPress={() => setViewing(shown => !shown)}>{viewing ? 'Hide' : 'View'}</Button>
      </Row>
      {viewing && <div className="exo-set-item"><SentPayloads /></div>}
      <Row label="Delete my data">
        {confirming ? (
          <>
            <Button variant="ghost" onPress={() => setConfirming(false)}>Cancel</Button>
            <button type="button" className="exo-set-btn is-danger" onClick={() => void remove()}>Delete</button>
          </>
        ) : (
          <button type="button" className="exo-set-btn" onClick={() => setConfirming(true)}>Delete</button>
        )}
      </Row>
      {message && <p className="exo-set-item text-[length:calc(12px*var(--exo-scale,1))] text-muted" role="status">{message}</p>}
    </Section>
  )
}

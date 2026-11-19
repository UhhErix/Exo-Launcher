import { useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { backupDayLabel } from '../../lib/backupDay'
import { Button } from '../ui/Button'
import { Spinner } from '../ui/Spinner'
import { errorText } from '../../lib/errorWords'

const FAILED = 'Your data could not be restored. Start fresh, or try again.'

/**
 * The restore offer (first run only, when another PC's daily backup exists). Restore keeps the step busy until Exo restarts:
 * the footer's Start fresh waits on `busy` (the panel owns it), so a restart is never raced by a decline.
 */
export function RestoreStep({ offer, busy, onBusy, onRefused }: { offer: { day: string; device: string; files: number }; busy: boolean; onBusy: (busy: boolean) => void; onRefused: () => void }) {
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null)

  async function restore() {
    if (busy) return
    onBusy(true)
    setMessage(null)
    try {
      const done = await host.backupRestore({ day: offer.day, device: offer.device })
      if (done.ok) {
        // Stays busy: Exo restarts to load the data.
        setMessage({ text: 'Restored. Exo is restarting to load your data.', error: false })
        return
      }
      // The data is restored but Exo could not restart: this run's saves are frozen, so the step holds with its message, and nothing else is offered.
      if (done.restored) {
        setMessage({ text: done.message, error: false })
        onRefused()
        return
      }
      setMessage({ text: errorText(done.message, FAILED), error: true })
      onBusy(false)
    } catch (error) {
      report(error, 'RestoreStep.restore')
      setMessage({ text: errorText(error, FAILED), error: true })
      onBusy(false)
    }
  }

  return (
    <div className="exo-ob-restore">
      <p className="exo-ob-lede">
        Exo found a backup from {backupDayLabel(offer.day)}: your settings, library folders, collections, custom covers, playtime and notes.
        You sign in again afterwards.
      </p>
      <Button variant="primary" isDisabled={busy} onPress={() => void restore()}>
        {busy ? <Spinner label="Restoring" /> : null}
        {busy ? 'Restoring…' : 'Restore my data'}
      </Button>
      {message && <p className={`exo-ob-note${message.error ? ' is-error' : ''}`} role={message.error ? 'alert' : 'status'}>{message.text}</p>}
    </div>
  )
}

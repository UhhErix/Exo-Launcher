import { useState } from 'react'
import { host, type JunkItem } from '../../lib/host'
import { cleanOutcome } from '../../lib/junkClean'
import { report } from '../../lib/report'
import { Row, State } from '../settings/parts'
import { ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'

/** Junk worth a row: under this, temp files come and go on their own. */
const JUNK_WORTH = 1e9

const size = (bytes: number) => bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`
const junkBytes = (junk: JunkItem[]) => junk.reduce((sum, item) => sum + item.bytes, 0)

/** Junk worth a row: past a gigabyte. */
export const junkWorth = (junk: JunkItem[]) => junkBytes(junk) >= JUNK_WORTH

/**
 * Junk files past a gigabyte (temporary files, old shader caches, Exo's old backups and the rest JunkFiles counts) with
 * Clean, asked in place (it doesn't go to the Recycle Bin): in PC › Needs you and first run's Clean up. What went, or
 * why nothing did, stays on the row. Nothing under a gigabyte.
 */
export function JunkRow({ junk, onCleaned }: { junk: JunkItem[]; onCleaned: () => void }) {
  const [cleaning, setCleaning] = useState(false)
  const [done, setDone] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  if (!junkWorth(junk) && !done && !error) return null

  const clean = async () => {
    setCleaning(true)
    setError(null)
    try {
      const result = await host.junkClean(junk.map(item => item.id))
      const outcome = cleanOutcome(result, size)
      setDone(outcome.done ?? null)
      setError(outcome.error ?? null)
      onCleaned()
    } catch (error) {
      report(error, 'JunkRow.clean')
      setError('Nothing was deleted. Try again.')
    }
    setCleaning(false)
  }

  return (
    <Row label="Junk files" detail={done ? undefined : size(junkBytes(junk))} error={error}>
      {done ? <State on>{done}</State> : (
        <ConfirmButton className="exo-set-btn is-danger" danger question={`Delete ${size(junkBytes(junk))}?`} disabled={cleaning} aria-busy={cleaning}
          lead={cleaning ? <Spinner label="Deleting junk" /> : null} onConfirm={() => void clean()}>
          Clean
        </ConfirmButton>
      )}
    </Row>
  )
}

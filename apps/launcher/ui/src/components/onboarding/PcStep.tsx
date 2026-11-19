import { Row, State } from '../settings/parts'
import { BestCheck, type BestKey, type BestStep } from '../gpu/BestSetup'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'

/**
 * First run's last step, Best settings for this PC: the same steps as the PC room's (gpu/BestSetup.tsx), each a row that
 * turns its ring while it runs and takes a green check once set; the footer's Use best settings runs them.
 */
export function PcStep({ steps, running, landed, failed }: {
  /** Null while this PC is still being read. */
  steps: BestStep[] | null
  running: BestKey | null
  landed: ReadonlySet<BestKey>
  failed: Partial<Record<BestKey, string>>
}) {
  return (
    <div className="exo-set-list">
      {steps === null ? <Wait label="Reading this PC">{times(4, i => <Row label={sample(i)}><State on>Set</State></Row>)}</Wait> : steps.map(step => (
        <Row key={step.key} label={step.what} error={failed[step.key]}>
          {running === step.key ? <State><Spinner label={`Setting ${step.what}`} /> Setting</State>
            : step.done ? <State on><BestCheck landed={landed.has(step.key)} />Set</State> : null}
        </Row>
      ))}
    </div>
  )
}

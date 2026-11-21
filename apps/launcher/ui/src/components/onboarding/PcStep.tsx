import { Row, State, SwitchRow } from '../settings/parts'
import { BestCheck, type BestKey, type BestStep } from '../gpu/BestSetup'
import type { LauncherSettings } from '../../lib/host'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'

export function PcStep({ steps, running, landed, failed, settings, onSave }: {
  steps: BestStep[] | null
  running: BestKey | null
  landed: ReadonlySet<BestKey>
  failed: Partial<Record<BestKey, string>>
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
}) {
  return (
    <div className="exo-set-list">
      {steps === null ? <Wait label="Reading this PC">{times(4, i => <Row label={sample(i)}><State on>Set</State></Row>)}</Wait> : steps.map(step => (
        <Row key={step.key} label={step.what} error={failed[step.key]}>
          {running === step.key ? <State><Spinner label={`Setting ${step.what}`} /> Setting</State>
            : step.done ? <State on><BestCheck landed={landed.has(step.key)} />Set</State> : null}
        </Row>
      ))}
      {settings ? (
        <SwitchRow id="ob-start-with-windows" label="Open Exo when Windows starts" detail="Starts quietly in the background when you sign in." checked={settings.startWithWindows === true}
          onChange={async on => await onSave({ startWithWindows: on }) ? null : 'Not saved. Try again.'} />
      ) : null}
    </div>
  )
}

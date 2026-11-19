import { useState } from 'react'
import { Switch } from '../Switch'
import { lucidSetupWords } from '../settings/LucidSection'
import { Row, State, SwitchRow } from '../settings/parts'
import { BestCheck, type BestKey, type BestStep } from '../gpu/BestSetup'
import { host, type LauncherSettings, type LucidState } from '../../lib/host'
import { AUTO_UPSCALERS_DETAIL, AUTO_UPSCALERS_LABEL } from '../../lib/upscalerNotice'
import { report } from '../../lib/report'
import { Wait, sample, times } from '../ui/Wait'
import { Spinner } from '../ui/Spinner'
import { ActionButton } from '../ui/ConfirmButton'

/**
 * First run's last step, Best settings for this PC: the same steps as the PC room's (gpu/BestSetup.tsx), each a row that
 * turns its ring while it runs and takes a green check once set; the footer's Use best settings runs them (frame
 * generation among them, on a card that gains from it). Then the switch Settings › Downloads has (DownloadsSection in
 * settings/GeneralSection.tsx) for DLSS, FSR and XeSS updating by themselves (on by default, shown as it is, so turning it
 * off here is the choice), and Lucid, the optional headphone EQ in Exo: Set up, which says what it will do first, and once
 * set up, Start with Exo.
 */
export function PcStep({ steps, running, landed, failed, settings, onSave, lucid, onLucid }: {
  /** Null while this PC is still being read. */
  steps: BestStep[] | null
  running: BestKey | null
  landed: ReadonlySet<BestKey>
  failed: Partial<Record<BestKey, string>>
  /** Null until the settings are read; the switches wait for them. */
  settings: LauncherSettings | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  lucid: LucidState | null
  onLucid: (next: LucidState) => void
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
        <SwitchRow id="ob-auto-upscalers" label={AUTO_UPSCALERS_LABEL} detail={AUTO_UPSCALERS_DETAIL} checked={settings.autoUpdateUpscalers !== false}
          onChange={async on => await onSave({ autoUpdateUpscalers: on }) ? null : 'Not saved. Try again.'} />
      ) : null}
      {lucid ? <LucidRows lucid={lucid} onLucid={onLucid} /> : null}
    </div>
  )
}

function LucidRows({ lucid, onLucid }: { lucid: LucidState; onLucid: (next: LucidState) => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  function run(call: Promise<LucidState>, failed: string) {
    setBusy(true)
    setError(null)
    call.then(next => { onLucid(next); setError(next.message ?? null) }, (cause: unknown) => { report(cause, 'PcStep.lucid'); setError(failed) })
      .finally(() => setBusy(false))
  }
  if (!lucid.found) return null
  if (!lucid.ready) {
    return (
      <Row label={lucid.running ? 'Setting up Lucid' : 'Set up Lucid'} detail={lucid.running ? 'Say yes when Windows asks, then choose your devices.' : lucidSetupWords(lucid)} error={error}>
        {lucid.running ? <Spinner label="Setting up Lucid" /> : (
          <ActionButton className="exo-set-btn" timeoutMs={null} context="PcStep.lucidSetup" fallback="Lucid didn't start. Try again."
            why="none" onFailed={failure => setError(failure.text)}
            onPress={() => { setError(null); return host.lucid('setup').then(next => { onLucid(next); return next.message ? { ok: false, message: next.message } : next }) }}>
            Set up
          </ActionButton>
        )}
      </Row>
    )
  }
  return (
    <Row label="Lucid headphone EQ" labelId="ob-lucid" error={error}>
      <Switch checked={lucid.startWithExo} disabled={busy} labelledBy="ob-lucid"
        onChange={on => run(host.lucid('set', { on }), "Lucid didn't change. Try again.")} />
    </Row>
  )
}

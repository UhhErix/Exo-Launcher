import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { backupDayLabel } from '../../lib/backupDay'
import { errorText } from '../../lib/errorWords'
import { ActionButton, ConfirmButton } from '../ui/ConfirmButton'
import { Spinner } from '../ui/Spinner'
import { Row, Section } from './parts'

type Kind = 'now' | 'export' | 'import'

const FAILED: Record<Kind, string> = {
  now: 'The backup was not made. Try again.',
  export: 'The backup file was not saved. Try again.',
  import: 'The backup was not restored. Try again.',
}

/** Settings › Backups: the device-only data a reinstall would lose (LauncherBackup), backed up daily; back up now, export, import. */
export function BackupSection() {
  const [lastDay, setLastDay] = useState<string | null | undefined>(undefined)
  // Backups that stopped (or the last one that left files out), in one plain sentence from the host.
  const [note, setNote] = useState<string | null>(null)
  const [busy, setBusy] = useState<Kind | null>(null)
  // The last action's outcome: a failure is shown under the control that caused it, a success as one status line.
  const [outcome, setOutcome] = useState<{ kind: Kind; ok: boolean; text: string } | null>(null)
  // Restored, but Exo could not restart: this run's saves are frozen, so the section holds with the message (no other backup action).
  const [held, setHeld] = useState<string | null>(null)

  useEffect(() => {
    host.backupStatus().then(status => {
      setLastDay(status.last?.day ?? null)
      setNote(status.note?.sentence ?? null)
    }, error => {
      report(error, 'BackupSection.status')
      setLastDay(null)
    })
  }, [])

  async function run(kind: Kind) {
    if (busy !== null) return
    setBusy(kind)
    setOutcome(null)
    try {
      const done: { ok: boolean; message?: string; cancelled?: boolean; restarting?: boolean; restored?: boolean } = kind === 'now' ? await host.backupNow() : kind === 'export' ? await host.backupExport() : await host.backupImport()
      if (done.cancelled) return
      if (done.restored) {
        setHeld(done.message ?? 'Restored. Close and reopen Exo to finish.')
        return
      }
      if (done.ok) setOutcome({ kind, ok: true, text: done.restarting ? 'Restored. Exo is restarting to load your data.' : done.message ?? 'Done.' })
      else setOutcome({ kind, ok: false, text: errorText(done.message, FAILED[kind]) })
    } catch (error) {
      report(error, `BackupSection.${kind}`)
      setOutcome({ kind, ok: false, text: errorText(error, FAILED[kind]) })
    } finally {
      setBusy(null)
    }
  }

  /** Back up now says how it went in itself (ActionButton): its ring, its check, then the day beside it reads again. */
  async function backUpNow() {
    setOutcome(null)
    setBusy('now')
    try { return await host.backupNow() } finally { setBusy(null) }
  }
  function backedUp() {
    void host.backupStatus().then(status => {
      setLastDay(status.last?.day ?? null)
      setNote(status.note?.sentence ?? null)
    }, error => report(error, 'BackupSection.status', { level: 'warn' }))
  }

  const failed = (kind: Kind) => outcome?.kind === kind && !outcome.ok ? outcome.text : null
  const locked = busy !== null || held !== null

  return (
    <Section id="backups" title="Backups">
      {/* Exo backs up daily on its own: the day of the last one, and the press that makes one now. */}
      <Row label="Last backup" error={failed('now')}>
        <span className="text-[length:calc(12px*var(--exo-scale,1))] text-muted" aria-live="polite">
          {lastDay === undefined ? '' : lastDay === null ? 'None yet' : backupDayLabel(lastDay)}
        </span>
        <ActionButton className="exo-set-btn" disabled={held !== null || (busy !== null && busy !== 'now')} timeoutMs={null} context="BackupSection.now"
          fallback={FAILED.now} why="none" onFailed={error => setOutcome({ kind: 'now', ok: false, text: error.text })}
          onPress={backUpNow} onDone={backedUp}>Back up now</ActionButton>
      </Row>
      {/* Import replaces what is here and restarts Exo: its button asks first, and that question is the only place it is said. */}
      <Row label="Backup file" error={failed('export') ?? failed('import')}>
        <button type="button" className="exo-set-btn" disabled={locked} onClick={() => void run('export')}>
          {busy === 'export' ? <><Spinner label="Exporting" /> Exporting…</> : 'Export'}
        </button>
        <ConfirmButton className="exo-set-btn" question="Replace what's here and restart?" danger disabled={locked} onConfirm={() => void run('import')}>
          {busy === 'import' ? <><Spinner label="Importing" /> Importing…</> : 'Import'}
        </ConfirmButton>
      </Row>
      {note !== null && <p className="exo-set-item text-[length:calc(12px*var(--exo-scale,1))] text-fg" role="status">{note}</p>}
      {held !== null && <p className="exo-set-item text-[length:calc(12px*var(--exo-scale,1))] text-fg" role="status">{held}</p>}
      {held === null && outcome?.ok && <p className="exo-set-item text-[length:calc(12px*var(--exo-scale,1))] text-muted" role="status">{outcome.text}</p>}
    </Section>
  )
}

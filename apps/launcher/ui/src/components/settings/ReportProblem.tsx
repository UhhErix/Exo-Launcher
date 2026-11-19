import { useEffect, useId, useState } from 'react'
import { Check } from '../../brand/icons'
import { bugReportUrl, MAX_CONTACT, MAX_MESSAGE, reportPayload, sentWords } from '../../lib/bugReport'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Dialog } from '../ui/Dialog'
import { ErrorNote } from '../ui/ErrorNote'
import { Spinner } from '../ui/Spinner'
import { Unfold } from '../ui/Unfold'

type Phase = { at: 'editing' } | { at: 'sending' } | { at: 'sent'; words: string }

/**
 * Settings › About › Report a problem (lib/bugReport.ts): what happened, Attach logs (on, with the scrubbed log it sends
 * shown under Show what's sent), and a contact if you'd like a reply. Send goes to the team's private tracker through
 * Exo's report service, so no GitHub account is needed; its button turns into the issue's number in place. Open on
 * GitHub opens Exo Launcher's public bug form with the version and the words, never the logs or the contact.
 */
export function ReportProblem({ version, onClose }: { version: string | null; onClose: () => void }) {
  const [message, setMessage] = useState('')
  const [attachLogs, setAttachLogs] = useState(true)
  const [contact, setContact] = useState('')
  const [excerpt, setExcerpt] = useState<string | null>(null)
  const [showing, setShowing] = useState(false)
  const [phase, setPhase] = useState<Phase>({ at: 'editing' })
  const [error, setError] = useState<string | null>(null)
  const logsLabel = useId()

  // The preview is what Send attaches, read once when the form opens.
  useEffect(() => {
    let live = true
    host.reportPreview()
      .then(result => { if (live) setExcerpt(result.ok ? result.excerpt ?? '' : '') })
      .catch(cause => { report(cause, 'ReportProblem.preview'); if (live) setExcerpt('') })
    return () => { live = false }
  }, [])

  const payload = reportPayload({ message, attachLogs, contact, excerpt })
  const sent = phase.at === 'sent'

  async function send() {
    if (!payload || phase.at !== 'editing') return
    setPhase({ at: 'sending' })
    setError(null)
    try {
      const result = await host.reportSend(payload)
      if (!result.ok) throw new Error(result.message || 'Couldn’t send the report. Try again, or use Open on GitHub.')
      setPhase({ at: 'sent', words: sentWords(result) })
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Couldn’t send the report. Try again, or use Open on GitHub.')
      setPhase({ at: 'editing' })
    }
  }

  return (
    <Dialog title="Report a problem" form className="exo-report" onClose={onClose} actions={
      <>
        <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(bugReportUrl(version, message))}>Open on GitHub</button>
        <button type="button" className="exo-set-btn" onClick={onClose}>{sent ? 'Done' : 'Cancel'}</button>
        <button type="button" className={`exo-set-btn is-primary exo-report-send${sent ? ' is-sent' : ''}`} aria-live="polite"
          disabled={!payload || phase.at !== 'editing' || (attachLogs && excerpt === null)} onClick={() => void send()}>
          {phase.at === 'sending' ? <><Spinner label="Sending" />Sending</> : sent ? <><Check size={14} />{phase.words}</> : 'Send'}
        </button>
      </>
    }>
      <label className="exo-report-label">
        <span>What happened?</span>
        <textarea className="exo-field exo-report-text" data-autofocus value={message} maxLength={MAX_MESSAGE} rows={5} readOnly={sent}
          placeholder="What you did, what you expected and what happened instead" onChange={event => setMessage(event.target.value)} />
      </label>
      <div className="exo-report-row">
        <span id={logsLabel} className="exo-report-row-label">Attach logs</span>
        <button type="button" className="exo-set-link" aria-expanded={showing} disabled={!attachLogs} onClick={() => setShowing(on => !on)}>
          {showing ? 'Hide what’s sent' : 'Show what’s sent'}
        </button>
        <Switch checked={attachLogs} labelledBy={logsLabel} disabled={sent} onChange={on => { setAttachLogs(on); if (!on) setShowing(false) }} />
      </div>
      <Unfold open={showing && attachLogs}>
        <pre className="exo-report-preview" tabIndex={0} aria-label="The log Send attaches">
          {excerpt === null ? 'Reading the log…' : excerpt || 'Nothing in Exo’s log to attach.'}
        </pre>
      </Unfold>
      <label className="exo-report-label">
        <span>Contact (optional)</span>
        <input className="exo-field" value={contact} maxLength={MAX_CONTACT} readOnly={sent}
          placeholder="An email or a name, if you’d like a reply" onChange={event => setContact(event.target.value)} />
      </label>
      <p className="exo-report-note">Send goes privately to Exo’s team. Open on GitHub posts in public, without logs.</p>
      {error ? <ErrorNote className="exo-dialog-error" message={error} /> : null}
    </Dialog>
  )
}

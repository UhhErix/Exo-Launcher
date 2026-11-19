import { memo, useState } from 'react'
import { host } from '../../lib/host'
import { invalidateUpscalerStatus } from '../../lib/upscalerCache'
import { WHATS_NEW } from '../../lib/whatsNew'
import { updateWords } from '../shell/useAppUpdate'
import { Progress, ProgressWords } from '../ui/Progress'
import { ActionButton } from '../ui/ConfirmButton'
import { Row, Section, State } from './parts'
import { ReportProblem } from './ReportProblem'

/** Changelog opens the sheet of every release's notes (LauncherApp); the rest open in the browser. */
const LINKS = [
  { label: 'Changelog', url: null },
  { label: 'Privacy', url: 'https://getexo.app/privacy' },
  { label: 'Support Exo', url: 'https://www.buymeacoffee.com/UhhErix' },
] as const

type CheckUpdate = () => Promise<{ ok: boolean; message?: string; updateAvailable?: boolean; latest?: string | null }>
type Note = { text: string; error: boolean } | null

export type UpdateProps = {
  updateBusy: boolean
  updateError: string | null
  updatePercent: number | null
  updateStatus: string
  updateAvailable: boolean
  onCheckUpdate: CheckUpdate
  onInstallUpdate: () => Promise<void> | void
}

function UpdateRow({ version, updateBusy, updateError, updatePercent, updateStatus, updateAvailable, onCheckUpdate, onInstallUpdate }: UpdateProps & { version: string | null }) {
  const [note, setNote] = useState<Note>(null)

  // The button says how the check went (ActionButton: its ring, its check, or Bad with the reason under the row); the
  // answer's words stay beside it.
  async function check() {
    setNote(null)
    const result = await onCheckUpdate()
    if (result.ok) setNote({ text: result.message || (result.updateAvailable ? `Version ${result.latest ?? ''} is available.` : "You're up to date."), error: false })
    return result
  }

  // A failed install comes back from the updater (updateError), never as a rejection.
  function install() {
    setNote(null)
    void onInstallUpdate()
  }

  // The update going in says what and how much, as the update sheet does.
  const { words, share } = updateWords(updateStatus, updatePercent)
  return (
    <Row label={version ? `Exo Launcher ${version}` : 'Exo Launcher'} error={note?.error ? note.text : updateBusy ? null : updateError}>
      {/* The update going in is a transfer: its words and its bar in the row, where the button was (never a bar in a button). */}
      {updateBusy ? (
        <>
          <State><ProgressWords words={words} value={share} /></State>
          <Progress size="small" value={share} label={words} />
        </>
      ) : (
        <>
          <State>{note && !note.error ? note.text : null}</State>
          {updateAvailable ? (
            <button type="button" className="exo-set-btn is-primary" onClick={install}>Install update</button>
          ) : (
            <ActionButton className="exo-set-btn" context="Settings.checkUpdate" fallback="Couldn’t check for updates. Try again."
              why="none" onFailed={error => setNote({ text: error.text, error: true })} onPress={check}>
              Check for updates
            </ActionButton>
          )}
        </>
      )}
    </Row>
  )
}

function RebuildCachesRow() {
  const [error, setError] = useState<string | null>(null)
  // The button says it in itself (ActionButton): its ring while it clears, its check once cleared; a failure is Bad
  // with the reason under the row.
  async function rebuild() {
    setError(null)
    for (const scope of ['sizes', 'upscalers'] as const) {
      const result = await host.repairCache(scope)
      if (!result.ok) return result
      if (scope === 'upscalers') invalidateUpscalerStatus()
    }
    return { ok: true }
  }
  return (
    <Row label="Clear caches" error={error}>
      <ActionButton className="exo-set-btn" context="Settings.clearCaches" fallback="Caches could not be cleared. Try again."
        why="none" onFailed={failure => setError(failure.text)} onPress={rebuild}>
        Clear
      </ActionButton>
    </Row>
  )
}

/**
 * Settings › About: Exo's version with Check for updates beside it (where people look for it: it was at the foot of
 * General until 3.26), the caches, and the links: Report a problem opens its form (ReportProblem: sent to the team's
 * private tracker, or opened as a public GitHub issue).
 */
export const AboutSection = memo(function AboutSection({ version, ...update }: UpdateProps & { version: string | null }) {
  const [reporting, setReporting] = useState(false)
  return (
    <Section id="updates">
      <UpdateRow version={version} {...update} />
      <RebuildCachesRow />
      <div className="exo-set-item">
        <ul className="exo-set-links">
          <li>
            <button type="button" className="exo-set-link" aria-haspopup="dialog" onClick={() => setReporting(true)}>Report a problem</button>
          </li>
          {LINKS.map(link => (
            <li key={link.label}>
              <button type="button" className="exo-set-link" onClick={() => link.url ? void host.openUrl(link.url) : window.dispatchEvent(new Event(WHATS_NEW))}>{link.label}</button>
            </li>
          ))}
        </ul>
      </div>
      {reporting ? <ReportProblem version={version} onClose={() => setReporting(false)} /> : null}
    </Section>
  )
})

import { memo, useState } from 'react'
import { bugReportUrl } from '../../lib/bugReport'
import { host } from '../../lib/host'
import { invalidateUpscalerStatus } from '../../lib/upscalerCache'
import { WHATS_NEW } from '../../lib/whatsNew'
import { updateWords } from '../shell/useAppUpdate'
import { Progress, ProgressWords } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'
import { Row, Section, State } from './parts'

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
  const [checking, setChecking] = useState(false)
  const [note, setNote] = useState<Note>(null)

  async function check() {
    setChecking(true)
    setNote(null)
    try {
      const result = await onCheckUpdate()
      setNote({
        text: result.message || (result.updateAvailable ? `Version ${result.latest ?? ''} is available.` : "You're up to date."),
        error: !result.ok,
      })
    } catch (error) {
      setNote({ text: error instanceof Error ? error.message : 'Couldn’t check for updates. Try again.', error: true })
    } finally {
      setChecking(false)
    }
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
            <button type="button" className="exo-set-btn" disabled={checking} onClick={() => void check()}>
              {checking ? <Spinner label="Checking for updates" /> : null}
              Check for updates
            </button>
          )}
        </>
      )}
    </Row>
  )
}

function RebuildCachesRow() {
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<Note>(null)
  // Its button is disabled while it works, and a click's render lands before the next click can.
  async function rebuild() {
    setBusy(true)
    setNote(null)
    try {
      for (const scope of ['sizes', 'upscalers'] as const) {
        const result = await host.repairCache(scope)
        if (!result.ok) throw Error(result.message || 'Caches could not be cleared. Try again.')
        if (scope === 'upscalers') invalidateUpscalerStatus()
      }
      setNote({ text: 'Cleared', error: false })
    } catch (error) {
      setNote({ text: error instanceof Error ? error.message : 'Caches could not be cleared. Try again.', error: true })
    } finally {
      setBusy(false)
    }
  }
  return (
    <Row label="Clear caches" error={note?.error ? note.text : null}>
      {note && !note.error ? <State>{note.text}</State> : null}
      <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void rebuild()}>
        {busy ? <Spinner label="Clearing caches" /> : null}
        Clear
      </button>
    </Row>
  )
}

/**
 * Settings › About: Exo's version with Check for updates beside it (where people look for it: it was at the foot of
 * General until 3.26), the caches, and the links: Report a problem opens GitHub's bug form with this version filled in.
 */
export const AboutSection = memo(function AboutSection({ version, ...update }: UpdateProps & { version: string | null }) {
  return (
    <Section id="updates">
      <UpdateRow version={version} {...update} />
      <RebuildCachesRow />
      <div className="exo-set-item">
        <ul className="exo-set-links">
          {[{ label: 'Report a problem', url: bugReportUrl(version) }, ...LINKS].map(link => (
            <li key={link.label}>
              <button type="button" className="exo-set-link" onClick={() => link.url ? void host.openUrl(link.url) : window.dispatchEvent(new Event(WHATS_NEW))}>{link.label}</button>
            </li>
          ))}
        </ul>
      </div>
    </Section>
  )
})

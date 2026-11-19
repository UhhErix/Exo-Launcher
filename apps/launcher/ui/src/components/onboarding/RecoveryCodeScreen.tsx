import { useEffect, useState, type KeyboardEvent } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { recoveryCodeShown, refreshAccount } from '../account/useAccount'
import { Button } from '../ui/Button'
import { RECOVERY_CODE_LENGTH, normalizeRecoveryCode, recoveryCodeConfirmed } from './accountCode'

/**
 * The recovery code, shown once (CLIENT.md 4.1 C): six groups of four, Copy, Save as text file, and a typed confirmation.
 * Continue stays disabled until the typed text reads back as the same 24 characters. Continue tells the host the code was
 * confirmed (the session stops waiting on it) and reads the account state again before the screen goes, so nothing that
 * hosts it can close under it first. While it shows, the account gate leaves this code to it (recoveryCodeShown).
 */
export function RecoveryCodeScreen({ code, onDone, onKeyDown }: { code: string; onDone: () => void; onKeyDown: (event: KeyboardEvent<HTMLElement>) => void }) {
  const [typed, setTyped] = useState('')
  const [note, setNote] = useState<string | null>(null)
  const confirmed = recoveryCodeConfirmed(typed, code)
  const [finishing, setFinishing] = useState(false)
  const [failed, setFailed] = useState(false)
  useEffect(() => recoveryCodeShown(code), [code])

  async function finish() {
    if (!confirmed || finishing) return
    setFinishing(true)
    try {
      const answer = await host.exoAccountCodeConfirmed()
      if (!answer.ok) throw new Error('The code was not marked as confirmed.')
      await refreshAccount()
    } catch (error) {
      // The code stays on screen: leaving now would bring a new code at the next start for a code the player saved.
      report(error, 'RecoveryCodeScreen.confirm', { level: 'warn' })
      setFailed(true)
      setFinishing(false)
      return
    }
    onDone()
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(code)
      setNote('Copied.')
    } catch (error) {
      report(error, 'RecoveryCodeScreen.copy', { level: 'warn' })
      setNote("Copy didn't work. Select the code and copy it.")
    }
  }

  async function save() {
    try {
      const result = await host.exoAccountSaveCode(code)
      if (result.cancelled) return
      setNote(result.ok ? 'Saved.' : result.message ?? "The file wasn't saved. Try again.")
    } catch (error) {
      report(error, 'RecoveryCodeScreen.save')
      setNote("The file wasn't saved. Try again.")
    }
  }

  return (
    <section className="exo-ob-account" onKeyDown={onKeyDown} aria-labelledby="exo-account-code-title">
      <h2 id="exo-account-code-title" className="exo-ob-subtitle">Save your recovery code</h2>
      <p className="exo-ob-lede">This is the only way back in if you forget your password. Exo doesn&rsquo;t keep it, and you won&rsquo;t see it again.</p>
      <ol className="exo-ob-code" aria-label="Recovery code">
        {code.split('-').map((group, index) => <li key={index}>{group}</li>)}
      </ol>
      <div className="exo-ob-form-actions">
        <Button variant="ghost" onPress={() => void copy()}>Copy</Button>
        <Button variant="ghost" onPress={() => void save()}>Save as text file</Button>
      </div>
      {note ? <p className="exo-ob-hint" role="status">{note}</p> : null}
      <label className="exo-ob-label" htmlFor="exo-account-confirm">Type the code to confirm</label>
      <input id="exo-account-confirm" className="exo-field" autoComplete="off" spellCheck={false} value={typed}
        onChange={event => setTyped(event.target.value)} maxLength={RECOVERY_CODE_LENGTH + 8} aria-describedby="exo-account-confirm-hint" />
      <p id="exo-account-confirm-hint" className="exo-ob-hint">
        {normalizeRecoveryCode(typed).length}/{RECOVERY_CODE_LENGTH} characters. Dashes and spaces don&rsquo;t matter.
      </p>
      <div className="exo-ob-form-actions">
        {failed ? <p className="exo-ob-error" role="alert">That didn&rsquo;t go through. Try again.</p> : null}
        <Button variant="primary" className="exo-ob-cta" isDisabled={!confirmed || finishing} onPress={() => void finish()}>{failed ? 'Try again' : 'Continue'}</Button>
      </div>
    </section>
  )
}

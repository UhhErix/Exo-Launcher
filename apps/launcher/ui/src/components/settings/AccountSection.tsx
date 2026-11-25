import { useState, type FormEvent } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { errorText } from '../../lib/errorWords'
import { RecoveryCodeScreen } from '../onboarding/RecoveryCodeScreen'
import { sendAccountRequest, useAccount, type AccountOverview } from '../account/useAccount'
import { heldSentences } from './accountWords'
import { Row, Section } from './parts'
import { ActionButton } from '../ui/ConfirmButton'

type Sessions = { device: string; created: number; lastUsed: number; current: boolean; thisPc: boolean; name?: string | null }[]

const when = (seconds: number) => seconds > 0
  ? new Date(seconds * 1000).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
  : ''

/**
 * Settings › Account, the same page in Exo Launcher, Exo Cinema and Exo Browser: the rows say what each action does and
 * a form opens in place under the row that asked for it. Signed out, one row that signs in or creates the account.
 */
export function AccountSection() {
  const { overview, refresh } = useAccount()
  // A code from a sign-up or reset here is held above the signed-in and signed-out pages: the session is saved before
  // the code is typed back, and the page switching under it must not take the code with it.
  const [recovery, setRecovery] = useState<string | null>(null)
  if (recovery) {
    return (
      <Section id="account" title="Account">
        <div className={SUB}><RecoveryCodeScreen code={recovery} onDone={() => { setRecovery(null); void refresh() }} onKeyDown={() => undefined} /></div>
      </Section>
    )
  }
  if (overview === null) return null
  return overview.signedIn ? <SignedIn overview={overview} onChanged={refresh} /> : <SignedOut onSignedIn={refresh} onCode={setRecovery} />
}

/** What is wrong with sync, or null while it is working: sync runs on its own, so it has a row only when it is stuck. */
function syncProblem(sync: AccountOverview['sync']) {
  if (sync.signedOut) return 'Your session ended. Sign in again to keep syncing.'
  if (sync.pushStopped) return 'Your sync storage is full. Your data stays on this PC.'
  return null
}

/** Sync's row, only while sync is stuck: why, and the one press that tries again now. */
function SyncRow({ sync, onSynced }: { sync: AccountOverview['sync']; onSynced: () => void }) {
  const [error, setError] = useState<string | null>(null)
  const problem = syncProblem(sync)
  if (!problem) return null
  // The button says how it went in itself; the row reads again once its check has shown.
  return (
    <Row label="Sync" detail={problem} error={error}>
      <ActionButton className="exo-set-btn" context="AccountSection.sync" fallback="Exo couldn’t sync. Try again."
        why="none" onFailed={failure => setError(failure.text)} onPress={() => { setError(null); return host.exoAccountSync() }} onDone={onSynced}>Sync now</ActionButton>
    </Row>
  )
}

function SignedIn({ overview, onChanged }: { overview: AccountOverview; onChanged: () => void }) {
  return (
    <>
      <Section id="account" title="Account">
        <Row label={overview.email ?? 'Exo account'}><SignOut onDone={onChanged} /></Row>
        <SyncRow sync={overview.sync} onSynced={onChanged} />
        {overview.sync.held.length > 0 ? (
          <Row label={`Held on this PC · ${overview.sync.held.length}`} detail={heldSentences(overview.sync.held).join(' ')} />
        ) : null}
      </Section>
      <Security />
      <Devices onChanged={onChanged} />
      <Section id="account-data" title="Your data">
        <ExportData />
        <DeleteAccount onDone={onChanged} />
      </Section>
    </>
  )
}

/** The forms under the rows, as Exo Browser opens them: one at a time, the row's button turning into Cancel. */
const SUB = 'exo-account-sub'
/** The password rule, shown inside the empty field, not as a line under it. */
const PASSWORD_RULE = 'At least 12 characters'

function Security() {
  const [open, setOpen] = useState<'none' | 'password' | 'recovery'>('none')
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [note, setNote] = useState('')
  const [code, setCode] = useState<string | null>(null)

  // Each form's button says how it went (ActionButton); the form closes once its check has shown, and a refusal is the
  // form's error line, the button in Bad.
  async function changePassword() {
    setError('')
    if (next.length < 12) return { ok: false, message: 'Use at least 12 characters.' }
    return host.exoAccountChangePassword(current, next)
  }
  function passwordChanged() {
    setCurrent(''); setNext(''); setOpen('none')
    setNote('Password changed. Other PCs were signed out.')
  }

  async function newRecoveryCode() {
    setError('')
    // Counted before the call: the host saves the waiting code at once, and the account sheet must not open over this one.
    const result = await sendAccountRequest(() => host.exoAccountNewRecoveryCode(password))
    return result.ok && !result.recoveryCode ? { ok: false, message: 'The code was not made. Try again.' } : result
  }
  function codeMade(result: unknown) {
    const made = (result as { recoveryCode?: string | null }).recoveryCode
    if (!made) return
    setPassword(''); setOpen('none')
    setCode(made)
  }

  if (code) return <Section id="account-security" title="Security"><div className={SUB}><RecoveryCodeScreen code={code} onDone={() => setCode(null)} onKeyDown={() => undefined} /></div></Section>

  return (
    <Section id="account-security" title="Security">
      <Row label="Change password">
        <button type="button" className="exo-set-btn" onClick={() => { setOpen(open === 'password' ? 'none' : 'password'); setError('') }}>{open === 'password' ? 'Cancel' : 'Change'}</button>
      </Row>
      {open === 'password' ? (
        <form className={SUB} onSubmit={pressSubmit} noValidate>
          <label className="exo-ob-label" htmlFor="exo-account-current">Current password</label>
          <input id="exo-account-current" className="exo-field" type="password" autoComplete="current-password" value={current} onChange={e => setCurrent(e.target.value)} />
          <label className="exo-ob-label" htmlFor="exo-account-new">New password</label>
          <input id="exo-account-new" className="exo-field" type="password" autoComplete="new-password" placeholder={PASSWORD_RULE} value={next} onChange={e => setNext(e.target.value)} />
          {error ? <p className="exo-ob-error" role="alert">{error}</p> : null}
          <div className="exo-ob-form-actions">
            <ActionButton className="exo-set-btn is-primary" disabled={!current || !next} context="AccountSection.changePassword"
              fallback="The password was not changed. Try again." why="none" onFailed={failure => setError(failure.text)}
              onPress={changePassword} onDone={passwordChanged}>Change password</ActionButton>
          </div>
        </form>
      ) : null}
      <Row label="Replace recovery code">
        <button type="button" className="exo-set-btn" onClick={() => { setOpen(open === 'recovery' ? 'none' : 'recovery'); setError('') }}>{open === 'recovery' ? 'Cancel' : 'Replace'}</button>
      </Row>
      {open === 'recovery' ? (
        <form className={SUB} onSubmit={pressSubmit} noValidate>
          <label className="exo-ob-label" htmlFor="exo-account-code-password">Password</label>
          <input id="exo-account-code-password" className="exo-field" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} />
          {error ? <p className="exo-ob-error" role="alert">{error}</p> : null}
          <div className="exo-ob-form-actions">
            <ActionButton className="exo-set-btn is-primary" disabled={!password} context="AccountSection.recoveryCode"
              fallback="The code was not made. Try again." why="none" onFailed={failure => setError(failure.text)}
              onPress={newRecoveryCode} onDone={codeMade}>Make a new code</ActionButton>
          </div>
        </form>
      ) : null}
      {note ? <p className="exo-account-hint" role="status">{note}</p> : null}
    </Section>
  )
}

function Devices({ onChanged }: { onChanged: () => void }) {
  const [sessions, setSessions] = useState<Sessions | null>(null)
  const [open, setOpen] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  function load() {
    void host.exoAccountSessions().then(
      (result) => { setSessions(result.ok ? result.sessions : []); if (!result.ok) setNote(result.message ?? null) },
      (error: unknown) => report(error, 'AccountSection.sessions', { level: 'warn' }),
    )
  }

  function revoked() {
    setNote('That PC was signed out.')
    load()
    onChanged()
  }

  return (
    <Section id="account-sessions" title="Sessions">
      <Row label="Signed-in PCs">
        <button type="button" className="exo-set-btn" onClick={() => { setOpen(value => !value); setNote(null); if (!open) load() }}>{open ? 'Hide' : 'Show'}</button>
      </Row>
      {open ? (
        sessions === null ? <p className="exo-account-hint">Loading…</p>
          : sessions.length === 0 ? <p className="exo-account-hint">No PCs are signed in.</p>
            : (
              <ul className="exo-account-list">
                {sessions.map(row => (
                  <li key={row.device} className="exo-account-row">
                    <span>{row.thisPc ? (row.name ? `${row.name} (This PC)` : 'This PC') : (row.name || 'Another PC')}</span>
                    <span className="exo-account-hint">Last used {when(row.lastUsed)}</span>
                    {row.thisPc ? null : (
                      <ActionButton className="exo-set-btn" question="Remove?" danger context="AccountSection.revoke" fallback="That PC was not signed out. Try again."
                        onPress={() => { setNote(null); return host.exoAccountRevoke(row.device) }} onDone={revoked}>Remove</ActionButton>
                    )}
                  </li>
                ))}
              </ul>
            )
      ) : null}
      {note ? <p className="exo-account-hint" role="status">{note}</p> : null}
    </Section>
  )
}

function ExportData() {
  const [note, setNote] = useState<string | null>(null)
  // Not an ActionButton: Windows' Save dialog is the wait, and a cancelled one is no result to show.
  async function exportData() {
    try {
      const result = await host.exoAccountExport()
      if (result.cancelled) return
      setNote(result.ok ? 'Saved.' : errorText(result.message, 'The file was not saved. Try again.'))
    } catch (error) {
      report(error, 'AccountSection.export')
      setNote(errorText(error, 'The file was not saved. Try again.'))
    }
  }
  return (
    <Row label="Export my data">
      <button type="button" className="exo-set-btn" onClick={() => void exportData()}>Save a copy</button>
      {note ? <span className="exo-account-hint" role="status">{note}</span> : null}
    </Row>
  )
}

function SignOut({ onDone }: { onDone: () => void }) {
  const [confirming, setConfirming] = useState(false)
  // Beside the account's email, the row's one control.
  return confirming ? (
    <span className="exo-account-inline">
      <button type="button" className="exo-set-btn" onClick={() => setConfirming(false)}>Keep me signed in</button>
      <ActionButton className="exo-set-btn is-primary" context="AccountSection.signOut" fallback="Exo couldn’t sign you out. Try again."
        onPress={() => host.exoAccountSignOut()} onDone={onDone}>Sign out</ActionButton>
    </span>
  ) : <button type="button" className="exo-set-btn" onClick={() => setConfirming(true)}>Sign out</button>
}

function DeleteAccount({ onDone }: { onDone: () => void }) {
  const [password, setPassword] = useState('')
  const [open, setOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)

  return (
    <>
      <Row label="Delete account">
        <button type="button" className="exo-set-btn is-danger" onClick={() => { setOpen(value => !value); setError(null) }}>{open ? 'Cancel' : 'Delete'}</button>
      </Row>
      {open ? (
        <form className={SUB} onSubmit={pressSubmit} noValidate>
          <label className="exo-ob-label" htmlFor="exo-account-delete-password">Password</label>
          <input id="exo-account-delete-password" className="exo-field" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} />
          <p className="exo-ob-hint">This deletes the account and every item synced to it. Your games, playtime and settings stay on this PC.</p>
          {error ? <p className="exo-ob-error" role="alert">{error}</p> : null}
          <div className="exo-ob-form-actions">
            <ActionButton className="exo-set-btn is-danger" disabled={!password} context="AccountSection.delete"
              fallback="The account was not deleted. Try again." why="none" onFailed={failure => setError(failure.text)}
              onPress={() => { setError(null); return host.exoAccountDelete(password) }} onDone={onDone}>Delete my account</ActionButton>
          </div>
        </form>
      ) : null}
    </>
  )
}

/** Signed out: one row that says what the account is, and the form that signs in or creates it, as Exo Browser's Settings. */
function SignedOut({ onSignedIn, onCode }: { onSignedIn: () => void; onCode: (code: string) => void }) {
  const [mode, setMode] = useState<'signin' | 'create' | 'reset'>('signin')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [show, setShow] = useState(false)
  const [error, setError] = useState('')
  const [offline, setOffline] = useState(false)

  async function submit() {
    setError('')
    const result = await sendAccountRequest(() => mode === 'create' ? host.exoAccountCreate(email, password)
      : mode === 'signin' ? host.exoAccountSignIn(email, password)
        : host.exoAccountReset(email, code, password))
    setOffline(!result.ok && result.offline === true)
    return result
  }
  function signedIn(result: unknown) {
    const issued = (result as { recoveryCode?: string | null }).recoveryCode
    if (issued) onCode(issued)
    else { setPassword(''); onSignedIn() }
  }

  const heading = mode === 'create' ? 'Create your Exo account' : mode === 'reset' ? 'Reset your password' : 'Sign in to Exo'
  return (
    <Section id="account" title="Account">
      <Row label={heading} stacked>
        <form className={SUB} onSubmit={pressSubmit} noValidate>
          <label className="exo-ob-label" htmlFor="exo-account-email">Email</label>
          <input id="exo-account-email" className="exo-field" type="email" autoComplete="off" spellCheck={false} value={email} onChange={e => setEmail(e.target.value)} />
          {mode === 'reset' ? (
            <>
              <label className="exo-ob-label" htmlFor="exo-account-reset-code">Recovery code</label>
              <input id="exo-account-reset-code" className="exo-field" type="text" autoComplete="off" spellCheck={false} value={code} onChange={e => setCode(e.target.value)} />
            </>
          ) : null}
          <label className="exo-ob-label" htmlFor="exo-account-password">Password</label>
          <input id="exo-account-password" className="exo-field" type={show ? 'text' : 'password'} autoComplete={mode === 'create' ? 'new-password' : 'current-password'} placeholder={mode === 'signin' ? undefined : PASSWORD_RULE} value={password} onChange={e => setPassword(e.target.value)} />
          {mode === 'signin' ? <p className="exo-ob-hint"><button type="button" className="exo-account-link" onClick={() => setShow(value => !value)}>{show ? 'Hide' : 'Show'}</button></p> : null}
          {error ? <p className="exo-ob-error" role="alert">{error}</p> : null}
          {offline ? <p className="exo-ob-hint">Exo works without an account for now.</p> : null}
          <div className="exo-ob-form-actions">
            <ActionButton className="exo-set-btn is-primary" disabled={!email || !password} context={`AccountSection.${mode}`}
              fallback="Exo couldn’t reach your account. Try again." why="none" onFailed={failure => setError(failure.text)}
              onPress={submit} onDone={signedIn}>{mode === 'create' ? 'Create account' : mode === 'reset' ? 'Reset password' : 'Sign in'}</ActionButton>
            <button type="button" className="exo-account-link" onClick={() => { setMode(mode === 'create' ? 'signin' : 'create'); setError('') }}>{mode === 'create' ? 'I already have one' : 'Create an account'}</button>
            {mode === 'signin' ? <button type="button" className="exo-account-link" onClick={() => { setMode('reset'); setError('') }}>Forgot your password?</button> : null}
          </div>
        </form>
      </Row>
    </Section>
  )
}

/** Enter in a form presses its action button, so a keyboard submit shows the same ring, check or failure as a press. */
function pressSubmit(event: FormEvent<HTMLFormElement>) {
  event.preventDefault()
  event.currentTarget.querySelector<HTMLButtonElement>('.exo-action:not(:disabled)')?.click()
}

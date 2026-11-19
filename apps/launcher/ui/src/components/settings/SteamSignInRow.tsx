import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { host, type SteamSignInStep } from '../../lib/host'
import { report } from '../../lib/report'
import { Qr } from '../ui/Qr'
import { Spinner } from '../ui/Spinner'
import { ErrorNote } from '../ui/ErrorNote'

type Account = { signedIn: boolean; accountName: string | null }
type Result = { ok: boolean; message?: string | null }
type Mode = 'qr' | 'password'

/** Steam's library sign-in as Settings' Steam row sees it: the account, and the in-place panel's open state. */
export type SteamLibrary = {
  account: Account | null
  open: boolean
  begin: () => Promise<Result>
  close: () => void
  refresh: () => void
  signOut: () => Promise<Result>
}

const TRY_AGAIN = 'Could not start the sign-in. Try again.'

/** The library sign-in's state, read from the host; `begin` opens the panel in the Steam row (the Account menu's item). */
export function useSteamLibrary(): SteamLibrary {
  const [account, setAccount] = useState<Account | null>(null)
  const [open, setOpen] = useState(false)
  const refresh = useCallback(() => {
    host.steamAccount().then(setAccount).catch(() => setAccount({ signedIn: false, accountName: null }))
  }, [])
  useEffect(() => { refresh() }, [refresh])
  const begin = useCallback(() => { setOpen(true); return Promise.resolve({ ok: true }) }, [])
  const close = useCallback(() => setOpen(false), [])
  const signOut = useCallback(async (): Promise<Result> => {
    try {
      await host.steamSignOut()
      refresh()
      return { ok: true }
    } catch (cause) {
      report(cause, 'SteamLibrary.signOut')
      return { ok: false, message: 'Could not sign out of Steam. Try again.' }
    }
  }, [refresh])
  return { account, open, begin, close, refresh, signOut }
}

type Sink = {
  setStep: (step: SteamSignInStep) => void
  setBusy: (busy: boolean) => void
  done: () => void
}

function settle(next: SteamSignInStep, sink: Sink) {
  sink.setBusy(false)
  if (next.state === 'done') sink.done()
  else sink.setStep(next)
}

function fail(cause: unknown, sink: Sink) {
  report(cause, 'SteamLibraryPanel')
  sink.setBusy(false)
  sink.setStep({ state: 'failed', message: TRY_AGAIN })
}

async function attempt(run: () => Promise<SteamSignInStep>, sink: Sink) {
  sink.setBusy(true)
  try {
    settle(await run(), sink)
  } catch (cause) {
    fail(cause, sink)
  }
}

/**
 * The sign-in panel that opens inside Settings' Steam row, spanning its width: a 160 px QR on the left and three steps
 * beside it, or the account name and password (with Steam Guard inline) in its place. It is mounted only while open;
 * leaving it, or a sign-in that completes, ends the sign-in on the host.
 */
export function SteamLibraryPanel({ library }: { library: SteamLibrary }) {
  const [mode, setMode] = useState<Mode>('qr')
  const [step, setStep] = useState<SteamSignInStep | null>(null)
  const [busy, setBusy] = useState(false)
  const sink: Sink = { setStep, setBusy, done: () => { library.refresh(); library.close() } }

  // Mounted only while open: the QR starts at once, and leaving cancels a sign-in still running.
  useEffect(() => {
    void attempt(() => host.steamSignInQr(), sink)
    return () => { void host.steamSignInCancel().catch(() => undefined) }
    // The sink is rebuilt each render from stable setters and callbacks, so this runs once per open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // While the phone is asked, ask Steam again after the interval it gave.
  useEffect(() => {
    if (!step || (step.state !== 'waiting' && step.state !== 'approve')) return
    const timer = window.setTimeout(() => {
      host.steamSignInPoll().then(next => settle(next, sink), cause => fail(cause, sink))
    }, (step.intervalSeconds ?? 5) * 1000)
    return () => window.clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [step])

  function usePassword() {
    void host.steamSignInCancel().catch(() => undefined)
    setMode('password')
    setStep(null)
  }

  function useQr() {
    setMode('qr')
    setStep(null)
    void attempt(() => host.steamSignInQr(), sink)
  }

  const ended = step?.state === 'expired' || step?.state === 'failed'
  return (
    <section className="exo-steam-panel" aria-label="Sign in to Steam">
      {mode === 'qr' ? <QrCode step={step} busy={busy} onNew={useQr} /> : null}
      <div className="exo-steam-panel-body">
        {mode === 'qr'
          ? <QrSteps ended={ended} message={step?.message ?? null} />
          : <PasswordBody step={step} ended={ended} busy={busy} sink={sink} />}
        <div className="exo-steam-panel-actions">
          {mode === 'qr'
            ? <button type="button" className="exo-set-btn" onClick={usePassword}>Use a password instead</button>
            : <button type="button" className="exo-set-btn" onClick={useQr}>Scan a code instead</button>}
          <button type="button" className="exo-set-btn" onClick={library.close}>Cancel</button>
        </div>
      </div>
    </section>
  )
}

function QrCode({ step, busy, onNew }: { step: SteamSignInStep | null; busy: boolean; onNew: () => void }) {
  if (step?.state === 'waiting' && step.challengeUrl) {
    return <Qr text={step.challengeUrl} label="Steam sign-in code" className="exo-steam-qr" />
  }
  if (step?.state === 'expired' || step?.state === 'failed') {
    return <button type="button" className="exo-set-btn is-primary exo-steam-qr-new" disabled={busy} onClick={onNew}>New code</button>
  }
  return <div className="exo-steam-qr-wait" aria-live="polite"><Spinner label="Getting a sign-in code" /></div>
}

function QrSteps({ ended, message }: { ended: boolean; message: string | null }) {
  return (
    <>
      <h3 className="exo-steam-panel-title">Scan with the Steam app</h3>
      <ol className="exo-steam-steps">
        <li>Open the Steam app on your phone.</li>
        <li>Tap the scan button in the app.</li>
        <li>Point it at this code and approve the sign-in.</li>
      </ol>
      {ended ? <ErrorNote message={message ?? TRY_AGAIN} /> : null}
    </>
  )
}

/** The password sign-in: the name and password fields side by side (stacked on a narrow window), then Steam Guard or the phone approval inline. */
function PasswordBody({ step, ended, busy, sink }: { step: SteamSignInStep | null; ended: boolean; busy: boolean; sink: Sink }) {
  if (step?.state === 'guard') return <GuardForm busy={busy} sink={sink} />
  if (step?.state === 'approve') return <p className="exo-steam-panel-title">Approve the sign-in in the Steam app on your phone.</p>
  if (step === null || ended) return <PasswordForm busy={busy} sink={sink} ended={ended} message={step?.message ?? null} />
  return <p className="exo-set-item-detail">Signing in to Steam…</p>
}

function PasswordForm({ busy, sink, ended, message }: { busy: boolean; sink: Sink; ended: boolean; message: string | null }) {
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  function submit(event: FormEvent) {
    event.preventDefault()
    const sent = password
    setPassword('')
    sink.setBusy(true)
    host.steamSignInPassword(name.trim(), sent).then(next => settle(next, sink), cause => fail(cause, sink))
  }
  return (
    <form className="exo-steam-panel-form" onSubmit={submit}>
      <div className="exo-steam-fields">
        <label className="exo-steam-field">
          <span className="exo-set-item-label">Username</span>
          <input className="exo-field" value={name} autoComplete="username" spellCheck={false} onChange={e => setName(e.target.value)} />
        </label>
        <label className="exo-steam-field">
          <span className="exo-set-item-label">Password</span>
          <input className="exo-field" type="password" value={password} autoComplete="current-password" onChange={e => setPassword(e.target.value)} />
        </label>
      </div>
      {ended ? <ErrorNote message={message ?? TRY_AGAIN} /> : null}
      <button type="submit" className="exo-set-btn is-primary" disabled={busy || name.trim() === '' || password === ''}>
        {busy ? <Spinner label="Signing in" /> : null}Sign in
      </button>
    </form>
  )
}

function GuardForm({ busy, sink }: { busy: boolean; sink: Sink }) {
  const [code, setCode] = useState('')
  function submit(event: FormEvent) {
    event.preventDefault()
    const sent = code.trim()
    setCode('')
    sink.setBusy(true)
    host.steamSignInGuard(sent).then(next => settle(next, sink), cause => fail(cause, sink))
  }
  return (
    <form className="exo-steam-panel-form" onSubmit={submit}>
      <label className="exo-steam-field">
        <span className="exo-set-item-label">Steam Guard code</span>
        <input className="exo-field" value={code} autoComplete="one-time-code" spellCheck={false} onChange={e => setCode(e.target.value)} />
      </label>
      <button type="submit" className="exo-set-btn is-primary" disabled={busy || code.trim() === ''}>Continue</button>
    </form>
  )
}

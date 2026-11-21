import { useState, type FormEvent, type KeyboardEvent } from 'react'
import { Button } from '../ui/Button'

type Keys = (event: KeyboardEvent<HTMLElement>) => void

export function ChooseScreen({ ended, onCreate, onSignIn, onKeyDown }: { ended: boolean; onCreate: () => void; onSignIn: () => void; onKeyDown: Keys }) {
  return (
    <div className="exo-ob-account" onKeyDown={onKeyDown}>
      <p className="exo-ob-lede">{ended ? 'Your session ended on this PC. Sign in again to keep syncing.' : 'Exo syncs your library, settings and playtime across PCs.'}</p>
      <div className="exo-ob-choice" role="group" aria-label="Account">
        <Button variant="ghost" className="exo-ob-tile" onPress={onCreate}>
          <span className="exo-ob-tile-title">Create an account</span>
        </Button>
        <Button variant="ghost" className="exo-ob-tile" onPress={onSignIn}>
          <span className="exo-ob-tile-title">Sign in</span>
        </Button>
      </div>
    </div>
  )
}

export function OfflineScreen({ onLater, onTryAgain, onSignOut, onKeyDown }: { onLater: () => void; onTryAgain: () => void; onSignOut?: () => void; onKeyDown: Keys }) {
  return (
    <section className="exo-ob-account" onKeyDown={onKeyDown} aria-labelledby="exo-account-offline-title">
      <h2 id="exo-account-offline-title" className="exo-ob-subtitle">Can&rsquo;t reach Exo&rsquo;s account service</h2>
      <div className="exo-ob-form-actions">
        <Button variant="ghost" onPress={onLater}>Continue without an account</Button>
        {onSignOut ? <Button variant="ghost" onPress={onSignOut}>Sign out of this PC</Button> : null}
        <Button variant="primary" className="exo-ob-cta" onPress={onTryAgain}>Try again</Button>
      </div>
    </section>
  )
}

export function NewCodeForm({ password, onPassword, error, busy, lockedSeconds, onSubmit, onForgot, onSignOut, onKeyDown }: {
  password: string; onPassword: (value: string) => void; error: string | null; busy: boolean; lockedSeconds: number
  onSubmit: (event: FormEvent) => void; onForgot: () => void; onSignOut: () => void; onKeyDown: Keys
}) {
  const locked = lockedSeconds > 0
  return (
    <form className="exo-ob-account" onSubmit={onSubmit} onKeyDown={onKeyDown} noValidate>
      <h2 className="exo-ob-subtitle">Make a new recovery code</h2>
      <p className="exo-ob-lede">Your last recovery code wasn&rsquo;t confirmed. Enter your password to make a new one.</p>
      <label className="exo-ob-label" htmlFor="exo-account-newcode-password">Password</label>
      <input id="exo-account-newcode-password" className="exo-field" type="password" autoComplete="current-password" value={password}
        onChange={event => onPassword(event.target.value)} aria-invalid={!!error} aria-describedby={error ? 'exo-account-newcode-error' : undefined} />
      {error && !locked ? <p id="exo-account-newcode-error" className="exo-ob-error" role="alert">{error}</p> : null}
      {locked ? (
        <p className="exo-ob-error" role="status">
          Too many tries from this PC. Wait {Math.floor(lockedSeconds / 60)}:{String(lockedSeconds % 60).padStart(2, '0')} and try again.
        </p>
      ) : null}
      <Button variant="ghost" onPress={onForgot}>Forgot your password?</Button>
      <div className="exo-ob-form-actions">
        <Button variant="ghost" isDisabled={busy} onPress={onSignOut}>Sign out of this PC</Button>
        <Button variant="primary" className="exo-ob-cta" type="submit" isDisabled={busy || locked || !password}>{busy ? 'Making a new code' : 'Make a new code'}</Button>
      </div>
    </form>
  )
}
export function CreateForm({ email, onEmail, password, onPassword, errors, takenSignIn, busy, locked, onSubmit, onSignIn, onBack, onKeyDown }: {
  email: string; onEmail: (value: string) => void; password: string; onPassword: (value: string) => void
  errors: { email?: string; password?: string; form?: string }; takenSignIn: boolean; busy: boolean; locked: boolean
  onSubmit: (event: FormEvent) => void; onSignIn: () => void; onBack: () => void; onKeyDown: Keys
}) {
  const [showPassword, setShowPassword] = useState(false)
  return (
    <form className="exo-ob-account" onSubmit={onSubmit} onKeyDown={onKeyDown} noValidate>
      <h2 className="exo-ob-subtitle">Create your account</h2>
      <label className="exo-ob-label" htmlFor="exo-account-email">Email</label>
      <input id="exo-account-email" className="exo-field" type="email" autoComplete="off" value={email} onChange={event => onEmail(event.target.value)}
        aria-invalid={!!errors.email} aria-describedby={errors.email ? 'exo-account-email-error' : undefined} />
      {errors.email ? (
        <p id="exo-account-email-error" className="exo-ob-error" role="alert">
          {errors.email}{takenSignIn ? <> <Button variant="ghost" onPress={onSignIn}>Sign in instead</Button></> : null}
        </p>
      ) : null}
      <label className="exo-ob-label" htmlFor="exo-account-password">Password</label>
      <div className="exo-ob-password">
        <input id="exo-account-password" className="exo-field" type={showPassword ? 'text' : 'password'} autoComplete="new-password" value={password}
          onChange={event => onPassword(event.target.value)} aria-invalid={!!errors.password}
          aria-describedby={errors.password ? 'exo-account-password-error' : 'exo-account-password-hint'} />
        <Button variant="ghost" aria-pressed={showPassword} onPress={() => setShowPassword(value => !value)}>{showPassword ? 'Hide' : 'Show'}</Button>
      </div>
      {errors.password ? <p id="exo-account-password-error" className="exo-ob-error" role="alert">{errors.password}</p>
        : <p id="exo-account-password-hint" className="exo-ob-hint">At least 12 characters</p>}
      {errors.form ? <p className="exo-ob-error" role="alert">{errors.form}</p> : null}
      <div className="exo-ob-form-actions">
        <Button variant="ghost" onPress={onBack}>Back</Button>
        <Button variant="primary" className="exo-ob-cta" type="submit" isDisabled={busy || locked}>{busy ? 'Creating your account' : 'Create account'}</Button>
      </div>
    </form>
  )
}

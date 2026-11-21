import type { FormEvent, KeyboardEvent } from 'react'
import { Button } from '../ui/Button'

export function SignInForm({ email, password, onEmail, onPassword, error, lockedSeconds, busy, onSubmit, onBack, onKeyDown, onForgot }: {
  email: string
  password: string
  onEmail: (value: string) => void
  onPassword: (value: string) => void
  error: string | null
  lockedSeconds: number
  busy: boolean
  onSubmit: (event: FormEvent) => void
  onBack: () => void
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void
  onForgot: () => void
}) {
  const locked = lockedSeconds > 0
  return (
    <form className="exo-ob-account" onSubmit={onSubmit} onKeyDown={onKeyDown} noValidate>
      <h2 className="exo-ob-subtitle">Sign in to Exo</h2>
      <label className="exo-ob-label" htmlFor="exo-account-email">Email</label>
      <input id="exo-account-email" className="exo-field" type="email" autoComplete="off" value={email} onChange={event => onEmail(event.target.value)} />
      <label className="exo-ob-label" htmlFor="exo-account-password">Password</label>
      <input id="exo-account-password" className="exo-field" type="password" autoComplete="current-password" value={password}
        onChange={event => onPassword(event.target.value)} />
      {error ? <p className="exo-ob-error" role="alert">{error}</p> : null}
      {locked ? (
        <p className="exo-ob-error" role="alert">
          Too many tries for this email from this PC. Wait {Math.floor(lockedSeconds / 60)}:{String(lockedSeconds % 60).padStart(2, '0')} and try again.
        </p>
      ) : null}
      <Button variant="ghost" onPress={onForgot}>Forgot your password?</Button>
      <div className="exo-ob-form-actions">
        <Button variant="ghost" onPress={onBack}>Back</Button>
        <Button variant="primary" className="exo-ob-cta" type="submit" isDisabled={busy || locked}>Sign in</Button>
      </div>
    </form>
  )
}

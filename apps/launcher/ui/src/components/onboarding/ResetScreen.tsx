import { useState, type FormEvent, type KeyboardEvent } from 'react'
import { Button } from '../ui/Button'

export function ResetScreen({ initialEmail, busy, error, onSubmit, onBack, onKeyDown }: {
  initialEmail: string
  busy: boolean
  error: string | null
  onSubmit: (email: string, code: string, password: string) => void
  onBack: () => void
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void
}) {
  const [email, setEmail] = useState(initialEmail)
  const [code, setCode] = useState('')
  const [password, setPassword] = useState('')
  const [tooShort, setTooShort] = useState(false)

  function submit(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    if (password.length < 12) return setTooShort(true)
    setTooShort(false)
    onSubmit(email, code, password)
  }

  const shown = tooShort ? 'Use at least 12 characters.' : error
  return (
    <form className="exo-ob-account" onSubmit={submit} onKeyDown={onKeyDown} noValidate>
      <h2 className="exo-ob-subtitle">Reset your password</h2>
      <label className="exo-ob-label" htmlFor="exo-reset-email">Email</label>
      <input id="exo-reset-email" className="exo-field" type="email" autoComplete="off" value={email} onChange={event => setEmail(event.target.value)} />
      <label className="exo-ob-label" htmlFor="exo-reset-code">Recovery code</label>
      <input id="exo-reset-code" className="exo-field" autoComplete="off" spellCheck={false} value={code} onChange={event => setCode(event.target.value)} />
      <label className="exo-ob-label" htmlFor="exo-reset-password">New password</label>
      <input id="exo-reset-password" className="exo-field" type="password" autoComplete="new-password" value={password} onChange={event => setPassword(event.target.value)} />
      <p className="exo-ob-hint">At least 12 characters. Every PC signed in to this account will sign in again.</p>
      {shown ? <p className="exo-ob-error" role="alert">{shown}</p> : null}
      <div className="exo-ob-form-actions">
        <Button variant="ghost" onPress={onBack}>Back</Button>
        <Button variant="primary" className="exo-ob-cta" type="submit" isDisabled={busy || !code || !password}>Reset password</Button>
      </div>
    </form>
  )
}

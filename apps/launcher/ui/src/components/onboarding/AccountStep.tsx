import { useEffect, useReducer, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { firstScreen, nextFlow, resetEmail, type AccountScreen, type FlowEvent, type FlowState } from '../../lib/accountFlow'
import { host, type ExoAccountOutcome } from '../../lib/host'
import { report } from '../../lib/report'
import { refreshAccount, sendAccountRequest, useAccount } from '../account/useAccount'
import { RecoveryCodeScreen } from './RecoveryCodeScreen'
import { ChooseScreen, CreateForm, NewCodeForm, OfflineScreen } from './AccountScreens'
import { SignInForm } from './SignInForm'
import { ResetScreen } from './ResetScreen'

export type { AccountScreen } from '../../lib/accountFlow'

const MIN_PASSWORD = 12
const UNEXPECTED = "Something went wrong on Exo's side. Try again."

interface AccountStepProps {
  /** The account exists and its recovery code, if one was issued, was typed back: the step is done. */
  onDone: () => void
  /** Continue without an account while offline: the gate asks again when the service answers. */
  onLater: () => void
  /** Every move of the step's screen table (lib/accountFlow.ts): setup shows its Back only on the choice. */
  onScreen?: (state: FlowState) => void
}

/** Escape goes back to the choice from a form; never from the code, a new code or the choice itself. */
function accountEscape(event: KeyboardEvent<HTMLElement>, screen: AccountScreen, back: () => void) {
  if (event.key !== 'Escape' || !['create', 'signin', 'reset', 'offline'].includes(screen)) return
  event.preventDefault()
  back()
}

/** The lock countdown in m:ss. */
function lockedLeft(lockedUntil: number | null, now: number): number {
  return lockedUntil === null ? 0 : Math.max(0, Math.ceil((lockedUntil - now) / 1000))
}

/** What an answer moves the table to. */
function answerOf(outcome: ExoAccountOutcome, expectsCode: boolean): Extract<FlowEvent, { type: 'answer' }>['result'] {
  if (!outcome.ok) return outcome.offline ? 'offline' : 'refused'
  if (!expectsCode) return 'signedIn'
  return outcome.recoveryCode ? 'code' : 'noCode'
}

export function AccountStep({ onDone, onLater, onScreen }: AccountStepProps) {
  const { overview } = useAccount()
  const [flow, dispatchFlow] = useReducer(nextFlow, { screen: firstScreen(overview?.codePending), busy: false })
  const screen = flow.screen
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<{ email?: string; password?: string; form?: string }>({})
  const [lockedUntil, setLockedUntil] = useState<number | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const [code, setCode] = useState<string | null>(null)
  const [takenSignIn, setTakenSignIn] = useState(false)
  const offlineFrom = useRef<'create' | 'signin' | 'reset' | 'newCode'>('create')
  // Where Back on the reset form goes: Sign in, or the new-code step it was opened from.
  const resetFrom = useRef<'signin' | 'newCode'>('signin')

  function dispatch(event: FlowEvent) {
    if (event.type === 'open') { setErrors({}); setLockedUntil(null) }
    dispatchFlow(event)
  }

  // The callers pass new functions each render: the effects read the latest through refs and run on the table's moves only.
  const latest = useRef({ onDone, onScreen })
  useEffect(() => { latest.current = { onDone, onScreen } })
  useEffect(() => { latest.current.onScreen?.(flow) }, [flow])
  useEffect(() => { if (flow.screen === 'signedIn') latest.current.onDone() }, [flow.screen])
  // The state read after the step mounted: a code issued and never typed back makes a new one first.
  useEffect(() => { if (overview?.codePending && screen === 'choose') dispatch({ type: 'open', to: 'newCode' }) }, [overview?.codePending, screen])

  // The lock countdown: the button stays disabled until Retry-After runs out (CLIENT.md 4.2).
  useEffect(() => {
    if (lockedUntil === null) return
    const timer = window.setInterval(() => setNow(Date.now()), 250)
    return () => window.clearInterval(timer)
  }, [lockedUntil])
  const locked = lockedLeft(lockedUntil, now) > 0

  function screenFailed(outcome: ExoAccountOutcome) {
    if (outcome.offline) return
    if (outcome.error === 'locked') {
      setLockedUntil(Date.now() + (outcome.retryAfterSeconds ?? 60) * 1000)
      setNow(Date.now())
    }
    if (outcome.error === 'bad-email') return setErrors({ email: outcome.message })
    if (outcome.error === 'bad-password' || outcome.error === 'common-password') return setErrors({ password: outcome.message })
    if (outcome.error === 'email-taken') {
      setTakenSignIn(true)
      return setErrors({ email: outcome.message })
    }
    setErrors({ form: outcome.message })
  }

  /** One request from a form: busy while it runs, then the answer moves the table. */
  async function send(from: 'create' | 'signin' | 'reset' | 'newCode', call: () => Promise<ExoAccountOutcome>, expectsCode: boolean) {
    if (flow.busy || locked) return
    dispatch({ type: 'submit' })
    try {
      // Counted while on its way, and its code on screen from the moment it lands (the sheet holds itself on both).
      const outcome = await sendAccountRequest(call)
      if (!outcome.ok) screenFailed(outcome)
      else { setPassword(''); setCode(outcome.recoveryCode ?? null) }
      if (outcome.offline) offlineFrom.current = from
      dispatch({ type: 'answer', result: answerOf(outcome, expectsCode) })
    } catch (error) {
      report(error, `AccountStep.${from}`)
      setErrors({ form: UNEXPECTED })
      dispatch({ type: 'answer', result: 'refused' })
    }
  }

  function create(event: FormEvent) {
    event.preventDefault()
    if (password.length < MIN_PASSWORD) return setErrors({ password: 'Use at least 12 characters.' })
    void send('create', () => host.exoAccountCreate(email, password), true)
  }

  function signIn(event: FormEvent) {
    event.preventDefault()
    void send('signin', () => host.exoAccountSignIn(email, password), false)
  }

  /** Sign out of this PC from the new-code step: the session goes, and the step starts at the choice again. */
  async function signOut() {
    if (flow.busy) return
    try {
      await host.exoAccountSignOut()
      await refreshAccount()
      dispatch({ type: 'signedOut' })
    } catch (error) {
      report(error, 'AccountStep.signOut')
      setErrors({ form: UNEXPECTED })
    }
  }

  function forgot(from: 'signin' | 'newCode') {
    resetFrom.current = from
    setEmail(typed => resetEmail(typed, from, overview?.email))
    dispatch({ type: 'open', to: 'reset' })
  }

  function newCode(event: FormEvent) {
    event.preventDefault()
    void send('newCode', () => host.exoAccountNewRecoveryCode(password), true)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLElement>) => accountEscape(event, screen, () => dispatch({ type: 'open', to: 'choose' }))

  if (screen === 'signedIn') return null

  if (screen === 'code' && code !== null) return <RecoveryCodeScreen code={code} onDone={() => dispatch({ type: 'confirm' })} onKeyDown={onKeyDown} />

  const open = (to: 'choose' | 'create' | 'signin' | 'reset' | 'newCode') => () => dispatch({ type: 'open', to })

  if (screen === 'choose') return <ChooseScreen ended={overview?.sessionEnded === true} onCreate={open('create')} onSignIn={open('signin')} onKeyDown={onKeyDown} />

  if (screen === 'offline') {
    return <OfflineScreen onLater={onLater} onTryAgain={open(offlineBack(offlineFrom.current, takenSignIn))} onKeyDown={onKeyDown}
      onSignOut={offlineFrom.current === 'newCode' ? () => void signOut() : undefined} />
  }

  if (screen === 'reset') {
    return (
      <ResetScreen initialEmail={email} busy={flow.busy} onBack={open(resetFrom.current)} onKeyDown={onKeyDown}
        error={errors.form ?? null} onSubmit={(resetEmail, resetCode, resetPassword) => { setEmail(resetEmail); void send('reset', () => host.exoAccountReset(resetEmail, resetCode, resetPassword), true) }} />
    )
  }

  if (screen === 'newCode') {
    return (
      <NewCodeForm password={password} onPassword={setPassword} error={errors.form ?? null} busy={flow.busy} lockedSeconds={lockedLeft(lockedUntil, now)}
        onSubmit={newCode} onForgot={() => forgot('newCode')} onSignOut={() => void signOut()} onKeyDown={onKeyDown} />
    )
  }

  if (screen === 'signin') {
    return (
      <SignInForm email={email} password={password} onEmail={setEmail} onPassword={setPassword} error={errors.form ?? null}
        lockedSeconds={lockedLeft(lockedUntil, now)} busy={flow.busy} onSubmit={signIn} onBack={open('choose')} onKeyDown={onKeyDown}
        onForgot={() => forgot('signin')} />
    )
  }

  return (
    <CreateForm email={email} onEmail={setEmail} password={password} onPassword={setPassword} errors={errors} takenSignIn={takenSignIn}
      busy={flow.busy} locked={locked} onSubmit={create} onSignIn={open('signin')} onBack={open('choose')} onKeyDown={onKeyDown} />
  )
}

/** Where Try again goes from offline: the form that found it, or Sign in after an address that is taken. */
function offlineBack(from: 'create' | 'signin' | 'reset' | 'newCode', takenSignIn: boolean): 'create' | 'signin' | 'reset' | 'newCode' {
  return from === 'create' && takenSignIn ? 'signin' : from
}
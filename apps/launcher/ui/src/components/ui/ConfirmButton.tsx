import { Children, isValidElement, useEffect, useLayoutEffect, useReducer, useRef, useState, type ButtonHTMLAttributes, type MouseEvent, type ReactNode } from 'react'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { cn } from '../../lib/cn'
import { actionReducer, idleAction, settled, refusal, SUCCESS_HOLD_MS, type ActionPhase } from '../../lib/actionState'
import { plainError, type PlainError } from '../../lib/errorWords'
import { report } from '../../lib/report'
import { Alert, CHECK_PATH } from '../../brand/icons'
import { Spinner } from './Spinner'
import { CopyLogs } from './ErrorNote'

/** How long a control stays armed with nothing pressed. */
const ARMED_MS = 4000

let live: HTMLElement | null = null

/** One polite live region for every armed question, so a screen reader hears it once, as it is asked. */
function announce(text: string) {
  if (typeof document === 'undefined') return
  if (!live?.isConnected) {
    live = document.createElement('span')
    live.className = 'sr-only'
    live.setAttribute('role', 'status')
    live.setAttribute('aria-live', 'polite')
    document.body.append(live)
  }
  live.textContent = ''
  if (text) window.requestAnimationFrame(() => { if (live) live.textContent = text })
}

/** The control settles into its new size with a compositor-only scale spring, on the base time (DESIGN.md › Feel). */
export function morph(element: HTMLElement, from: number) {
  const to = element.getBoundingClientRect().width
  if (shouldReduceMotion() || to <= 0 || from <= 0 || Math.abs(to - from) < 1) return
  const root = getComputedStyle(document.documentElement)
  const easing = root.getPropertyValue('--ease-spring').trim() || 'ease-out'
  const duration = parseFloat(root.getPropertyValue('--dur-base')) || 160
  const ratio = Math.max(0.5, Math.min(2, from / to))
  element.animate([{ transform: `scaleX(${ratio})` }, { transform: 'scaleX(1)' }], { duration, easing })
  const label = element.querySelector<HTMLElement>('.exo-confirm-label')
  if (label && typeof label.animate === 'function') {
    label.animate([{ transform: `scaleX(${1 / ratio})` }, { transform: 'scaleX(1)' }], { duration, easing })
  }
}

/**
 * Confirm in place (DESIGN.md › Confirm): the first press arms the control, its words become the question
 * ("Uninstall?") and its width morphs to them; a second press, Enter or Space, runs it. Escape, a press elsewhere,
 * focus leaving or 4 s with nothing pressed put it back. Spread `ref` and `className` on the control, call `press` on
 * its press, and show `label` (ConfirmLabel) for its words. `arm` asks without a press (Escape or a press outside a
 * sheet with changes arms its Cancel), focusing the control so Enter answers it.
 */
export function useConfirmInPlace<T extends HTMLElement = HTMLButtonElement>(question: string, onConfirm: () => void, danger = false) {
  const [armed, setArmed] = useState(false)
  const ref = useRef<T>(null)
  const from = useRef<number | null>(null)
  const on = useRef(false)
  const go = useRef(onConfirm)
  useEffect(() => { go.current = onConfirm })
  const set = (next: boolean) => {
    if (on.current === next) return
    on.current = next
    from.current = ref.current?.getBoundingClientRect().width ?? null
    setArmed(next)
  }
  useLayoutEffect(() => {
    const element = ref.current
    if (element && from.current !== null) morph(element, from.current)
    from.current = null
  }, [armed])
  useEffect(() => {
    const element = ref.current
    if (!armed || !element) return
    announce(question)
    const off = () => set(false)
    const timer = window.setTimeout(off, ARMED_MS)
    const press = (event: PointerEvent) => { if (!element.contains(event.target as Node)) off() }
    // Escape reaches the armed control before its sheet or menu (lib/layer.ts and Menu skip it), so it only disarms.
    const key = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      event.stopPropagation()
      off()
    }
    element.dataset.exoArmed = ''
    document.addEventListener('pointerdown', press, true)
    element.addEventListener('keydown', key)
    element.addEventListener('focusout', off)
    return () => {
      window.clearTimeout(timer)
      delete element.dataset.exoArmed
      document.removeEventListener('pointerdown', press, true)
      element.removeEventListener('keydown', key)
      element.removeEventListener('focusout', off)
    }
  }, [armed, question])
  return {
    armed,
    ref,
    className: armed ? cn('is-armed', danger && 'is-danger') : undefined,
    press: () => {
      if (!on.current) { set(true); return }
      set(false)
      announce('')
      go.current()
    },
    arm: () => {
      ref.current?.focus({ preventScroll: true })
      set(true)
    },
  }
}

/** A control's words, the question while armed: the new words fade in on the base time as the width morphs. */
export function ConfirmLabel({ armed, question, children }: { armed: boolean; question: string; children: ReactNode }) {
  const [shown, setShown] = useState(armed)
  const [changed, setChanged] = useState(false)
  if (shown !== armed) { setShown(armed); setChanged(true) }
  return <span key={armed ? 'ask' : 'rest'} className={cn('exo-confirm-label', changed && 'is-in')}>{armed ? question : children}</span>
}

export type ActionOptions<T> = {
  /** The caller's own sentence for a failure nothing better explains ("Exo couldn't remove it. Try again."). */
  fallback?: string
  /** How long it may wait before it says so (60 s); null for work that ends itself (an install, a queued job). */
  timeoutMs?: number | null
  /** Where a failure is logged (report()): the component and the action, "Settings.riotId". */
  context?: string
  /** After the check's hold: the control settles into its next real state (the row goes, the words change). */
  onDone?: (value: T) => void
  /** A failure, for a caller that also says it elsewhere (a row's error line). */
  onFailed?: (error: PlainError) => void
}

function isThenable(value: unknown): value is PromiseLike<unknown> {
  return !!value && (typeof value === 'object' || typeof value === 'function') && typeof (value as { then?: unknown }).then === 'function'
}

/**
 * One press's life (lib/actionState.ts): `press` runs the work. Work that answers at once (no promise) and says yes
 * leaves the control at rest, as a plain button; a promise makes it pending (its ring, after 300ms), then success (the
 * check, held 1.2 s, then `onDone`) or error (Bad, shaking once, its reason beside it; the next press tries again). A
 * refusal (`{ ok: false, message }`, `false`) is a failure as a throw is, and a wait past `timeoutMs` fails too, so no
 * control spins forever. Every failure reaches app.log with its original words (report()); the page shows plain ones.
 */
export function useAction<T = unknown>(work: () => T | Promise<T>, options: ActionOptions<T> = {}) {
  const [state, dispatch] = useReducer(actionReducer, idleAction)
  // The run and phase as of now, for presses and answers that land between renders.
  const run = useRef(0)
  const phase = useRef<ActionPhase>('idle')
  const latest = useRef({ work, options })
  useEffect(() => { latest.current = { work, options } })
  const hold = useRef(0)
  useEffect(() => () => window.clearTimeout(hold.current), [])

  const fail = (mine: number, error: PlainError, cause: unknown) => {
    dispatch({ type: 'reject', run: mine, error })
    phase.current = 'error'
    const { options } = latest.current
    report(cause instanceof Error ? cause : new Error(error.detail ?? error.text), options.context ?? 'action', { level: 'warn' })
    announce(error.text)
    options.onFailed?.(error)
  }

  const press = async () => {
    if (phase.current === 'pending') return
    window.clearTimeout(hold.current)
    const { work, options } = latest.current
    let started: unknown
    let thrown: { cause: unknown } | null = null
    try { started = work() } catch (cause) { thrown = { cause } }
    if (!thrown && !isThenable(started) && !refusal(started)) {
      // Done at once: a plain button's press.
      phase.current = 'idle'
      dispatch({ type: 'reset' })
      return
    }
    run.current += 1
    const mine = run.current
    phase.current = 'pending'
    dispatch({ type: 'start' })
    const answer = thrown
      ? { ok: false as const, error: plainError(thrown.cause, options.fallback), cause: thrown.cause }
      : await settled(() => started, { timeoutMs: options.timeoutMs, fallback: options.fallback })
    if (run.current !== mine) return
    if (!answer.ok) { fail(mine, answer.error, answer.cause); return }
    dispatch({ type: 'resolve', run: mine })
    phase.current = 'success'
    announce('Done')
    hold.current = window.setTimeout(() => {
      dispatch({ type: 'settle', run: mine })
      phase.current = 'idle'
      latest.current.options.onDone?.(answer.value as T)
    }, SUCCESS_HOLD_MS)
  }

  return {
    phase: state.phase,
    error: state.phase === 'error' ? state.error : null,
    busy: state.phase === 'pending',
    press,
    /** Back to rest: the error's reason goes (a field changed, the row was put back); a late answer is dropped. */
    reset: () => { window.clearTimeout(hold.current); run.current += 1; phase.current = 'idle'; dispatch({ type: 'reset' }) },
  }
}

/** Whether a control's children hold words (a glyph-only control's states are glyphs too). */
function hasWords(children: ReactNode): boolean {
  return Children.toArray(children).some(child =>
    typeof child === 'string' || typeof child === 'number' || (isValidElement<{ children?: ReactNode }>(child) && hasWords(child.props.children)))
}

/** Words drawn from an attribute, so a face that isn't showing adds nothing to the control's text (what a screen reader and a test read). */
const Words = ({ words }: { words: string }) => <span className="exo-action-words" data-words={words} />

/**
 * The control's faces, stacked in one cell so it keeps one size whatever it shows (no layout shift): its own words, the
 * ring (with `working` words if given) and the check drawing itself (with `done`). One shows at a time; they crossfade
 * (exo-shell.css › Actions). A failure keeps the control's own words, in Bad (pressing them again is Try again); a
 * glyph-only control shows the alert in its glyph's place.
 */
function Faces({ phase, glyph, lead, rest, working, done }: {
  phase: ActionPhase; glyph: boolean; lead?: ReactNode; rest: ReactNode; working?: string; done?: string
}) {
  return (
    <span className="exo-action-stack">
      <span className={cn('exo-action-face is-rest', (phase === 'idle' || (phase === 'error' && !glyph)) && 'is-on')}>{lead}{rest}</span>
      <span className={cn('exo-action-face is-pending', phase === 'pending' && 'is-on')} aria-hidden="true">
        {phase === 'pending' ? <Spinner size={14} /> : null}{working && !glyph ? <Words words={working} /> : null}
      </span>
      <span className={cn('exo-action-face is-success', phase === 'success' && 'is-on')} aria-hidden="true">
        <svg className="exo-action-check" width="16" height="16" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>
        {done && !glyph ? <Words words={done} /> : null}
      </span>
      {glyph ? <span className={cn('exo-action-face is-error', phase === 'error' && 'is-on')} aria-hidden="true"><Alert size={16} /></span> : null}
    </span>
  )
}

/** A failure's reason beside the control that failed: the plain sentence, and Copy details with what was really said. */
export function ActionWhy({ error, className }: { error: PlainError; className?: string }) {
  return (
    <span className={cn('exo-action-why', className)} role="alert">
      <span className="exo-action-why-text">{error.text}</span>
      <CopyLogs error={error.detail ?? error.text} />
    </span>
  )
}

type ActionButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'onClick' | 'title'> & ActionOptions<unknown> & {
  /** The work. A promise (or a host answer) makes the control show it: ring, check or its failure. */
  onPress: () => unknown
  /** Ask in place first ("Uninstall?"; DESIGN.md › Confirm). */
  question?: string
  danger?: boolean
  /** A press that runs it without asking (Discord's Shift). */
  now?: (event: MouseEvent<HTMLButtonElement>) => boolean
  /** A glyph before the words, kept while armed. */
  lead?: ReactNode
  /** Words beside the ring while it works ("Removing"); the ring alone otherwise. */
  working?: string
  /** Words beside the check ("Removed"); the check alone otherwise. */
  done?: string
  /** Where the reason goes: before the control (a row's control column; the default), after it (a menu, a stacked form), or nowhere (the caller says it). */
  why?: 'before' | 'after' | 'none'
}

/**
 * Exo's one button for an action (DESIGN.md › Feel): it reacts in itself. Pressed, it asks first if it has a
 * `question`, then runs `onPress`; while that works it shows the ring in its own size, on success a check that draws
 * itself and holds 1.2 s before `onDone` settles it into its next state (the row goes, Undo shows), and on failure it
 * turns Bad, shakes once and says why beside itself with Copy details; its next press tries again, without asking again.
 * Any of Exo's buttons (`exo-set-btn` and its kinds, `exo-play`, `exo-game-tool`, a menu row); a glyph-only one
 * (`exo-confirm-icon`) keeps glyphs for every state. Interruptible: a press on a held check runs again at once. Under
 * Reduce motion the faces only crossfade.
 */
export function ActionButton({ onPress, question, danger, now, lead, working, done, why, fallback, timeoutMs, context, onDone, onFailed,
  className, children, ...rest }: ActionButtonProps) {
  const action = useAction(onPress, { fallback, timeoutMs, context, onDone, onFailed })
  const ask = useConfirmInPlace<HTMLButtonElement>(question ?? '', () => void action.press(), danger)
  const glyph = !hasWords(children)
  const asks = Boolean(question) && action.phase !== 'error'
  const place = why ?? (rest.role?.startsWith('menuitem') ? 'after' : 'before')
  const reason = action.error && place !== 'none' ? <ActionWhy key="why" error={action.error} /> : null
  const button = (
    <button type="button" {...rest} key="action"
      ref={ask.ref}
      aria-busy={action.busy || undefined}
      data-action={action.phase}
      className={cn(className, 'exo-confirm exo-action', ask.className, action.phase !== 'idle' && `is-${action.phase}`)}
      onClick={event => {
        if (action.busy) return
        if (!asks || (!ask.armed && now?.(event))) void action.press()
        else ask.press()
      }}>
      <Faces phase={action.phase} glyph={glyph} lead={lead} working={working} done={done}
        rest={<ConfirmLabel armed={ask.armed} question={question ?? ''}>{children}</ConfirmLabel>} />
    </button>
  )
  if (!reason) return button
  return place === 'after' ? <>{button}{reason}</> : <>{reason}{button}</>
}

/**
 * A button that confirms in place: ActionButton with its question. `onConfirm` may return the work's promise (or a host
 * answer), and the control then shows it: ring, check, or its failure with the reason.
 */
export function ConfirmButton({ onConfirm, question, ...rest }: Omit<ActionButtonProps, 'onPress' | 'question'> & { question: string; onConfirm: () => unknown }) {
  return <ActionButton {...rest} question={question} onPress={onConfirm} />
}

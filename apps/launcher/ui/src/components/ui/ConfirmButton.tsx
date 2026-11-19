import { useEffect, useLayoutEffect, useRef, useState, type ButtonHTMLAttributes, type MouseEvent, type ReactNode } from 'react'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { cn } from '../../lib/cn'

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

/** The control's width springs from what it was to what its new words need, on the move time (DESIGN.md › Feel). */
export function morph(element: HTMLElement, from: number) {
  const to = element.getBoundingClientRect().width
  if (shouldReduceMotion() || Math.abs(to - from) < 1) return
  const root = getComputedStyle(document.documentElement)
  const easing = root.getPropertyValue('--ease-spring').trim() || 'ease-out'
  const duration = parseFloat(root.getPropertyValue('--dur-move')) || 220
  element.animate([{ width: `${from}px` }, { width: `${to}px` }], { duration, easing })
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

/**
 * A button that confirms in place: any of Exo's buttons (`exo-set-btn` with `is-primary` or `is-compact`, `exo-play`,
 * `exo-game-tool`, a menu row), its words the question while armed, in Bad when `danger`. `lead`: a glyph or ring
 * before the words, kept while armed. An icon-only control adds `exo-confirm-icon`: armed, its glyph (its children)
 * becomes a pill of the question's words (exo-shell.css). `now`: a press that runs it without asking (Discord's Shift).
 */
export function ConfirmButton({ question, danger, onConfirm, now, className, lead, children, ...rest }:
  Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'onClick' | 'title'> & {
    question: string
    danger?: boolean
    onConfirm: () => void
    now?: (event: MouseEvent<HTMLButtonElement>) => boolean
    lead?: ReactNode
  }) {
  const ask = useConfirmInPlace<HTMLButtonElement>(question, onConfirm, danger)
  return (
    <button type="button" {...rest}
      ref={ask.ref}
      className={cn(className, 'exo-confirm', ask.className)} onClick={event => { if (!ask.armed && now?.(event)) onConfirm(); else ask.press() }}>
      {lead}<ConfirmLabel armed={ask.armed} question={question}>{children}</ConfirmLabel>
    </button>
  )
}

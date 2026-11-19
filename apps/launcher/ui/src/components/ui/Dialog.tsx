import { useId, useLayoutEffect, useRef, useState, type FormEvent, type HTMLAttributes, type KeyboardEvent, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'
import { useDismiss } from '../../lib/dismiss'
import { useLayer } from '../../lib/layer'
import { fromKeyboard, shouldReduceMotion } from '../../lib/motionPreference'
import { cn } from '../../lib/utils'

/** How far it hangs from its control, and stays from the window's edge. */
const GAP = 6
const EDGE = 12
/** A press this recent opened it: what was pressed is its control. */
const OPENER_MS = 1500

let pressed: { at: Element; x: number; y: number; time: number } | null = null
if (typeof document !== 'undefined') {
  document.addEventListener('pointerdown', (event) => {
    if (event.target instanceof Element) pressed = { at: event.target, x: event.clientX, y: event.clientY, time: performance.now() }
  }, true)
}

type Opener = { element: Element | null; box: DOMRect | null }

/** Clears the cached pointer opener on route/view transitions so cross-room dialogs don't anchor to a stale click. */
export function clearDialogOpener() {
  pressed = null
}

/** What opened it: explicit anchor if given, else the control just pressed, else the connected focused element. */
function openerNow(anchor?: Element | DOMRect | null): Opener {
  if (anchor instanceof DOMRect) return { element: null, box: anchor }
  if (anchor instanceof Element) return anchor.isConnected ? { element: anchor, box: anchor.getBoundingClientRect() } : { element: null, box: null }
  if (pressed && performance.now() - pressed.time < OPENER_MS) {
    const control = pressed.at.closest('button, a, [role^="menuitem"], [tabindex]') ?? pressed.at
    if (control.isConnected) return { element: control, box: control.getBoundingClientRect() }
    return { element: null, box: new DOMRect(pressed.x, pressed.y, 0, 0) }
  }
  const focused = document.activeElement
  return focused && focused.isConnected && focused !== document.body && focused !== document.documentElement
    ? { element: focused, box: focused.getBoundingClientRect() }
    : { element: null, box: null }
}

/**
 * Under its control, from the control's left edge (its right edge when that runs out of the window); above it when
 * there is no room below; kept inside the window. It grows from the corner nearest its control. With no control
 * (opened by a key with nothing focused) it hangs from the top of the window, across its middle.
 */
function place(box: HTMLElement, opener: Opener) {
  const from = opener.element?.isConnected ? opener.element.getBoundingClientRect() : opener.box
  const width = box.offsetWidth
  const height = box.offsetHeight
  const right = window.innerWidth - EDGE
  const bottom = window.innerHeight - EDGE
  const anchor = from ?? new DOMRect((window.innerWidth - width) / 2, window.innerHeight * 0.15, width, 0)
  const above = anchor.top - GAP - height
  const up = anchor.bottom + GAP + height > bottom && above >= EDGE
  const top = up ? above : Math.max(EDGE, Math.min(anchor.bottom + GAP, bottom - height))
  const fromRight = anchor.left + width > right
  const left = Math.max(EDGE, Math.min(fromRight ? anchor.right - width : anchor.left, right - width))
  box.style.top = `${Math.round(top)}px`
  box.style.left = `${Math.round(left)}px`
  box.style.transformOrigin = `${fromRight ? 'right' : 'left'} ${up ? 'bottom' : 'top'}`
}

/**
 * Large glass leaves by fading on the quick time (DESIGN.md › Motion › Appear). Its owner has already taken it away, so
 * a still copy stands in its place, in the top layer, while it fades. Only once it is really gone (React's strict
 * mode runs this without taking it away), and not under Reduce motion or from the keyboard, where glass is there or
 * gone at once.
 */
function fadeAway(box: HTMLElement) {
  if (!box.matches(':popover-open') || shouldReduceMotion() || fromKeyboard()) return
  const parent = box.parentNode
  const ghost = box.cloneNode(true) as HTMLElement
  ghost.removeAttribute('role')
  ghost.removeAttribute('aria-labelledby')
  for (const named of ghost.querySelectorAll('[id]')) named.removeAttribute('id')
  ghost.inert = true
  ghost.setAttribute('aria-hidden', 'true')
  ghost.style.transition = 'none'
  queueMicrotask(() => {
    if (box.isConnected || !parent?.isConnected) return
    parent.appendChild(ghost)
    ghost.showPopover()
    const root = getComputedStyle(document.documentElement)
    const duration = parseFloat(root.getPropertyValue('--dur-quick')) || 120
    const easing = root.getPropertyValue('--ease-out').trim() || 'ease-out'
    // Gone with its fade, or by the clock when the page is hidden and draws no frames (it counts as open meanwhile).
    const gone = () => ghost.remove()
    window.setTimeout(gone, duration + 100)
    ghost.animate([{ opacity: 1 }, { opacity: 0 }], { duration, easing, fill: 'forwards' }).finished.then(gone, gone)
  })
}

/**
 * Large glass hung from the control that opened it (DESIGN.md › Confirm), never in the window's middle and never over a
 * scrim: Exo's dialog and every form or flow too big for its row (Discord's invite, poll, report, verify). It grows from
 * its control's side and fades away; Escape or a press outside closes it (the press goes no further), Tab stays inside,
 * and focus starts on what asks for it (`data-autofocus`), else the popover, and goes back when it closes. It stays
 * through focus leaving the window (a file picker). `portal={false}`: drawn where it's rendered, inside a room whose
 * colors its words use (Discord's), else from the page's root; either way it is in the top layer, over every sheet and
 * the Bar. `onSubmit` makes it a form.
 */
export function Popover({ label, labelledBy, anchor, className, onClose, portal = true, onSubmit, onKeyDown, children, ...rest }:
  Omit<HTMLAttributes<HTMLElement>, 'popover' | 'role' | 'title' | 'onSubmit'> & {
    label?: string
    labelledBy?: string
    anchor?: Element | DOMRect | null
    onClose: () => void
    portal?: boolean
    onSubmit?: (event: FormEvent<HTMLFormElement>) => void
  }) {
  const ref = useRef<HTMLElement>(null)
  const [opener] = useState(() => openerNow(anchor))
  // What it holds mounts once it shows, so a field's autoFocus lands (nothing can take focus in a popover not yet shown).
  const [shown, setShown] = useState(false)
  useLayer(ref, onClose)
  useDismiss(ref, onClose, { modal: true })
  useLayoutEffect(() => {
    const element = ref.current
    if (!element) return
    if (!element.matches(':popover-open')) element.showPopover()
    setShown(true)
    const fit = () => place(element, opener)
    fit()
    const sizes = new ResizeObserver(fit)
    sizes.observe(element)
    window.addEventListener('resize', fit)
    return () => {
      sizes.disconnect()
      window.removeEventListener('resize', fit)
      fadeAway(element)
    }
  }, [ref, opener])
  const props = {
    ...rest,
    popover: 'manual' as const,
    className: cn('exo-hung', className),
    role: 'dialog',
    'aria-modal': true,
    'aria-label': label,
    'aria-labelledby': labelledBy,
    tabIndex: -1,
    // lib/layer.ts leaves Escape inside a popover to the popover: a dropdown or an armed control in it takes it first.
    onKeyDown: (event: KeyboardEvent<HTMLElement>) => {
      onKeyDown?.(event)
      if (event.key !== 'Escape' || event.defaultPrevented) return
      event.preventDefault()
      event.stopPropagation()
      onClose()
    },
  }
  const element = onSubmit
    ? <form {...props} ref={ref as RefObject<HTMLFormElement>} noValidate onSubmit={onSubmit}>{shown ? children : null}</form>
    : <div {...props} ref={ref as RefObject<HTMLDivElement>}>{shown ? children : null}</div>
  return portal ? createPortal(element, document.body) : element
}

/** Exo's one dialog (a Popover): its title, what it holds, then its buttons at the right. `form`: its body holds fields, set further apart. */
export function Dialog({ title, anchor, children, actions, onClose, form, portal = true, className }: {
  title: string
  anchor?: Element | DOMRect | null
  children?: ReactNode
  actions: ReactNode
  onClose: () => void
  form?: boolean
  portal?: boolean
  className?: string
}) {
  const id = useId()
  return (
    <Popover labelledBy={id} anchor={anchor} className={cn('exo-dialog exo-glass is-panel', className)} portal={portal} onClose={onClose}>
      <h2 id={id}>{title}</h2>
      {children ? <div className={cn('exo-dialog-body', form && 'is-form')}>{children}</div> : null}
      <div className="exo-dialog-actions">{actions}</div>
    </Popover>
  )
}

/**
 * A question with fields before something that can't be taken back (a driver's install, its options): Cancel
 * (focused: Enter alone never confirms), then the action.
 */
export function ConfirmDialog({ title, action, children, form, onConfirm, onClose }: {
  title: string
  /** What the action names, e.g. "Install". */
  action: string
  children?: ReactNode
  form?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <Dialog title={title} onClose={onClose} form={form} actions={
      <>
        <button type="button" className="exo-set-btn" data-autofocus onClick={onClose}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" onClick={onConfirm}>{action}</button>
      </>
    }>
      {children}
    </Dialog>
  )
}

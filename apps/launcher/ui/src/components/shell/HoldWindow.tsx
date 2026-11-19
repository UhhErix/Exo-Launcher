import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type RefObject } from 'react'
import { focusWhenActive, trapTab } from '../../lib/focusTrap'
import { WindowChrome } from '../WindowChrome'

/** A screen holding the window: its root, and what Escape does there (nothing when null). */
type Hold = { root: RefObject<HTMLElement | null>; escape: RefObject<(() => void) | null | undefined> }

/** The screens holding the window, the newest (the one on top) last. */
const holds: Hold[] = []
/** The children of <body> made inert here, and only those: each gets its old state back. */
const held = new Set<HTMLElement>()
let watch: MutationObserver | null = null
const PRESSES = ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'auxclick', 'dblclick', 'contextmenu'] as const

/** The child of <body> that holds a screen's root: its portal. */
function portal(node: Element | null | undefined) {
  while (node?.parentElement && node.parentElement !== document.body) node = node.parentElement
  return node?.parentElement === document.body ? node : null
}

function top() {
  const hold = holds.at(-1)
  const box = hold?.root.current
  return hold && box ? { hold, box } : null
}

/** Every child of <body> but the top screen's portal is inert, those that come later too; a screen let go takes back only what it set. */
function apply() {
  const keep = portal(top()?.box)
  for (const node of held) {
    if (keep && node !== keep && node.isConnected) continue
    node.inert = false
    held.delete(node)
  }
  if (!keep) return
  for (const node of document.body.children) {
    if (node === keep || !(node instanceof HTMLElement) || node.inert || node instanceof HTMLScriptElement) continue
    node.inert = true
    held.add(node)
  }
}

/** Keys reach only the screen: no shortcut behind it (Ctrl+K, Backspace, a room's number) hears them. Tab goes round in it. */
function keys(event: KeyboardEvent) {
  const on = top()
  if (!on) return
  event.stopImmediatePropagation()
  if (event.type !== 'keydown') return
  if (event.key === 'Tab') trapTab(on.box, event)
  else if (event.key === 'Escape') {
    event.preventDefault()
    on.hold.escape.current?.()
  }
}

/** A list of the screen's own that can scroll under the pointer. */
function scrolls(target: EventTarget | null, box: HTMLElement) {
  for (let node = target instanceof Element ? target : null; node && box.contains(node); node = node.parentElement) {
    if (node.scrollHeight > node.clientHeight + 1 && /auto|scroll/.test(getComputedStyle(node).overflowY)) return true
  }
  return false
}

/** A press goes only to the screen and the window's own buttons; outside it reaches nothing, not even a layer's light dismiss. */
function presses(event: Event) {
  const on = top()
  if (!on) return
  const target = event.target instanceof Element ? event.target : null
  if (target && (on.box.contains(target) || (portal(on.box)?.contains(target) && target.closest('.exo-winbtn')))) {
    // Middle-press autoscroll only in a list of its own.
    if (event.type === 'mousedown' && (event as MouseEvent).button === 1 && !scrolls(target, on.box)) event.preventDefault()
    return
  }
  event.preventDefault()
  event.stopImmediatePropagation()
}

/** A wheel or a touch moves only the screen's own list (whose overscroll is contained), never what is under it. */
function still(event: Event) {
  const on = top()
  if (on && !scrolls(event.target, on.box)) event.preventDefault()
}

function listen(on: boolean) {
  if (on) {
    window.addEventListener('keydown', keys, true)
    window.addEventListener('keyup', keys, true)
    for (const type of PRESSES) window.addEventListener(type, presses, true)
    window.addEventListener('wheel', still, { capture: true, passive: false })
    window.addEventListener('touchmove', still, { capture: true, passive: false })
    watch = new MutationObserver(apply)
    watch.observe(document.body, { childList: true })
  } else {
    window.removeEventListener('keydown', keys, true)
    window.removeEventListener('keyup', keys, true)
    for (const type of PRESSES) window.removeEventListener(type, presses, true)
    window.removeEventListener('wheel', still, true)
    window.removeEventListener('touchmove', still, true)
    watch?.disconnect()
    watch = null
  }
}

/**
 * A screen that holds the window until it is answered (the update sheet, What's New, the driver screen): nothing under it
 * scrolls or takes input. Every other child of <body> is inert (what is portaled there later too), keys go only to it (Tab
 * goes round inside `root`; Escape runs `onEscape`, when there is one), a press, wheel, touch or middle press outside it
 * reaches nothing, and focus moves in (to [data-autofocus], else `root`) only while Exo's window is active. The window's
 * own buttons stay usable: draw WindowButtons in the screen. Letting go puts back exactly what it set, on a remount too,
 * and focus where it was. Html's overflow is already hidden for good (tokens.css), so there is no scroll lock to undo.
 */
export function useHoldWindow(root: RefObject<HTMLElement | null>, on: boolean, onEscape?: (() => void) | null) {
  const escape = useRef(onEscape)
  useEffect(() => { escape.current = onEscape })
  useEffect(() => {
    const box = root.current
    if (!on || !portal(box)) return
    const hold: Hold = { root, escape }
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null
    holds.push(hold)
    if (holds.length === 1) listen(true)
    apply()
    if (box && !box.contains(document.activeElement)) focusWhenActive(box.querySelector<HTMLElement>('[data-autofocus]') ?? box)
    return () => {
      holds.splice(holds.indexOf(hold), 1)
      apply()
      if (!holds.length) listen(false)
      if (before?.isConnected && !before.closest('[inert]')) focusWhenActive(before)
    }
  }, [root, on])
}

/**
 * The window's own buttons, laid exactly over the Bar's (whose places the host keeps as the band's holes and Windows'
 * maximize region), so minimize, maximize and close work as ever while a screen holds the window over the Bar.
 */
export function WindowButtons() {
  const [box, setBox] = useState<CSSProperties | null>(null)
  const frame = useRef(0)
  useLayoutEffect(() => {
    const measure = () => {
      const buttons = [...document.querySelectorAll('.exo-titlebar .exo-winbtn')].map(button => button.getBoundingClientRect()).filter(rect => rect.width > 0)
      if (!buttons.length) { setBox(null); return }
      const left = Math.min(...buttons.map(rect => rect.left))
      const right = Math.max(...buttons.map(rect => rect.right))
      const top = Math.min(...buttons.map(rect => rect.top))
      const bottom = Math.max(...buttons.map(rect => rect.bottom))
      setBox({ left, top, width: right - left, height: bottom - top })
    }
    measure()
    const again = () => { cancelAnimationFrame(frame.current); frame.current = requestAnimationFrame(measure) }
    window.addEventListener('resize', again)
    return () => { window.removeEventListener('resize', again); cancelAnimationFrame(frame.current) }
  }, [])
  return <div className="exo-window-buttons" style={box ?? undefined}><WindowChrome /></div>
}

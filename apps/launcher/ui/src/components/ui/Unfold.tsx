import { useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { shouldReduceMotion } from '../../lib/motionPreference'

/**
 * A form opened in place (DESIGN.md › Confirm): under the control that asked, in its row or card, its height springing
 * open on the move time while its words fade in on the base time; its buttons arrive by the opening alone, since glass
 * never fades whole. It springs shut as its words fade on the quick time. Under Reduce motion the height snaps and the
 * words crossfade. Closed, it keeps what it last showed until it has gone; Escape in it is `onEscape`.
 */
export function Unfold({ open, className, onEscape, children }: { open: boolean; className?: string; onEscape?: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const [shown, setShown] = useState(open)
  // What it showed while open, kept for its closing.
  const [kept, setKept] = useState(children)
  if (open && kept !== children) setKept(children)
  if (open && !shown) setShown(true)
  useLayoutEffect(() => {
    const box = ref.current
    if (!box) return
    const root = getComputedStyle(document.documentElement)
    const time = (name: string) => parseFloat(root.getPropertyValue(name)) || 160
    const ease = (name: string) => root.getPropertyValue(name).trim() || 'ease-out'
    const height = `${box.getBoundingClientRect().height}px`
    const animations: Animation[] = []
    box.style.overflow = 'clip'
    if (!shouldReduceMotion()) {
      animations.push(box.animate([{ height: open ? '0px' : height }, { height: open ? height : '0px' }],
        { duration: time('--dur-move'), easing: ease('--ease-spring'), fill: 'forwards' }))
    }
    for (const part of box.children) {
      if (part.matches('button') || part.querySelector('button')) continue
      animations.push(part.animate([{ opacity: open ? 0 : 1 }, { opacity: open ? 1 : 0 }],
        { duration: time(open ? '--dur-base' : '--dur-quick'), easing: ease('--ease-out'), fill: 'forwards' }))
    }
    let live = true
    void Promise.all(animations.map(animation => animation.finished)).then(() => {
      if (!live) return
      box.style.overflow = ''
      if (open) animations.forEach(animation => animation.cancel())
      else setShown(false)
    }, () => undefined)
    return () => {
      live = false
      box.style.overflow = ''
      animations.forEach(animation => animation.cancel())
    }
  }, [open])
  if (!shown) return null
  return (
    <div ref={ref} className={className}
      onKeyDown={(event: KeyboardEvent) => {
        if (event.key !== 'Escape' || !onEscape || event.defaultPrevented) return
        event.preventDefault()
        event.stopPropagation()
        onEscape()
      }}>
      {open ? children : kept}
    </div>
  )
}

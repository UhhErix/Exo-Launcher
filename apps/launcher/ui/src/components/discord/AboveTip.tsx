import { useLayoutEffect, useRef } from 'react'
import { createPortal } from 'react-dom'

/** What a tip names, where the screen has it (its getBoundingClientRect). */
export type TipAnchor = { left: number; top: number; right: number; bottom: number }

export function anchorOf(el: Element): TipAnchor {
  const box = el.getBoundingClientRect()
  return { left: box.left, top: box.top, right: box.right, bottom: box.bottom }
}

const EDGE = 8, GAP = 6
const HIDDEN: React.CSSProperties = { left: 0, top: 0, visibility: 'hidden' }

/**
 * Discord's small tip, centered over what it names, or under it when there is no room above, and kept inside the window
 * at either side. It is drawn in the room itself: a glass row it sits in (badges, reactions) blurs what is behind it,
 * which makes that row the frame a fixed tip is placed in, so a tip drawn inside it landed away from what it names.
 */
export function AboveTip({ at, children }: { at: TipAnchor; children: React.ReactNode }) {
  const box = useRef<HTMLSpanElement>(null)
  // Placed here, not through state: measured at the window's corner each time, so a tip that wrapped where it last stood
  // (near an edge) is never sized by that.
  useLayoutEffect(() => {
    const el = box.current
    if (!el) return
    Object.assign(el.style, { left: '0px', top: '0px' })
    const tip = el.getBoundingClientRect()
    const center = (at.left + at.right) / 2
    const left = Math.max(EDGE, Math.min(center - tip.width / 2, window.innerWidth - tip.width - EDGE))
    const above = at.top - GAP - tip.height
    const top = above >= EDGE ? above : Math.max(EDGE, Math.min(at.bottom + GAP, window.innerHeight - tip.height - EDGE))
    Object.assign(el.style, { left: `${left}px`, top: `${top}px`, visibility: 'visible' })
  }, [at, children])
  const tip = <span ref={box} className="exo-dc-tip is-above" role="tooltip" style={HIDDEN}>{children}</span>
  const room = document.querySelector('main.exo-dc')
  return room ? createPortal(tip, room) : tip
}

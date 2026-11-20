import { useLayoutEffect, useRef } from 'react'

const SELECTED = '[aria-pressed="true"], [aria-checked="true"], [aria-selected="true"], [aria-current]:not([aria-current="false"])'

export interface GlideRect {
  readonly left: number
  readonly top: number
  readonly width: number
  readonly height: number
}

export interface GlideBoxMetrics {
  readonly rect: GlideRect
  readonly origin: GlideRect
  readonly offsetWidth: number
  readonly clientLeft: number
  readonly clientTop: number
  readonly scrollLeft: number
  readonly scrollTop: number
  readonly itemOffsetWidth: number
  readonly itemOffsetHeight: number
}

export interface GlideGeometry {
  readonly x: number
  readonly y: number
  readonly w: number
  readonly h: number
}

/** Measures the selected item in unscaled local coordinates of its `.exo-glide` container. */
export function computeGlideGeometry(metrics: GlideBoxMetrics): GlideGeometry | null {
  if (metrics.itemOffsetWidth <= 0 || metrics.itemOffsetHeight <= 0) return null
  const scale = metrics.origin.width / metrics.offsetWidth || 1
  return {
    x: (metrics.rect.left - metrics.origin.left) / scale - metrics.clientLeft + metrics.scrollLeft,
    y: (metrics.rect.top - metrics.origin.top) / scale - metrics.clientTop + metrics.scrollTop,
    w: metrics.itemOffsetWidth,
    h: metrics.itemOffsetHeight,
  }
}

/** Builds a composite-only transform from `from` (or resting `target`) to `target`'s intrinsic size. */
export function glideTransform(target: GlideGeometry, from?: GlideGeometry): string {
  if (!from || target.w <= 0 || target.h <= 0 || from.w <= 0 || from.h <= 0) {
    return `translate3d(${target.x}px, ${target.y}px, 0) scaleX(1) scaleY(1)`
  }
  const sx = from.w / target.w
  const sy = from.h / target.h
  return `translate3d(${from.x}px, ${from.y}px, 0) scaleX(${sx}) scaleY(${sy})`
}

function sameGeometry(a: GlideGeometry | null, b: GlideGeometry | null): boolean {
  return !!a && !!b && a.x === b.x && a.y === b.y && a.w === b.w && a.h === b.h
}

function instantMove(): boolean {
  const root = document.documentElement.dataset
  return root.exoMotion === 'reduce' || root.exoInput === 'key'
}

/**
 * The selected pill glides: one non-inheriting sibling fill under the group slides to the chosen
 * item using composite-only `transform` on the spring over `--dur-move` instead of animating layout.
 * Put the returned ref and `exo-glide` on the group; the selected item is whichever carries
 * aria-pressed, aria-checked, aria-selected or aria-current.
 */
export function useGlide<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  useLayoutEffect(() => {
    const box = ref.current
    if (!box) return
    const pill = document.createElement('span')
    pill.className = 'exo-glide-pill'
    pill.setAttribute('aria-hidden', 'true')
    box.prepend(pill)
    // A group mounted hidden (Settings before it opens) arms only after its
    // first visible placement, so the fill never slides in from nothing.
    let ready = 0
    let last: GlideGeometry | null = null
    let move = 0
    const glide = () => {
      const id = ++move
      box.classList.add('is-gliding')
      const runs = typeof pill.getAnimations === 'function'
        ? pill.getAnimations().filter(run => (run as CSSTransition).transitionProperty === 'transform')
        : []
      void Promise.allSettled(runs.map(run => run.finished)).then(() => { if (id === move) box.classList.remove('is-gliding') })
    }
    const place = () => {
      const on = box.querySelector<HTMLElement>(SELECTED)
      const pillStyle = pill.style
      if (!on || on.offsetWidth === 0 || on.offsetHeight === 0) {
        pillStyle.opacity = '0'
        last = null
        return
      }
      // Read before writes: capture current visual box if interrupting an active flight.
      const rect = on.getBoundingClientRect()
      const origin = box.getBoundingClientRect()
      const liveRect = box.classList.contains('is-gliding') ? pill.getBoundingClientRect() : null
      const next = computeGlideGeometry({
        rect,
        origin,
        offsetWidth: box.offsetWidth,
        clientLeft: box.clientLeft,
        clientTop: box.clientTop,
        scrollLeft: box.scrollLeft,
        scrollTop: box.scrollTop,
        itemOffsetWidth: on.offsetWidth,
        itemOffsetHeight: on.offsetHeight,
      })
      if (!next) {
        pillStyle.opacity = '0'
        last = null
        return
      }
      const scale = origin.width / box.offsetWidth || 1
      const from: GlideGeometry | null = liveRect && liveRect.width > 0 && liveRect.height > 0
        ? {
            x: (liveRect.left - origin.left) / scale - box.clientLeft + box.scrollLeft,
            y: (liveRect.top - origin.top) / scale - box.clientTop + box.scrollTop,
            w: liveRect.width / scale,
            h: liveRect.height / scale,
          }
        : last
      const animate = !!last && !sameGeometry(next, last) && box.classList.contains('is-glide-ready') && !instantMove()
      last = next
      if (animate && from) {
        pillStyle.transition = 'none'
        pillStyle.width = `${next.w}px`
        pillStyle.height = `${next.h}px`
        pillStyle.opacity = '1'
        pillStyle.transform = glideTransform(next, from)
        void pill.offsetWidth
        pillStyle.transition = ''
        pillStyle.transform = glideTransform(next)
        glide()
      } else {
        pillStyle.transition = ''
        pillStyle.width = `${next.w}px`
        pillStyle.height = `${next.h}px`
        pillStyle.transform = glideTransform(next)
        pillStyle.opacity = '1'
      }
      if (!ready) ready = requestAnimationFrame(() => box.classList.add('is-glide-ready'))
    }
    place()
    const sizes = new ResizeObserver(place)
    const watchChildren = () => {
      sizes.disconnect()
      sizes.observe(box)
      for (const child of box.children) if (child !== pill) sizes.observe(child)
    }
    watchChildren()
    const attributes = new MutationObserver((records) => {
      if (records.some(record => record.type === 'childList')) watchChildren()
      place()
    })
    attributes.observe(box, { subtree: true, childList: true, attributes: true, attributeFilter: ['aria-pressed', 'aria-checked', 'aria-selected', 'aria-current'] })
    // Measured while the page was out of sight or mid-transition (its boxes scaled), the fill could land between two
    // items and stay there: it is placed again when the window comes back and when a transition ends.
    const again = () => requestAnimationFrame(place)
    const shown = () => { if (document.visibilityState === 'visible') again() }
    const transitions = new MutationObserver(again)
    transitions.observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-vt'] })
    document.addEventListener('visibilitychange', shown)
    window.addEventListener('focus', again)
    return () => {
      cancelAnimationFrame(ready)
      move++
      box.classList.remove('is-gliding')
      pill.remove()
      attributes.disconnect()
      sizes.disconnect()
      transitions.disconnect()
      document.removeEventListener('visibilitychange', shown)
      window.removeEventListener('focus', again)
    }
  }, [])
  return ref
}

import { useCallback, useEffect, useRef, useState } from 'react'
import { shouldReduceMotion } from './motionPreference'

export function useTrackArrows<T extends HTMLElement>(count: number, options?: { wheel?: boolean }) {
  const trackRef = useRef<T>(null)
  const [edges, setEdges] = useState({ start: true, end: true })

  const measure = useCallback(() => {
    const el = trackRef.current
    if (!el) return
    const start = el.scrollLeft < 4
    const end = el.scrollLeft + el.clientWidth >= el.scrollWidth - 4
    setEdges(prev => (prev.start === start && prev.end === end ? prev : { start, end }))
  }, [])

  useEffect(() => {
    measure()
    const el = trackRef.current
    if (!el) return
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    const raf = requestAnimationFrame(measure)
    return () => {
      cancelAnimationFrame(raf)
      observer.disconnect()
    }
  }, [measure, count])

  const wheelEnabled = !!options?.wheel
  useEffect(() => {
    if (!wheelEnabled) return
    const el = trackRef.current
    if (!el) return
    const onWheel = (event: WheelEvent) => {
      if (Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return
      if (el.scrollWidth <= el.clientWidth + 2) return
      event.preventDefault()
      el.scrollLeft += event.deltaY
      measure()
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [wheelEnabled, measure])

  const page = useCallback((dir: 1 | -1, fraction = 0.8) => {
    const el = trackRef.current
    if (!el) return
    el.scrollBy({
      left: dir * el.clientWidth * fraction,
      behavior: shouldReduceMotion() ? 'auto' : 'smooth',
    })
  }, [])

  return { trackRef, edges, measure, page }
}

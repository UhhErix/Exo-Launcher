import { useCallback, useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type RefObject } from 'react'
import { allowHover } from '../../lib/hoverGate'
import type { ScrubMark } from '../../lib/librarySearch'

/** How long the index stays after the last scroll, so it is there to grab once the wheel stops. */
const AWAKE_MS = 1200
/** Two labels closer than this on the track: the later one is a dot. */
const LABEL_GAP = 18
/** A grid this short (in screens) needs no index. */
const MIN_SCREENS = 1.5

type Placed = { mark: ScrubMark; top: number; y: number; label: boolean }

/** Where a grid's row starts in its scroll root's content: the windowed grid's own geometry, read off the page. */
function rowTop(root: HTMLElement, grid: HTMLElement, index: number): number {
  const columns = Number(grid.dataset.columnCount) || 1
  const card = parseFloat(getComputedStyle(root).getPropertyValue('--exo-card-w')) || 160
  const style = getComputedStyle(grid)
  const gap = parseFloat(style.rowGap) || 0
  const padding = parseFloat(style.getPropertyValue('--exo-grid-padding-top')) || 0
  const offset = grid.getBoundingClientRect().top - root.getBoundingClientRect().top + root.scrollTop
  return offset + padding + Math.floor(index / columns) * (card * 1.5 + gap)
}

/**
 * The Library's index down the right edge, as Photos' scrubber: the current sort's groups (A to Z, Today to Never, 100+
 * hr ...), each where its games are in the grid. It shows while the grid scrolls or the pointer is at the edge, and
 * nothing at rest, so it never sits over a cover you are looking at. Pressed or dragged, it jumps to the group under the
 * pointer, the group's name riding beside it. Pointer only: the grid's own keys (Page Up and Down, Home, End, a letter
 * under Name) do the same from the keyboard.
 */
export function LibraryScrubber({ marks, active, covered, scrollRootRef }: {
  marks: ScrubMark[]
  /** The Library is the room on screen... */
  active: boolean
  /** ...with nothing over it (a game's sheet). */
  covered: boolean
  scrollRootRef: RefObject<HTMLElement | null>
}) {
  const shown = active && !covered
  const track = useRef<HTMLDivElement>(null)
  const [awake, setAwake] = useState(false)
  const [placed, setPlaced] = useState<Placed[]>([])
  const [at, setAt] = useState<{ y: number; label: string } | null>(null)
  const [now, setNow] = useState(0)
  const dragging = useRef(false)
  const grid = useCallback(() => scrollRootRef.current?.querySelector<HTMLElement>('.exo-library-grid .exo-windowed-game-grid') ?? null, [scrollRootRef])

  /** Each mark's place on the track, from the grid as it is laid out now. */
  const place = useCallback(() => {
    const root = scrollRootRef.current
    const list = grid()
    const height = track.current?.clientHeight ?? 0
    if (!root || !list || !height || root.scrollHeight < root.clientHeight * MIN_SCREENS) { setPlaced([]); return }
    const range = root.scrollHeight - root.clientHeight
    const under = parseFloat(getComputedStyle(root).scrollPaddingTop) || 0
    let last = -Infinity
    setPlaced(marks.map(mark => {
      const top = Math.max(0, rowTop(root, list, mark.index) - under)
      const y = Math.min(1, top / range) * height
      const label = y - last >= LABEL_GAP
      if (label) last = y
      return { mark, top, y, label }
    }))
    setNow(Math.min(1, root.scrollTop / range) * height)
  }, [grid, marks, scrollRootRef])

  // Awake while the grid scrolls, and for a moment after.
  useEffect(() => {
    const root = scrollRootRef.current
    if (!root || marks.length < 2 || !shown) return
    let timer = 0
    let frame = 0
    const onScroll = () => {
      window.clearTimeout(timer)
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => {
        const range = root.scrollHeight - root.clientHeight
        setNow(range > 0 ? Math.min(1, root.scrollTop / range) * (track.current?.clientHeight ?? 0) : 0)
      })
      setAwake(true)
      timer = window.setTimeout(() => { if (!dragging.current) setAwake(false) }, AWAKE_MS)
    }
    const observer = new ResizeObserver(() => place())
    observer.observe(root)
    root.addEventListener('scroll', onScroll, { passive: true })
    return () => { window.clearTimeout(timer); cancelAnimationFrame(frame); observer.disconnect(); root.removeEventListener('scroll', onScroll) }
  }, [marks, place, scrollRootRef, shown])
  // Placed again when it wakes: the grid above may have grown (Continue, a folder) since.
  useEffect(() => { if (awake) place() }, [awake, place])

  const nearest = (y: number) => placed.reduce<Placed | null>((best, item) => !best || Math.abs(item.y - y) < Math.abs(best.y - y) ? item : best, null)
  const jump = (event: ReactPointerEvent<HTMLDivElement>) => {
    const box = track.current?.getBoundingClientRect()
    const root = scrollRootRef.current
    if (!box || !root) return
    const y = Math.max(0, Math.min(box.height, event.clientY - box.top))
    const item = nearest(y)
    if (!item) return
    setAt({ y: item.y, label: item.mark.label })
    if (dragging.current && Math.abs(root.scrollTop - item.top) > 1) root.scrollTo({ top: item.top })
  }
  if (marks.length < 2 || !shown) return null
  return (
    <div
      ref={track}
      className={`exo-scrubber-track${awake || at ? ' is-awake' : ''}${marks.some(mark => mark.label.length > 2) ? ' is-words' : ''}`}
      aria-hidden="true"
      onPointerEnter={event => { if (!allowHover(event)) return; place(); setAwake(true) }}
      onPointerLeave={() => { if (!dragging.current) { setAt(null); setAwake(false) } }}
      onPointerMove={jump}
      onPointerDown={event => {
        if (event.button !== 0) return
        event.currentTarget.setPointerCapture(event.pointerId)
        dragging.current = true
        jump(event)
      }}
      onPointerUp={event => {
        dragging.current = false
        event.currentTarget.releasePointerCapture(event.pointerId)
      }}
      onLostPointerCapture={() => { dragging.current = false }}
    >
      {placed.length ? (
        <div className="exo-scrubber">
          {placed.map(item => item.label
            ? <span key={item.mark.index} className="exo-scrubber-label" style={{ top: item.y }}>{item.mark.label}</span>
            : <span key={item.mark.index} className="exo-scrubber-dot" style={{ top: item.y }} />)}
          <span className="exo-scrubber-now" style={{ top: now }} />
        </div>
      ) : null}
      {at ? <span className="exo-scrubber-bubble exo-badge" style={{ top: at.y }}>{at.label}</span> : null}
    </div>
  )
}

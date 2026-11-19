import { Children, useLayoutEffect, useRef, type ReactNode, type RefObject } from 'react'
import { Heart } from '../../brand/icons'
import { railWidth } from './rail'

/** The gap between the hero and the columns, between columns and between panels, as in devices.css. */
const GAP = 12
/** A column of panels: never so narrow a row's choices wrap (times text size), nor so wide a label and its control drift apart. */
const COLUMN_MIN = 400
const COLUMN_MAX = 760
const MAX_COLUMNS = 4
/** How much a row may grow so a shorter column ends level with the others (53px rows up to 69). */
const ROW_GROW = 16
const STACKED = '(width < 860px)'

type Plan = { key: string; count: number; cuts: number[]; column: number }

/**
 * A device's page, laid out as Exo Mouse lays out a mouse (DESIGN.md › Devices): one screen, no scroll. The hero on the
 * left, the same rail as Exo Mouse's (railWidth) and the room's whole height: the device's picture in its own light, its
 * name and how it's linked, a readout of figures on hairlines, and thanks to whoever made what it's built on at its foot.
 * Its panels (each child is one) stand in columns beside it, placed by useBand, every column ending level with the hero.
 * The audio page and the controller page are built on it.
 */
export function DevicePage({ name, status, art, stats, credit, children }: {
  name: string
  status: string
  art: ReactNode
  /** The readout: label and figure pairs; a pair whose figure is null isn't drawn. */
  stats: { label: string; value: ReactNode | null }[]
  credit?: ReactNode
  children: ReactNode
}) {
  const shown = stats.filter(stat => stat.value !== null && stat.value !== undefined)
  const { page, stack } = useBand(Children.toArray(children).length)
  return (
    <section ref={page} className="exo-device-page" aria-label={name}>
      <aside className="exo-device-page-hero">
        <div className="exo-device-hero-art">{art}</div>
        <div className="exo-device-page-id">
          <h1>{name}</h1>
          <p>{status}</p>
        </div>
        {shown.length ? (
          <dl className="exo-device-page-stats">
            {shown.map(stat => <div key={stat.label}><dt>{stat.label}</dt><dd>{stat.value}</dd></div>)}
          </dl>
        ) : null}
        {credit ? (
          <p className="exo-device-page-credit">
            {credit}
            <Heart filled size={12} className="exo-device-page-heart" />
          </p>
        ) : null}
      </aside>
      <div ref={stack} className="exo-device-page-panels">
        {children}
        {/* Where a column ends: useBand shows one fewer than it makes columns. */}
        {Array.from({ length: MAX_COLUMNS - 1 }, (_, i) => <span key={i} className="exo-device-page-break" aria-hidden="true" hidden />)}
      </div>
    </section>
  )
}

/** One panel: its name over its rows; a panel with no rows isn't drawn (styles/system.css). */
export function DevicePanel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="exo-system-panel exo-device-page-panel" aria-label={title}>
      <h2>{title}</h2>
      <div className="exo-system-panel-body">{children}</div>
    </section>
  )
}

/**
 * Lays the page out again when the room changes size, the text size changes (the name's height does), a panel comes
 * or goes, or a panel's rows change (a test's results, a search's matches), at most once a frame. Live readings (a
 * stick's dot, a trigger's pull) change attributes only, which nothing here watches.
 */
function useBand(panelCount: number) {
  const page = useRef<HTMLElement>(null)
  const stack = useRef<HTMLDivElement>(null)
  const plan = useRef<Plan | null>(null)
  useLayoutEffect(() => layOut(page.current, stack.current, plan), [panelCount])
  useLayoutEffect(() => {
    const p = page.current
    const s = stack.current
    const room = p?.parentElement
    if (!p || !s || !room) return
    let frame = 0
    const again = () => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => layOut(p, s, plan))
    }
    const sized = new ResizeObserver(again)
    sized.observe(room)
    const title = p.querySelector('.exo-device-page-id h1')
    if (title) sized.observe(title)
    const changed = new MutationObserver(again)
    changed.observe(s, { childList: true, subtree: true, characterData: true })
    return () => { sized.disconnect(); changed.disconnect(); cancelAnimationFrame(frame) }
  }, [])
  return { page, stack }
}

const scaleOf = () => parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1

/** Every way to split n panels, in order, into `count` columns: the index each column after the first starts at. */
function* splits(n: number, count: number, from = 1): Generator<number[]> {
  if (count === 1) { yield []; return }
  for (let at = from; at <= n - count + 1; at++) for (const rest of splits(n, count - 1, at + 1)) yield [at, ...rest]
}

type Laid = { count: number; cuts: number[]; column: number; band: number; fits: boolean; empty: number }
type Column = { start: number; end: number; height: number; rows: number }

/**
 * The band: as Exo Mouse's, the fewest equal columns (400 to 760px) beside the rail that hold every panel in the room's
 * height, so no column stands half empty, and of the ways to split the panels into them the most even; when none
 * fits, the most columns the room holds, and the page scrolls with the hero staying. The band is the room's height, or the tallest column's when that is taller; a shorter column
 * grows its rows (up to 16px each) and its last panel takes the rest, so every column ends level with the hero. Once
 * planned, the same columns hold while their panels change (a test's results come in, a search opens) as long as they
 * still fit, so nothing jumps mid-test; a new size or a panel coming or going plans again.
 */
function layOut(p: HTMLElement | null, s: HTMLElement | null, last: RefObject<Plan | null>) {
  const room = p?.parentElement
  if (!p || !s || !room) return
  const panels = [...s.querySelectorAll<HTMLElement>(':scope > .exo-device-page-panel')]
  const breaks = [...s.querySelectorAll<HTMLElement>(':scope > .exo-device-page-break')]
  if (window.matchMedia(STACKED).matches) {
    unlay(p, s, panels, breaks)
    last.current = null
    return
  }
  const box = getComputedStyle(p)
  const width = p.clientWidth - parseFloat(box.paddingLeft) - parseFloat(box.paddingRight)
  const height = room.clientHeight - parseFloat(box.paddingTop) - parseFloat(box.paddingBottom)
  // Hidden (another room, Exo Mouse's page): nothing to measure; the next showing lays it out.
  if (width <= 0 || height <= 0) return
  const band = sizeUp(s, panels, width, height)
  const kept = last.current
  // Same room, same panels: keep the columns while they still fit.
  const same = kept?.key === band.key ? band.judge(kept.count, kept.cuts, band.measure(kept.column), kept.column) : null
  const laid = same?.fits ? same : band.search()
  last.current = { key: band.key, count: laid.count, cuts: laid.cuts, column: laid.column }
  band.apply(p, laid, breaks)
}

/** A narrow window: the panels stack under the hero, as the stylesheet lays them. */
function unlay(p: HTMLElement, s: HTMLElement, panels: HTMLElement[], breaks: HTMLElement[]) {
  for (const name of ['--exo-device-hero-w', '--exo-device-hero-h', '--exo-device-band']) p.style.removeProperty(name)
  s.style.removeProperty('--exo-device-col-w')
  panels.forEach(panel => { panel.style.order = ''; panel.classList.remove('is-last'); panel.style.removeProperty('--exo-device-row-grow') })
  breaks.forEach(br => { br.hidden = true })
}

/** What the band is made of, measured: each shown panel's height at a column's width and its rows. */
function sizeUp(s: HTMLElement, panels: HTMLElement[], width: number, height: number) {
  const k = scaleOf()
  const hero = railWidth(width, k)
  const columnMin = COLUMN_MIN * k
  const rowGrow = ROW_GROW * k

  // Natural heights: one column at a time, rows at their own size.
  s.classList.add('is-measuring')
  panels.forEach(panel => panel.style.removeProperty('--exo-device-row-grow'))
  const shown = panels.filter(panel => panel.offsetHeight > 0)
  const rows = shown.map(panel => panel.querySelectorAll('.exo-set-item').length)
  const measure = (column: number) => {
    s.style.setProperty('--exo-device-col-w', `${column}px`)
    return shown.map(panel => panel.offsetHeight)
  }
  const columnsOf = (heights: number[], cuts: number[]): Column[] => [0, ...cuts].map((start, c) => {
    const end = cuts[c] ?? shown.length
    const own = heights.slice(start, end)
    return { start, end, height: own.reduce((t, h) => t + h, 0) + GAP * (own.length - 1), rows: rows.slice(start, end).reduce((t, r) => t + r, 0) }
  })
  const judge = (count: number, cuts: number[], heights: number[], column: number): Laid => {
    const columns = columnsOf(heights, cuts)
    const tallest = Math.max(...columns.map(col => col.height))
    const band = Math.max(tallest, height)
    // Squared: two half-empty columns are better than one standing empty beside a full one.
    const empty = columns.reduce((t, col) => t + Math.max(0, band - col.height - col.rows * rowGrow) ** 2, 0)
    return { count, cuts, column, band, fits: tallest <= height + 0.5, empty }
  }
  const better = (a: Laid, b: Laid | null) => !b || (a.fits !== b.fits ? a.fits : a.fits ? a.empty < b.empty - 1 : a.band < b.band)
  const columnFor = (count: number) => (width - hero) / count - GAP

  /** The fewest columns that hold the panels (none wider than COLUMN_MAX unless there are too few panels to fill the room), every split of them: the best. */
  const search = (): Laid => {
    let most = Math.min(MAX_COLUMNS, Math.max(1, shown.length))
    while (most > 1 && columnFor(most) < columnMin) most--
    if (!shown.length) return judge(1, [], [], Math.max(columnMin, Math.min(COLUMN_MAX, columnFor(1))))
    let best: Laid | null = null
    for (let count = 1; count <= most; count++) {
      if (columnFor(count) > COLUMN_MAX && count < most) continue
      const column = Math.max(columnMin, Math.min(COLUMN_MAX, columnFor(count)))
      const heights = measure(column)
      best = null
      for (const cuts of splits(shown.length, count)) {
        const tried = judge(count, cuts, heights, column)
        if (better(tried, best)) best = tried
      }
      if (best?.fits) return best
    }
    return best ?? judge(1, [], [], columnMin)
  }

  /**
   * Sets it: the widths, the band, each panel's column, the rows each column grows to end level, and the breaks. The
   * heights are read again at the final width (a narrower column may wrap a line), and the band never ends above one.
   */
  const apply = (p: HTMLElement, laid: Laid, breaks: HTMLElement[]) => {
    const columns = columnsOf(measure(laid.column), laid.cuts)
    const band = Math.max(laid.band, ...columns.map(col => col.height))
    s.classList.remove('is-measuring')
    p.style.setProperty('--exo-device-hero-w', `${hero}px`)
    p.style.setProperty('--exo-device-band', `${band}px`)
    // A band taller than the room scrolls; the hero stays the room's height, as Exo Mouse's does.
    p.style.setProperty('--exo-device-hero-h', `${height}px`)
    columns.forEach((col, c) => {
      const grow = col.rows ? Math.max(0, Math.min(rowGrow, Math.floor((band - col.height) / col.rows))) : 0
      const own = shown.slice(col.start, col.end)
      own.forEach((panel, i) => {
        panel.style.order = String(c * 100 + i)
        panel.style.setProperty('--exo-device-row-grow', `${grow}px`)
        panel.classList.toggle('is-last', i === own.length - 1)
      })
    })
    breaks.forEach((br, i) => {
      br.hidden = i >= laid.count - 1
      br.style.order = String(i * 100 + 99)
    })
  }

  const key = `${Math.round(width)}x${Math.round(height)}|${k}|${shown.map(panel => panel.getAttribute('aria-label')).join(',')}`
  return { key, measure, judge, search, apply }
}

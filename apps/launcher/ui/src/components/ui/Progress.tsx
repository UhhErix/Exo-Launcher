import type { CSSProperties, ReactNode } from 'react'
import { cn } from '../../lib/utils'

export type ProgressProps = {
  /** How far along, 0 to 1; null or undefined while that isn't known (the lit segment glides along instead). */
  value?: number | null
  /** What is happening, for screen readers ("Downloading Elden Ring"). Shown under the bar with `caption`. */
  label: string
  /** The label under the bar too, muted, with how far in whole percent (a page or list waiting on something). */
  caption?: boolean
  /** small: on a card or art; large: a window's or an update's one bar. */
  size?: 'small' | 'large'
  /** Along the inside foot of what holds it (a button, a pill, a card's art), which must be positioned. */
  edge?: boolean
  /** done: it finished (green); bad: it stopped short (red). */
  tone?: 'done' | 'bad'
  /** Drawn only. A separate progressbar carries the figures, because this one sits inside a button. */
  decorative?: boolean
  className?: string
}

/**
 * How far along, in whole percent, for a share done (0 to 1): floored, so it says 100% only once all of it is done, and
 * null while the amount isn't known. The tiny lift keeps a share like 29 / 100 (0.2899… in floating point) at 29.
 */
export function wholePercent(value: number | null | undefined): number | null {
  if (typeof value !== 'number' || !Number.isFinite(value)) return null
  const share = Math.min(1, Math.max(0, value))
  return Math.min(share < 1 ? 99 : 100, Math.floor(share * 100 + 1e-9))
}

/**
 * A wait's words, then how far it is, live ("Downloading · 2.1 of 40 GB · 54%"); the words alone while the amount isn't
 * known. The figure is tabular and holds the width of "100%" from the start, so nothing moves as it counts. It is for the
 * eyes: screen readers get the same figure as the bar's value, and a live region beside it isn't read out at each step.
 */
export function ProgressWords({ words, value }: { words: ReactNode; value?: number | null }) {
  const percent = wholePercent(value)
  // The figure and an invisible "100%" share one cell, so the words keep their place as it counts and the line stays one line tall.
  return <>{words}{percent === null ? null : <span aria-hidden="true">{' · '}<span className="exo-percent"><span>{percent}%</span></span></span>}</>
}

/**
 * Exo's bar (DESIGN.md › Motion › Waits), for work with a measure: a download, an install, a compile, an upload, a counted scan.
 * Short waits in a control spin (Spinner, `exo-spin`) and content on its way shines (Skeleton); never a bar in a button.
 * A Liquid Glass track, the ink's fill with a sheen running through it; with no number,
 * a lit segment glides along the track. It moves on transforms only, linear as progress is, and holds still under
 * Reduce motion and while Exo is in the background. The words beside it end in its whole percent (ProgressWords).
 */
export function Progress({ value, label, caption, size, edge, tone, decorative, className }: ProgressProps) {
  const percent = wholePercent(value)
  const known = percent !== null
  const fraction = known ? Math.min(1, Math.max(0, value as number)) : 0
  const bar = (
    <span
      role={decorative ? undefined : 'progressbar'}
      aria-hidden={decorative || undefined}
      aria-label={decorative ? undefined : label}
      aria-valuemin={!decorative && known ? 0 : undefined}
      aria-valuemax={!decorative && known ? 100 : undefined}
      aria-valuenow={!decorative ? percent ?? undefined : undefined}
      className={cn('exo-progress', !known && 'is-unknown', size && `is-${size}`, edge && 'is-edge', tone && `is-${tone}`, !caption && className)}
      style={{ '--exo-progress': fraction } as CSSProperties}
    >
      <span className="exo-progress-track"><span className="exo-progress-fill" /></span>
    </span>
  )
  if (!caption) return bar
  return (
    <span className={cn('exo-progress-block', className)}>
      {bar}
      <span className="exo-progress-caption" aria-hidden="true"><ProgressWords words={label} value={value} /></span>
    </span>
  )
}

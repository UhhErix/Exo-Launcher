export type GamepadKind = 'xbox' | 'playstation' | 'nintendo'
export type Direction = 'up' | 'down' | 'left' | 'right'

export type SpatialRect = {
  left: number
  top: number
  right: number
  bottom: number
  width: number
  height: number
}

export type GamepadGlyphs = {
  confirm: string
  back: string
  action: string
  search: string
  view: string
  prevRoom: string
  nextRoom: string
  prevTab: string
  nextTab: string
  menu: string
}

export function detectGamepadKind(id: string): GamepadKind {
  const lower = id.toLowerCase()
  if (/dualsense|dualshock|054c|sony|wireless controller|ps4|ps5|playstation/.test(lower)) return 'playstation'
  if (/nintendo|pro controller|joy-con|057e|switch/.test(lower)) return 'nintendo'
  return 'xbox'
}

export function gamepadLabels(kind: GamepadKind): GamepadGlyphs {
  if (kind === 'playstation') {
    return { confirm: '✕', back: '○', action: '◻', search: '△', view: 'Create', prevRoom: 'L1', nextRoom: 'R1', prevTab: 'L2', nextTab: 'R2', menu: 'Options' }
  }
  if (kind === 'nintendo') {
    return { confirm: 'A', back: 'B', action: 'X', search: 'Y', view: '-', prevRoom: 'L', nextRoom: 'R', prevTab: 'ZL', nextTab: 'ZR', menu: '+' }
  }
  return { confirm: 'A', back: 'B', action: 'X', search: 'Y', view: 'View', prevRoom: 'LB', nextRoom: 'RB', prevTab: 'LT', nextTab: 'RT', menu: 'Menu' }
}

function centerOf(rect: SpatialRect) {
  return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }
}

function overlapSpan(a0: number, a1: number, b0: number, b1: number): number {
  return Math.max(0, Math.min(a1, b1) - Math.max(a0, b0))
}

export function scoreDirectionCandidate(from: SpatialRect, candidate: SpatialRect, dir: Direction): number | null {
  const fc = centerOf(from)
  const cc = centerOf(candidate)
  const dx = cc.x - fc.x
  const dy = cc.y - fc.y
  const horizontal = dir === 'left' || dir === 'right'
  const primary = dir === 'right' ? dx : dir === 'left' ? -dx : dir === 'down' ? dy : -dy
  if (primary <= 4) return null
  const cross = Math.abs(horizontal ? dy : dx)
  const aligned = horizontal
    ? overlapSpan(from.top, from.bottom, candidate.top, candidate.bottom) > 0
    : overlapSpan(from.left, from.right, candidate.left, candidate.right) > 0
  if (!aligned && cross > primary * 1.8) return null
  const edgeGap = dir === 'right'
    ? Math.max(0, candidate.left - from.right)
    : dir === 'left'
      ? Math.max(0, from.left - candidate.right)
      : dir === 'down'
        ? Math.max(0, candidate.top - from.bottom)
        : Math.max(0, from.top - candidate.bottom)
  const crossCost = aligned ? cross * 1.25 - 32 : cross * 5.2 + 120
  return edgeGap * 1.15 + primary * 0.35 + crossCost
}

export function pickSpatialTarget<T>(
  from: SpatialRect,
  candidates: ReadonlyArray<{ item: T; rect: SpatialRect }>,
  dir: Direction,
): T | null {
  let best: T | null = null
  let bestScore = Number.POSITIVE_INFINITY
  for (const candidate of candidates) {
    const score = scoreDirectionCandidate(from, candidate.rect, dir)
    if (score !== null && score < bestScore) {
      bestScore = score
      best = candidate.item
    }
  }
  return best
}

export function stepSliderValue(min: number, max: number, step: number | null, current: number, delta: -1 | 1): number {
  const span = Math.max(0, max - min)
  const inc = step && Number.isFinite(step) && step > 0 ? step : span > 0 ? span / 20 : 1
  const raw = current + delta * inc
  const clamped = Math.min(max, Math.max(min, raw))
  const decimals = inc.toString().includes('.') ? inc.toString().split('.')[1].length : 0
  return Number(clamped.toFixed(Math.min(decimals, 4)))
}

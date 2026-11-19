import { shouldReduceMotion } from './motionPreference'

// --ease-spring's spring (tokens.css): damped at 0.9, its natural frequency in units of the animation's duration,
// fitted to the token's linear() to within 0.0007, so a curve baked here and the token are one shape.
const DAMPING = 0.9
const FREQUENCY = 5.473
// Faster than this the spring overshoots visibly before it lands (0.17% at 2, 0.7% at 5): a harder throw is capped.
const MAX_VELOCITY = 2
const TOLERANCE = 0.002
const ID = 'exo-spring'
const baked = new Map<number, string>()

/** Where the spring is at t (0 to 1 of its duration), leaving 0 for 1 at `velocity` (progress per duration). */
function at(t: number, velocity: number) {
  const decay = DAMPING * FREQUENCY
  const ringing = FREQUENCY * Math.sqrt(1 - DAMPING * DAMPING)
  return 1 + Math.exp(-decay * t) * ((velocity - decay) / ringing * Math.sin(ringing * t) - Math.cos(ringing * t))
}

/**
 * --ease-spring leaving at `velocity` (progress per duration: a thing thrown at v px/ms that has d px to go over T ms
 * leaves at v·T/d) as a CSS linear() easing, so the compositor runs it. At 0 it is the token's own curve. It always
 * ends at 1 on its last frame.
 */
export function springEasing(velocity = 0) {
  const v = Number.isFinite(velocity) ? Math.round(Math.min(MAX_VELOCITY, Math.max(-MAX_VELOCITY, velocity)) * 10) / 10 : 0
  let easing = baked.get(v)
  if (!easing) {
    const points = Array.from({ length: 81 }, (_, i) => [i / 80, at(i / 80, v)])
    points[80] = [1, 1]
    // The fewest points that stay within TOLERANCE of the spring (Douglas-Peucker).
    const keep = new Set([0, 80])
    const split = (from: number, to: number) => {
      let worst = 0, index = -1
      for (let i = from + 1; i < to; i++) {
        const [t, x] = points[i], [t0, x0] = points[from], [t1, x1] = points[to]
        const off = Math.abs(x - (x0 + (x1 - x0) * (t - t0) / (t1 - t0)))
        if (off > worst) { worst = off; index = i }
      }
      if (worst <= TOLERANCE) return
      keep.add(index)
      split(from, index)
      split(index, to)
    }
    split(0, 80)
    easing = `linear(${[...keep].sort((a, b) => a - b).map(i => i === 0 ? '0' : i === 80 ? '1'
      : `${+points[i][1].toFixed(4)} ${+(points[i][0] * 100).toFixed(2)}%`).join(', ')})`
    baked.set(v, easing)
  }
  return easing
}

/** Stops the element's spring where it is on screen, holds it there in its own translate, and says where that is. */
export function holdSpring(element: HTMLElement): [number, number] {
  const [x = 0, y = 0] = getComputedStyle(element).translate.split(' ').map(n => parseFloat(n) || 0)
  for (const run of element.getAnimations()) if (run.id === ID) run.cancel()
  element.style.translate = `${x}px ${y}px`
  return [x, y]
}

/**
 * Brings the element from (x, y) px off its laid-out place home on the spring over the move time, leaving at the
 * velocity it was let go with (vx, vy in px/ms; the part of it across the way home is dropped, so one curve moves
 * it on the compositor). A newer spring takes over from where this one is on screen (holdSpring). Under Reduce motion it lands
 * at once.
 */
export function springHome(element: HTMLElement, x: number, y: number, vx = 0, vy = 0) {
  for (const run of element.getAnimations()) if (run.id === ID) run.cancel()
  element.style.translate = ''
  const far = x * x + y * y
  if (far < 1 || shouldReduceMotion()) return
  const duration = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--dur-move')) || 220
  const velocity = -(vx * x + vy * y) / far * duration
  element.animate({ translate: [`${x}px ${y}px`, '0px 0px'] }, { id: ID, duration, easing: springEasing(velocity) })
}

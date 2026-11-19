import { Fragment, useLayoutEffect, useRef, type ReactNode } from 'react'
import { Skeleton } from 'skullet/react'
import { onPageActivityChanged, pageIsActive } from '../../lib/pageActivity'
import { cn } from '../../lib/utils'

/**
 * Content on its way (DESIGN.md › Waits): `children` is the real thing's own markup with sample words, laid out unseen,
 * and Skullet draws a skeleton in exactly that shape, so nothing moves when the content takes its place. Samples must
 * not fetch or subscribe: use the row's plain markup, never a component that reads its id. Screen readers hear `label`;
 * leave it off a part of a wait that something around it already announces.
 * Only the wait goes in here, never the content: Skullet watches and stores the shape of what it reveals, too costly
 * for a live list.
 */
export function Wait({ label, className, children }: { label?: string; className?: string; children: ReactNode }) {
  const box = useRef<HTMLDivElement>(null)
  // Skullet's sweep is its own Web Animations, out of reach of the CSS that pauses Exo's loops. Off screen, backstage
  // (a warm room laid out behind the one in front) or in the background data-exo-still sets --skullet-animation to none
  // (waits.css), and touching the sample has Skullet measure again and read it. Hiding a room (BootGate's Activity)
  // destroys Skullet's measuring and leaves the DOM, so its infinite animations would run on under display: none for
  // good: they go here, and the next showing draws them anew.
  useLayoutEffect(() => {
    const root = box.current
    const ghost = root?.querySelector('[skullet-ghost]')
    if (!root || !ghost) return
    const room = root.closest('.exo-room')
    let seen = true
    const sync = () => ghost.toggleAttribute('data-exo-still', !seen || !pageIsActive() || !!room?.classList.contains('is-backstage'))
    sync()
    const off = onPageActivityChanged(sync)
    const watch = new IntersectionObserver(([entry]) => { seen = entry.isIntersecting; sync() })
    watch.observe(root)
    const stage = new MutationObserver(sync)
    if (room) stage.observe(room, { attributes: true, attributeFilter: ['class'] })
    return () => {
      off()
      watch.disconnect()
      stage.disconnect()
      const bones = root.querySelector('[skullet-layer]')?.shadowRoot
      for (const bone of bones?.children ?? []) for (const animation of bone.getAnimations()) animation.cancel()
      bones?.replaceChildren()
    }
  }, [])
  return <div ref={box} className={cn('exo-wait', className)} role={label ? 'status' : undefined} aria-label={label}><Skeleton loading>{children}</Skeleton></div>
}

/** A row's state still being read (Checking): one word's bar in the row's control column. */
export function WaitWord({ label }: { label: string }) {
  return <Wait label={label} className="exo-wait-word"><span>Checking</span></Wait>
}

const SAMPLE = ['Sample name', 'Another longer sample name', 'A short one', 'Sample of a middling name', 'A very long sample name for a row', 'Name']

/** Words of a varied length for a sample, the `i`th of a list, so its rows don't read as one bar repeated. */
export const sample = (i: number) => SAMPLE[i % SAMPLE.length]

/** A sample list's rows. */
export function times(n: number, row: (i: number) => ReactNode) {
  return Array.from({ length: n }, (_, i) => <Fragment key={i}>{row(i)}</Fragment>)
}

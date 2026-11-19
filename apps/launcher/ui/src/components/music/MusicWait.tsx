import { cn } from '../../lib/utils'
import { Wait, sample, times } from '../ui/Wait'

const art = <span className="exo-music-art" skullet-block="" />

/** Home's shapes: the recent grid and two shelves. */
function HomeSample() {
  return <>
    <section className="exo-music-section">
      <div className="exo-music-recent">{times(8, i => <div className="exo-music-recent-tile"><span className="exo-music-recent-open">{art}<span className="exo-music-row-title">{sample(i)}</span></span></div>)}</div>
    </section>
    {times(2, shelf => (
      <section className="exo-music-section">
        <div className="exo-music-section-head"><h2>{sample(shelf)}</h2></div>
        <div className="exo-music-shelf">{times(8, i => <div className="exo-music-card"><span className="exo-music-card-open">{art}<span className="exo-music-card-title">{sample(i)}</span><span className="exo-music-card-sub">{sample(i + 2)}</span></span></div>)}</div>
      </section>
    ))}
  </>
}

/** The first page still on its way after the room has shown (a slow service): Home's shapes where Home will be. */
export function PageBones({ label }: { label: string }) {
  return <Wait label={label}><HomeSample /></Wait>
}

/**
 * The room before its first showing (DESIGN.md › Waits): the room's own frame, your library down the left, Home in the
 * middle (the recent grid, two shelves) and the player along the bottom, each a clear skeleton in the place
 * and size of what comes, shining. It stands from the moment the room opens, through the service starting, until your
 * library and the first page are both in; then it fades once and the room is there whole, never squares, then a frame,
 * then content. `leaving` starts the fade; `onLeft` follows it.
 */
export function MusicWait({ name, leaving = false, onLeft }: { name: string; leaving?: boolean; onLeft?: () => void }) {
  return (
    <div className={cn('exo-music exo-music-cover', onLeft && 'is-over', leaving && 'is-leaving')} role="status" aria-label={`Opening ${name}`}
      onAnimationEnd={event => { if (event.target === event.currentTarget && leaving) onLeft?.() }}>
      <div className="exo-music-side" aria-hidden="true">
        <Wait>
          {times(2, i => <span className="exo-music-row exo-music-nav"><svg width="16" height="16" /><span>{sample(i + 5)}</span></span>)}
          <span className="exo-music-search" skullet-block="" />
          <ul className="exo-music-library">{times(12, i => <li className="exo-music-library-item"><span className="exo-music-row exo-music-library-row">{art}<span className="exo-music-row-text"><span className="exo-music-row-title">{sample(i)}</span><span className="exo-music-row-sub">{sample(i + 2)}</span></span></span></li>)}</ul>
        </Wait>
      </div>
      <div className="exo-music-main exo-music-cover-page" aria-hidden="true"><Wait><HomeSample /></Wait></div>
      <div className="exo-glass exo-music-bar" aria-hidden="true">
        <Wait><div className="exo-music-bar-song"><span className="exo-music-art exo-music-bar-art" skullet-block="" /><span className="exo-music-row-text"><span className="exo-music-row-title">{sample(0)}</span><span className="exo-music-row-sub">{sample(2)}</span></span></div></Wait>
        <Wait><div className="exo-music-bar-controls"><span className="exo-music-bar-play" skullet-block="" /></div></Wait>
      </div>
    </div>
  )
}

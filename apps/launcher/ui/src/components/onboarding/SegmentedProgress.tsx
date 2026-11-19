/** The first run's progress: one segment per step, 2 px high, 4 px apart, 24 px from the top (SPEC-v2). Filled segments are done. */
export function SegmentedProgress({ count, done, label }: { count: number; done: number; label: string }) {
  return (
    <div className="exo-ob-segments" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={count} aria-valuenow={done} aria-valuetext={label}>
      {Array.from({ length: count }, (_, index) => <span key={index} className={index < done ? 'is-done' : undefined} aria-hidden="true" />)}
    </div>
  )
}

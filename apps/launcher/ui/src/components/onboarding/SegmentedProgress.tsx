export function SegmentedProgress({ count, done, label }: { count: number; done: number; label: string }) {
  return (
    <div className="exo-ob-segments" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={count} aria-valuenow={done} aria-valuetext={label}>
      {Array.from({ length: count }, (_, index) => <span key={index} className={index < done ? 'is-done' : undefined} aria-hidden="true" />)}
    </div>
  )
}

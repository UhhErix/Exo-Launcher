/** A still reading of how full something is (DESIGN.md › Grammar › Meters); the words beside it say the amount. Work under way is `Progress`. */
export function Meter({ share, tone }: { share: number; tone?: 'is-warn' | 'is-bad' | null }) {
  return <span className={`exo-meter${tone ? ` ${tone}` : ''}`} aria-hidden="true"><span style={{ width: `${Math.round(Math.max(0, Math.min(1, share)) * 100)}%` }} /></span>
}

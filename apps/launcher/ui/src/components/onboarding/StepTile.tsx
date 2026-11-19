import type { ReactNode } from 'react'
import { Check } from '../../brand/icons'

/**
 * The first run's one tile (SPEC-v2), for every step: 64 px tall, an icon, a title and one line of detail. The status or
 * action sits on the right (Connected, Sign in, Add a folder). A choice tile is a radio (or a toggle button) with the
 * 2 px ring when it is the one chosen; a status tile is read-only.
 */
export function StepTile({ icon, title, detail, selected, onPress, action, radio, tabIndex, ariaDisabled }: {
  icon: ReactNode
  title: string
  detail?: ReactNode
  /** A choice: true when chosen. Undefined for a status tile. */
  selected?: boolean
  onPress?: () => void
  /** What sits on the right: a status word, or a button. Not used on a choice tile, which shows its own mark. */
  action?: ReactNode
  /** The choice belongs to a radio group (arrow keys move between them). */
  radio?: boolean
  tabIndex?: number
  ariaDisabled?: boolean
}) {
  const right = action ?? (selected !== undefined ? (selected ? <Check size={14} /> : null) : null)
  const body = (
    <>
      <span className="exo-step-tile-icon" aria-hidden="true">{icon}</span>
      <span className="exo-step-tile-words">
        <span className="exo-step-tile-title">{title}</span>
        {detail ? <span className="exo-step-tile-detail">{detail}</span> : null}
      </span>
      {right ? <span className="exo-step-tile-action">{right}</span> : null}
    </>
  )
  if (!onPress) return <div className="exo-step-tile">{body}</div>
  return (
    <button
      type="button"
      className={`exo-step-tile is-choice${selected ? ' is-selected' : ''}`}
      role={radio ? 'radio' : undefined}
      aria-checked={radio ? selected === true : undefined}
      aria-pressed={radio ? undefined : selected === true}
      tabIndex={tabIndex}
      aria-disabled={ariaDisabled || undefined}
      onClick={onPress}
    >
      {body}
    </button>
  )
}

import type { ReactNode } from 'react'
import { Check } from '../../brand/icons'

export function StepTile({ icon, title, detail, selected, onPress, action, radio, tabIndex, ariaDisabled }: {
  icon: ReactNode
  title: string
  detail?: ReactNode
  selected?: boolean
  onPress?: () => void
  action?: ReactNode
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
